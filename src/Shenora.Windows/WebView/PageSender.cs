using Shenora.Core.Shell;

namespace Shenora.Windows;

/// <summary>
/// The control whose page sent the request being dispatched, so a module that acts on "this window" acts on the page's
/// own. The page transports set it around each dispatch: <see cref="WebViewIpcBridge"/>, and <see cref="ChromiumView"/>
/// through <see cref="PageUiDispatcher"/>. Null for a programmatic send.
/// <para>
/// It flows as an execution context does, so work a page's request starts (a task, a timer) is that page's too, as it
/// is for the request's own continuations. A <see cref="SecondaryWindows"/> thread is nobody's, whoever opened it. Held
/// weakly, so such work does not keep a closed page's controls alive.
/// </para>
/// </summary>
internal static class PageSender
{
    private static readonly AsyncLocal<WeakReference<Control>?> Sender = new();

    public static Control? Current => IsPage(out var page) ? page : null;

    /// <summary>Whether what runs is a page's. <paramref name="page"/> is null when that page's control has since been
    /// collected, which is not the same as a programmatic send's no page at all.</summary>
    public static bool IsPage(out Control? page)
    {
        page = null;
        if (Sender.Value is not { } sender) return false;
        page = sender.TryGetTarget(out var control) ? control : null;
        return true;
    }

    /// <summary>Mark what runs until disposal, and every continuation it starts, as <paramref name="page"/>'s; null as
    /// nobody's.</summary>
    public static Scope Enter(Control? page)
    {
        var previous = Sender.Value;
        Sender.Value = page is null ? null : new WeakReference<Control>(page);
        return new Scope(previous);
    }

    public readonly struct Scope(WeakReference<Control>? previous) : IDisposable
    {
        public void Dispose() => Sender.Value = previous;
    }
}

/// <summary>
/// A page's thread, over a control: what it runs there runs as that control's page (<see cref="PageSender"/>). A
/// Chromium page's IPC is dispatched in work posted here, so its requests carry the view that shows it.
/// </summary>
internal sealed class PageUiDispatcher(Control page) : IUiDispatcher
{
    private readonly WinFormsUiDispatcher _ui = new(page);

    public UiTargetState State => _ui.State;

    public bool IsOnUiThread => _ui.IsOnUiThread;

    public bool Post(Action work) => _ui.Post(() =>
    {
        using (PageSender.Enter(page)) work();
    });

    public bool Post(Func<Task> work) => _ui.Post(() =>
    {
        using (PageSender.Enter(page)) return work();
    });

    public Task InvokeAsync(Action work, CancellationToken cancellationToken = default) => _ui.InvokeAsync(() =>
    {
        using (PageSender.Enter(page)) work();
    }, cancellationToken);

    public Task InvokeAsync(Func<Task> work, CancellationToken cancellationToken = default) => _ui.InvokeAsync(() =>
    {
        using (PageSender.Enter(page)) return work();
    }, cancellationToken);

    public Task<T> InvokeAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default) => _ui.InvokeAsync(() =>
    {
        using (PageSender.Enter(page)) return work();
    }, cancellationToken);

    public Task<T> InvokeOrDefaultAsync<T>(Func<Task<T>> work, T fallback, CancellationToken cancellationToken = default) =>
        _ui.InvokeOrDefaultAsync(() =>
        {
            using (PageSender.Enter(page)) return work();
        }, fallback, cancellationToken);
}
