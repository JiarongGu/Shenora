using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;
using Shenora.Core.Events;
using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// Runs the app on CEF's message loop, on the main thread (D83's Views model): CEF started as
/// <see cref="CefStartup"/> starts it, or taken over from <see cref="ChromiumEarlyStart"/> when it started as the app
/// was composed (D87), the app's hooks once CEF's context exists, and after the loop the hooks stop before CEF shuts
/// down.
/// </summary>
internal sealed unsafe class ChromiumRunner(ChromiumHostOptions options, CefUiDispatcher ui, ChromiumWindows windows, ChromiumTray? tray = null,
    ILogger<ChromiumRunner>? log = null, ChromiumSplash? splash = null)
    : IShenoraRunner
{
    private SplashSession? _splash;

    /// <summary>What the shell starts CEF with, from the app's options, paths and environment: the same whether it
    /// starts as the app is composed or when it runs.</summary>
    internal static CefStartup.Settings SettingsFor(ChromiumHostOptions options, ShenoraPaths paths, ShenoraEnvironment environment) =>
        new(options.UserDataFolder ?? paths.DataArea("chromium"), IsDevelopment(options, environment), options.DevToolsPort,
            options.Window.BackgroundColor is { } color ? (uint)color.ToArgb() : null, MultiThreadedLoop: false)
        { Locale = options.Locale, Windowless = options.OffscreenSessions };

    private static bool IsDevelopment(ChromiumHostOptions options, ShenoraEnvironment environment) =>
        options.IsDevelopment ?? environment.IsDevelopment;

    public void Run(ShenoraApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var isDevelopment = IsDevelopment(options, app.Environment);
        var single = ChromiumSingleInstance.Process;
        // A launch UseChromium's gate already turned away started no CEF, and touches none now.
        ChromiumApp? cefApp = null;
        if (!ChromiumEarlyStart.Process.HasStarted && single.Result is not SingleInstanceResult.AlreadyRunning)
        {
            // CEF's subprocesses first: an app running an exe of its own with no launcher beside it is each of them too,
            // and one that met the gate would take the scope as a second instance.
            cefApp = new ChromiumApp(() => Started(app, isDevelopment), relaunched: single.Relaunched);
            var code = CefStartup.ExecuteIfSubprocess(cefApp, log);
            if (code >= 0) { Environment.Exit(code); return; }
        }
        if (!single.Enter(options.SingleInstance, app.ApplicationName, app.Paths, app.Args, app.Services.GetService<ILogger<SingleInstanceGuard>>()))
        {
            if (!single.Lose(app, options.SingleInstance!)) return;
            // The running instance was shutting down and let the scope go: this launch starts in its place, with the CEF
            // it did not start when the gate turned it away.
            cefApp ??= new ChromiumApp(() => Started(app, isDevelopment), relaunched: single.Relaunched);
        }
        if (single.Result is SingleInstanceResult.Unverified)
            AppCallback.Log(log, () => "[Shenora.Chromium] The single-instance gate could not tell whether another instance runs; this one starts unguarded",
                LogLevel.Warning);
        // The process's exception channels, from here on the app's OnUnhandledException (as WinFormsBootstrap wires them).
        ChromiumUnhandledExceptions.Install(options);
        // Past the gate, so a launch it turned away shows nothing; before CEF, which the splash exists not to wait on.
        if (options.Splash is { } splashOptions) StartSplash(app, splashOptions);

        try
        {
            try
            {
                if (!ChromiumEarlyStart.Process.TakeOver(options, () => Started(app, isDevelopment)))
                    CefStartup.Initialize(cefApp!, SettingsFor(options, app.Paths, app.Environment));
            }
            catch (ChromiumAlreadyRunningException ex)
            {
                // Past the gate (turned off, or scoped narrower than the data folder), but CEF's own rule holds: it
                // handed this launch to the process that owns the folder, which comes to the front. This one is done.
                AppCallback.Log(log, () => $"[Shenora.Chromium] {ex.Message}", LogLevel.Information);
                return;
            }

            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new CefUiContext(ui, log));
            try
            {
                Cef.cef_run_message_loop();
            }
            finally
            {
                // Shutdown begins: a later launch from here on waits to start in this one's place.
                single.StopListening();
                // Still on CEF's UI thread, where the icon was made; before CEF goes, or it lingers until hovered.
                AppCallback.Run(() => tray?.Stop(), ex => AppCallback.Log(log, () => "[Shenora.Chromium] Removing the tray icon failed", LogLevel.Warning, ex));
                // Its boot work told to stop before the app's services go.
                _splash?.Dispose();
                ui.MarkGone();
                AppCallback.Run(app.Stop, ex => AppCallback.Log(log, () => "[Shenora.Chromium] Stopping the app failed", LogLevel.Error, ex));
                Cef.cef_shutdown();
                // The thread's own context back: left in place, an `async Main` awaiting after Run (an `await using`)
                // posted its continuation to a UI that was gone, and never resumed.
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
        finally
        {
            // A CEF that would not start leaves no splash behind it.
            _splash?.Dispose();
            // Released LAST and explicitly, so a --restarted relaunch waiting on it proceeds the moment shutdown is done.
            single.Release();
        }
    }

    // Never throws: a splash that cannot start costs the splash.
    private void StartSplash(ShenoraApplication app, ChromiumSplashOptions splashOptions)
    {
        try
        {
            var session = new SplashSession(splashOptions, options.Window.Title ?? app.ApplicationName, options.Window.BackgroundColor,
                app.Services, app.Services.GetService<IEventBus>(), () => SplashSurfaces.Create(log), TimeProvider.System, log,
                SystemTheme.IsDark(), app.Services.GetService<IColorScheme>()?.Scheme ?? ColorScheme.System);
            _splash = session;
            if (splash is not null) splash.Session = session;
            windows.Splash = session;
            // Only a card needs to know where the window will open, before CEF can say.
            if (splashOptions.Card is null) session.Start(default, []);
            else
            {
                var workAreas = SplashSurfaces.WorkAreas();
                session.Start(windows.MainWindowPlan(app.Services, workAreas), workAreas);
            }
        }
        catch (Exception ex)
        {
            AppCallback.Log(log, () => "[Shenora.Chromium] The splash could not start; the app starts without it", LogLevel.Error, ex);
            // Lifted, so the main window opens with no splash and no strip waiting on one.
            _splash?.Abort();
        }
    }

    /// <summary>CEF's context exists: the UI thread is usable, the app starts, the main window opens.</summary>
    private void Started(ShenoraApplication app, bool isDevelopment)
    {
        ui.MarkReady();
        try
        {
            app.Start();
            windows.Initialize(app, isDevelopment);
            windows.Open(ChromiumWindows.MainWindowName, options.Window);
            // A tray that cannot be shown costs the tray, never the app.
            AppCallback.Run(() => tray?.Start(), ex => AppCallback.Log(log, () => "[Shenora.Chromium] The tray icon could not be shown", LogLevel.Error, ex));
            // A later launch, through the gate's channel or handed over by CEF: the main window comes to the front, opened
            // again if it was closed, and the app gets the launch's arguments.
            ChromiumSingleInstance.Process.Listen(activation => ui.Post(() =>
            {
                windows.Open(ChromiumWindows.MainWindowName, options.Window);
                options.SingleInstance?.OnActivated?.Invoke(app, activation);
            }), log);
        }
        catch (Exception ex)
        {
            AppCallback.Log(log, () => "[Shenora.Chromium] The app could not start; quitting", LogLevel.Critical, ex);
            _splash?.Abort();
            Cef.cef_quit_message_loop();
        }
    }
}
