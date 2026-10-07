using System.Collections.Concurrent;
using Shenora.Chromium.Host;
using Shenora.Core.Shell;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Chromium shell's single-instance gate, without CEF: the scope, the losing launch, and where a later launch goes
/// once the running app listens, from the gate's channel or handed over by CEF (<see cref="ChromiumSingleInstance.Relaunched"/>).
/// CEF's own hand-over and the window coming forward were run end to end in the shell.
/// </summary>
public class ChromiumSingleInstanceTests
{
    private static string UniqueRoot() => @"C:\ShenoraTests\" + Guid.NewGuid().ToString("n");

    private static ShenoraApplication App(string root, params string[] args) =>
        ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "Shenora.Tests.Chromium",
            Args = args,
            BaseDirectory = root,
            GetEnvironmentVariable = _ => null,
        }).Build();

    [Fact]
    public void No_options_is_no_gate()
    {
        var gate = new ChromiumSingleInstance();
        using var app = App(UniqueRoot());
        Assert.True(gate.Enter(null, app.ApplicationName, app.Paths, app.Args, null));
        Assert.Null(gate.Result);
    }

    [Fact]
    public void The_gate_is_decided_once_and_the_scope_is_the_install()
    {
        var root = UniqueRoot();
        using var app = App(root);
        var gate = new ChromiumSingleInstance();
        try
        {
            Assert.True(gate.Enter(new SingleInstanceHostOptions(), app.ApplicationName, app.Paths, app.Args, null));
            Assert.True(gate.Enter(new SingleInstanceHostOptions(), app.ApplicationName, app.Paths, app.Args, null));
            Assert.Equal(SingleInstanceResult.Acquired, gate.Result);

            // Another process of the same install is turned away.
            using var other = new SingleInstanceGuard(app.ApplicationName, app.Paths.RootDir);
            SingleInstanceResult? otherResult = null;
            var another = new Thread(() => otherResult = other.TryAcquire());   // another thread, as another process
            another.Start();
            Assert.True(another.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal(SingleInstanceResult.AlreadyRunning, otherResult);
        }
        finally { gate.Release(); }
    }

    [Fact]
    public void A_losing_launch_hands_the_running_app_its_arguments_and_lets_go()
    {
        var root = UniqueRoot();
        var arrived = new BlockingCollection<SingleInstanceLaunch>();
        using var app = App(root, "--open", "a.txt");
        using (var running = new ThreadHeldGuard(app.ApplicationName, app.Paths.RootDir, activated: arrived.Add))
        {
            var gate = new ChromiumSingleInstance();
            var options = new SingleInstanceHostOptions();
            Assert.False(gate.Enter(options, app.ApplicationName, app.Paths, app.Args, null));
            Assert.Equal(SingleInstanceResult.AlreadyRunning, gate.Result);
            gate.Lose(app, options);

            Assert.True(arrived.TryTake(out var activation, TimeSpan.FromSeconds(10)));
            Assert.Equal(["--open", "a.txt"], activation!.Arguments);
            Assert.Null(gate.Result);
        }
    }

    [Fact]
    public void A_launch_that_finds_the_running_app_shutting_down_starts_in_its_place()
    {
        var root = UniqueRoot();
        using var app = App(root, "--open", "a.txt");
        var running = new ThreadHeldGuard(app.ApplicationName, app.Paths.RootDir, activated: _ => { });
        running.StopListening();   // its loop ended: shutdown began
        _ = Task.Run(() =>
        {
            Thread.Sleep(300);
            running.Dispose();     // the rest of its shutdown, then the scope, last
        });
        var gate = new ChromiumSingleInstance();
        var options = new SingleInstanceHostOptions { RestartWaitTimeout = TimeSpan.FromSeconds(10) };
        try
        {
            Assert.False(gate.Enter(options, app.ApplicationName, app.Paths, app.Args, null));
            Assert.True(gate.Lose(app, options));                       // start here
            Assert.Equal(SingleInstanceResult.Acquired, gate.Result);  // and own the scope, to listen in turn
        }
        finally { gate.Release(); }
    }

    [Fact]
    public void An_apps_own_second_instance_callback_replaces_the_activation()
    {
        var root = UniqueRoot();
        var arrived = new BlockingCollection<SingleInstanceLaunch>();
        using var app = App(root, "x");
        using var running = new ThreadHeldGuard(app.ApplicationName, app.Paths.RootDir, activated: arrived.Add);
        var gate = new ChromiumSingleInstance();
        ShenoraApplication? reported = null;
        var options = new SingleInstanceHostOptions { OnSecondInstance = (a, _) => reported = a };

        Assert.False(gate.Enter(options, app.ApplicationName, app.Paths, app.Args, null));
        gate.Lose(app, options);

        Assert.Same(app, reported);
        Assert.False(arrived.TryTake(out _, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void A_later_launch_reaches_the_running_app_by_either_way_once_it_listens()
    {
        var root = UniqueRoot();
        using var app = App(root);
        var gate = new ChromiumSingleInstance();
        var arrived = new BlockingCollection<SingleInstanceLaunch>();
        try
        {
            Assert.True(gate.Enter(new SingleInstanceHostOptions(), app.ApplicationName, app.Paths, app.Args, null));

            // Before the main window is open, a launch waits for it (on macOS the first launch's own document arrives so).
            gate.Relaunched(new SingleInstanceLaunch(["early"], "."));
            Assert.Empty(arrived);
            Assert.True(gate.Listen(arrived.Add, null));
            Assert.True(arrived.TryTake(out var early, TimeSpan.FromSeconds(5)));
            Assert.Equal(["early"], early!.Arguments);

            gate.Relaunched(new SingleInstanceLaunch(["from-cef"], "."));
            Assert.True(arrived.TryTake(out var fromCef, TimeSpan.FromSeconds(5)));
            Assert.Equal(["from-cef"], fromCef!.Arguments);

            using var later = new SingleInstanceGuard(app.ApplicationName, app.Paths.RootDir);
            Assert.True(later.ActivateRunning(["from-the-channel"]));
            Assert.True(arrived.TryTake(out var fromChannel, TimeSpan.FromSeconds(10)));
            Assert.Equal(["from-the-channel"], fromChannel!.Arguments);
            Assert.Empty(arrived);
        }
        finally { gate.Release(); }

        // Released: the scope is free, and CEF's hand-over has nowhere to go, then or later.
        gate.Relaunched(new SingleInstanceLaunch(["late"], "."));
        Assert.False(arrived.TryTake(out _, TimeSpan.FromMilliseconds(200)));
        gate.Listen(arrived.Add, null);
        Assert.False(arrived.TryTake(out _, TimeSpan.FromMilliseconds(200)));
        using var successor = new ThreadHeldGuard(app.ApplicationName, app.Paths.RootDir);
        Assert.True(successor.Acquired);
    }

    [Fact]
    public void With_the_gate_off_CEFs_hand_over_still_arrives()
    {
        var gate = new ChromiumSingleInstance();
        using var app = App(UniqueRoot());
        var arrived = new BlockingCollection<SingleInstanceLaunch>();
        Assert.True(gate.Enter(null, app.ApplicationName, app.Paths, app.Args, null));
        Assert.True(gate.Listen(arrived.Add, null));
        gate.Relaunched(new SingleInstanceLaunch(["x"], "."));
        Assert.True(arrived.TryTake(out _, TimeSpan.FromSeconds(5)));
        gate.Release();
    }
}
