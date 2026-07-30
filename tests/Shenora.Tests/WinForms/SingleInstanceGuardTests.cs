using Shenora.WinForms;

namespace Shenora.Tests.WinForms;

/// <summary>
/// Holds a <see cref="SingleInstanceGuard"/> on a dedicated thread — the guard's contract is
/// cross-process, and an OS mutex is per-thread reentrant, so in-process "another instance"
/// simulations must own from a different thread (as a second process would). Dispose releases on
/// the owning thread (the clean-shutdown handoff); <c>abandon: true</c> lets the thread exit
/// still holding (the crashed-predecessor path).
/// </summary>
internal sealed class ThreadHeldGuard : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _release = new();
    private bool _disposed;

    public bool Acquired { get; private set; }

    public ThreadHeldGuard(string applicationName, string scope, bool abandon = false)
    {
        using var acquired = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            var guard = new SingleInstanceGuard(applicationName, scope);
            Acquired = guard.TryAcquire();
            acquired.Set();
            _release.Wait(TimeSpan.FromSeconds(30));
            if (!abandon) guard.Dispose(); // release on the OWNING thread; else exit holding
        })
        { IsBackground = true };
        _thread.Start();
        acquired.Wait(TimeSpan.FromSeconds(30));
    }

    /// <summary>Let the holding thread finish (releasing or abandoning) and join it.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _release.Set();
        _thread.Join(TimeSpan.FromSeconds(30));
        _release.Dispose();
    }
}

public class SingleInstanceGuardTests
{
    private static string UniqueScope() => @"C:\ShenoraTests\" + Guid.NewGuid().ToString("n");

    [Fact]
    public void ChannelKey_normalizes_case_and_trailing_separators()
    {
        var a = SingleInstanceGuard.ChannelKey(@"C:\App");
        Assert.Equal(a, SingleInstanceGuard.ChannelKey(@"C:\App\"));
        Assert.Equal(a, SingleInstanceGuard.ChannelKey(@"c:\app"));
        Assert.Equal(a, SingleInstanceGuard.ChannelKey(@"c:\app/"));
    }

    [Fact]
    public void ChannelKey_differs_per_scope_and_is_hex()
    {
        var a = SingleInstanceGuard.ChannelKey(@"C:\AppA");
        var b = SingleInstanceGuard.ChannelKey(@"C:\AppB");
        Assert.NotEqual(a, b);
        Assert.Matches("^[0-9a-f]{8}$", a);
        Assert.Equal(SingleInstanceGuard.ChannelKey(null), SingleInstanceGuard.ChannelKey(""));
    }

    [Fact]
    public void Second_acquire_on_same_scope_fails_until_first_is_released()
    {
        var scope = UniqueScope();

        using (var running = new ThreadHeldGuard("Shenora.Tests", scope))
        {
            Assert.True(running.Acquired);

            using var second = new SingleInstanceGuard("Shenora.Tests", scope);
            Assert.False(second.TryAcquire());
            Assert.NotEqual(0u, second.ActivateMessageId); // the loser still resolves the channel, so it can broadcast
        } // running instance shuts down cleanly here

        using var relaunch = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.True(relaunch.TryAcquire()); // released → re-acquirable (the updater-restart path)
    }

    [Fact]
    public void Different_scopes_run_side_by_side()
    {
        var baseScope = UniqueScope();
        using var a = new SingleInstanceGuard("Shenora.Tests", baseScope + @"\a");
        using var b = new SingleInstanceGuard("Shenora.Tests", baseScope + @"\b");
        Assert.True(a.TryAcquire());
        Assert.True(b.TryAcquire());
    }

    [Fact]
    public void TryAcquire_is_idempotent_and_leaks_no_handle()
    {
        // A second call used to overwrite the field with a fresh Mutex handle, leaking the first. And
        // because an OS mutex is per-thread REENTRANT, the second WaitOne(0) succeeded on this very
        // thread — so it reported ownership while Dispose could then release only one of the two
        // handles: the mutex stayed held after shutdown, and the fast --restarted handoff (which waits
        // for the predecessor to let go) timed out against a corpse (P5.5 H2).
        var scope = UniqueScope();
        var guard = new SingleInstanceGuard("Shenora.Tests", scope);

        Assert.True(guard.TryAcquire());
        Assert.True(guard.TryAcquire()); // already ours = success, and no second handle taken

        guard.Dispose();

        // The one release really did let go: a fresh guard on another thread (as a new process would)
        // can now take it. If a handle had leaked, this would fail.
        using var successor = new ThreadHeldGuard("Shenora.Tests", scope);
        Assert.True(successor.Acquired);
    }

    [Fact]
    public void Widened_wait_acquires_once_the_predecessor_releases()
    {
        var scope = UniqueScope();
        using var predecessor = new ThreadHeldGuard("Shenora.Tests", scope);
        Assert.True(predecessor.Acquired);

        using var relaunch = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.False(relaunch.TryAcquire()); // zero-wait: still held

        // Predecessor finishes its shutdown while the relaunch waits — the --restarted handoff.
        _ = Task.Run(() =>
        {
            Thread.Sleep(100);
            predecessor.Dispose();
        });
        Assert.True(relaunch.TryAcquire(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Widened_wait_times_out_while_the_predecessor_stays()
    {
        var scope = UniqueScope();
        using var running = new ThreadHeldGuard("Shenora.Tests", scope);
        Assert.True(running.Acquired);

        using var second = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.False(second.TryAcquire(TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public void Abandoned_predecessor_yields_ownership()
    {
        var scope = UniqueScope();
        using (var crashed = new ThreadHeldGuard("Shenora.Tests", scope, abandon: true))
        {
            Assert.True(crashed.Acquired);
        } // thread exits still holding → the OS marks the mutex abandoned

        using var relaunch = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.True(relaunch.TryAcquire()); // AbandonedMutexException → ours now
    }
}
