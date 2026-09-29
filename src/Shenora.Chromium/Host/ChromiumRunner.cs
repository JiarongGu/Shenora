using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;

namespace Shenora.Chromium.Host;

/// <summary>
/// Runs the app on CEF's message loop, on the main thread (D83's Views model): CEF started as
/// <see cref="CefStartup"/> starts it, the app's hooks once CEF's context exists, and after the loop the hooks stop
/// before CEF shuts down.
/// </summary>
internal sealed unsafe class ChromiumRunner(ChromiumHostOptions options, CefUiDispatcher ui, ChromiumWindows windows, ChromiumTray? tray = null,
    ILogger<ChromiumRunner>? log = null)
    : IShenoraRunner
{
    public void Run(ShenoraApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var isDevelopment = options.IsDevelopment ?? app.Environment.IsDevelopment;
        var cefApp = new ChromiumApp(() => Started(app, isDevelopment));

        var code = CefStartup.ExecuteIfSubprocess(cefApp, log);
        if (code >= 0) { Environment.Exit(code); return; }

        CefStartup.Initialize(cefApp, new CefStartup.Settings(
            options.UserDataFolder ?? app.Paths.DataArea("chromium"), isDevelopment, options.DevToolsPort,
            options.Window.BackgroundColor is { } color ? (uint)color.ToArgb() : null, MultiThreadedLoop: false));

        SynchronizationContext.SetSynchronizationContext(new CefUiContext(ui, log));
        try
        {
            Cef.cef_run_message_loop();
        }
        finally
        {
            // Still on CEF's UI thread, where the icon was made; before CEF goes, or it lingers until hovered.
            AppCallback.Run(() => tray?.Stop(), ex => AppCallback.Log(log, () => "[Shenora.Chromium] Removing the tray icon failed", LogLevel.Warning, ex));
            ui.MarkGone();
            AppCallback.Run(app.Stop, ex => AppCallback.Log(log, () => "[Shenora.Chromium] Stopping the app failed", LogLevel.Error, ex));
            Cef.cef_shutdown();
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
        }
        catch (Exception ex)
        {
            AppCallback.Log(log, () => "[Shenora.Chromium] The app could not start; quitting", LogLevel.Critical, ex);
            Cef.cef_quit_message_loop();
        }
    }
}
