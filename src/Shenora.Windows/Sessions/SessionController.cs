using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;
using Shenora.Core.Shell;

namespace Shenora.Windows;

/// <summary>One captured browser cookie (see <see cref="SessionController.GetCookiesAsync"/>).</summary>
public sealed record SessionCookie(string Name, string Value, string Domain, string Path);

/// <summary>
/// A page-initiated download, reported as <see cref="SessionEvents.DownloadStarting"/>.
/// ⚠ Whether the browser's own download is CANCELLED is the session type's policy, not this record's:
/// a session with a <see cref="SessionController"/> cancels it so the app can fetch the URL with its own
/// progress and resume, while a pooled render session leaves it alone.
/// </summary>
public sealed record DownloadHit(string Url, string? FileName);

/// <summary>
/// The primitives a session driver drives over the live window; an off-screen co-browse host reuses the
/// SAME controller. Every browser call marshals to the form's UI thread, so the driver can call them
/// from any continuation with plain await.
///
/// 🔴 It DRIVES; it does not report. What the browser does arrives on the app's
/// <see cref="Shenora.Core.Events.IEventBus"/> as <see cref="SessionEvents"/>, scoped by
/// <see cref="Id"/>.
///
/// A FOREGROUND controller (a real interactive window) adds two window behaviours the off-screen
/// co-browse host must NOT have: the user's close is HELD (cancelled) so the driver gets a final
/// cookie read — <see cref="WindowClosed"/> fires instead — and <see cref="Reveal"/>/
/// <see cref="FitToBox"/> manage the on-screen window. On a background host those are inert: a
/// hidden infrastructure window vetoing its own close would veto <c>Application.Exit</c>, and its
/// viewport is driven by CDP rather than by the window size.
/// </summary>
public sealed class SessionController
{
    private readonly Form _form;
    private readonly Shenora.Core.Shell.IUiDispatcher _ui;   // the one marshal owner (D19/D20)
    private readonly WebView2Control _web;
    private readonly Func<Uri, CancellationToken, Task<bool>>? _navigationGuard;
    private readonly Action<bool>? _onLoading;
    private readonly bool _foreground; // a real interactive window (true) vs an off-screen co-browse host (false)
    private readonly CancellationTokenSource _closed = new();
    private bool _finishing;
    private bool _held;      // the one grace veto has been spent
    private bool _revealed;

    /// <summary>
    /// The soft cap on one navigation, matching <c>RenderSessionPoolOptions.NavigationTimeout</c>'s
    /// default.
    /// <para>
    /// 🔴 <b>Without it a navigate could wait forever.</b> `NavigationCompleted` never fires if the
    /// renderer dies mid-load, so a `StreamingSession` whose page crashed reports the death through
    /// `OnEnded` and `Frames` while the in-flight `NavigateAsync` simply never returns — `DisposeAsync`
    /// does not complete it either.
    /// </para>
    /// <para>
    /// ⚠ SOFT, as the sibling's is: the cap completes the wait rather than throwing, because "the load
    /// is taking a while" is not an error and the caller can look at the page. A caller who wants to
    /// give up passes a token, which still surfaces as cancellation.
    /// </para>
    /// </summary>
    private static readonly TimeSpan NavigationCap = TimeSpan.FromSeconds(30);

    internal SessionController(Form form, WebView2Control web,
        Func<Uri, CancellationToken, Task<bool>>? navigationGuard, Action<bool>? onLoading, bool foreground,
        string id)
    {
        Id = id;
        _form = form;
        _ui = new Shenora.Windows.WinFormsUiDispatcher(form);
        _web = web;
        _navigationGuard = navigationGuard;
        _onLoading = onLoading;
        _foreground = foreground;
        // RevealImmediately windows start on screen. Derived from OffscreenWindow's own constant, so
        // moving the park position cannot silently break reveal detection.
        _revealed = foreground && !OffscreenWindow.IsParked(form);

        if (_foreground)
        {
            // A real interactive window: HOLD the user's close so the driver can do its final read.
            // A background co-browse host must NEVER do this — it would veto Application.Exit.
            _form.FormClosing += (_, e) =>
            {
                if (!ShouldHoldClose(_finishing, e.CloseReason, _held)) return;
                _held = true;
                e.Cancel = true;        // hold the WebView2 alive so the flow can capture cookies…
                if (!_closed.IsCancellationRequested) _closed.Cancel(); // …then wrap up via WindowClosed
            };
        }
        // 🔴 POLICY ONLY. Observing what the browser does is SessionEvents' job; this class reports
        // nothing.
        //
        // The browser's own download is CANCELLED: an interactive session hands the URL to the app,
        // which fetches it with its own progress and resume. The event still reaches subscribers as
        // DOWNLOAD_STARTING, published by the handler SessionBrowser wired first.
        _web.CoreWebView2.DownloadStarting += (_, e) =>
        {
            try { e.Cancel = true; }
            catch { /* the operation may already be gone */ }
        };
        // ⚠ NewWindowRequested is NOT wired here, and must not be: setting `e.Handled = true` on top of
        // SessionBrowser's own popup policy silently overrules an app that set `OnWindowRequest` to
        // allow a popup — on exactly the session type a human is looking at. One owner: the hook.
    }

    /// <summary>
    /// This session's identity — the SCOPE its browser publishes every <see cref="SessionEvents"/>
    /// under. Subscribe with it to hear only this session:
    /// <c>bus.SubscribeToModule(SessionEvents.Module, controller.Id, handler)</c>.
    /// </summary>
    public string Id { get; }

    /// <summary>Fires when the user closed the window (the close itself is held — see the class doc).
    /// A background co-browse host never holds a close, so this only fires for a foreground window.</summary>
    public CancellationToken WindowClosed => _closed.Token;

    /// <summary>Called by the host once the flow returns, so the real close is allowed.</summary>
    internal void Finish() => _finishing = true;

    /// <summary>
    /// Should this close be HELD, so the driver gets its final cookie read?
    /// <para>
    /// 🔴 <b>ONCE, and only for a close the USER asked for.</b> Vetoing
    /// <see cref="CloseReason.ApplicationExitCall"/> or <see cref="CloseReason.WindowsShutDown"/> lets a
    /// session window refuse <c>Application.Exit</c> and keep the whole app alive; vetoing EVERY attempt
    /// leaves a modal window the user cannot close by any means when a driver awaits something that
    /// never completes. So the grace is spent after one use: the first close asks the driver to wrap up,
    /// and a second means the user has said it twice.
    /// </para>
    /// <para>
    /// ⚠ <see cref="CloseReason.UserClosing"/> ALSO covers a programmatic <c>form.Close()</c>
    /// (<c>winforms-shell.md</c>) — correct here rather than a hazard: the host's own close is already
    /// excluded by <paramref name="finishing"/>, so anything else closing this window is a caller the
    /// driver should get one chance to answer.
    /// </para>
    /// <para>Internal + static so the rule is testable without a live browser.</para>
    /// </summary>
    /// <param name="finishing">The flow returned and the host is closing the window itself.</param>
    /// <param name="reason">Why the form is closing.</param>
    /// <param name="alreadyHeld">A close has already been held once.</param>
    internal static bool ShouldHoldClose(bool finishing, CloseReason reason, bool alreadyHeld) =>
        !finishing && !alreadyHeld && reason is CloseReason.UserClosing;

    /// <summary>
    /// Navigate the window — http(s) only, and through the options' navigation guard when set: the URLs
    /// are data-driven, and this window both DISCLOSES the rendered page and accepts input, so an
    /// unguarded navigate at a loopback/LAN host is full interactive exposure. Completes when the
    /// navigation completes.
    /// </summary>
    public Task NavigateAsync(string url, CancellationToken cancellationToken = default) => OnUiAsync(async () =>
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("url must be an absolute http(s) URL", nameof(url));
        if (_navigationGuard is { } guard && !await guard(uri, cancellationToken).ConfigureAwait(true))
            throw new InvalidOperationException($"Navigation refused by the navigation guard: {uri.Host}");

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            _web.CoreWebView2.NavigationCompleted -= OnNav;
            done.TrySetResult();
        }
        _web.CoreWebView2.NavigationCompleted += OnNav;
        try
        {
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            overall.CancelAfter(NavigationCap);   // a dead renderer never raises NavigationCompleted
            _web.CoreWebView2.Navigate(uri.ToString());
            // WhenAny never throws, and the two ways it completes MEAN different things: the cap is a
            // soft "carry on and look at the page", the caller's own token is "I gave up" and must
            // surface so it cannot be mistaken for a finished load.
            await Task.WhenAny(done.Task, Task.Delay(Timeout.Infinite, overall.Token)).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _web.CoreWebView2.NavigationCompleted -= OnNav; // detach on cancellation too (else it leaks until the next nav)
        }
        return true;
    });

    /// <summary>Run JS on the live page; returns WebView2's JSON-encoded result.</summary>
    // WaitAsync: a driver cancelled mid-call must stop waiting even though the browser call runs on.
    public Task<string> ExecuteScriptAsync(string javaScript, CancellationToken cancellationToken = default) =>
        OnUiAsync(() => _web.CoreWebView2.ExecuteScriptAsync(javaScript)).WaitAsync(cancellationToken);

    /// <summary>
    /// The cookies visible from <paramref name="origin"/>. ⚠ The origin is a SEPARATE knob from the
    /// navigated URL: session cookies often live on a PARENT domain the host can't see, so read from
    /// the API origin the app will actually call.
    /// </summary>
    public Task<IReadOnlyList<SessionCookie>> GetCookiesAsync(string origin, CancellationToken cancellationToken = default) =>
        OnUiAsync(async () =>
        {
            var list = new List<SessionCookie>();
            foreach (var cookie in await _web.CoreWebView2.CookieManager.GetCookiesAsync(origin).ConfigureAwait(true))
                list.Add(new SessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path));
            return (IReadOnlyList<SessionCookie>)list;
        }).WaitAsync(cancellationToken);

    /// <summary>
    /// Bring a silent-refresh window on screen — interaction is needed after all. Idempotent, and INERT
    /// on a background co-browse host. Centers on the working area, activates, and focuses the WebView
    /// so keyboard input goes to the page immediately.
    /// </summary>
    public void Reveal()
    {
        if (!_foreground) return;
        PostUi(() =>
        {
            if (_revealed || _form.IsDisposed) return;
            _revealed = true;
            // NOT `_form.ShowInTaskbar = true` — that setter RECREATES the window handle, under a live
            // WebView2, at the one moment this window matters. See WindowActivation.ShowTaskbarButton.
            WindowActivation.ShowTaskbarButton(_form);
            var area = Screen.FromControl(_form).WorkingArea;
            _form.Location = new Point(area.X + (area.Width - _form.Width) / 2, area.Y + (area.Height - _form.Height) / 2);
            _form.Activate();
            _form.BringToFront();
            _web.Focus();
        });
    }

    /// <summary>
    /// Shrink the window to the content box the driver measured IN THE PAGE (CSS px). WebView2 reports
    /// CSS px (DPI-independent) and WinForms ClientSize is PHYSICAL px, so this converts by the window's
    /// own DeviceDpi and clamps to the working area; sub-plausible sizes are ignored. INERT on a
    /// background co-browse host.
    /// </summary>
    public void FitToBox(int cssWidth, int cssHeight)
    {
        if (!_foreground || cssWidth < 100 || cssHeight < 100) return;
        PostUi(() =>
        {
            if (_form.IsDisposed) return;
            var area = Screen.FromControl(_form).WorkingArea;
            _form.ClientSize = ComputeFitSize(cssWidth, cssHeight, _form.DeviceDpi, area.Size);
            if (_revealed)
                _form.Location = new Point(area.X + (area.Width - _form.Width) / 2, area.Y + (area.Height - _form.Height) / 2);
        });
    }

    /// <summary>CSS px → physical px by the window's own DPI, clamped into the working area
    /// (margins leave room for the window chrome).</summary>
    internal static Size ComputeFitSize(int cssWidth, int cssHeight, int deviceDpi, Size workArea)
    {
        // DpiHelper owns the CSS-px → physical-px conversion, and guards a non-positive DPI.
        var scale = Shenora.Windows.DpiHelper.ScaleFromDeviceDpi(deviceDpi);
        return new Size(
            Math.Min((int)Math.Round(cssWidth * scale), workArea.Width - 40),
            Math.Min((int)Math.Round(cssHeight * scale), workArea.Height - 60));
    }

    /// <summary>Toggle the app's loading overlay (routes to <see cref="InteractiveSessionOptions.OnLoading"/>).</summary>
    public void SetLoading(bool loading) => PostUi(() => _onLoading?.Invoke(loading));

    /// <summary>
    /// Marshal a WebView2 call to the form's UI thread (a driver continuation may resume off it),
    /// through the ONE owner. ⚠ Pre-handle, <c>InvokeRequired</c> LIES — it returns false — so a
    /// hand-rolled <c>IsHandleCreated || !InvokeRequired</c> check mistakes "no handle" for "already on
    /// the UI thread" and runs the WebView2 call off-thread. The dispatcher answers <c>NotReady</c>
    /// with a faulted task instead.
    /// </summary>
    private Task<T> OnUiAsync<T>(Func<Task<T>> work) => _ui.InvokeAsync(work);

    private void PostUi(Action work) => _ui.Post(work);
}
