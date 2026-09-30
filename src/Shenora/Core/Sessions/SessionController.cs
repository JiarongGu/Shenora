using Shenora.Core.Shell;

namespace Shenora.Core.Sessions;

/// <summary>
/// The primitives a session driver drives over the live browser; an off-screen co-browse host reuses the SAME
/// controller. Every browser call marshals to the shell's UI thread, so the driver can call them from any continuation
/// with plain await.
///
/// 🔴 It DRIVES; it does not report. What the browser does arrives on the app's
/// <see cref="Shenora.Core.Events.IEventBus"/> as <see cref="SessionEvents"/>, scoped by <see cref="Id"/>.
///
/// A FOREGROUND controller (a real interactive window) adds two window behaviours the off-screen co-browse host must NOT
/// have: the user's close is HELD so the driver gets a final cookie read (<see cref="WindowClosed"/> fires instead),
/// and <see cref="Reveal"/>/<see cref="FitToBox"/> manage the on-screen window. On a background host those are inert: a
/// hidden infrastructure window vetoing its own close would veto the app's exit, and its viewport is driven by the
/// DevTools protocol rather than by the window size.
/// </summary>
public sealed class SessionController
{
    private readonly IUiDispatcher _ui;   // the one marshal owner (D19/D20)
    private readonly ISessionBrowser _browser;
    private readonly ISessionWindow? _window;   // a real interactive window, or null for an off-screen co-browse host
    private readonly Func<Uri, CancellationToken, Task<bool>>? _navigationGuard;
    private readonly Action<bool>? _onLoading;
    private readonly CancellationTokenSource _closed = new();
    private bool _finishing;
    private bool _held;      // the one grace veto has been spent

    /// <summary>
    /// The soft cap on one navigation, matching <see cref="RenderSessionPoolOptions.NavigationTimeout"/>'s default.
    /// <para>
    /// 🔴 <b>Without it a navigate could wait forever.</b> The engine never reports a navigation completed if the
    /// renderer dies mid-load, so a <see cref="StreamingSession"/> whose page crashed reports the death through
    /// <c>OnEnded</c> and <c>Frames</c> while the in-flight <see cref="NavigateAsync"/> simply never returns.
    /// </para>
    /// <para>
    /// ⚠ SOFT, as the pool's is: the cap completes the wait rather than throwing, because "the load is taking a while" is
    /// not an error and the caller can look at the page. A caller who wants to give up passes a token, which still
    /// surfaces as cancellation.
    /// </para>
    /// </summary>
    private static readonly TimeSpan NavigationCap = TimeSpan.FromSeconds(30);

    internal SessionController(IUiDispatcher ui, ISessionBrowser browser, ISessionWindow? window,
        Func<Uri, CancellationToken, Task<bool>>? navigationGuard, Action<bool>? onLoading, string id)
    {
        Id = id;
        _ui = ui;
        _browser = browser;
        _window = window;
        _navigationGuard = navigationGuard;
        _onLoading = onLoading;

        if (_window is not null)
        {
            // A real interactive window: HOLD the user's close so the driver can do its final read.
            // A background co-browse host must NEVER do this: it would veto the app's exit.
            _window.Closing = byUser =>
            {
                if (!ShouldHoldClose(_finishing, byUser, _held)) return true;
                _held = true;           // hold the browser alive so the flow can capture cookies…
                if (!_closed.IsCancellationRequested) _closed.Cancel(); // …then wrap up via WindowClosed
                return false;
            };
        }
        // 🔴 POLICY ONLY. Observing what the browser does is SessionEvents' job; this class reports nothing.
        //
        // The browser's own download is CANCELLED: an interactive session hands the URL to the app, which fetches it
        // with its own progress and resume. The event still reaches subscribers as DOWNLOAD_STARTING.
        _browser.CancelDownloads = true;
        // ⚠ The new-window policy is NOT set here, and must not be: overruling the browser's own hook would silently
        // defeat an app that set OnWindowRequest to allow a popup, on exactly the session type a human is looking at.
        // One owner: the hook.
    }

    /// <summary>
    /// This session's identity: the SCOPE its browser publishes every <see cref="SessionEvents"/> under. Subscribe with
    /// it to hear only this session: <c>bus.SubscribeToModule(SessionEvents.Module, controller.Id, handler)</c>.
    /// </summary>
    public string Id { get; }

    /// <summary>Fires when the user closed the window (the close itself is held; see the class doc). A background
    /// co-browse host never holds a close, so this only fires for a foreground window.</summary>
    public CancellationToken WindowClosed => _closed.Token;

    /// <summary>Called by the host once the flow returns, so the real close is allowed.</summary>
    internal void Finish() => _finishing = true;

    /// <summary>
    /// Should this close be HELD, so the driver gets its final cookie read?
    /// <para>
    /// 🔴 <b>ONCE, and only for a close a PERSON asked for.</b> Vetoing the app's exit or the OS's shutdown lets a
    /// session window keep the whole app alive; vetoing EVERY attempt leaves a modal window the user cannot close by any
    /// means when a driver awaits something that never completes. So the grace is spent after one use: the first close
    /// asks the driver to wrap up, and a second means the user has said it twice.
    /// </para>
    /// <para>
    /// ⚠ On Windows a programmatic <c>form.Close()</c> also counts as the user's (<c>winforms-shell.md</c>), which is
    /// correct here rather than a hazard: the host's own close is already excluded by <paramref name="finishing"/>, so
    /// anything else closing this window is a caller the driver should get one chance to answer.
    /// </para>
    /// </summary>
    /// <param name="finishing">The flow returned and the host is closing the window itself.</param>
    /// <param name="byUser">A person (or code acting for one) is closing it, rather than the app or the OS ending.</param>
    /// <param name="alreadyHeld">A close has already been held once.</param>
    internal static bool ShouldHoldClose(bool finishing, bool byUser, bool alreadyHeld) =>
        !finishing && !alreadyHeld && byUser;

    /// <summary>
    /// Navigate: http(s) only, and through the options' navigation guard when set, since the URLs are data-driven and
    /// this browser both DISCLOSES the rendered page and accepts input, so an unguarded navigate at a loopback or LAN
    /// host is full interactive exposure. Completes when the navigation completes.
    /// </summary>
    public Task NavigateAsync(string url, CancellationToken cancellationToken = default) => OnUiAsync(async () =>
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("url must be an absolute http(s) URL", nameof(url));
        if (_navigationGuard is { } guard && !await guard(uri, cancellationToken).ConfigureAwait(true))
            throw new InvalidOperationException($"Navigation refused by the navigation guard: {uri.Host}");

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNav(SessionNavigationResult _) => done.TrySetResult();
        _browser.NavigationCompleted += OnNav;
        try
        {
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            overall.CancelAfter(NavigationCap);   // a dead renderer never completes a navigation
            _browser.Navigate(uri.ToString());
            // WhenAny never throws, and the two ways it completes MEAN different things: the cap is a soft "carry on and
            // look at the page", the caller's own token is "I gave up" and must surface so it cannot be mistaken for a
            // finished load.
            await Task.WhenAny(done.Task, Task.Delay(Timeout.Infinite, overall.Token)).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _browser.NavigationCompleted -= OnNav;   // on cancellation too, or it leaks until the next navigation
        }
        return true;
    });

    /// <summary>Run script on the live page; returns its value JSON-encoded.</summary>
    // WaitAsync: a driver cancelled mid-call must stop waiting even though the browser call runs on.
    public Task<string?> ExecuteScriptAsync(string javaScript, CancellationToken cancellationToken = default) =>
        OnUiAsync(() => _browser.ExecuteScriptAsync(javaScript)).WaitAsync(cancellationToken);

    /// <summary>
    /// The cookies visible from <paramref name="origin"/>. ⚠ The origin is a SEPARATE knob from the navigated URL:
    /// session cookies often live on a PARENT domain the host can't see, so read from the API origin the app will
    /// actually call.
    /// </summary>
    public Task<IReadOnlyList<SessionCookie>> GetCookiesAsync(string origin, CancellationToken cancellationToken = default) =>
        OnUiAsync(() => _browser.GetCookiesAsync(origin)).WaitAsync(cancellationToken);

    /// <summary>
    /// Bring a silent-refresh window on screen: interaction is needed after all. Idempotent, and INERT on a background
    /// co-browse host. Centres on the display, activates, and focuses the page so keyboard input goes there at once.
    /// </summary>
    public void Reveal()
    {
        if (_window is not { } window) return;
        PostUi(window.Reveal);
    }

    /// <summary>
    /// Size the window to the content box the driver measured IN THE PAGE (CSS px), within the display; sub-plausible
    /// sizes are ignored. INERT on a background co-browse host.
    /// </summary>
    public void FitToBox(int cssWidth, int cssHeight)
    {
        if (_window is not { } window || cssWidth < 100 || cssHeight < 100) return;
        PostUi(() => window.FitToContent(cssWidth, cssHeight));
    }

    /// <summary>Toggle the app's loading overlay (routes to <see cref="InteractiveSessionOptions.OnLoading"/>).</summary>
    public void SetLoading(bool loading) => PostUi(() => _onLoading?.Invoke(loading));

    /// <summary>Marshal a browser call to the shell's UI thread (a driver continuation may resume off it), through the
    /// ONE owner, which answers a thread that is not running with a faulted task.</summary>
    private Task<T> OnUiAsync<T>(Func<Task<T>> work) => _ui.InvokeAsync(work);

    private void PostUi(Action work) => _ui.Post(work);
}
