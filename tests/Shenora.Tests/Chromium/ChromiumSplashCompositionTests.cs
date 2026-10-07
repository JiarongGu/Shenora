using System.Drawing;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Chromium.Serving;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The splash composed into the Chromium shell: the handle an app resolves, where the splash plans the main window to
/// be, and the main window's handshake reaching it. CEF itself is not running; its UI thread is a fake.
/// </summary>
public class ChromiumSplashCompositionTests
{
    private static (ShenoraApplication App, ChromiumWindows Windows) Compose(ChromiumHostOptions options)
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Splash test" });
        builder.UseChromium(options);
        var app = builder.Build();
        var ui = new CefUiDispatcher(_ => true, () => true);
        ui.MarkReady();
        return (app, new ChromiumWindows(options, ui, app.Services.GetRequiredService<IMessageDispatcher>(), null, null, new ShellLauncher()));
    }

    [Fact]
    public void The_handle_is_registered_with_or_without_a_splash_and_does_nothing_without_one()
    {
        foreach (var splash in new[] { null, new ChromiumSplashOptions() })
        {
            var (app, _) = Compose(new ChromiumHostOptions { SingleInstance = null, Splash = splash });
            using (app)
            {
                var handle = app.Services.GetRequiredService<ChromiumSplash>();
                Assert.Same(handle, app.Services.GetRequiredService<ChromiumSplash>());
                handle.Close();
            }
        }
    }

    [Fact]
    public void Without_saved_state_the_splash_plans_only_the_windows_size_for_the_surface_to_centre()
    {
        // CEF centres such a window by each OS's own rule (macOS: above the middle, measured 86 points off exact), so the
        // plan carries no place and the surface centres itself the same way.
        var (app, windows) = Compose(new ChromiumHostOptions { SingleInstance = null, Window = new ChromiumWindowOptions { Width = 1000, Height = 760 } });
        using (app)
        {
            Assert.Equal(new ChromiumWindowGeometry.Plan(1000, 760, null, null, false),
                windows.MainWindowPlan(app.Services, [new Rectangle(0, 0, 1920, 1040), new Rectangle(1920, 0, 1280, 1000)]));
            Assert.Equal(new ChromiumWindowGeometry.Plan(1000, 760, null, null, false), windows.MainWindowPlan(app.Services, []));
        }
    }

    [Fact]
    public void With_saved_state_the_splash_plans_what_the_window_restores_from_the_one_store_the_window_uses()
    {
        var store = new FakeWindowStateStore { Stored = new WindowState(900, 700, 100, 50, WindowPlacement.Maximized) };
        var made = 0;
        var (app, windows) = Compose(new ChromiumHostOptions
        {
            SingleInstance = null,
            WindowState = new WindowStateHostOptions { Store = _ => { made++; return store; } },
        });
        using (app)
        {
            Assert.Equal(new ChromiumWindowGeometry.Plan(900, 700, 100, 50, true), windows.MainWindowPlan(app.Services, [new Rectangle(0, 0, 1920, 1040)]));
            Assert.True(store.LoadCalled);
            windows.Initialize(app, isDevelopment: false);
            Assert.Equal(1, made);   // the app's factory is asked once, not once by the splash and again by the window
        }
    }

    [Fact]
    public async Task The_main_windows_handshake_reaches_the_splash_and_another_windows_does_not()
    {
        var options = new ChromiumHostOptions { SingleInstance = null };
        var (app, windows) = Compose(options);
        using (app)
        {
            windows.Initialize(app, isDevelopment: false);
            var surface = new RecordingSurface();
            using var session = new SplashSession(new ChromiumSplashOptions { FadeOut = TimeSpan.Zero }, "App", null, app.Services, null,
                () => surface, new FakeTimeProvider(), null, null);
            windows.Splash = session;
            session.Start(new ChromiumWindowGeometry.Plan(400, 300, 0, 0, false), []);
            session.WindowOpened(1, new SplashOverlayLayout(false, 32, new SplashTitleBarOptions(), null, null));

            await Handshake(windows, "other");
            Assert.False(surface.Disposed);
            await Handshake(windows, ChromiumWindows.MainWindowName);
            Assert.True(surface.Disposed);
        }
    }

    // A page of the named window completes its ready handshake through the bridge the shell gives that window.
    private static async Task Handshake(ChromiumWindows windows, string name)
    {
        var origins = ChromiumOrigins.For("app.local", null, isDevelopment: false);
        ChromiumIpcBridge? bridge = null;
        _ = new ChromiumWindow(name, new ChromiumWindowOptions(), new ChromiumServing(null, origins, new ChromiumInterceptor()), origins,
            browser => bridge = windows.NewBridge(browser), _ => { }, null);
        bridge!.Incoming(JsonSerializer.Serialize(new { id = "h", category = "ipc", module = IpcHostBridge.HandshakeModule, type = IpcHostBridge.HandshakeType }));
        for (var i = 0; i < 100 && !bridge.IsClientReady; i++) await Task.Delay(10);
        Assert.True(bridge.IsClientReady);
    }

    private sealed class RecordingSurface : ISplashSurface
    {
        public bool Disposed { get; private set; }
        public void ShowCard(Rectangle dipRect, SplashRender render) { }
        public void ShowOver(nint mainWindow, SplashOverlayLayout layout, SplashRender render) { }
        public void Invalidate() { }
        public void FollowOwner() { }
        public void Reveal(Action shown) => shown();
        public void FadeOut(TimeSpan duration, Action done) => done();
        public void Dispose() => Disposed = true;
    }
}
