using System.Runtime.CompilerServices;

namespace Shenora.Windows;

/// <summary>
/// Disables a window while a dialog it owns is up, with a nested count so overlapping dialogs do not enable it early:
/// <see cref="FormInteraction"/>'s blocking, for any window rather than the main one.
/// </summary>
internal static class WindowBlock
{
    private sealed class Count
    {
        public int Value;
    }

    private static readonly ConditionalWeakTable<Form, Count> Counts = new();

    public static void Block(Form window)
    {
        lock (Counts)
        {
            var count = Counts.GetOrCreateValue(window);
            if (++count.Value == 1) Apply(window, count);
        }
    }

    public static void Unblock(Form window)
    {
        lock (Counts)
        {
            if (!Counts.TryGetValue(window, out var count)) return;
            count.Value = Math.Max(0, count.Value - 1);
            if (count.Value == 0) Apply(window, count);
        }
    }

    // ⚠ Posted, never invoked: this runs under the lock, and a blocking Invoke under a lock is the pool-vs-UI deadlock.
    // The post applies the LATEST state when it runs, so an unblock that overtakes a block cannot leave the window disabled.
    // A window that cannot take a post is closing, and blocking it is a no-op.
    private static void Apply(Form window, Count count) =>
        new WinFormsUiDispatcher(window).Post(() =>
        {
            lock (Counts) window.Enabled = count.Value == 0;
        });
}
