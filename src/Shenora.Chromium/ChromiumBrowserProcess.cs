using Microsoft.Extensions.Logging;
using Shenora.Chromium.Host;
using Shenora.Chromium.Interop;

namespace Shenora.Chromium;

/// <summary>Inputs for <see cref="ChromiumBrowserProcess.Run"/>.</summary>
public sealed class ChromiumBrowserProcessOptions
{
    /// <summary>
    /// Where Chromium keeps this browser's profile (cookies, history, bookmarks and preferences, in its <c>Default</c>
    /// folder), its cache, and its log, <c>cef.log</c>. Required, so the profile is never one of Chromium's choosing
    /// under the user's own. Not a folder another of the app's Chromium processes uses.
    /// </summary>
    public required string UserDataFolder { get; init; }

    /// <summary>
    /// Chrome DevTools' port, open in production (D86): this process holds no app page, so the port reaches only the
    /// browser's own windows. Windows can be made over it (<c>Target.createTarget</c> with <c>newWindow</c>), and a
    /// target made so opens in Chromium's own window. Loopback only. 0 = none.
    /// <para>
    /// It is a relay onto Chromium's own endpoint that passes everything through, except that a new tab is announced as
    /// a <c>page</c> from the start: Chromium says <c>other</c> first, which a client waiting for a page (Playwright's
    /// MCP server, Chrome DevTools') never takes up.
    /// </para>
    /// </summary>
    public int RemoteDebuggingPort { get; init; }

    /// <summary>Keep cookies that have no expiry across a restart, which is how a site's sign-in usually survives one.</summary>
    public bool PersistSessionCookies { get; init; }

    /// <summary>The language of Chromium's own windows and the pages' default, such as <c>zh-CN</c>. Null is the OS's UI
    /// language (on Linux, CEF reads the environment's).</summary>
    public string? Locale { get; init; }

    /// <summary>A page to open a window on as the process starts. Null opens none, and windows come from the debugging
    /// port.</summary>
    public Uri? StartUrl { get; init; }
}

/// <summary>
/// Chromium as a browser, in a process that holds none of the app (D86): Chromium's own windows, with their tabs,
/// address bar, history, find, downloads and devtools, and a debugging port that is open in production because no
/// bridge is in its reach.
/// <para>
/// The app runs it as a second process of its OWN executable: it starts <c>Environment.ProcessPath</c> with an
/// argument of its own, and its <c>Main</c> sees that argument and calls <see cref="Run"/> before anything else. So an
/// install carries one Chromium, and on Windows CEF's launcher sandboxes this process as it does the app.
/// </para>
/// <para>
/// ⚠ A page in this browser is the open web, and Chromium's checks stay on, where the app's own pages turn its
/// local-network checks off (D83).
/// </para>
/// </summary>
public static class ChromiumBrowserProcess
{
    /// <summary>
    /// Run this process as the browser, on the calling thread: the app's main thread, <c>[STAThread]</c> on Windows as
    /// a window's thread is. It blocks until the last window closes, once one has opened, or until
    /// <paramref name="stop"/> fires, which closes every window first. Once per process, and nothing else in it starts
    /// Chromium.
    /// </summary>
    /// <param name="options">The profile's folder, the debugging port and the browser's settings.</param>
    /// <param name="stop">Close every window and return, when the app that started this process has gone.</param>
    /// <param name="log">Diagnostics.</param>
    /// <returns>The code to exit with: 0, a CEF subprocess's own when this process was one, and 1 when the window at
    /// <see cref="ChromiumBrowserProcessOptions.StartUrl"/> could not be made.</returns>
    /// <exception cref="InvalidOperationException">Chromium would not start (the message names its log), the
    /// debugging port is taken, or Chromium already runs in this process: <see cref="ChromiumHostExtensions.UseChromium"/>
    /// starts it as the app is composed (D87), so this is decided first.</exception>
    public static int Run(ChromiumBrowserProcessOptions options, CancellationToken stop = default, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.UserDataFolder))
            throw new ArgumentException($"{nameof(ChromiumBrowserProcessOptions.UserDataFolder)} is required.", nameof(options));
        if (options.RemoteDebuggingPort is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(ChromiumBrowserProcessOptions.RemoteDebuggingPort)} must be 0 to 65535.");
        if (options.StartUrl is { IsAbsoluteUri: false })
            throw new ArgumentException($"{nameof(ChromiumBrowserProcessOptions.StartUrl)} must be absolute.", nameof(options));
        if (ChromiumEarlyStart.Process.HasStarted)
            throw new InvalidOperationException(
                "Chromium already runs in this process: UseChromium started it for the app. Call ChromiumBrowserProcess.Run before composing the app.");

        var client = new BrowserProcessClient(log);
        var exitCode = 0;
        var cefApp = new ChromiumApp(() =>
        {
            if (stop.IsCancellationRequested || options.StartUrl is not { } url) return;
            if (!OpenWindow(client, url))
            {
                AppCallback.Log(log, () => $"[Shenora.Chromium] Browser process: CEF would not open a window on {url}", LogLevel.Error);
                exitCode = 1;
                Cef.cef_quit_message_loop();
            }
        }, client);

        var code = CefStartup.ExecuteIfSubprocess(cefApp, log);
        if (code >= 0) return code;

        // The port clients are given is a relay's, onto the engine's own on a free loopback port: the relay calls a new
        // tab a page from the start, which the engine does only later (CdpRelay). Started first, so a port already in
        // use fails here rather than after Chromium is up.
        CdpRelay? relay = null;
        var enginePort = 0;
        if (options.RemoteDebuggingPort > 0)
        {
            enginePort = FreePort();
            try { relay = CdpRelay.Start(options.RemoteDebuggingPort, enginePort); }
            catch (System.Net.Sockets.SocketException ex)
            {
                throw new InvalidOperationException($"The debugging port {options.RemoteDebuggingPort} is not free.", ex);
            }
        }

        try
        {
            CefStartup.Initialize(cefApp, new CefStartup.Settings(options.UserDataFolder, IsDevelopment: false, DevToolsPort: 0,
                BackgroundColor: null, MultiThreadedLoop: false)
            {
                PagelessDebugPort = enginePort,
                PersistSessionCookies = options.PersistSessionCookies,
                Locale = options.Locale,
                Profile = "Default",
            });
        }
        catch
        {
            relay?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        // Registered once CEF can take a task, which then waits for the loop: a stop that came first closes nothing
        // and quits as the loop starts. Unregistered before CEF shuts down, so no stop posts to a CEF that has gone.
        var registration = stop.Register(() => CefTask.Post(cef_thread_id_t.TID_UI, client.CloseAll));
        try
        {
            Cef.cef_run_message_loop();
        }
        finally
        {
            registration.Dispose();
            relay?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Cef.cef_shutdown();
        }
        return exitCode;
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    // Chrome style with no parent: Chromium's own window, as a tab of it. CEF's UI thread.
    private static unsafe bool OpenWindow(BrowserProcessClient client, Uri url)
    {
        var info = new _cef_window_info_t
        {
            size = (nuint)sizeof(_cef_window_info_t),
            runtime_style = cef_runtime_style_t.CEF_RUNTIME_STYLE_CHROME,
        };
        var settings = new _cef_browser_settings_t { size = (nuint)sizeof(_cef_browser_settings_t) };
        var text = url.AbsoluteUri;
        fixed (char* p = text)
        {
            var s = CefStrings.View(p, text.Length);
            return Cef.cef_browser_host_create_browser(&info, client.ForCef(), &s, &settings, null, null) == 1;
        }
    }
}
