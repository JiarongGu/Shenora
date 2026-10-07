using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Shenora.Core.Shell;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Core;

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
    public void The_channel_name_stays_short_for_a_long_application_name()
    {
        // A macOS socket path holds 104 bytes, and a pipe there is a socket under the temp folder.
        using var guard = new SingleInstanceGuard(new string('x', 200), UniqueScope());
        Assert.Matches("^shenora-[0-9a-f]{24}$", guard.ChannelName);
        Assert.DoesNotContain('\\', guard.MutexName);
        Assert.DoesNotContain('/', new SingleInstanceGuard("a/b\\c").MutexName);
    }

    [Fact]
    public void Second_acquire_on_same_scope_fails_until_first_is_released()
    {
        var scope = UniqueScope();

        using (var running = new ThreadHeldGuard("Shenora.Tests", scope))
        {
            Assert.True(running.Acquired);

            using var second = new SingleInstanceGuard("Shenora.Tests", scope);
            Assert.Equal(SingleInstanceResult.AlreadyRunning, second.TryAcquire());
        } // running instance shuts down cleanly here

        using var relaunch = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.Equal(SingleInstanceResult.Acquired, relaunch.TryAcquire()); // released → re-acquirable (the updater-restart path)
    }

    [Fact]
    public void Different_scopes_run_side_by_side()
    {
        var baseScope = UniqueScope();
        using var a = new SingleInstanceGuard("Shenora.Tests", baseScope + @"\a");
        using var b = new SingleInstanceGuard("Shenora.Tests", baseScope + @"\b");
        Assert.Equal(SingleInstanceResult.Acquired, a.TryAcquire());
        Assert.Equal(SingleInstanceResult.Acquired, b.TryAcquire());
        Assert.NotEqual(a.ChannelName, b.ChannelName);
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

        Assert.Equal(SingleInstanceResult.Acquired, guard.TryAcquire());
        Assert.Equal(SingleInstanceResult.Acquired, guard.TryAcquire()); // already ours = success, and no second handle taken

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
        Assert.Equal(SingleInstanceResult.AlreadyRunning, relaunch.TryAcquire()); // zero-wait: still held

        // Predecessor finishes its shutdown while the relaunch waits — the --restarted handoff.
        _ = Task.Run(() =>
        {
            Thread.Sleep(100);
            predecessor.Dispose();
        });
        Assert.Equal(SingleInstanceResult.Acquired, relaunch.TryAcquire(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Widened_wait_times_out_while_the_predecessor_stays()
    {
        var scope = UniqueScope();
        using var running = new ThreadHeldGuard("Shenora.Tests", scope);
        Assert.True(running.Acquired);

        using var second = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.Equal(SingleInstanceResult.AlreadyRunning, second.TryAcquire(TimeSpan.FromMilliseconds(150)));
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
        // Widened wait, not the zero-wait TryAcquire(): after Thread.Join returns, there is a tiny
        // window on some Windows kernels (seen live on a windows-latest CI runner, 2026-08-01) where
        // the CLR reports the thread ended before the OS has processed the mutex-abandonment
        // side-effect — so WaitOne(0) returns false cleanly instead of throwing AbandonedMutexException,
        // and TryAcquire() reports "already running" against a corpse. The widened wait falls through
        // to a blocking WaitOne that observes the abandonment as soon as the kernel flips the bit,
        // which is what real production usage does anyway (the --restarted handoff uses 25 s for
        // exactly this class of timing).
        Assert.Equal(SingleInstanceResult.Acquired, relaunch.TryAcquire(TimeSpan.FromSeconds(2))); // AbandonedMutexException → ours now
    }

    [Fact]
    public void A_later_launch_hands_the_running_instance_its_arguments_and_folder()
    {
        var scope = UniqueScope();
        var arrived = new BlockingCollection<SingleInstanceLaunch>();
        using var running = new ThreadHeldGuard("Shenora.Tests", scope, activated: arrived.Add);
        Assert.True(running.Acquired);

        using var later = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.Equal(SingleInstanceResult.AlreadyRunning, later.TryAcquire());
        Assert.True(later.ActivateRunning(["--open", "a file.txt", "日本語"]));

        Assert.True(arrived.TryTake(out var activation, TimeSpan.FromSeconds(10)));
        Assert.Equal(["--open", "a file.txt", "日本語"], activation!.Arguments);
        Assert.Equal(Environment.CurrentDirectory, activation.WorkingDirectory);

        // And the next one, over a channel that reopened for it.
        Assert.True(later.ActivateRunning());
        Assert.True(arrived.TryTake(out var second, TimeSpan.FromSeconds(10)));
        Assert.Empty(second!.Arguments);
    }

    [Fact]
    public void A_launch_waits_for_a_running_instance_that_has_not_listened_yet()
    {
        // The running app listens once its window can come forward; a launch in the meantime waits for the channel.
        var scope = UniqueScope();
        using var running = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.Equal(SingleInstanceResult.Acquired, running.TryAcquire());
        var arrived = new BlockingCollection<SingleInstanceLaunch>();

        var later = new SingleInstanceGuard("Shenora.Tests", scope);
        var sent = false;
        // A thread rather than an awaited task: this test's thread owns the mutex, and must release it itself.
        var sender = new Thread(() => sent = later.ActivateRunning(["late"], TimeSpan.FromSeconds(10)));
        sender.Start();
        Thread.Sleep(500);
        running.Listen(arrived.Add);

        Assert.True(sender.Join(TimeSpan.FromSeconds(15)) && sent);
        Assert.True(arrived.TryTake(out var activation, TimeSpan.FromSeconds(10)));
        Assert.Equal(["late"], activation!.Arguments);
        later.Dispose();
    }

    [Fact]
    public void A_running_instance_that_stopped_listening_takes_no_later_launch()
    {
        var scope = UniqueScope();
        var arrived = new BlockingCollection<SingleInstanceLaunch>();
        using var running = new ThreadHeldGuard("Shenora.Tests", scope, activated: arrived.Add);
        running.StopListening();   // its shutdown began

        using var later = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.Equal(SingleInstanceResult.AlreadyRunning, later.TryAcquire());   // the scope is still held
        Assert.False(later.ActivateRunning(["late"], TimeSpan.FromMilliseconds(300)));
        Assert.False(arrived.TryTake(out _, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void A_launch_that_finds_the_running_instance_shutting_down_starts_in_its_place()
    {
        var scope = UniqueScope();
        var running = new ThreadHeldGuard("Shenora.Tests", scope, activated: _ => { });
        running.StopListening();
        _ = Task.Run(() =>
        {
            Thread.Sleep(300);   // the rest of its shutdown, then the scope let go, last
            running.Dispose();
        });

        using var later = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.Equal(SingleInstanceResult.AlreadyRunning, later.TryAcquire());
        var clock = Stopwatch.StartNew();
        Assert.Equal(SingleInstanceResult.Acquired, later.ActivateOrTakeOver(["late"], TimeSpan.FromSeconds(10)));
        // As soon as the scope is let go, not after a hand-over that spins out its whole timeout on a channel already
        // gone: closing the app and opening it again lands here.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"started after {clock.Elapsed}");
    }

    [Fact]
    public void A_launch_that_arrives_before_the_running_instance_listens_is_handed_over_once_it_does()
    {
        var scope = UniqueScope();
        var arrived = new BlockingCollection<SingleInstanceLaunch>();
        using var starting = new ThreadHeldGuard("Shenora.Tests", scope);
        _ = Task.Run(() =>
        {
            Thread.Sleep(300);   // the rest of its start
            starting.Listen(arrived.Add);
        });

        using var later = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.Equal(SingleInstanceResult.AlreadyRunning, later.TryAcquire());
        Assert.Equal(SingleInstanceResult.AlreadyRunning, later.ActivateOrTakeOver(["late"], TimeSpan.FromSeconds(10)));
        Assert.True(arrived.TryTake(out var launch, TimeSpan.FromSeconds(10)));
        Assert.Equal(["late"], launch!.Arguments);
    }

    [Fact]
    public async Task Two_launches_that_meet_one_shutdown_start_one_instance_and_hand_it_the_other()
    {
        var scope = UniqueScope();
        var running = new ThreadHeldGuard("Shenora.Tests", scope, activated: _ => { });
        running.StopListening();
        var arrived = new BlockingCollection<SingleInstanceLaunch>();
        using var finished = new ManualResetEventSlim();

        SingleInstanceResult Launch(string name)
        {
            using var guard = new SingleInstanceGuard("Shenora.Tests", scope);
            var result = guard.ActivateOrTakeOver([name], TimeSpan.FromSeconds(10));
            if (result is SingleInstanceResult.Acquired)
            {
                guard.Listen(arrived.Add);
                finished.Wait(TimeSpan.FromSeconds(30));   // released on this, the owning, thread
            }
            return result;
        }
        var first = Task.Run(() => Launch("first"));
        var second = Task.Run(() => Launch("second"));
        Thread.Sleep(300);
        running.Dispose();

        var handed = arrived.TryTake(out var launch, TimeSpan.FromSeconds(10));
        finished.Set();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(handed);
        Assert.Single(results, r => r is SingleInstanceResult.Acquired);
        Assert.Equal(results[0] is SingleInstanceResult.Acquired ? "second" : "first", launch!.Arguments.Single());
    }

    [Fact]
    public void A_launch_handed_to_a_listening_instance_does_not_take_over()
    {
        var scope = UniqueScope();
        var arrived = new BlockingCollection<SingleInstanceLaunch>();
        using var running = new ThreadHeldGuard("Shenora.Tests", scope, activated: arrived.Add);

        using var later = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.Equal(SingleInstanceResult.AlreadyRunning, later.TryAcquire());
        Assert.Equal(SingleInstanceResult.AlreadyRunning, later.ActivateOrTakeOver(["late"], TimeSpan.FromSeconds(10)));
        Assert.True(arrived.TryTake(out var launch, TimeSpan.FromSeconds(10)));
        Assert.Equal(["late"], launch!.Arguments);
    }

    [Fact]
    public void A_launch_that_cannot_reach_an_instance_still_running_exits_after_the_wait()
    {
        // Still starting and never listening within the wait: it keeps the scope, and the launch exits when the wait ends.
        var scope = UniqueScope();
        using var starting = new ThreadHeldGuard("Shenora.Tests", scope);

        using var later = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.Equal(SingleInstanceResult.AlreadyRunning, later.TryAcquire());
        Assert.Equal(SingleInstanceResult.AlreadyRunning, later.ActivateOrTakeOver(["late"], TimeSpan.FromMilliseconds(600)));
    }

    [Fact]
    public void A_launch_with_nobody_listening_says_so()
    {
        using var later = new SingleInstanceGuard("Shenora.Tests", UniqueScope());
        Assert.False(later.ActivateRunning(["x"], TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void A_throwing_handler_does_not_close_the_channel()
    {
        var scope = UniqueScope();
        var arrived = new BlockingCollection<SingleInstanceLaunch>();
        var first = true;
        using var running = new ThreadHeldGuard("Shenora.Tests", scope, activated: activation =>
        {
            if (first) { first = false; throw new InvalidOperationException("the app's handler"); }
            arrived.Add(activation);
        });

        using var later = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.True(later.ActivateRunning(["one"]));
        Assert.True(later.ActivateRunning(["two"]));
        Assert.True(arrived.TryTake(out var activation, TimeSpan.FromSeconds(10)));
        Assert.Equal(["two"], activation!.Arguments);
    }

    [Fact]
    public void A_connection_that_is_not_a_launch_is_dropped_and_the_channel_stays()
    {
        var scope = UniqueScope();
        var arrived = new BlockingCollection<SingleInstanceLaunch>();
        using var running = new ThreadHeldGuard("Shenora.Tests", scope, activated: arrived.Add);
        using var later = new SingleInstanceGuard("Shenora.Tests", scope);

        using (var junk = new NamedPipeClientStream(".", later.ChannelName, PipeDirection.Out, PipeOptions.CurrentUserOnly))
        {
            junk.Connect(5000);
            junk.Write(Encoding.UTF8.GetBytes("{\"arguments\":[1,2],\"workingDirectory\":\"x\"}"));
        }
        Assert.True(later.ActivateRunning(["real"]));
        Assert.True(arrived.TryTake(out var activation, TimeSpan.FromSeconds(10)));
        Assert.Equal(["real"], activation!.Arguments);
        Assert.False(arrived.TryTake(out _, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void A_connection_that_never_writes_does_not_close_the_channel()
    {
        // A peer that connects and holds must not keep the channel: the read gives up on it, and the next launch still
        // gets through.
        var scope = UniqueScope();
        using var running = new SingleInstanceGuard("Shenora.Tests", scope) { ReadTimeout = TimeSpan.FromMilliseconds(300) };
        Assert.Equal(SingleInstanceResult.Acquired, running.TryAcquire());
        var arrived = new BlockingCollection<SingleInstanceLaunch>();
        running.Listen(arrived.Add);

        using var stuck = new NamedPipeClientStream(".", running.ChannelName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        stuck.Connect(5000);
        Thread.Sleep(1000);   // past the read timeout, still holding

        var later = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.True(later.ActivateRunning(["after"], TimeSpan.FromSeconds(5)));
        Assert.True(arrived.TryTake(out var launch, TimeSpan.FromSeconds(10)));
        Assert.Equal(["after"], launch!.Arguments);
        later.Dispose();
    }

    [Fact]
    public void Two_users_of_one_install_have_channels_of_their_own()
    {
        // The pipe's name is the machine's, so it carries the user as the mutex does; the same user gets the same one.
        using var a = new SingleInstanceGuard("Shenora.Tests", @"C:\One\Install");
        using var b = new SingleInstanceGuard("Shenora.Tests", @"C:\One\Install");
        Assert.Equal(a.ChannelName, b.ChannelName);
        Assert.EndsWith(SingleInstanceGuard.ChannelKey($"{Environment.UserDomainName}\\{Environment.UserName}#{System.Diagnostics.Process.GetCurrentProcess().SessionId}"),
            a.ChannelName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"arguments\":\"x\",\"workingDirectory\":\"y\"}")]
    [InlineData("{\"arguments\":[],\"workingDirectory\":3}")]
    [InlineData("{\"workingDirectory\":\"y\"}")]
    public void Anything_but_a_launch_decodes_to_nothing(string message) =>
        Assert.Null(SingleInstanceGuard.Decode(Encoding.UTF8.GetBytes(message)));

    [Fact]
    public void A_launch_round_trips()
    {
        var activation = new SingleInstanceLaunch(["a b", "\"quoted\"", ""], @"C:\somewhere");
        var decoded = SingleInstanceGuard.Decode(SingleInstanceGuard.Encode(activation));
        Assert.Equal(activation.Arguments, decoded!.Arguments);
        Assert.Equal(activation.WorkingDirectory, decoded.WorkingDirectory);
    }

    [Fact]
    public void Only_the_owner_listens()
    {
        var scope = UniqueScope();
        using var running = new ThreadHeldGuard("Shenora.Tests", scope);
        using var later = new SingleInstanceGuard("Shenora.Tests", scope);
        Assert.Equal(SingleInstanceResult.AlreadyRunning, later.TryAcquire());
        Assert.Throws<InvalidOperationException>(() => later.Listen(_ => { }));
    }
}
