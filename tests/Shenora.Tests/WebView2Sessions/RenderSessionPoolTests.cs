using Shenora.WebView2.Sessions;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

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

        public Fixture()
        {
            _ = Anchor.Handle; // BeginInvoke needs a created handle
        }

        public RenderSessionPool CreatePool(int capacity = 2, bool failCreation = false, bool failReset = false)
        {
            var pool = new RenderSessionPool(new RenderSessionPoolOptions
            {
                Anchor = Anchor,
                Capacity = capacity,
                Browser = new SessionBrowserOptions { ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "session-tests", "unused"), KeepAliveInBackground = true },
            })
            {
                InstanceFactoryOverride = _ =>
                {
                    if (failCreation) throw new InvalidOperationException("creation failed");
                    Created++;
                    // A dormant WebView2 control (no core) — the seams keep the pool from touching it.
                    return Task.FromResult(new RenderSessionPool.PoolInstance(
                        new Form { ShowInTaskbar = false }, new WebView2Control()));
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
