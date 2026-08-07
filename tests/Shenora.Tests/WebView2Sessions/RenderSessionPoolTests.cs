using Shenora.Windows;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;
using Shenora.Core.Ipc;

namespace Shenora.Tests.WebView2Sessions;

/// <summary>
/// Pool ACCOUNTING (capacity queue, LIFO reuse, poisoned-instance discard, failure-releases-slot)
/// over the internal factory/reset seams — real browser processes are the sample-e2e's subject,
/// the family precedent. The anchor is a real handle-created control so BeginInvoke posts work;
/// tests pump them with Application.DoEvents.
/// </summary>
public class RenderSessionPoolTests
{
    private sealed class Fixture : IDisposable
    {
        public Form Anchor { get; } = new() { ShowInTaskbar = false };

        public int Created;

        /// <summary>The instance the factory handed out last — tests that need to mark it poisoned.</summary>
        public RenderSessionPool.PoolInstance? Last;

        public Fixture()
        {
            _ = Anchor.Handle; // BeginInvoke needs a created handle
        }

        public RenderSessionPool CreatePool(int capacity = 2, bool failCreation = false, bool failReset = false,
                                           TimeSpan? opTimeout = null)
        {
            var pool = new RenderSessionPool(new RenderSessionPoolOptions
            {
                Anchor = Anchor,
                Capacity = capacity,
                Browser = new SessionBrowserOptions { ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "session-tests", "unused"), KeepAliveInBackground = true },
                OpTimeout = opTimeout ?? TimeSpan.FromSeconds(60),
            })
            {
                InstanceFactoryOverride = _ =>
                {
                    if (failCreation) throw new InvalidOperationException("creation failed");
                    Created++;
                    // A dormant WebView2 control (no core) — the seams keep the pool from touching it.
                    Last = new RenderSessionPool.PoolInstance(
                        new Form { ShowInTaskbar = false }, new WebView2Control());
                    return Task.FromResult(Last);
                },
                ResetOverride = _ => Task.FromResult(!failReset),
            };
            return pool;
        }

        public void Pump(int rounds = 20)
        {
            for (var i = 0; i < rounds; i++)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
        }

        public void Dispose() => Anchor.Dispose();
    }

    [Fact]
    public async Task Lease_and_return_recycles_via_lifo()
    {
        using var fixture = new Fixture();
        using var pool = fixture.CreatePool(capacity: 2);

        var session = await pool.LeaseAsync();
        Assert.Equal(1, fixture.Created);
        Assert.Equal(1, pool.AvailablePermits);

        await session.DisposeAsync();
        fixture.Pump();

        Assert.Equal(1, pool.FreeCount);
        Assert.Equal(2, pool.AvailablePermits);

        // The next lease reuses the warm instance — no new creation.
        var again = await pool.LeaseAsync();
        Assert.Equal(1, fixture.Created);
        Assert.Equal(0, pool.FreeCount);
        await again.DisposeAsync();
        fixture.Pump();
    }

    [Fact]
    public async Task Session_dispose_is_idempotent_one_return_only()
    {
        using var fixture = new Fixture();
        using var pool = fixture.CreatePool(capacity: 2);

        var session = await pool.LeaseAsync();
        await session.DisposeAsync();
        await session.DisposeAsync(); // second dispose must NOT double-return / double-release
        fixture.Pump();

        Assert.Equal(1, pool.FreeCount);
        Assert.Equal(2, pool.AvailablePermits);
    }

    [Fact]
    public async Task Leases_past_the_cap_wait_until_a_return()
    {
        using var fixture = new Fixture();
        using var pool = fixture.CreatePool(capacity: 1);

        var first = await pool.LeaseAsync();
        var second = pool.LeaseAsync(); // queued — the cap is 1
        Assert.False(second.IsCompleted);

        await first.DisposeAsync();
        // The queued lease completes once the posted return releases the slot.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!second.IsCompleted && DateTime.UtcNow < deadline) fixture.Pump(2);
        Assert.True(second.IsCompleted);

        await (await second).DisposeAsync();
        fixture.Pump();
    }

    [Fact]
    public async Task A_failed_creation_releases_the_slot()
    {
        using var fixture = new Fixture();
        using var pool = fixture.CreatePool(capacity: 1, failCreation: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.LeaseAsync());

        Assert.Equal(1, pool.AvailablePermits); // no leaked permit — the pool stays usable
    }

    [Fact]
    public async Task A_failed_reset_discards_instead_of_repooling()
    {
        using var fixture = new Fixture();
        using var pool = fixture.CreatePool(capacity: 2, failReset: true);

        var session = await pool.LeaseAsync();
        Assert.Equal(1, pool.CreatedCount);

        await session.DisposeAsync();
        fixture.Pump();

        Assert.Equal(0, pool.FreeCount);       // the poisoned instance was NOT re-pooled
        Assert.Equal(0, pool.CreatedCount);    // and its creation slot was freed
        Assert.Equal(2, pool.AvailablePermits);
    }

    [Fact]
    public async Task A_cancelled_wait_leaves_no_leaked_permit()
    {
        using var fixture = new Fixture();
        using var pool = fixture.CreatePool(capacity: 1);
        var held = await pool.LeaseAsync();

        // Pre-cancelled so the whole path completes synchronously on this thread — a
        // timer-driven cancel would resume the awaiter on a pool thread, and DoEvents there
        // pumps the wrong queue (the anchor lives on THIS thread).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.LeaseAsync(cts.Token));

        await held.DisposeAsync();
        fixture.Pump();
        Assert.Equal(1, pool.AvailablePermits);
    }

    [Fact]
    public async Task Dispose_cancels_a_queued_lease_instead_of_hanging_it()
    {
        using var fixture = new Fixture();
        var pool = fixture.CreatePool(capacity: 1);
        var held = await pool.LeaseAsync();
        var queued = pool.LeaseAsync(); // parked on the full capacity queue
        Assert.False(queued.IsCompleted);

        pool.Dispose(); // must WAKE the waiter (a wire request awaiting it would otherwise hang forever)

        // WaitAsync(timeout) bounds the assertion: a cancelled waiter throws OCE promptly; a
        // still-hanging one would surface as a TimeoutException (a clean FAIL, never a suite stall).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        await held.DisposeAsync();
        fixture.Pump();
    }

    [Fact]
    public async Task A_return_after_dispose_discards_instead_of_repooling()
    {
        using var fixture = new Fixture();
        var pool = fixture.CreatePool(capacity: 1);
        var session = await pool.LeaseAsync();

        pool.Dispose();            // drains _free; the lease is still out
        await session.DisposeAsync(); // posts a Return that pumps AFTER dispose
        fixture.Pump();

        Assert.Equal(0, pool.FreeCount); // NOT pushed into a stack nobody will ever drain
    }

    [Fact]
    public async Task Disposed_sessions_reject_operations()
    {
        using var fixture = new Fixture();
        using var pool = fixture.CreatePool();
        var session = await pool.LeaseAsync();
        await session.DisposeAsync();
        fixture.Pump();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetHtmlAsync());
    }

    [Fact]
    public async Task Navigation_rejects_non_web_urls_before_touching_the_browser()
    {
        using var fixture = new Fixture();
        using var pool = fixture.CreatePool();
        var session = await pool.LeaseAsync();
        try
        {
            var pending = session.NavigateAsync("file:///C:/Windows/System32/cmd.exe");
            fixture.Pump();
            await Assert.ThrowsAsync<ArgumentException>(() => pending);
        }
        finally
        {
            await session.DisposeAsync();
            fixture.Pump();
        }
    }

    // ---- P5.5 H2: the sessions robustness cluster --------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_reset_navigation_fails_closed_when_it_does_not_complete(bool completes)
    {
        // The REAL reset path (the pool's other reset test can only drive ResetOverride, which is
        // exactly why "returns true unconditionally" survived five phase reviews). A blank navigation
        // that never completes means the renderer is not answering, so the instance must be discarded.
        var navigation = new TaskCompletionSource();
        if (completes) navigation.SetResult();

        var ok = await RenderSessionPool.AwaitResetNavigationAsync(navigation.Task, TimeSpan.FromMilliseconds(80));

        Assert.Equal(completes, ok);
    }

    [Fact]
    public async Task A_failed_reset_navigation_also_fails_closed()
    {
        // Not only a timeout: a navigation that faults must not report a healthy instance either.
        var navigation = new TaskCompletionSource();
        navigation.SetException(new InvalidOperationException("renderer gone"));

        Assert.False(await RenderSessionPool.AwaitResetNavigationAsync(navigation.Task, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task An_abandoned_operation_times_out_and_poisons_the_instance()
    {
        using var stalled = new StalledAnchor();
        var instance = StalledAnchor.NewInstance();
        using var pool = StalledAnchor.PoolOver(stalled.Form, instance, TimeSpan.FromMilliseconds(150));

        var session = await pool.LeaseAsync();

        // Before H2 the caller hung here forever — no token to observe, no cap. Escaping alone was
        // still not enough: the wedged instance went straight back into the pool, so every later lease
        // inherited it.
        await Assert.ThrowsAsync<TimeoutException>(() => session.GetHtmlAsync());

        Assert.True(instance.Poisoned); // → Return discards it (see the poisoned-discard test below)
    }

    [Fact]
    public async Task A_caller_cancelled_operation_surfaces_as_cancellation_not_a_timeout()
    {
        using var stalled = new StalledAnchor();
        var instance = StalledAnchor.NewInstance();
        using var pool = StalledAnchor.PoolOver(stalled.Form, instance, TimeSpan.FromSeconds(30));

        var session = await pool.LeaseAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The op cap must not rewrite the caller's own cancellation as a wedge.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetHtmlAsync(cts.Token));

        // Poisoned all the same, and deliberately so: the caller walked away while the operation was
        // still outstanding, so the renderer may still be mid-script. Handing that page to the next
        // lease is the risk; a browser restart is the cost.
        Assert.True(instance.Poisoned);
    }

    [Fact]
    public async Task A_poisoned_instance_is_discarded_without_attempting_a_reset()
    {
        using var fixture = new Fixture();
        using var pool = fixture.CreatePool(capacity: 1);
        var resets = 0;
        pool.ResetOverride = _ => { resets++; return Task.FromResult(true); };

        var session = await pool.LeaseAsync();
        fixture.Last!.Poisoned = true; // a dead renderer, or an operation that was abandoned
        await session.DisposeAsync();
        fixture.Pump();

        Assert.Equal(0, resets);             // a crashed renderer can never be reset back — don't try
        Assert.Equal(0, pool.FreeCount);     // not re-pooled
        Assert.Equal(0, pool.CreatedCount);  // its creation slot is freed for a fresh one
        Assert.Equal(1, pool.AvailablePermits);
    }

    [Fact]
    public async Task An_ordinary_operation_failure_does_not_poison_the_instance()
    {
        using var fixture = new Fixture();
        using var pool = fixture.CreatePool(capacity: 1);
        var session = await pool.LeaseAsync();

        // The body RAN and threw (a rejected URL). The page is fine, so discarding the instance would
        // cost a browser startup on every ordinary error — the reason "never completed" is tracked
        // rather than inferred from the exception.
        var pending = session.NavigateAsync("file:///C:/Windows/System32/cmd.exe");
        fixture.Pump();
        await Assert.ThrowsAsync<ArgumentException>(() => pending);

        await session.DisposeAsync();
        fixture.Pump();

        Assert.Equal(1, pool.FreeCount); // re-pooled, not discarded
    }

    [Fact]
    public async Task Interceptors_cannot_be_installed_after_the_lease_is_returned()
    {
        using var fixture = new Fixture();
        using var pool = fixture.CreatePool();
        var session = await pool.LeaseAsync();
        await session.DisposeAsync();
        fixture.Pump();

        // The instance now belongs to the NEXT lease, so a late tap would stream that lease's API
        // responses and posted messages to the previous caller — cross-lease disclosure.
        Assert.Throws<ObjectDisposedException>(() => session.OnNetwork(_ => { }));
        Assert.Throws<ObjectDisposedException>(() => session.OnMessage(_ => { }));
    }

    [Fact]
    public async Task Dispose_cancels_an_in_flight_instance_creation()
    {
        using var fixture = new Fixture();
        var pool = fixture.CreatePool(capacity: 1);
        var started = new TaskCompletionSource();
        pool.InstanceFactoryOverride = async ct =>
        {
            started.TrySetResult();
            // Creation takes seconds in reality (browser spawn + profile attach). The factory must
            // receive the LINKED token, or a pool disposed mid-creation lets it finish and publish a
            // live off-screen window with a browser process holding the profile lock.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        };

        var lease = pool.LeaseAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        pool.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, pool.AvailablePermits); // the permit came back
    }

    [Theory]
    [InlineData("OpTimeout", 0)]
    [InlineData("NavigationTimeout", 0)]
    [InlineData("ResetTimeout", 0)]
    [InlineData("OpTimeout", -1)]
    // TimeSpan.MaxValue as a stand-in for "no timeout" must fail HERE, not from the middle of an
    // operation: CancellationTokenSource.CancelAfter and Task.WaitAsync both throw above int.MaxValue ms.
    [InlineData("OpTimeout", 1)]
    [InlineData("NavigationTimeout", 1)]
    [InlineData("ResetTimeout", 1)]
    public void Unusable_timeouts_are_rejected_at_construction(string which, int mode)
    {
        using var fixture = new Fixture();
        var browser = new SessionBrowserOptions { ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "session-tests", "unused") };
        var bad = mode switch { 0 => TimeSpan.Zero, -1 => TimeSpan.FromSeconds(-1), _ => TimeSpan.MaxValue };

        // Construction-time validation is the package convention: a bad budget would otherwise surface
        // much later as an instantly-abandoned operation, or a throw from inside an op, with nothing
        // naming the option that caused it.
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderSessionPool(new RenderSessionPoolOptions
        {
            Anchor = fixture.Anchor,
            Browser = browser,
            OpTimeout = which == "OpTimeout" ? bad : TimeSpan.FromSeconds(60),
            NavigationTimeout = which == "NavigationTimeout" ? bad : TimeSpan.FromSeconds(30),
            ResetTimeout = which == "ResetTimeout" ? bad : TimeSpan.FromSeconds(5),
        }));
    }

    [Fact]
    public async Task A_throwing_app_logger_cannot_hang_a_lease_or_leak_a_permit()
    {
        // Found by this batch's own phase review. An ILogger is APP code, and the package's logging
        // (added in H4.7) invoked it unguarded — including inside the instance-creation catch, where a
        // throw escaped BEFORE TrySetException and left the lease's task never completing, and inside
        // the return body, where it escaped before _capacity.Release().
        using var fixture = new Fixture();
        using var pool = new RenderSessionPool(new RenderSessionPoolOptions
        {
            Anchor = fixture.Anchor,
            Capacity = 1,
            Browser = new SessionBrowserOptions { ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "session-tests", "unused") },
            Log = new ThrowingLogger(),
        })
        {
            InstanceFactoryOverride = _ => Task.FromResult(new RenderSessionPool.PoolInstance(
                new Form { ShowInTaskbar = false }, new WebView2Control())),
            ResetOverride = _ => Task.FromResult(false), // force the discard path, which is what logs
        };

        var session = await pool.LeaseAsync();
        await session.DisposeAsync();
        fixture.Pump();

        Assert.Equal(1, pool.AvailablePermits); // the permit came back despite the logger throwing
        Assert.Equal(0, pool.FreeCount);
    }

    /// <summary>An app logger that fails on every call — a dead file sink, a disposed provider.</summary>
    private sealed class ThrowingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            throw new ObjectDisposedException(nameof(ThrowingLogger));

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
                                Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter) =>
            throw new ObjectDisposedException(nameof(ThrowingLogger));
    }

    [Fact]
    public void A_zero_offscreen_client_size_is_rejected_at_construction()
    {
        using var fixture = new Fixture();

        // A 0×0 viewport lets the page "load" with every element sized zero, so any site that gates on
        // window size behaves as if on a phantom display — with nothing anywhere pointing at the
        // viewport as the cause (P5.5 H3).
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderSessionPool(new RenderSessionPoolOptions
        {
            Anchor = fixture.Anchor,
            Browser = new SessionBrowserOptions { ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "session-tests", "unused") },
            OffscreenClientSize = new Size(0, 0),
        }));
    }

    [Fact]
    public async Task A_non_positive_init_timeout_is_rejected_rather_than_blamed_on_a_profile_lock()
    {
        // Both of SessionBrowser's WaitAsync calls would expire immediately, so init failed instantly
        // with the profile-LOCK diagnosis — sending the caller hunting a zombie msedgewebview2 process
        // that does not exist (P5.5 H3).
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SessionBrowser.InitializeAsync(
            new WebView2Control(),
            new SessionBrowserOptions
            {
                ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "session-tests", "unused"),
                InitTimeout = TimeSpan.Zero,
            }));

        Assert.Contains(nameof(SessionBrowserOptions.InitTimeout), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An anchor whose message queue NOBODY pumps, realized on its own thread.
    /// <para>
    /// The thread matters: <c>WinFormsUiDispatcher</c> correctly runs a body INLINE when it is already
    /// on the target's UI thread, and the test thread is the anchor's UI thread in
    /// <see cref="Fixture"/>. So "just don't pump" would prove nothing there — the body would run
    /// synchronously and every operation would succeed. Marshalling to a FOREIGN, unpumped thread is
    /// what makes "the operation never completes" deterministic, which is the shape of a renderer that
    /// never answers a script call.
    /// </para>
    /// <para>
    /// The thread must stay alive after creating the handle (an exited thread destroys it, and
    /// <c>BeginInvoke</c> would throw instead of parking), so it blocks until disposal.
    /// </para>
    /// </summary>
    private sealed class StalledAnchor : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;
        private Form? _form;

        public StalledAnchor()
        {
            _thread = new Thread(() =>
            {
                _form = new Form { ShowInTaskbar = false };
                _ = _form.Handle; // realized on THIS thread; its queue is never drained
                _ready.Set();
                _release.Wait();
                try { _form.Dispose(); } catch { /* teardown */ }
            })
            { IsBackground = true, Name = "stalled-anchor" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(10)), "the stalled anchor never realized its handle");
        }

        public Form Form => _form!;

        /// <summary>A dormant instance (no browser core) — nothing in these tests touches it.</summary>
        public static RenderSessionPool.PoolInstance NewInstance() =>
            new(new Form { ShowInTaskbar = false }, new WebView2Control());

        public static RenderSessionPool PoolOver(Form anchor, RenderSessionPool.PoolInstance instance, TimeSpan opTimeout) =>
            new(new RenderSessionPoolOptions
            {
                Anchor = anchor,
                Capacity = 1,
                Browser = new SessionBrowserOptions { ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "session-tests", "unused") },
                OpTimeout = opTimeout,
            })
            {
                InstanceFactoryOverride = _ => Task.FromResult(instance),
                ResetOverride = _ => Task.FromResult(true),
            };

        public void Dispose()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(5));
            _ready.Dispose();
            _release.Dispose();
        }
    }

    [Fact]
    public async Task The_navigation_guard_can_refuse()
    {
        using var fixture = new Fixture();
        using var pool = new RenderSessionPool(new RenderSessionPoolOptions
        {
            Anchor = fixture.Anchor,
            Browser = new SessionBrowserOptions { ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "session-tests", "unused"), KeepAliveInBackground = true },
            NavigationGuard = (uri, _) => Task.FromResult(!uri.IsLoopback), // the SSRF-shaped policy seam
        })
        {
            InstanceFactoryOverride = _ => Task.FromResult(new RenderSessionPool.PoolInstance(
                new Form { ShowInTaskbar = false }, new WebView2Control())),
            ResetOverride = _ => Task.FromResult(true),
        };
        var session = await pool.LeaseAsync();
        try
        {
            var pending = session.NavigateAsync("http://127.0.0.1/admin");
            fixture.Pump();
            await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        }
        finally
        {
            await session.DisposeAsync();
            fixture.Pump();
        }
    }
}
