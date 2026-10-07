using System.Collections.Concurrent;
using Shenora;
using Shenora.Core.Shell;
using Shenora.Tests.TestSupport;
using Shenora.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Shenora.Tests.WinForms;

/// <summary>
/// Drives the full WinForms runner through the internal test seams (<c>MessageLoop</c> replaces
/// the blocking pump, <c>SkipProcessInit</c> skips the process-global WinForms init). The real
/// pump path is proven against the sample app (P2.6 e2e), same as WebView2 environment creation.
/// </summary>
public class WinFormsRunnerTests
{
    private static string UniqueRoot() => @"C:\ShenoraTests\" + Guid.NewGuid().ToString("n");

    private static ShenoraApplicationBuilder Builder(string root, params string[] args) =>
        ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "Shenora.Tests.Host",
            Args = args,
            BaseDirectory = root,
            GetEnvironmentVariable = _ => null,
        });

    [Fact]
    public void UseWinForms_registers_the_runner_and_its_options()
    {
        var builder = Builder(UniqueRoot());
        var options = new WindowsHostOptions { MainForm = _ => new Form() };
        builder.UseWindows(options);
        using var app = builder.Build();

        Assert.NotNull(app.Services.GetRequiredService<IShenoraRunner>());
        Assert.Same(options, app.Services.GetRequiredService<WindowsHostOptions>());
    }

    [Fact]
    public void Run_executes_the_documented_order()
    {
        var order = new List<string>();
        var root = UniqueRoot();
        var builder = Builder(root);
        builder.OnStarting(_ => order.Add("starting.1"));
        builder.OnStarting(_ => order.Add("starting.2"));
        builder.OnStopping(_ => order.Add("stopping.1"));
        builder.OnStopping(_ => order.Add("stopping.2"));
        builder.UseWindows(new WindowsHostOptions
        {
            MainForm = _ =>
            {
                order.Add("form");
                return new Form();
            },
            SkipProcessInit = true,
            MessageLoop = _ => order.Add("loop"),
            SingleInstance = new SingleInstanceHostOptions { Scope = root },
        });

        using var app = builder.Build();
        app.Run();

        // Starting hooks in registration order, then the form, then the loop; stopping hooks in
        // REVERSE registration order (see IShenoraLifecycleHook).
        Assert.Equal(
            ["starting.1", "starting.2", "form", "loop", "stopping.2", "stopping.1"],
            order);
    }

    [Fact]
    public void Stopping_hooks_run_even_when_a_starting_hook_fails()
    {
        var order = new List<string>();
        var root = UniqueRoot();
        var builder = Builder(root);
        builder.OnStarting(_ => throw new InvalidOperationException("startup failed"));
        builder.OnStopping(_ => order.Add("stopping"));
        builder.UseWindows(new WindowsHostOptions
        {
            MainForm = _ => new Form(),
            SkipProcessInit = true,
            MessageLoop = _ => order.Add("loop"),
            SingleInstance = new SingleInstanceHostOptions { Scope = root },
        });

        using var app = builder.Build();
        Assert.Throws<InvalidOperationException>(app.Run);
        Assert.Equal(["stopping"], order); // no loop — but shutdown still ran
    }

    [Fact]
    public void Second_instance_takes_the_losing_path_without_building_the_app_window()
    {
        var root = UniqueRoot();
        using var running = new ThreadHeldGuard("Shenora.Tests.Host", root);
        Assert.True(running.Acquired);

        var formCreated = false;
        ShenoraApplication? reported = null;
        var builder = Builder(root);
        builder.OnStarting(_ => Assert.Fail("lifecycle hooks must not run for a losing launch"));
        builder.UseWindows(new WindowsHostOptions
        {
            MainForm = _ =>
            {
                formCreated = true;
                return new Form();
            },
            SkipProcessInit = true,
            MessageLoop = _ => Assert.Fail("the loop must not run for a losing launch"),
            SingleInstance = new SingleInstanceHostOptions
            {
                Scope = root,
                OnSecondInstance = (app, _) => reported = app,
            },
        });

        using var built = builder.Build();
        built.Run();

        Assert.False(formCreated);
        Assert.Same(built, reported);
    }

    [Fact]
    public void The_main_window_loses_its_system_animations_as_its_handle_is_created_when_asked()
    {
        // Before it shows: WinForms raises HandleCreated inside CreateHandle, ahead of the show. DWM's attribute cannot
        // be read back, so the call is what the test sees.
        nint disabled = 0, handle = 0;
        var builder = Builder(UniqueRoot());
        builder.UseWindows(new WindowsHostOptions
        {
            MainForm = _ => new Form(),
            SkipProcessInit = true,
            SingleInstance = null,
            WindowAnimations = WindowAnimations.None,
            DisableSystemAnimations = hwnd => disabled = hwnd,
            MessageLoop = form => handle = form.Handle,
        });
        using (var built = builder.Build()) built.Run();

        Assert.NotEqual(0, handle);
        Assert.Equal(handle, disabled);
    }

    [Fact]
    public void The_main_window_keeps_its_system_animations_by_default()
    {
        var asked = false;
        var builder = Builder(UniqueRoot());
        builder.UseWindows(new WindowsHostOptions
        {
            MainForm = _ => new Form(),
            SkipProcessInit = true,
            SingleInstance = null,
            DisableSystemAnimations = _ => asked = true,
            MessageLoop = form => _ = form.Handle,
        });
        using (var built = builder.Build()) built.Run();

        Assert.False(asked);
    }

    // Set-only (reading it back answers E_INVALIDARG): this pins the call DWM accepts, not its polarity.
    [Fact]
    public void The_system_animations_call_is_one_DWM_accepts()
    {
        using var form = new Form { ShowInTaskbar = false };
        Assert.True(DwmTransitions.Disable(form.Handle));
    }

    [Fact]
    public void A_losing_launch_hands_the_running_instance_its_arguments_by_default()
    {
        var root = UniqueRoot();
        var arrived = new BlockingCollection<SingleInstanceLaunch>();
        using var running = new ThreadHeldGuard("Shenora.Tests.Host", root, activated: arrived.Add);
        Assert.True(running.Acquired);

        var builder = Builder(root, "--open", "report.txt");
        builder.UseWindows(new WindowsHostOptions
        {
            MainForm = _ => new Form(),
            SkipProcessInit = true,
            MessageLoop = _ => Assert.Fail("the loop must not run for a losing launch"),
            SingleInstance = new SingleInstanceHostOptions { Scope = root },
        });

        using var built = builder.Build();
        built.Run();

        Assert.True(arrived.TryTake(out var activation, TimeSpan.FromSeconds(10)));
        Assert.Equal(["--open", "report.txt"], activation!.Arguments);
    }

    [Fact]
    public void A_launch_that_finds_the_running_instance_shutting_down_starts_in_its_place()
    {
        var root = UniqueRoot();
        var running = new ThreadHeldGuard("Shenora.Tests.Host", root, activated: _ => { });
        running.StopListening();
        _ = Task.Run(() =>
        {
            Thread.Sleep(300);
            running.Dispose();
        });

        bool? listening = null;
        var builder = Builder(root, "--open", "report.txt");
        builder.UseWindows(new WindowsHostOptions
        {
            MainForm = _ => new Form(),
            SkipProcessInit = true,
            MessageLoop = _ => listening = Reach(root),
            SingleInstance = new SingleInstanceHostOptions { Scope = root, RestartWaitTimeout = TimeSpan.FromSeconds(10) },
        });
        using (var built = builder.Build()) built.Run();

        Assert.True(listening);                                                // it started, and takes later launches
        Assert.Equal(SingleInstanceResult.Acquired, TakeElsewhere(root));       // and let the scope go as it stopped
    }

    [Fact]
    public void The_running_instance_stops_taking_launches_before_its_stop_hooks_and_lets_the_scope_go_last()
    {
        // A later launch that reached the instance once its shutdown began was handed to an app that would not come
        // forward, and lost. So the channel closes before the stop hooks run, while the scope stays held through them.
        var root = UniqueRoot();
        bool? reachedRunning = null, reachedStopping = null;
        SingleInstanceResult? scopeStopping = null;
        var builder = Builder(root);
        builder.OnStopping(_ =>
        {
            reachedStopping = Reach(root);
            scopeStopping = TakeElsewhere(root);
        });
        builder.UseWindows(new WindowsHostOptions
        {
            MainForm = _ => new Form(),
            SkipProcessInit = true,
            MessageLoop = _ => reachedRunning = Reach(root),
            SingleInstance = new SingleInstanceHostOptions { Scope = root },
        });
        using (var built = builder.Build()) built.Run();

        Assert.True(reachedRunning);
        Assert.False(reachedStopping);
        Assert.Equal(SingleInstanceResult.AlreadyRunning, scopeStopping);
        Assert.Equal(SingleInstanceResult.Acquired, TakeElsewhere(root));
    }

    private static bool Reach(string root)
    {
        using var later = new SingleInstanceGuard("Shenora.Tests.Host", root);
        return later.ActivateRunning([], TimeSpan.FromMilliseconds(300));
    }

    // On another thread, as another process: an OS mutex is reentrant on the thread that holds it.
    private static SingleInstanceResult TakeElsewhere(string root) => Task.Run(() =>
    {
        using var other = new SingleInstanceGuard("Shenora.Tests.Host", root);
        return other.TryAcquire();
    }).GetAwaiter().GetResult();

    [Fact]
    public void The_running_instance_restores_its_form_and_hands_OnActivated_the_launch()
    {
        // The whole running side through a real show sequence: Shown → the channel opens → a later launch arrives on the
        // channel's thread → marshalled to the form's → the form restored → the app's callback, on the UI thread.
        Sta.Run(() =>
        {
            var root = UniqueRoot();
            SingleInstanceLaunch? received = null;
            var restored = false;
            var onUiThread = false;
            Form? main = null;
            var builder = Builder(root);
            builder.UseWindows(new WindowsHostOptions
            {
                MainForm = _ => main = new Form
                {
                    WindowState = FormWindowState.Minimized, ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(0, 0, 200, 150),
                },
                SkipProcessInit = true,
                SingleInstance = new SingleInstanceHostOptions
                {
                    Scope = root,
                    OnActivated = (_, launch) =>
                    {
                        received = launch;
                        restored = main!.WindowState == FormWindowState.Normal;
                        onUiThread = !main.InvokeRequired;
                        main.Close();
                    },
                },
                MessageLoop = form =>
                {
                    // After the runner's own Shown handler, which opened the channel.
                    form.Shown += (_, _) => Task.Run(() =>
                    {
                        using var later = new SingleInstanceGuard("Shenora.Tests.Host", root);
                        later.ActivateRunning(["--open", "x.txt"]);
                    });
                    Application.Run(form);
                },
            });

            using var app = builder.Build();
            app.Run();

            Assert.Equal(["--open", "x.txt"], received!.Arguments);
            Assert.True(restored);
            Assert.True(onUiThread);
        });
    }

    [Fact]
    public void A_running_instance_that_never_showed_its_window_is_still_brought_forward()
    {
        // A tray app: its own loop, and the window never shown. The channel opened on Shown, so it never opened, and a
        // later launch waited, gave up and exited with this app still hidden.
        Sta.Run(() =>
        {
            var root = UniqueRoot();
            var shown = false;
            Form? main = null;
            var builder = Builder(root);
            builder.UseWindows(new WindowsHostOptions
            {
                MainForm = _ => main = new Form
                {
                    ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(0, 0, 200, 150),
                },
                SkipProcessInit = true,
                SingleInstance = new SingleInstanceHostOptions
                {
                    Scope = root,
                    OnActivated = (_, _) =>
                    {
                        shown = main!.Visible;
                        main.Close();
                        Application.ExitThread();
                    },
                },
                MessageLoop = hidden =>
                {
                    using var bail = new System.Windows.Forms.Timer { Interval = 15_000 };   // a broken run ends
                    bail.Tick += (_, _) => Application.ExitThread();
                    bail.Start();
                    _ = Task.Run(() =>
                    {
                        using var later = new SingleInstanceGuard("Shenora.Tests.Host", root);
                        later.ActivateRunning(["--open", "x.txt"], TimeSpan.FromSeconds(10));
                    });
                    Application.Run();
                },
            });

            using var app = builder.Build();
            app.Run();

            Assert.True(shown, "the hidden window was not brought forward");
        });
    }

    [Fact]
    public void Restarted_relaunch_waits_out_the_predecessor()
    {
        var root = UniqueRoot();
        var predecessor = new ThreadHeldGuard("Shenora.Tests.Host", root);
        try
        {
            Assert.True(predecessor.Acquired);
            _ = Task.Run(() =>
            {
                Thread.Sleep(150); // the outgoing instance finishing its shutdown
                predecessor.Dispose();
            });

            var ran = false;
            var builder = Builder(root, "--restarted");
            builder.UseWindows(new WindowsHostOptions
            {
                MainForm = _ => new Form(),
                SkipProcessInit = true,
                MessageLoop = _ => ran = true,
                SingleInstance = new SingleInstanceHostOptions
                {
                    Scope = root,
                    RestartWaitTimeout = TimeSpan.FromSeconds(10),
                },
            });

            using var app = builder.Build();
            app.Run();
            Assert.True(ran);
        }
        finally
        {
            predecessor.Dispose();
        }
    }

    [Fact]
    public void Window_state_applies_before_the_loop_and_saves_on_close()
    {
        var root = UniqueRoot();
        var store = new FakeWindowStateStore { Stored = new WindowState(500, 400, null, null, WindowPlacement.Normal) };
        var sizeInLoop = Size.Empty;
        var builder = Builder(root);
        builder.UseWindows(new WindowsHostOptions
        {
            MainForm = _ => new Form(),
            SkipProcessInit = true,
            SingleInstance = null, // deliberately multi-instance — also covers the no-guard path
            WindowState = new WindowStateHostOptions { Store = _ => store },
            MessageLoop = form =>
            {
                // Force the handle FIRST — Application.Run would do so as part of Show, which is
                // also when the parameterless AttachTo's deferred apply runs (0.1.2). Reading
                // form.Size before the handle exists would see the pre-apply default. Close()
                // also needs the handle to raise FormClosing/FormClosed via WM_CLOSE.
                _ = form.Handle;
                sizeInLoop = form.Size;
                form.Close(); // fires FormClosed → the save
            },
        });

        using var app = builder.Build();
        app.Run();

        // Compute the expected via the SAME (work-area-clamped) overload Apply now uses (0.1.1),
        // not the 3-arg one, so this assertion cannot silently drift on a runner whose primary
        // work area is smaller than the saved size.
        var expected = WindowStateManager.ToPhysical(
            store.Stored, DpiHelper.SystemScale(), new WindowStateOptions(),
            Screen.AllScreens.Select(s => s.WorkingArea));
        Assert.True(store.LoadCalled);
        Assert.Equal(expected.Width, sizeInLoop.Width);
        Assert.Equal(expected.Height, sizeInLoop.Height);
        Assert.NotNull(store.Saved);
        Assert.Equal(WindowPlacement.Normal, store.Saved!.Placement);
    }
}
