using System.Drawing;
using Microsoft.Extensions.Logging;

namespace Shenora.Core.Sessions;

/// <summary>Inputs for <see cref="RenderSessionPool"/>.</summary>
public sealed class RenderSessionPoolOptions
{
    /// <summary>The shell's browsers (D91): registered by <c>UseWindows</c> and <c>UseChromium</c> as
    /// <see cref="ISessionHost"/>.</summary>
    public required ISessionHost Host { get; init; }

    /// <summary>Browser configuration for the pool's instances (they share one profile). Set
    /// <see cref="SessionBrowserOptions.KeepAliveInBackground"/>: the instances render off-screen and their script must
    /// keep running.</summary>
    public required SessionBrowserOptions Browser { get; init; }

    /// <summary>
    /// Diagnostics. Null = silent. Browser-level events (init failure, suppressed popups, denied permissions, a dead
    /// renderer) report through <see cref="SessionBrowserOptions.Log"/> on <see cref="Browser"/> instead.
    /// </summary>
    public ILogger? Log { get; init; }

    /// <summary>
    /// Max concurrent leased sessions (default 3). Leases past the cap WAIT until one is returned: a queue, not a
    /// failure.
    /// </summary>
    public int Capacity { get; init; } = 3;

    /// <summary>
    /// Dev/test mode: a VISIBLE window per session (cascaded, watchable) instead of off-screen host. Either way a
    /// session drives only its browser; it never cares what hosts it.
    /// </summary>
    public bool VisiblePerSession { get; init; }

    /// <summary>
    /// Consulted before every EXPLICIT session navigation (return false to refuse). Wire your SSRF/allow-list policy
    /// here: sessions navigate data-driven URLs, and a server-reachable loopback/LAN/metadata host behind an unguarded
    /// navigate is a request-forgery hole. Null = any http(s) URL. Setting it also makes the pool cancel any unvetted
    /// navigation to another AUTHORITY, so a guard-approved URL answering <c>302 → http://127.0.0.1:8080/admin</c> is not
    /// followed.
    /// <para>
    /// 🔴 <b><see cref="SessionBrowserOptions.RequestFilter"/> is a SIEVE, not the boundary.</b> It adds breadth over
    /// redirect targets and subresources, but it FAILS OPEN. What holds unconditionally is this guard plus the kit's own
    /// cross-authority cancellation, both of which fail CLOSED.
    /// </para>
    /// </summary>
    public Func<Uri, CancellationToken, Task<bool>>? NavigationGuard { get; init; }

    /// <summary>Size of the off-screen surface, in device-independent pixels (a desktop-sized viewport: some sites gate
    /// on window size).</summary>
    public Size OffscreenClientSize { get; init; } = new(1280, 1600);

    /// <summary>
    /// Hard cap on ONE leased-session operation: the UI-thread marshal of a navigate, a script, an HTML read or a
    /// DevTools call (default 60 s). When it expires the caller gets a <see cref="TimeoutException"/> and the instance is
    /// marked unusable, so returning the lease DISCARDS it instead of re-pooling a page whose script thread is blocked.
    /// <para>
    /// ⚠ Keep this comfortably ABOVE <see cref="NavigationTimeout"/>: a navigate's own soft cap is part of the
    /// operation, so a lower value here reports a legitimately slow load as a wedge.
    /// </para>
    /// </summary>
    public TimeSpan OpTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long <c>NavigateAsync</c> waits for the document to load before returning what is there (default 30 s). A
    /// SOFT cap: the caller decides what "settled" means via its own script polling, so a slow page is not an error; it
    /// just stops holding the lease open.
    /// </summary>
    public TimeSpan NavigationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a returned instance's reset-to-<c>about:blank</c> may take before the instance is treated as unusable and
    /// DISCARDED rather than re-pooled (default 5 s). A blank navigation that does not complete means the renderer is not
    /// answering.
    /// </summary>
    public TimeSpan ResetTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// A BOUNDED pool and queue of driveable off-screen browser sessions, over the shell's browsers (D91). Sessions are
/// LEASED: the caller owns navigation, its own script and the page's events for the life of a lease, and several run
/// in PARALLEL up to the cap. A lease takes a free instance (LIFO keeps a warm one hot), creates one lazily under the
/// cap, or WAITS on the capacity queue; returning one resets it to <c>about:blank</c> and re-pools it, and a failed reset
/// DISCARDS it. Every instance shares one profile. Dispose with the owning window.
/// </summary>
public sealed class RenderSessionPool : IDisposable
{
    private readonly RenderSessionPoolOptions _options;
    private readonly SemaphoreSlim _capacity; // gates leases to Capacity concurrent sessions: the queue
    private readonly CancellationTokenSource _disposeCts = new(); // cancels queued leases when the pool disposes
    private readonly object _lock = new();
    private readonly Stack<PoolInstance> _free = new(); // idle instances ready to re-lease (LIFO keeps a warm one hot)

    // The browsers over the pool's single profile share one engine profile (a WebView2 environment, a CEF request
    // context). Owned by the pool, never the process: see ISessionHost.CreateContext for why.
    private ISessionBrowserContext? _context;
    private int _created;                                // total instances realized (≤ cap; grows, shrinks on discard)
    private bool _disposed;

    // Test seams: pool ACCOUNTING (capacity, LIFO, discard, failure-releases-slot) is proven with fakes through these.
    internal Func<CancellationToken, Task<PoolInstance>>? InstanceFactoryOverride;
    internal Func<PoolInstance, Task<bool>>? ResetOverride;

    /// <summary>A pool bounded by <paramref name="options"/>. Nothing starts until the first lease.</summary>
    public RenderSessionPool(RenderSessionPoolOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.Host);
        if (options.Capacity < 1) throw new ArgumentOutOfRangeException(nameof(options), "Capacity must be at least 1.");
        // Validate at CONSTRUCTION: a non-positive budget would otherwise surface much later as an instantly-cancelled
        // operation or an instantly-discarded instance, with nothing pointing at the option that caused it.
        RequireUsableTimeout(options.OpTimeout, nameof(RenderSessionPoolOptions.OpTimeout));
        RequireUsableTimeout(options.NavigationTimeout, nameof(RenderSessionPoolOptions.NavigationTimeout));
        RequireUsableTimeout(options.ResetTimeout, nameof(RenderSessionPoolOptions.ResetTimeout));
        // A zero/negative size gives a 0×0 viewport: the page "loads", every element has zero size, and any site that
        // gates on window size behaves as if on a phantom display, with nothing to suggest the viewport is the problem.
        if (options.OffscreenClientSize.Width < 1 || options.OffscreenClientSize.Height < 1)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(RenderSessionPoolOptions.OffscreenClientSize)} must be positive in both dimensions.");
        _capacity = new SemaphoreSlim(options.Capacity, options.Capacity);

        // The upper bound is not pedantry: these feed CancellationTokenSource.CancelAfter and Task.WaitAsync, both of
        // which THROW above int.MaxValue milliseconds (~24.8 days), so TimeSpan.MaxValue as "no timeout" would fail from
        // the middle of an operation.
        static void RequireUsableTimeout(TimeSpan value, string name)
        {
            if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(options),
                    $"{name} must be positive and no more than {TimeSpan.FromMilliseconds(int.MaxValue).TotalDays:0.#} days.");
        }
    }

    /// <summary>One pooled browser. The pool alone creates, resets and discards it.</summary>
    internal sealed record PoolInstance(ISessionBrowser Browser)
    {
        /// <summary>
        /// The authority the caller's <see cref="RenderSessionPoolOptions.NavigationGuard"/> last approved, or null
        /// before any explicit navigate. The navigation policy reads it to reject an UNVETTED hop to another authority;
        /// cleared on return so a recycled instance can't inherit it.
        /// </summary>
        internal string? ApprovedOrigin { get; set; }   // host + port; see RenderSession.NavigateAsync

        /// <summary>
        /// Set when this instance's renderer died, or an operation on it was abandoned. ⚠ Its browser object survives a
        /// crash, so nothing else marks it unusable and it would be reset, re-pooled and re-leased forever;
        /// <see cref="Return"/> discards a poisoned instance instead.
        /// </summary>
        internal bool Poisoned { get; set; }

        /// <summary>
        /// The identity of the lease currently holding this instance: the SCOPE of everything its browser publishes on
        /// <see cref="SessionBrowserOptions.Events"/>. Re-assigned on every lease AND on every return, because the
        /// browser outlives the lease.
        /// <para>
        /// 🔴 <b>Never null, including while idle</b>: a null scope is a GLOBAL BROADCAST that reaches every subscriber,
        /// so the about:blank reset between two leases would be delivered to all of them. An idle instance gets an
        /// identity nobody holds instead, which only <see cref="Shenora.Core.Events.IEventBus.SubscribeToAll"/> sees.
        /// </para>
        /// </summary>
        internal string Scope { get; set; } = NewSessionId();
    }

    /// <summary>A fresh session identity: one shape for every session type.</summary>
    internal static string NewSessionId() => Guid.NewGuid().ToString("n");

    /// <summary>
    /// Lease a session (never null). Returns a free instance; else creates one under the cap; else waits for one to be
    /// returned. Cancels cleanly while waiting; a creation failure releases the capacity slot so the pool never leaks a
    /// permit.
    /// </summary>
    public async Task<RenderSession> LeaseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Link the dispose token so a queued lease is CANCELLED (not left hanging forever) when the pool disposes.
        // WaitAsync is outside the try: if it throws (cancelled/disposed) no permit was taken, so there is nothing to
        // release.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        await _capacity.WaitAsync(linked.Token).ConfigureAwait(false); // held for the whole lease
        try
        {
            PoolInstance? instance;
            lock (_lock) instance = _free.Count > 0 ? _free.Pop() : null;
            if (instance is null)
            {
                // The LINKED token, not the caller's: creation takes SECONDS (browser-process spawn + profile attach),
                // and disposing the pool mid-creation would otherwise let that creation run to completion and publish a
                // live browser that then holds the profile lock with nothing left to dispose it.
                instance = await (InstanceFactoryOverride ?? CreateInstanceAsync)(linked.Token).ConfigureAwait(false);
                lock (_lock) _created++; // accounted HERE (not in the factory) so the test seam counts too
            }
            // A fresh identity PER LEASE, not per instance: the browser is recycled but the work is not, so a subscriber
            // filtering on the previous lease's scope must not start receiving this one's events. Cleared in Return.
            instance.Scope = NewSessionId();
            return new RenderSession(this, instance, _options);
        }
        catch
        {
            // Acquired a permit but failed to hand out an instance → give the slot straight back (guarded: Dispose may
            // have torn the semaphore down concurrently).
            try { _capacity.Release(); } catch { }
            throw;
        }
    }

    /// <summary>Realize a new instance ON THE UI THREAD: a browser from the shell, off-screen (or a visible dev
    /// window).</summary>
    private async Task<PoolInstance> CreateInstanceAsync(CancellationToken cancellationToken)
    {
        var host = _options.Host;
        var handoff = new TaskCompletionSource<PoolInstance>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 🔴 THE TOKEN HAS TO REACH THE RETURNED TASK, not only the posted body: a post the UI thread never runs (its
        // loop has ended) would otherwise leave the lease waiting forever WHILE HOLDING A CAPACITY PERMIT, and
        // Dispose() cancelling the pool's token could not free it either.
        using var cancelled = cancellationToken.Register(() => handoff.TrySetCanceled(cancellationToken));

        // What the browser's callbacks mark: it exists before the browser does, because a renderer can die at any time,
        // including while the browser is still being made.
        var pending = new PendingInstance();
        var posted = host.Ui.Queue(async () =>
        {
            ISessionBrowser? made = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ISessionBrowserContext context;
                lock (_lock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    context = _context ??= host.CreateContext();
                }
                var n = Volatile.Read(ref _created);
                made = await host.CreateAsync(new SessionBrowserDefinition
                {
                    Options = _options.Browser,
                    // Read per emit, not captured: this browser is re-leased under a NEW identity each time, and the
                    // handlers wired for it are wired once. Never null, even before the instance exists (see Scope).
                    Scope = () => pending.Instance?.Scope ?? pending.Scope,
                    Context = context,
                    OnGone = _ =>
                    {
                        pending.Gone = true;
                        if (pending.Instance is { } alive) alive.Poisoned = true;
                    },
                    ViewportSize = _options.OffscreenClientSize,
                    VisibleTitle = _options.VisiblePerSession ? $"Render session {n + 1}" : null,
                }, cancellationToken).ConfigureAwait(true);

                var instance = new PoolInstance(made) { Poisoned = pending.Gone };
                pending.Instance = instance;
                WireNavigationPolicy(instance);
                // An off-screen page never saves a file: DOWNLOAD_STARTING still reports it. Left on, a WebView2 pool
                // browser saved into the user's Downloads folder, while CEF's cancelled it anyway (Alloy's default).
                made.CancelDownloads = true;
                // ⚠ A false return means the lease was abandoned while the browser was being made, so NOBODY OWNS it:
                // handing ownership over is what TrySetResult means, and failing to is a teardown obligation, or the
                // cancellation trades a hang for a leaked browser process holding the profile lock.
                if (!handoff.TrySetResult(instance)) DiscardInstance(instance);
            }
            catch (Exception ex)
            {
                if (made is not null) DiscardInstance(new PoolInstance(made));
                if (ex is OperationCanceledException) handoff.TrySetCanceled(cancellationToken);
                else handoff.TrySetException(ex);
            }
        });
        if (!posted)
            handoff.TrySetException(new ObjectDisposedException(nameof(ISessionHost), "The shell's UI thread is gone."));

        // ⚠ AWAITED, not returned: `using var` on a method that returned the task would dispose the registration at the
        // `return`, before the token could ever fire.
        return await handoff.Task.ConfigureAwait(false);
    }

    // What a browser's callbacks reach before its pool instance exists.
    private sealed class PendingInstance
    {
        public readonly string Scope = NewSessionId();
        public volatile PoolInstance? Instance;
        public volatile bool Gone;
    }

    /// <summary>
    /// Cancel an UNVETTED navigation to another authority for the instance's whole life: wired once, on the UI thread,
    /// right after the browser is made, and only when the app configured a
    /// <see cref="RenderSessionPoolOptions.NavigationGuard"/>. Without it a guard-approved URL that answers
    /// <c>302 → http://127.0.0.1:8080/admin</c> is followed anyway: the caller vetted host X, and nothing vetted host Y.
    /// <para>
    /// An AUTHORITY COMPARISON rather than the guard itself, because the engine's navigation-starting callback is
    /// synchronous, so an <c>async</c> policy cannot be awaited there and blocking on it would deadlock the UI thread.
    /// ⚠ <b>MAIN FRAME ONLY</b>: a cross-origin IFRAME is a subresource, and subresources are
    /// <see cref="SessionBrowserOptions.RequestFilter"/>'s job.
    /// </para>
    /// <para>
    /// NOT applied to <see cref="InteractiveSession"/>: a human-in-the-loop flow legitimately redirects across hosts
    /// (OAuth), and a window is human-driven rather than a data-driven SSRF surface.
    /// </para>
    /// </summary>
    private void WireNavigationPolicy(PoolInstance instance)
    {
        if (_options.NavigationGuard is null) return;
        instance.Browser.CancelNavigation = uri => IsUnvettedHop(uri, instance.ApprovedOrigin);
    }

    /// <summary>
    /// Should this navigation be cancelled? The whole rule, extracted from the callback so it can be TESTED; the
    /// callback itself needs a live browser.
    /// </summary>
    /// <param name="candidate">The URI the browser is about to navigate to.</param>
    /// <param name="approvedOrigin">The authority (host + port) the guard vetted, or null when nothing has been vetted
    /// yet.</param>
    internal static bool IsUnvettedHop(string candidate, string? approvedOrigin)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;   // the about:blank reset, data:, …
        if (approvedOrigin is null) return false;                  // nothing vetted yet
        // Authority, not Host: a different PORT on the same host is a different origin, and treating it as the same one
        // is what let a 302 to :8080/admin through.
        return !string.Equals(uri.Authority, approvedOrigin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Return an instance: reset it to <c>about:blank</c> ON THE UI THREAD, re-pool it, and release the capacity slot. A
    /// reset failure DISCARDS the instance (the slot is still released), and so does a return into a pool that has since
    /// disposed. Best-effort throughout: a return must never throw back into a caller's dispose.
    /// <para>
    /// ⚠ This clears the DOM and script ONLY. The profile (cookies/localStorage/IndexedDB) is SHARED across every lease by
    /// design, so it is NOT trust-domain isolation; use separate pools for those.
    /// </para>
    /// </summary>
    internal void Return(PoolInstance instance)
    {
        // BEFORE the reset, which navigates to about:blank and therefore raises navigation events of its own. Left alone
        // they would be published under the finished lease's scope, telling a subscriber that had not yet unsubscribed
        // that its page had just navigated away.
        instance.Scope = NewSessionId();
        var posted = _options.Host.Ui.Queue(async () =>
        {
            bool ok;
            try
            {
                // A crashed renderer can never be reset back to a usable state, so don't try: discard it straight away.
                ok = !instance.Poisoned && await (ResetOverride ?? ResetToBlankAsync)(instance).ConfigureAwait(true);
            }
            catch
            {
                ok = false; // instance is wedged: drop it below
            }

            bool repooled = false;
            lock (_lock)
            {
                // Don't re-pool into a disposed pool: Dispose already drained _free, so a push here would leak the
                // instance (and its browser process holding the profile lock) forever.
                if (ok && !_disposed) { _free.Push(instance); repooled = true; }
            }
            if (!repooled)
            {
                if (!ok)
                {
                    // Name WHICH invariant discarded it: a dead renderer and a reset the renderer never answered are
                    // different diagnoses. Guarded: this sits BEFORE _capacity.Release(), so a throwing app logger would
                    // leak the permit.
                    var reason = instance.Poisoned
                        ? "the instance is poisoned: a dead renderer, or an operation that was abandoned"
                        : $"reset to about:blank did not complete within {_options.ResetTimeout.TotalSeconds:0}s";
                    SessionLog.Try(_options.Log, l => l.LogInformation(
                        "Discarding a session instance instead of re-pooling it ({Reason}); a fresh one will be created " +
                        "on the next lease.", reason));
                }
                DiscardInstance(instance);
                if (!ok) lock (_lock) _created--; // a discarded (poisoned) instance frees room for a fresh one
            }
            // Free the slot AFTER the reset settles (or the instance is dropped). Guarded: this can run after Dispose()
            // has torn the semaphore down.
            try { _capacity.Release(); } catch { }
        });
        if (!posted)
        {
            // The UI thread is gone (shell teardown): can't reset there. Release the slot so a shutting-down process
            // doesn't deadlock a pending lease; the browser dies with the app.
            try { _capacity.Release(); } catch { }
        }
    }

    private async Task<bool> ResetToBlankAsync(PoolInstance instance)
    {
        // Drop the previous lease's vetted authority with its DOM: a recycled instance must not inherit an approval the
        // NEXT caller's guard never granted.
        instance.ApprovedOrigin = null;

        var navDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The browser does not raise the abort of the previous lease's navigation, which this one replaces
        // (ISessionBrowser.NavigationCompleted): taken, the instance went back to the pool still loading the blank page.
        void OnNav(SessionNavigationResult _) => navDone.TrySetResult(true);
        instance.Browser.NavigationCompleted += OnNav;
        try
        {
            instance.Browser.Navigate("about:blank");
            return await AwaitResetNavigationAsync(navDone.Task, _options.ResetTimeout).ConfigureAwait(true);
        }
        finally
        {
            instance.Browser.NavigationCompleted -= OnNav;
        }
    }

    /// <summary>
    /// FAIL CLOSED on the reset navigation: true only when the blank navigation actually completed inside
    /// <paramref name="timeout"/>. A renderer that cannot answer a navigation to <c>about:blank</c> cannot answer the
    /// next lease's either, so a merely unresponsive instance must not be re-pooled.
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
            // Timed out, or the navigation itself failed: either way this instance is not reusable.
            return false;
        }
    }

    // On the UI thread.
    private void DiscardInstance(PoolInstance instance)
    {
        try
        {
            instance.Browser.Close();
        }
        catch (Exception ex)
        {
            // Teardown stays best-effort, but not SILENT: a discard that fails leaks a browser process holding the
            // profile lock, and the next launch's init hangs on it.
            SessionLog.Try(_options.Log, l => l.LogWarning(ex, "Discarding a pooled session instance failed."));
        }
    }

    /// <summary>Dispose the idle instances and the pool's profile, and CANCEL any queued leases (a waiter on the
    /// capacity queue would otherwise hang forever). Leased sessions die with the app; one still returning after this is
    /// discarded (see <see cref="Return"/>).
    /// <para>
    /// ⚠ <b>CALL IT ON THE UI THREAD</b> (as the owning window closes, before the shell's loop ends), never from a DI
    /// container's disposal on a worker. Unlike every other path in this class, this one does NOT marshal: a post placed
    /// after the shell's loop has ended is never run, so the browser processes would survive holding their profile
    /// folders' OS locks and the NEXT launch would hang making a browser on them.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        ISessionBrowserContext? context;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true; // Return observes this under the same lock and discards instead of re-pooling
            while (_free.Count > 0) DiscardInstance(_free.Pop());
            context = _context;
            _context = null;
        }
        try { _disposeCts.Cancel(); } catch { } // wake queued LeaseAsync waiters with a cancellation
        // Let go of the shared profile: holding it would keep the profile's browser process (and its folder OS lock)
        // alive for the rest of the process, so a caller that disposes the pool and then wipes the profile would always
        // fail.
        try { context?.Dispose(); } catch { }
        // Neither the semaphore nor the CTS is disposed: SemaphoreSlim only needs disposal if AvailableWaitHandle was
        // touched (it never is here), disposing it WHILE a just-cancelled waiter is unwinding can wedge that waiter, and
        // an in-flight LeaseAsync may still read the CTS's Token to build its linked source.
    }

    /// <summary>Test seams.</summary>
    internal int FreeCount { get { lock (_lock) return _free.Count; } }

    internal int CreatedCount { get { lock (_lock) return _created; } }

    internal int AvailablePermits => _capacity.CurrentCount;
}

/// <summary>A session's diagnostics, through the ONE owner of "an app logger that throws is a lost line".</summary>
internal static class SessionLog
{
    internal static void Try(ILogger? log, Action<ILogger> write)
    {
        if (log is null) return;
        Shenora.AppCallback.Run(() => write(log));
    }
}
