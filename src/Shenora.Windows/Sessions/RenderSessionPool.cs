using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Windows;

/// <summary>Inputs for <see cref="RenderSessionPool"/>.</summary>
public sealed class RenderSessionPoolOptions
{
    /// <summary>
    /// A live UI-thread control (typically the main window) every WebView2 op marshals onto — WebView2
    /// needs the app's message pump.
    /// </summary>
    public required Control Anchor { get; init; }

    /// <summary>Browser configuration for the pool's instances (they share one profile).
    /// Set <see cref="SessionBrowserOptions.KeepAliveInBackground"/> — the instances render
    /// off-screen and their JS must keep running.</summary>
    public required SessionBrowserOptions Browser { get; init; }
    /// <summary>
    /// Diagnostics. Null = silent. Browser-level events (init failure, suppressed popups, denied
    /// permissions, a dead renderer) report through <see cref="SessionBrowserOptions.Log"/> on
    /// <see cref="Browser"/> instead.
    /// </summary>
    public Microsoft.Extensions.Logging.ILogger? Log { get; init; }


    /// <summary>
    /// Max concurrent leased sessions (default 3). Leases past the cap WAIT until one is returned — a
    /// queue, not a failure.
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
    /// loopback/LAN/metadata host behind an unguarded navigate is a request-forgery hole. Null = any
    /// http(s) URL. Setting it also makes the pool cancel any unvetted CROSS-HOST navigation, so a
    /// guard-approved URL answering <c>302 → http://127.0.0.1:8080/admin</c> is not followed.
    /// <para>
    /// 🔴 <b><see cref="SessionBrowserOptions.RequestFilter"/> is a SIEVE, not the boundary.</b> It adds
    /// breadth over redirect targets and subresources, but it FAILS OPEN: one throw and an app that put
    /// its whole SSRF blocklist there has a policy that has stopped blocking. What holds unconditionally
    /// is this guard plus the kit's own cross-origin cancellation, both of which fail CLOSED.
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
    /// DISCARDS it instead of re-pooling a page whose JS thread is blocked.
    /// <para>
    /// ⚠ Keep this comfortably ABOVE <see cref="NavigationTimeout"/>: a navigate's own soft cap is part
    /// of the operation, so a lower value here reports a legitimately slow load as a wedge.
    /// </para>
    /// </summary>
    public TimeSpan OpTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long <c>NavigateAsync</c> waits for the document to load before returning what is there
    /// (default 30 s). A SOFT cap: the caller decides what "settled" means via its own script polling,
    /// so a slow page is not an error — it just stops holding the lease open.
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
/// A BOUNDED, mode-aware pool + queue of driveable off-screen WebView2 sessions. The runtime mode hosts
/// EVERY session's WebView2 on ONE shared hidden off-screen form; the dev/test mode
/// (<see cref="RenderSessionPoolOptions.VisiblePerSession"/>) gives each its own visible window. Sessions
/// are LEASED — the caller owns navigation, its own JS and the page's events for the life of a lease —
/// and several run in PARALLEL up to the cap. A lease takes a free instance (LIFO keeps a warm one hot),
/// creates one lazily under the cap, or WAITS on the capacity queue; returning one resets it to
/// <c>about:blank</c> and re-pools it, and a failed reset DISCARDS it. Dispose with the owning window.
/// </summary>
public sealed class RenderSessionPool : IDisposable
{
    private readonly RenderSessionPoolOptions _options;
    private readonly SemaphoreSlim _capacity; // gates leases to Capacity concurrent sessions — the queue
    private readonly CancellationTokenSource _disposeCts = new(); // cancels queued leases when the pool disposes
    private readonly object _lock = new();
    private readonly Stack<PoolInstance> _free = new(); // idle instances ready to re-lease (LIFO keeps a warm one hot)

    // ONE environment for the pool's single profile, instead of one per instance. Owner-scoped — see
    // SessionEnvironmentCache for why a static, profile-keyed cache would break
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
        // Validate at CONSTRUCTION: a non-positive budget would otherwise surface much later as an
        // instantly-cancelled operation or an instantly-discarded instance, with nothing pointing at
        // the option that caused it.
        RequireUsableTimeout(options.OpTimeout, nameof(RenderSessionPoolOptions.OpTimeout));
        RequireUsableTimeout(options.NavigationTimeout, nameof(RenderSessionPoolOptions.NavigationTimeout));
        RequireUsableTimeout(options.ResetTimeout, nameof(RenderSessionPoolOptions.ResetTimeout));
        // A zero/negative client size gives a 0×0 viewport: the page "loads", every element has zero
        // size, and any site that gates on window size behaves as if on a phantom display — with
        // nothing anywhere to suggest the viewport is the problem.
        if (options.OffscreenClientSize.Width < 1 || options.OffscreenClientSize.Height < 1)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(RenderSessionPoolOptions.OffscreenClientSize)} must be positive in both dimensions.");
        _capacity = new SemaphoreSlim(options.Capacity, options.Capacity);

        // The upper bound is not pedantry: these feed CancellationTokenSource.CancelAfter and
        // Task.WaitAsync, both of which THROW above int.MaxValue milliseconds (~24.8 days), so
        // TimeSpan.MaxValue as "no timeout" would fail from the middle of an operation.
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
        /// The host the caller's <see cref="RenderSessionPoolOptions.NavigationGuard"/> last approved,
        /// or null before any explicit navigate. <see cref="WireNavigationPolicy"/> reads it to reject
        /// an UNVETTED cross-host hop; cleared on return so a recycled instance can't inherit it.
        /// </summary>
        internal string? ApprovedOrigin { get; set; }   // host + port; see RenderSession.NavigateAsync

        /// <summary>
        /// Set when this instance's RENDER PROCESS died. ⚠ Its browser object survives the crash, so
        /// nothing else marks it unusable and it would be reset, re-pooled and re-leased forever;
        /// <see cref="Return"/> discards a poisoned instance instead.
        /// </summary>
        internal bool Poisoned { get; set; }

        /// <summary>
        /// The identity of the lease currently holding this instance — the SCOPE of everything its
        /// browser publishes on <see cref="SessionBrowserOptions.Events"/>. Re-assigned on every lease
        /// AND on every return, because the browser outlives the lease.
        /// <para>
        /// 🔴 <b>Never null, including while idle</b> — a null scope is a GLOBAL BROADCAST that reaches
        /// every subscriber, so the about:blank reset between two leases would be delivered to all of
        /// them. An idle instance gets an identity nobody holds instead, which only
        /// <see cref="Shenora.Core.Events.IEventBus.SubscribeToAll"/> sees.
        /// </para>
        /// </summary>
        internal string Scope { get; set; } = SessionBrowser.NewSessionId();
    }

    /// <summary>
    /// Lease a session (never null). Returns a free instance; else creates one under the cap;
    /// else waits for one to be returned. Cancels cleanly while waiting; a creation failure
    /// releases the capacity slot so the pool never leaks a permit.
    /// </summary>
    public async Task<RenderSession> LeaseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Link the dispose token so a queued lease is CANCELLED (not left hanging forever) when the
        // pool disposes. WaitAsync is outside the try: if it throws (cancelled/disposed) no permit was
        // taken, so there is nothing to release.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        await _capacity.WaitAsync(linked.Token).ConfigureAwait(false); // held for the whole lease
        try
        {
            PoolInstance? instance;
            lock (_lock) instance = _free.Count > 0 ? _free.Pop() : null;
            if (instance is null)
            {
                // The LINKED token, not the caller's: creation takes SECONDS (browser-process spawn +
                // profile attach), and disposing the pool mid-creation would let that creation run to
                // completion and publish a live off-screen window whose browser process then holds the
                // profile lock with nothing left to dispose it.
                instance = await (InstanceFactoryOverride ?? CreateInstanceAsync)(linked.Token).ConfigureAwait(false);
                lock (_lock) _created++; // accounted HERE (not in the factory) so the test seam counts too
            }
            // A fresh identity PER LEASE, not per instance: the browser is recycled but the work is not,
            // so a subscriber filtering on the previous lease's scope must not start receiving this
            // one's events. Cleared in Return.
            instance.Scope = SessionBrowser.NewSessionId();
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
    private async Task<PoolInstance> CreateInstanceAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<PoolInstance>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 🔴 THE TOKEN HAS TO REACH THE RETURNED TASK, not only the posted body. Every check below runs
        // INSIDE the BeginInvoke, and `BeginInvoke` succeeds whenever the handle exists — including
        // after `Application.Run` has returned, when nothing will pump it again. The lease then waits
        // forever WHILE HOLDING A CAPACITY PERMIT, so `Dispose()` cancelling `_disposeCts` cannot free
        // it either and the slot is gone for the process lifetime.
        using var cancelled = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        try
        {
            _options.Anchor.BeginInvoke(new Action(async () =>
            {
                Form? host = null;
                // 🔴 OWNERSHIP, not provenance. Creating the shared host does not make it this call's to
                // destroy — see the runtime branch below.
                var ownsHost = false;
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
                        ownsHost = true;   // a window per session: this call's to destroy
                        host.Show();
                    }
                    else
                    {
                        // Runtime: every session's WebView2 rides the ONE shared hidden form
                        // (webviews overlap off-screen — harmless, each renders independently).
                        //
                        // 🔴 `ownsHost` STAYS FALSE even when this call is the one that creates it. The
                        // shared host belongs to the POOL, and these lambdas INTERLEAVE: each yields at
                        // its multi-second init, so a second lease can parent its own control to this
                        // form while the first is still awaiting. Tearing the form down here would
                        // dispose that control with it and hand the other caller a dead session.
                        host = _sharedHost ??= OffscreenWindow.Create("Render sessions", _options.OffscreenClientSize);
                    }

                    web = new WebView2Control { Dock = DockStyle.Fill };
                    host.Controls.Add(web);

                    // The instance exists before init so the crash callback can mark it — a renderer
                    // can die at any time, including during the very first navigation.
                    var instance = new PoolInstance(host, web);
                    // The linked token (caller + pool dispose) reaches INIT itself, so a cancelled lease
                    // does not wait out the full InitTimeout before the re-check below. It gates the
                    // await only: the environment task is shared across this pool's instances.
                    await SessionBrowser.InitializeAsync(web, _options.Browser,
                        onProcessFailed: _ => instance.Poisoned = true,
                        // Read per emit, not captured: this browser is re-leased under a NEW identity
                        // each time, and the handlers wired here are wired once.
                        sessionScope: () => instance.Scope,
                        environmentCache: _environment,
                        cancellationToken: cancellationToken)
                        .ConfigureAwait(true);

                    // Re-check AFTER the multi-second init: a lease cancelled — or a pool disposed —
                    // during those seconds would otherwise still publish a fully live instance, an
                    // off-screen window nobody owns and a browser process holding the profile lock,
                    // because the cancelled caller never got a session to dispose.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        TearDown();
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }

                    WireNavigationPolicy(instance);
                    // ⚠ If the registration above already cancelled the task, NOBODY OWNS this instance —
                    // the caller has gone. Handing ownership over is what `TrySetResult` means, so a
                    // false return is a teardown obligation, not a no-op; otherwise the cancellation fix
                    // trades a hang for a leaked browser process holding the profile lock.
                    if (!tcs.TrySetResult(instance)) TearDown();
                }
                catch (Exception ex)
                {
                    TearDown();
                    tcs.TrySetException(ex);
                }

                // Undo everything this call realized, and NOTHING another one did: an abandoned control
                // can still finish attaching a live browser process that holds the very lock the init
                // timeout is diagnosing.
                //
                // ⚠ The CONTROL is always this call's; the HOST only in dev mode. See `ownsHost`.
                void TearDown()
                {
                    try
                    {
                        if (web is not null) { host?.Controls.Remove(web); web.Dispose(); }
                        if (ownsHost && host is not null) host.Dispose();
                    }
                    catch (Exception cleanupError)
                    {
                        // Best-effort, but not silent: a leaked control keeps the profile locked, which
                        // is the symptom the init timeout's message tries to explain. Through SessionLog
                        // because an app logger that throws HERE would escape before TrySetException
                        // below and hang the lease forever.
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

        // ⚠ AWAITED, not returned — see StreamingSession.StartAsync. `using var` on a non-async method
        // disposes the registration at the `return`, making it inert.
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Cancel an UNVETTED cross-host navigation for the instance's whole life — wired once here, on the
    /// UI thread, right after init, and only when the app configured a
    /// <see cref="RenderSessionPoolOptions.NavigationGuard"/>. Without it a guard-approved URL that
    /// answers <c>302 → http://127.0.0.1:8080/admin</c> is followed anyway: the caller vetted host X,
    /// and nothing vetted host Y.
    /// <para>
    /// A HOST COMPARISON rather than the guard itself, because <c>NavigationStarting</c> has NO deferral
    /// in the WebView2 SDK (verified), so an <c>async</c> policy cannot be awaited there and blocking on
    /// it would deadlock the UI thread. ⚠ <b>MAIN FRAME ONLY</b> — a cross-origin IFRAME is a
    /// subresource, and subresources are <see cref="SessionBrowserOptions.RequestFilter"/>'s job.
    /// </para>
    /// <para>
    /// NOT applied to <c>InteractiveSession</c>: a human-in-the-loop flow legitimately redirects across
    /// hosts (OAuth), and a window is human-driven rather than a data-driven SSRF surface.
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
                if (IsUnvettedHop(e.Uri, instance.ApprovedOrigin)) e.Cancel = true;
            }
            catch (Exception)
            {
                e.Cancel = true;
            }
        };
    }

    /// <summary>
    /// Should this navigation be cancelled? The whole rule, extracted from the event so it can be
    /// TESTED — the event itself needs a live browser.
    /// </summary>
    /// <param name="candidate">The URI the browser is about to navigate to.</param>
    /// <param name="approvedOrigin">The authority (host + port) the guard vetted, or null when nothing
    /// has been vetted yet.</param>
    internal static bool IsUnvettedHop(string candidate, string? approvedOrigin)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;   // the about:blank reset, data:, …
        if (approvedOrigin is null) return false;                  // nothing vetted yet
        // Authority, not Host: a different PORT on the same host is a different origin, and treating it
        // as the same one is what let a 302 to :8080/admin through.
        return !string.Equals(uri.Authority, approvedOrigin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Return an instance: reset it to <c>about:blank</c> ON THE UI THREAD, re-pool it, and release the
    /// capacity slot. A reset failure DISCARDS the instance (the slot is still released), and so does a
    /// return into a pool that has since disposed. Best-effort throughout — a return must never throw
    /// back into a caller's dispose.
    /// <para>
    /// ⚠ This clears the DOM/JS ONLY. The profile (cookies/localStorage/IndexedDB) is SHARED across
    /// every lease by design, so it is NOT trust-domain isolation; use separate pools for those.
    /// </para>
    /// </summary>
    internal void Return(PoolInstance instance)
    {
        // BEFORE the reset, which navigates to about:blank and therefore raises navigation events of
        // its own. Left alone they would be published under the finished lease's scope, telling a
        // subscriber that had not yet unsubscribed that its page had just navigated away.
        instance.Scope = SessionBrowser.NewSessionId();
        try
        {
            _options.Anchor.BeginInvoke(new Action(async () =>
            {
                bool ok;
                try
                {
                    // A crashed renderer can never be reset back to a usable state, so don't try —
                    // discard it straight away.
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
                    // Don't re-pool into a disposed pool — Dispose already drained _free, so a push here
                    // would leak the instance (and its browser process holding the profile lock) forever.
                    if (ok && !_disposed) { _free.Push(instance); repooled = true; }
                }
                if (!repooled)
                {
                    if (!ok)
                    {
                        // Name WHICH invariant discarded it: a dead renderer and a reset the renderer
                        // never answered are different diagnoses. Guarded — this sits BEFORE
                        // _capacity.Release(), so a throwing app logger would leak the permit.
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
            // The message loop is gone (host teardown) — can't reset on the UI thread. Release the slot
            // so a shutting-down process doesn't deadlock a pending lease; the window dies with the app.
            try { _capacity.Release(); } catch { }
        }
    }

    private async Task<bool> ResetToBlankAsync(PoolInstance instance)
    {
        // Drop the previous lease's vetted host with its DOM: a recycled instance must not inherit an
        // approval the NEXT caller's guard never granted.
        instance.ApprovedOrigin = null;

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
    /// inside <paramref name="timeout"/>. A renderer that cannot answer a navigation to
    /// <c>about:blank</c> cannot answer the next lease's either, so a merely unresponsive instance must
    /// not be re-pooled. Split out (and internal) so the real path is unit-testable — the pool's own
    /// reset test can only drive <c>ResetOverride</c>.
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
            // process holding the profile lock, and the next launch's init hangs on it. Guarded: this
            // runs both inside the posted return body and inside Dispose() under _lock.
            SessionLog.Try(_options.Log, l => l.LogWarning(ex, "Discarding a pooled session instance failed."));
        }
    }

    /// <summary>Dispose the idle instances and the shared host, and CANCEL any queued leases (a
    /// waiter on the capacity queue would otherwise hang forever). Leased sessions die with the
    /// app; one still returning after this is discarded (see <see cref="Return"/>).
    /// <para>
    /// ⚠ <b>CALL IT ON THE UI THREAD</b> (a <c>FormClosed</c> handler, or before <c>Application.Run</c>
    /// returns) — never from a DI container's disposal on a worker. Unlike every other path in this
    /// class, this one does NOT marshal: a post placed after <c>Application.Run</c> has returned is
    /// never pumped, so the browser processes would survive holding their profile folders' OS locks and
    /// the NEXT launch would hang in init on them.
    /// </para>
    /// </summary>
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
        // AvailableWaitHandle was touched (it never is here), disposing it WHILE a just-cancelled
        // waiter is unwinding can wedge that waiter, and an in-flight LeaseAsync may still read the
        // CTS's Token to build its linked source. Both are managed objects the GC reclaims.
    }

    /// <summary>Test seams.</summary>
    internal int FreeCount { get { lock (_lock) return _free.Count; } }

    internal int CreatedCount { get { lock (_lock) return _created; } }

    internal int AvailablePermits => _capacity.CurrentCount;
}
