using System.Runtime.InteropServices;

namespace Shenora.Windows;

/// <summary>
/// Lights a window's taskbar button to ask for attention, and puts it out: <c>FlashWindowEx</c>.
/// <para>
/// <see cref="Start"/> flashes the BUTTON alone, never the caption, until the window comes to the foreground, which Windows
/// stops by itself; <see cref="Stop"/> puts it out sooner. Both are posted to the window's own thread and never block, so
/// they can be called from whatever thread learned that something waits, and do nothing for a window that has no handle
/// (yet) or is gone.
/// </para>
/// </summary>
public static class TaskbarFlash
{
    private const uint FLASHW_STOP = 0;
    private const uint FLASHW_TRAY = 0x2;
    private const uint FLASHW_TIMERNOFG = 0xC;

    /// <summary>Light <paramref name="form"/>'s taskbar button until the window is in front. Any thread.</summary>
    public static void Start(Form? form) => Set(form, on: true, FlashWindowEx);

    /// <summary>Put <paramref name="form"/>'s taskbar button out. Any thread.</summary>
    public static void Stop(Form? form) => Set(form, on: false, FlashWindowEx);

    /// <summary>The call's arguments: the flags a start or a stop is asked with.</summary>
    internal static FlashInfo Describe(IntPtr window, bool on) => new()
    {
        Size = (uint)Marshal.SizeOf<FlashInfo>(),
        Window = window,
        Flags = on ? FLASHW_TRAY | FLASHW_TIMERNOFG : FLASHW_STOP,
        Count = 0,
        Timeout = 0,
    };

    /// <summary>As <see cref="Start"/> and <see cref="Stop"/>, with the shell call given: what a test records instead of lighting a button.</summary>
    internal static void Set(Form? form, bool on, Func<FlashInfo, bool> call)
    {
        if (form is null || form.IsDisposed || !form.IsHandleCreated) return;
        try
        {
            // BeginInvoke, never Invoke: the caller is often the thread that learned of it, which may be waiting on this one.
            form.BeginInvoke(() =>
            {
                if (form.IsDisposed || !form.IsHandleCreated) return;
                _ = call(Describe(form.Handle, on));
            });
        }
        catch (InvalidOperationException)
        {
            // The handle went between the look and the post: nothing to light.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    private static bool FlashWindowEx(FlashInfo info) => FlashWindowExNative(ref info);

    [DllImport("user32.dll", EntryPoint = "FlashWindowEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowExNative(ref FlashInfo info);
}
