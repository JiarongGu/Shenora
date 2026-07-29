using Shenora.Core;
using Shenora.WinForms;
using Microsoft.Extensions.DependencyInjection;

namespace Shenora.Tests.WinForms;

/// <summary>
/// Drives the full WinForms runner through the internal test seams (<c>MessageLoop</c> replaces
/// the blocking pump, <c>SkipProcessInit</c> skips the process-global WinForms init). The real
/// pump path is proven against the sample app (P2.6 e2e), same as WebView2 environment creation.
/// </summary>
public class WinFormsHostTests
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
        var options = new WinFormsHostOptions { MainForm = _ => new Form() };
        builder.UseWinForms(options);
        using var app = builder.Build();

        Assert.NotNull(app.Services.GetRequiredService<IShenoraRunner>());
        Assert.Same(options, app.Services.GetRequiredService<WinFormsHostOptions>());
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
        builder.UseWinForms(new WinFormsHostOptions
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
        builder.UseWinForms(new WinFormsHostOptions
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
        builder.UseWinForms(new WinFormsHostOptions
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
            builder.UseWinForms(new WinFormsHostOptions
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

    private sealed class FakeStore : IWindowStateStore
    {
        public WindowState? Stored { get; init; }
        public bool LoadCalled { get; private set; }
        public WindowState? Saved { get; private set; }
        public WindowState? Load()
        {
            LoadCalled = true;
            return Stored;
        }
        public void Save(WindowState state) => Saved = state;
    }

    [Fact]
    public void Window_state_applies_before_the_loop_and_saves_on_close()
    {
        var root = UniqueRoot();
        var store = new FakeStore { Stored = new WindowState(500, 400, null, null, false) };
        var sizeInLoop = Size.Empty;
        var builder = Builder(root);
        builder.UseWinForms(new WinFormsHostOptions
        {
            MainForm = _ => new Form(),
            SkipProcessInit = true,
            SingleInstance = null, // deliberately multi-instance — also covers the no-guard path
            WindowState = new WindowStateHostOptions { Store = _ => store },
            MessageLoop = form =>
            {
                sizeInLoop = form.Size;
                // Close() only raises FormClosing/FormClosed via WM_CLOSE, which needs a window
                // handle — under Application.Run one always exists; force it here in the seam.
                _ = form.Handle;
                form.Close(); // fires FormClosed → the save
            },
        });

        using var app = builder.Build();
        app.Run();

        var expected = WindowStateManager.ToPhysical(
            store.Stored, DpiHelper.SystemScale(), new WindowStateOptions());
        Assert.True(store.LoadCalled);
        Assert.Equal(expected.Width, sizeInLoop.Width);
        Assert.Equal(expected.Height, sizeInLoop.Height);
        Assert.NotNull(store.Saved);
        Assert.False(store.Saved!.Maximized);
    }
}
