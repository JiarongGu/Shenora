using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Windows;

/// <summary>Inputs for <see cref="RenderSessionPool"/>.</summary>
public sealed class RenderSessionPoolOptions
{
    /// <summary>
    /// A live UI-thread control (typically the main window) every WebView2 op marshals onto —
    /// the family's UI-thread anchor pattern; WebView2 needs the app's message pump.
    /// </summary>
    public required Control Anchor { get; init; }

    /// <summary>Browser configuration for the pool's instances (they share one profile).
    /// Set <see cref="SessionBrowserOptions.KeepAliveInBackground"/> — the instances render
    /// off-screen and their JS must keep running.</summary>
    public required SessionBrowserOptions Browser { get; init; }
    /// <summary>
    /// Diagnostics. Null = silent. The sessions package shipped with NO logging of any kind against
    /// ~30 swallowed catches, so a wedged pool was undiagnosable in production (P5.5 H4.7). Note the
    /// browser-level events (init failure, suppressed popups, denied permissions, a dead renderer)
    /// report through <see cref="SessionBrowserOptions.Log"/> on <see cref="Browser"/>.
    /// </summary>
    public Microsoft.Extensions.Logging.ILogger? Log { get; init; }


    /// <summary>
    /// Max concurrent leased sessions (the family default: 3). Leases past the cap WAIT until
    /// one is returned — a queue, not a failure.
    /// </summary>
    public int Capacity { get; init; } = 3;

    /// <summary>
    /// Dev/test mode: a VISIBLE window per session (cascaded, watchable) instead of the runtime
    /// mode's ONE shared hidden off-screen host. Either way a session drives only its WebView2 —
    /// it never cares which window hosts it.
    /// </summary>
    public bool VisiblePerSession { get; init; }

    /// <summary>
    /// Consulted before every EXPLICIT session navigation (return false to refuse). Wire your
    /// SSRF/allowlist policy here — sessions navigate data-driven URLs, and a server-reachable
    /// loopback/LAN/metadata host behind an unguarded navigate is a request-forgery hole (the
    /// source app guards every browser the same way). Null = any http(s) URL.
    /// <para>
    /// SCOPE, precisely (it was over-promised before the P5.5 review): this runs on the explicit
    /// <c>NavigateAsync</c> call. It CANNOT be consulted for redirects or in-page navigation, because
    /// WebView2's <c>NavigationStarting</c> event has no deferral and an async policy cannot be
    /// awaited inside it. What setting this DOES additionally buy you: the pool then cancels any
    /// unvetted CROSS-HOST navigation, so a guard-approved URL answering
    /// <c>302 → http://127.0.0.1:8080/admin</c> is not followed. Same-host hops stay allowed.
    /// </para>
    /// <para>
    /// For a full policy over redirect targets AND subresources, also set
    /// <see cref="SessionBrowserOptions.RequestFilter"/> on <see cref="Browser"/> — it is synchronous
    /// by design and sees every request. Guard = pre-check; request filter = enforcement.
    /// </para>
    /// </summary>
    public Func<Uri, CancellationToken, Task<bool>>? NavigationGuard { get; init; }

    /// <summary>Client size of the off-screen host (a desktop-sized viewport — some sites gate
    /// on window size). Dev windows use a smaller cascade size.</summary>
    public Size OffscreenClientSize { get; init; } = new(1280, 1600);

    /// <summary>
    /// Hard cap on ONE leased-session operation — the UI-thread marshal of a navigate, a script, an
    /// HTML read or a CDP call (default 60 s). When it expires the caller gets a
    /// <see cref="TimeoutException"/> and the instance is marked unusable, so returning the lease
    /// DISCARDS it instead of re-pooling a wedged page.
    /// <para>
    /// WHY IT IS NOT OPTIONAL (P5.5 H2): a page whose JS thread is blocked (a spin loop; before
    /// script dialogs were disabled, an <c>alert()</c>) makes <c>ExecuteScriptAsync</c>/
    /// <c>GetHtmlAsync</c> never complete. Escaping the await alone is not enough — the pool would
    /// re-pool the same dead page and every later lease would inherit it.
    /// </para>
    /// <para>
    /// Keep this comfortably ABOVE <see cref="NavigationTimeout"/>: a navigate's own soft cap is part
    /// of the operation, so a lower value here would report a legitimately slow load as a wedge.
    /// </para>
    /// </summary>
    public TimeSpan OpTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long <c>NavigateAsync</c> waits for the document to load before returning what is there
    /// (default 30 s). A SOFT cap by design — the caller decides what "settled" means via its own
    /// script polling, so a slow page is not an error; it just stops holding the lease open.
    /// </summary>
    public TimeSpan NavigationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a returned instance's reset-to-<c>about:blank</c> may take before the instance is
    /// treated as unusable and DISCARDED rather than re-pooled (default 5 s). A blank navigation that
    /// does not complete means the renderer is not answering.
    /// </summary>
    public TimeSpan ResetTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// A BOUNDED, mode-aware pool + queue of driveable off-screen WebView2 sessions, ported from the
/// server-backed sibling. The runtime mode hosts EVERY session's WebView2 on ONE shared hidden
/// off-screen form; the dev/test mode (<see cref="RenderSessionPoolOptions.VisiblePerSession"/>)
/// gives each session its own visible window to watch. Sessions are LEASED — the caller owns
/// navigation, its own JS, and the page's API/message events for the life of a lease — and
/// several run in PARALLEL up to the cap. A lease returns a free instance (LIFO keeps a warm one
/// hot), creates one lazily under the cap, or WAITS on the capacity queue; returning a session
/// resets it to <c>about:blank</c> and re-pools it — a failed reset DISCARDS the instance rather
/// than re-pooling a poisoned one. Dispose with the owning window.
/// </summary>
public sealed class RenderSessionPool : IDisposable
{
    private readonly RenderSessionPoolOptions _options;
    private readonly SemaphoreSlim _capacity; // gates leases to Capacity concurrent sessions — the queue
    private readonly CancellationTokenSource _disposeCts = new(); // cancels queued leases when the pool disposes
    private readonly object _lock = new();
    private readonly Stack<PoolInstance> _free = new(); // idle instances ready to re-lease (LIFO keeps a warm one hot)

    // ONE environment for the pool's single profile, instead of one per instance. Owner-scoped on
    // purpose — see SessionEnvironmentCache for why a static, profile-keyed cache would break
    // InteractiveSession.ClearProfile.
    private readonly SessionEnvironmentCache _environment = new();
    private int _created;                                // total instances realized (≤ cap; grows, shrinks on discard)
    private Form? _sharedHost;                           // the ONE hidden form runtime-mode webviews share (lazy)
    private bool _disposed;

    // Test seams: the real factory/reset need live browser processes; pool ACCOUNTING (capacity,
    // LIFO, discard, failure-releases-slot) is proven with fakes through these.
    internal Func<CancellationToken, Task<PoolInstance>>? InstanceFactoryOverride;
    internal Func<PoolInstance, Task<bool>>? ResetOverride;

    /// <summary>A pool bounded by <paramref name="options"/>. Nothing starts until the first lease.</summary>
    public RenderSessionPool(RenderSessionPoolOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.Capacity < 1) throw new ArgumentOutOfRangeException(nameof(options), "Capacity must be at least 1.");
        // Validate at CONSTRUCTION, the package convention: a non-positive budget would otherwise
        // surface much later as an instantly-cancelled operation or an instantly-discarded instance,
        // with nothing pointing at the option that caused it.
        RequireUsableTimeout(options.OpTimeout, nameof(RenderSessionPoolOptions.OpTimeout));
        RequireUsableTimeout(options.NavigationTimeout, nameof(RenderSessionPoolOptions.NavigationTimeout));
        RequireUsableTimeout(options.ResetTimeout, nameof(RenderSessionPoolOptions.ResetTimeout));
        // A zero/negative client size gives a 0×0 viewport (P5.5 H3): the page "loads", every element
        // has zero size, and any site that gates on window size behaves as if on a phantom display —
        // with nothing anywhere to suggest the viewport is the problem.
        if (options.OffscreenClientSize.Width < 1 || options.OffscreenClientSize.Height < 1)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(RenderSessionPoolOptions.OffscreenClientSize)} must be positive in both dimensions.");
        _capacity = new SemaphoreSlim(options.Capacity, options.Capacity);

        // The upper bound is not pedantry: these feed CancellationTokenSource.CancelAfter and
        // Task.WaitAsync, both of which THROW above int.MaxValue milliseconds (~24.8 days). Someone
        // reaching for TimeSpan.MaxValue to mean "no timeout" would otherwise get an
        // ArgumentOutOfRangeException from the middle of an operation instead of from here.
        static void RequireUsableTimeout(TimeSpan value, string name)
        {
            if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(options),
                    $"{name} must be positive and no more than {TimeSpan.FromMilliseconds(int.MaxValue).TotalDays:0.#} days.");
        }
    }

    /// <summary>One pooled WebView2 + the window hosting it. The pool alone creates/resets/discards it.</summary>
    internal sealed record PoolInstance(Form Host, WebView2Control Web)
    {
        /// <summary>
        /// The host the caller's async <see cref="RenderSessionPoolOptions.NavigationGuard"/> last
        /// approved, or null before any explicit navigate. Read by the navigation policy wired in
        /// <see cref="WireNavigationPolicy"/> to reject an UNVETTED cross-host hop. Cleared on
        /// return-to-pool so a recycled instance can't inherit the previous lease's approval.
        /// </summary>
        internal string? ApprovedHost { get; set; }

        /// <summary>
        /// Set when this instance's RENDER PROCESS died (P5.5 H4.4). Its browser object survives the
        /// crash, so nothing else marks it unusable: without this the instance was reset, re-pooled
        /// and re-leased forever, and every later lease burned the full navigation cap against a dead
        /// renderer. <see cref="Return"/> discards a poisoned instance instead of re-pooling it.
        /// </summary>
        internal bool Poisoned { get; set; }
    }

    /// <summary>
    /// Lease a session (never null). Returns a free instance; else creates one under the cap;
    /// else waits for one to be returned. Cancels cleanly while waiting; a creation failure
    /// releases the capacity slot so the pool never leaks a permit.
    /// </summary>
    public async Task<RenderSession> LeaseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Link the dispose token so a queued lease is CANCELLED (not left hanging forever) when
        // the pool disposes — a wedged wire request would otherwise never settle. WaitAsync is
        // outside the try: if it throws (cancelled/disposed) no permit was taken, so nothing to
        // release.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        await _capacity.WaitAsync(linked.Token).ConfigureAwait(false); // held for the whole lease
        try
        {
            PoolInstance? instance;
            lock (_lock) instance = _free.Count > 0 ? _free.Pop() : null;
            if (instance is null)
            {
                // The LINKED token, not the caller's: creation takes SECONDS (browser-process spawn +
                // profile attach), and disposing the pool mid-creation used to let that creation run to
                // completion and publish a live off-screen window whose browser process then held the
                // profile lock with nothing left to dispose it (P5.5 H2).
                instance = await (InstanceFactoryOverride ?? CreateInstanceAsync)(linked.Token).ConfigureAwait(false);
                lock (_lock) _created++; // accounted HERE (not in the factory) so the test seam counts too
            }
            return new RenderSession(this, instance, _options);
        }
        catch
        {
            // Acquired a permit but failed to hand out an instance → give the slot straight back
            // (guarded: Dispose may have torn the semaphore down concurrently).
            try { _capacity.Release(); } catch { }
            throw;
        }
    }

    /// <summary>Realize a new instance ON THE UI THREAD: an off-screen (or visible dev) host + a
    /// WebView2 initialized through <see cref="SessionBrowser"/>.</summary>
    private Task<PoolInstance> CreateInstanceAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<PoolInstance>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _options.Anchor.BeginInvoke(new Action(async () =>
            {
                Form? host = null;
                var freshHost = false; // did WE create the host this call (vs reuse the shared one)?
                WebView2Control? web = null;
                try
                {
                    if (cancellationToken.IsCancellationRequested) { tcs.TrySetCanceled(cancellationToken); return; }
                    if (_options.VisiblePerSession)
                    {
                        // Dev/test: a visible window per session, cascaded so several are watchable.
                        var n = _created;
                        host = new Form
                        {
                            Text = $"Render session {n + 1}",
                            StartPosition = FormStartPosition.Manual,
                            Location = new Point(40 + n * 40, 40 + n * 30),
                            ClientSize = new Size(760, 940),
                        };
                        freshHost = true;
                        host.Show();
                    }
                    else
                    {
                        // Runtime: every session's WebView2 rides the ONE shared hidden form
                        // (webviews overlap off-screen — harmless, each renders independently).
                        freshHost = _sharedHost is null;
                        host = _sharedHost ??= OffscreenWindow.Create("Render sessions", _options.OffscreenClientSize);
                    }

                    web = new WebView2Control { Dock = DockStyle.Fill };
                    host.Controls.Add(web);

                    // The instance exists before init so the crash callback can mark it — a renderer
                    // can die at any time, including during the very first navigation.
                    var instance = new PoolInstance(host, web);
                    // The linked token (caller + pool dispose) now reaches INIT itself (P5.5 H9.6):
                    // a cancelled lease used to wait out the full InitTimeout before the re-check below
                    // could fire. It gates the await only — the environment task is shared across this
                    // pool's instances, so cancelling creation for one caller would break the others.
                    await SessionBrowser.InitializeAsync(web, _options.Browser,
                        onProcessFailed: _ => instance.Poisoned = true, environmentCache: _environment,
                        cancellationToken: cancellationToken)
                        .ConfigureAwait(true);

                    // Re-check AFTER the multi-second init (P5.5 H2). The pre-check above was the only
                    // one, so a lease cancelled — or a pool disposed — during those seconds still
                    // published a fully live instance: an off-screen window nobody owns and a browser
                    // process holding the profile lock, because the cancelled caller never got a
                    // session to dispose.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        TearDown();
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }

                    WireNavigationPolicy(instance);
                    tcs.TrySetResult(instance);
                }
                catch (Exception ex)
                {
                    TearDown();
                    tcs.TrySetException(ex);
                }

                // Undo everything this call realized. Shared by the failure and the
                // cancelled-after-init paths: a failed or abandoned init must not leak the control
                // (runtime mode) or the window (dev mode) — otherwise every retry against a locked
                // profile orphans one, and an abandoned control can still finish attaching a live
                // browser process that holds the very lock the timeout is diagnosing.
                void TearDown()
                {
                    try
                    {
                        if (web is not null) { host?.Controls.Remove(web); web.Dispose(); }
                        if (freshHost && host is not null)
                        {
                            host.Dispose();
                            if (host == _sharedHost) _sharedHost = null; // let the next lease recreate it
                        }
                    }
                    catch (Exception cleanupError)
                    {
                        // Best-effort, but not silent: a leaked control keeps the profile locked, which
                        // is the symptom the init timeout's message tries to explain (P5.5 H4.7).
                        // Through SessionLog because an app logger that throws HERE would escape before
                        // TrySetException below and hang the lease forever — see SessionLog's docs.
                        SessionLog.Try(_options.Log, l =>
                            l.LogWarning(cleanupError, "Tearing down a failed session instance failed."));
                    }
                }
            }));
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        return tcs.Task;
    }

    /// <summary>
    /// Cancel an UNVETTED cross-host navigation for the instance's whole life — wired once here, on
    /// the UI thread, right after init, and only when the app configured a
    /// <see cref="RenderSessionPoolOptions.NavigationGuard"/>.
    /// <para>
    /// WHAT THIS CLOSES: the guard is documented as the app's SSRF/allowlist policy but was consulted
    /// ONLY inside the explicit <c>NavigateAsync</c> call, so a guard-approved URL that answered
    /// <c>302 → http://127.0.0.1:8080/admin</c> was followed anyway and its DOM handed back to the
    /// caller (found in the P0–P5 review). The caller vetted host X; nothing vetted host Y.
    /// </para>
    /// <para>
    /// WHY IT IS A HOST COMPARISON AND NOT THE GUARD ITSELF: <c>NavigationStarting</c> has NO
    /// deferral in the WebView2 SDK (verified — <c>CoreWebView2NavigationStartingEventArgs</c>
    /// exposes none), so an <c>async</c> policy simply cannot be awaited there; blocking on it would
    /// deadlock the UI thread it runs on. A synchronous, guard-independent rule is therefore the most
    /// that this event can enforce. Same-host hops (<c>http → https</c>, <c>/</c> → <c>/index.html</c>,
    /// in-page navigation) stay allowed; an unvetted cross-host hop is cancelled.
    /// </para>
    /// <para>
    /// FOR A FULL REDIRECT/SUBRESOURCE POLICY, use
    /// <see cref="SessionBrowserOptions.RequestFilter"/>: it is SYNCHRONOUS by design and is wired at
    /// the request layer with <c>WebResourceContext.All</c>, so it sees every request including
    /// redirect targets and subresources. The async guard is a pre-check; the request filter is the
    /// enforcement seam. Documented on both options.
    /// </para>
    /// <para>
    /// NOT applied to <c>InteractiveSession</c> deliberately: a human-in-the-loop flow legitimately redirects
    /// across hosts (OAuth), so cancelling unvetted hops there would break real sign-in flows. A
    /// window is human-driven, not a data-driven SSRF surface.
    /// </para>
    /// </summary>
    private void WireNavigationPolicy(PoolInstance instance)
    {
        if (_options.NavigationGuard is null) return;

        instance.Web.CoreWebView2.NavigationStarting += (_, e) =>
        {
            // Sync handler, but it runs inside a WebView2 event: an escaping exception is an
            // unhandled UI-thread crash, so everything is guarded and failure means CANCEL.
            try
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
                if (uri.Scheme is not ("http" or "https")) return; // the about:blank reset, data:, …
                if (instance.ApprovedHost is not { } approved) return; // nothing vetted yet
                if (string.Equals(uri.Host, approved, StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
            }
            catch (Exception)
            {
                e.Cancel = true;
            }
        };
    }

    /// <summary>
    /// Return an instance: reset it to <c>about:blank</c> ON THE UI THREAD (clears the previous
    /// page's DOM/JS so the next lease starts on a blank document), re-pool it, and release the
    /// capacity slot. NOTE this clears the DOM/JS only — the profile (cookies/localStorage/
    /// IndexedDB) is SHARED across every lease by design (one profile per pool), so this is NOT
    /// trust-domain isolation; use separate pools for separate trust domains. A reset failure
    /// DISCARDS the instance (a poisoned one must not re-pool) — the slot is still released so
    /// the pool creates a fresh one. If the pool has since disposed, the instance is discarded
    /// rather than pushed into a stack nobody will drain. Best-effort throughout: a return must
    /// never throw back into a caller's dispose.
    /// </summary>
    internal void Return(PoolInstance instance)
    {
        try
        {
            _options.Anchor.BeginInvoke(new Action(async () =>
            {
                bool ok;
                try
                {
                    // A crashed renderer can never be reset back to a usable state, so don't try —
                    // discard it straight away (P5.5 H4.4).
                    ok = !instance.Poisoned
                         && await (ResetOverride ?? ResetToBlankAsync)(instance).ConfigureAwait(true);
                }
                catch
                {
                    ok = false; // instance is wedged — drop it below
                }

                bool repooled = false;
                lock (_lock)
                {
                    // Don't re-pool into a disposed pool — Dispose already drained _free, so a
                    // push here would leak the instance (and its browser process holding the
                    // profile lock) forever.
                    if (ok && !_disposed) { _free.Push(instance); repooled = true; }
                }
                if (!repooled)
                {
                    if (!ok)
                    {
                        // Name WHICH invariant discarded it: a dead renderer (ProcessFailed / an
                        // abandoned operation) and a reset the renderer never answered are different
                        // diagnoses, and lumping them together is what made a wedged pool opaque.
                        // Guarded: this sits BEFORE _capacity.Release(), so a throwing app logger here
                        // used to leak the permit for the process lifetime (see SessionLog).
                        var reason = instance.Poisoned
                            ? "the instance is poisoned: a dead renderer, or an operation that was abandoned"
                            : $"reset to about:blank did not complete within {_options.ResetTimeout.TotalSeconds:0}s";
                        SessionLog.Try(_options.Log, l => l.LogInformation(
                            "Discarding a session instance instead of re-pooling it ({Reason}) — a fresh one " +
                            "will be created on the next lease.", reason));
                    }
                    DiscardInstance(instance);
                    if (!ok) lock (_lock) _created--; // a discarded (poisoned) instance frees room for a fresh one
                }
                // Free the slot AFTER the reset settles (or the instance is dropped). Guarded:
                // this delegate can run after Dispose() has torn the semaphore down.
                try { _capacity.Release(); } catch { }
            }));
        }
        catch
        {
            // The message loop is gone (host teardown) — can't reset on the UI thread. Release
            // the slot so a shutting-down process doesn't deadlock a pending lease; the window
            // dies with the app.
            try { _capacity.Release(); } catch { }
        }
    }

    private async Task<bool> ResetToBlankAsync(PoolInstance instance)
    {
        // Drop the previous lease's vetted host with its DOM: a recycled instance must not inherit an
        // approval the NEXT caller's guard never granted.
        instance.ApprovedHost = null;

        var navDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e) => navDone.TrySetResult(true);
        instance.Web.CoreWebView2.NavigationCompleted += OnNav;
        try
        {
            instance.Web.CoreWebView2.Navigate("about:blank");
            return await AwaitResetNavigationAsync(navDone.Task, _options.ResetTimeout).ConfigureAwait(true);
        }
        finally
        {
            instance.Web.CoreWebView2.NavigationCompleted -= OnNav;
        }
    }

    /// <summary>
    /// FAIL CLOSED on the reset navigation: true only when the blank navigation actually completed
    /// inside <paramref name="timeout"/>.
    /// <para>
    /// This used to swallow the wait's outcome and <c>return true</c> unconditionally, with a comment
    /// reasoning that "the next lease navigates away regardless" (P5.5 H2). It does not: a renderer
    /// that cannot answer a navigation to <c>about:blank</c> cannot answer the next lease's navigation
    /// either. So the documented "a failed reset DISCARDS the instance" invariant was reachable only
    /// via a THROW — a merely unresponsive instance was re-pooled forever, and every subsequent lease
    /// burned the full navigation cap against it before failing.
    /// </para>
    /// <para>Split out (and internal) so the real path is unit-testable: the pool's own reset test
    /// could only drive <c>ResetOverride</c>, which is precisely why this shipped unnoticed.</para>
    /// </summary>
    internal static async Task<bool> AwaitResetNavigationAsync(Task navigationCompleted, TimeSpan timeout)
    {
        try
        {
            await navigationCompleted.WaitAsync(timeout).ConfigureAwait(true);
            return true;
        }
        catch (Exception)
        {
            // Timed out, or the navigation itself failed — either way this instance is not reusable.
            return false;
        }
    }

    private void DiscardInstance(PoolInstance instance)
    {
        // Dev/test: dispose its own window (disposes its WebView2 too). Runtime: dispose ONLY
        // the WebView2 + detach it — the shared form stays for the other sessions.
        try
        {
            if (_options.VisiblePerSession)
            {
                instance.Host.Dispose();
            }
            else
            {
                instance.Host.Controls.Remove(instance.Web);
                instance.Web.Dispose();
            }
        }
        catch (Exception ex)
        {
            // Teardown stays best-effort, but no longer SILENT: a discard that fails leaks a browser
            // process holding the profile lock, and the next launch's init hangs on it — the exact
            // symptom the init-timeout message tries to explain (P5.5 H4.7). Guarded: this runs both
            // inside the posted return body and inside Dispose() under _lock.
            SessionLog.Try(_options.Log, l => l.LogWarning(ex, "Discarding a pooled session instance failed."));
        }
    }

    /// <summary>Dispose the idle instances and the shared host, and CANCEL any queued leases (a
    /// waiter on the capacity queue would otherwise hang forever). Leased sessions die with the
    /// app; one still returning after this is discarded (see <see cref="Return"/>).</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true; // Return observes this under the same lock and discards instead of re-pooling
            while (_free.Count > 0) DiscardInstance(_free.Pop());
        }
        try { _disposeCts.Cancel(); } catch { } // wake queued LeaseAsync waiters with a cancellation
        try { _sharedHost?.Dispose(); } catch { }
        // Let go of the shared environment: holding it would keep the profile's browser process — and
        // its folder OS lock — alive for the rest of the process, so a caller that disposes the pool
        // and then wipes the profile would always fail.
        _environment.Clear();
        // Neither the semaphore nor the CTS is disposed: SemaphoreSlim only needs disposal if
        // AvailableWaitHandle was touched (it never is here), and disposing it WHILE a just-
        // cancelled waiter is unwinding can wedge that waiter; the CTS holds no unmanaged handle
        // and an in-flight LeaseAsync may still read its Token to build its linked source. Both
        // are managed objects the GC reclaims.
    }

    /// <summary>Test seams.</summary>
    internal int FreeCount { get { lock (_lock) return _free.Count; } }

    internal int CreatedCount { get { lock (_lock) return _created; } }

    internal int AvailablePermits => _capacity.CurrentCount;
}
