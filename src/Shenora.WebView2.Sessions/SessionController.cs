using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.WebView2.Sessions;

/// <summary>One captured browser cookie (see <see cref="SessionController.GetCookiesAsync"/>).</summary>
public sealed record SessionCookie(string Name, string Value, string Domain, string Path);

/// <summary>A page-initiated download the browser reported (and cancelled) — the app fetches it itself.</summary>
public sealed record DownloadHit(string Url, string? FileName);

/// <summary>
/// The primitives a session driver drives over the live window, ported from the server-backed
/// sibling (its co-browse reuses the SAME controller over an off-screen form — it doesn't care).
/// Every browser call marshals to the form's UI thread, so the driver can call them from any
/// continuation with plain await.
///
/// A FOREGROUND controller (a real interactive window) adds two window behaviours the off-screen
/// co-browse host must NOT have: the user's close is HELD (cancelled) so the driver gets a final
/// cookie read — <see cref="WindowClosed"/> fires instead — and <see cref="Reveal"/>/
/// <see cref="FitToBox"/> manage the on-screen window. On a background host those are inert: a
/// hidden infrastructure window vetoing its own close would veto <c>Application.Exit</c>, and
/// revealing/resizing an invisible screencast host is nonsense (its viewport is driven by CDP).
/// </summary>
public sealed class SessionController
{
    private readonly Form _form;
    private readonly Shenora.Core.IUiDispatcher _ui;   // the one marshal owner (D19/D20)
    private readonly WebView2Control _web;
    private readonly Func<Uri, CancellationToken, Task<bool>>? _navigationGuard;
    private readonly Action<bool>? _onLoading;
    private readonly bool _foreground; // a real interactive window (true) vs an off-screen co-browse host (false)
    // The DRIVER's taps — they accumulate so composed drivers don't silently drop each other's, and
    // the host just reports what the browser does (the driver decides which URL is "the download").
    //
    // COPY-ON-WRITE, not List<T> (P5.5 H2). These are registered from the driver's thread — a driver
    // continuation resumes wherever the thread pool puts it — while the WebView2 event handlers read
    // them ON THE UI THREAD. A plain List<T> being appended during a read is a genuine data race, not
    // a theoretical one: ToArray() reads _size and then Array.Copy's the backing store, so an Add that
    // grows the array in between throws or copies a torn view; two concurrent Adds corrupt the list
    // outright. Publishing a fresh array under a lock makes every reader see one immutable snapshot
    // with no lock on the hot path. Fields are volatile so a reader can't observe a stale array
    // reference after the swap.
    private readonly object _tapLock = new();
    private volatile Action<string>[] _messageHandlers = [];
    private volatile Action<DownloadHit>[] _downloadHandlers = [];
    private volatile Action<string>[] _newWindowHandlers = [];
    private volatile Action<string>[] _navigationHandlers = [];
    private readonly CancellationTokenSource _closed = new();
    private bool _finishing;
    private bool _revealed;

    internal SessionController(Form form, WebView2Control web,
        Func<Uri, CancellationToken, Task<bool>>? navigationGuard, Action<bool>? onLoading, bool foreground)
    {
        _form = form;
        _ui = new Shenora.WinForms.WinFormsUiDispatcher(form);
        _web = web;
        _navigationGuard = navigationGuard;
        _onLoading = onLoading;
        _foreground = foreground;
        // RevealImmediately windows start on screen. Derived from OffscreenWindow's own constant
        // (P5.5 H4.5) — this used to hard-code a DIFFERENT threshold (-30000) than the park
        // coordinate (-32000), so moving the park position would have silently broken reveal detection.
        _revealed = foreground && !OffscreenWindow.IsParked(form);

        _web.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            string? message = null;
            try { message = e.TryGetWebMessageAsString(); } catch { /* not a string message */ }
            if (message is null) return;
            Fan(_messageHandlers, message);
        };
        if (_foreground)
        {
            // A real interactive window: HOLD the user's close so the driver can do its final read.
            // A background co-browse host must NEVER do this — it would veto Application.Exit.
            _form.FormClosing += (_, e) =>
            {
                if (_finishing) return; // the host's own close (flow finished) — allow it
                e.Cancel = true;        // hold the WebView2 alive so the flow can capture cookies…
                if (!_closed.IsCancellationRequested) _closed.Cancel(); // …then wrap up via WindowClosed
            };
        }
        // The host REPORTS the browser's raw events; the driver decides what they mean.
        // DownloadStarting = an in-place download (the browser's own is cancelled — the app
        // fetches it with its own progress/resume). NewWindowRequested = a new-tab link
        // (popup suppressed, URL handed over — a download button often does this).
        _web.CoreWebView2.DownloadStarting += (_, e) =>
        {
            try
            {
                var uri = e.DownloadOperation.Uri;
                var name = System.IO.Path.GetFileName(e.ResultFilePath ?? "");
                e.Cancel = true;
                Fan(_downloadHandlers, new DownloadHit(uri, string.IsNullOrEmpty(name) ? null : name));
            }
            catch { /* reporting is best-effort */ }
        };
        _web.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true; // never open a popup — the driver decides if this URL matters
            Fan(_newWindowHandlers, e.Uri);
        };
        _web.CoreWebView2.NavigationStarting += (_, e) => Fan(_navigationHandlers, e.Uri);
    }

    /// <summary>Fires when the user closed the window (the close itself is held — see the class doc).
    /// A background co-browse host never holds a close, so this only fires for a foreground window.</summary>
    public CancellationToken WindowClosed => _closed.Token;

    /// <summary>Called by the host once the flow returns, so the real close is allowed.</summary>
    internal void Finish() => _finishing = true;

    /// <summary>Listen for messages the page posts via <c>chrome.webview.postMessage</c>.</summary>
    public void OnMessage(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_tapLock) _messageHandlers = [.. _messageHandlers, handler];
    }

    /// <summary>Report page-initiated downloads (already cancelled browser-side).</summary>
    public void OnDownload(Action<DownloadHit> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_tapLock) _downloadHandlers = [.. _downloadHandlers, handler];
    }

    /// <summary>Report suppressed new-window requests (the URL a download button often opens).</summary>
    public void OnNewWindow(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_tapLock) _newWindowHandlers = [.. _newWindowHandlers, handler];
    }

    /// <summary>Report top-level navigations.</summary>
    public void OnNavigation(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_tapLock) _navigationHandlers = [.. _navigationHandlers, handler];
    }

    /// <summary>
    /// Navigate the window — http(s) only, and through the options' navigation guard when set:
    /// the URLs are data-driven, and this window both DISCLOSES the rendered page and accepts
    /// input, so an unguarded navigate at a loopback/LAN host is full interactive exposure (the
    /// source's measured SSRF rationale). Completes when the navigation completes.
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
        using var registration = cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));
        try
        {
            _web.CoreWebView2.Navigate(uri.ToString());
            await done.Task.ConfigureAwait(true);
        }
        finally
        {
            _web.CoreWebView2.NavigationCompleted -= OnNav; // detach on cancellation too (else it leaks until the next nav)
        }
        return true;
    });

    /// <summary>Run JS on the live page; returns WebView2's JSON-encoded result.</summary>
    // WaitAsync: the source accepted these tokens but never observed them (its listed gap) — a
    // driver cancelled mid-call must stop waiting even though the browser call itself runs on.
    public Task<string> ExecuteScriptAsync(string javaScript, CancellationToken cancellationToken = default) =>
        OnUiAsync(() => _web.CoreWebView2.ExecuteScriptAsync(javaScript)).WaitAsync(cancellationToken);

    /// <summary>
    /// The cookies visible from <paramref name="origin"/>. NOTE the origin is a SEPARATE knob
    /// from the navigated URL on purpose: session cookies often live on a PARENT domain the
    /// host can't see (the primary sibling's original capture bug — verified against the
    /// profile's cookie DB) — read from the API origin the app will actually call.
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
    /// Bring a silent-refresh window on screen — interaction is needed after all. Idempotent, and
    /// INERT on a background co-browse host (it has no on-screen presence to reveal). Centers on
    /// the working area, activates, and focuses the WebView so keyboard/QR-scan input goes to the
    /// page immediately (the primary sibling's reveal mechanics).
    /// </summary>
    public void Reveal()
    {
        if (!_foreground) return;
        PostUi(() =>
        {
            if (_revealed || _form.IsDisposed) return;
            _revealed = true;
            _form.ShowInTaskbar = true;
            var area = Screen.FromControl(_form).WorkingArea;
            _form.Location = new Point(area.X + (area.Width - _form.Width) / 2, area.Y + (area.Height - _form.Height) / 2);
            _form.Activate();
            _form.BringToFront();
            _web.Focus();
        });
    }

    /// <summary>
    /// Shrink the window to the content box the driver measured IN THE PAGE (CSS px) — WebView2
    /// reports CSS px (DPI-independent), WinForms ClientSize is PHYSICAL px, so this converts by
    /// the window's own DeviceDpi and clamps to the working area. Sub-plausible sizes are
    /// ignored (wait for a full box, not a partial). INERT on a background co-browse host — its
    /// physical surface is fixed and its viewport is driven by CDP, not the window size.
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
        // CSS px → physical px. DpiHelper owns the conversion (P5.5 H4.5) — reachable since D19 — and
        // it guards a non-positive DPI.
        var scale = Shenora.WinForms.DpiHelper.ScaleFromDeviceDpi(deviceDpi);
        return new Size(
            Math.Min((int)Math.Round(cssWidth * scale), workArea.Width - 40),
            Math.Min((int)Math.Round(cssHeight * scale), workArea.Height - 60));
    }

    /// <summary>Toggle the app's loading overlay (routes to <see cref="InteractiveSessionOptions.OnLoading"/>).</summary>
    public void SetLoading(bool loading) => PostUi(() => _onLoading?.Invoke(loading));

    /// <summary>
    /// Deliver to every tap, isolating each one: these run inside WebView2 event handlers, so one
    /// driver's throw must neither reach the browser nor stop the OTHER taps from being told. No
    /// snapshot copy is needed — the array is already immutable once published (see the field docs).
    /// </summary>
    private static void Fan<T>(Action<T>[] handlers, T value)
    {
        foreach (var handler in handlers)
            Shenora.Core.AppCallback.Run(() => handler(value));
    }

    /// <summary>
    /// Marshal a WebView2 call to the form's UI thread (a driver continuation may resume off it),
    /// through the ONE owner (P5.5 H4.2).
    /// <para>
    /// This site is why the collapse mattered. The comment it used to carry said: "IsHandleCreated
    /// FIRST: pre-handle, InvokeRequired lies (returns false), so 'no handle' would be mistaken for
    /// 'already on the UI thread' and run the WebView2 call off-thread" — and the very next line was
    /// <c>if (!_form.IsHandleCreated || !_form.InvokeRequired) return work();</c>, which does exactly
    /// that. Reachable through the co-browse background controller, whose driver continuations resume
    /// on a pool thread. The dispatcher answers <c>NotReady</c> with a faulted task instead.
    /// </para>
    /// </summary>
    private Task<T> OnUiAsync<T>(Func<Task<T>> work) => _ui.InvokeAsync(work);

    private void PostUi(Action work) => _ui.Post(work);
}
