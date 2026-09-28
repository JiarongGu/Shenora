using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Host;
using Shenora.Chromium.Interop;
using Shenora.Chromium.Serving;
using Shenora.Core.Events;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;

namespace Shenora.Chromium;

/// <summary>
/// Inputs for <see cref="ChromiumEngine"/>. The names match <see cref="ChromiumHostOptions"/> where the concept is the
/// same: where the pages come from is the app's, whichever host shows them.
/// </summary>
public sealed class ChromiumEngineOptions
{
    /// <summary>
    /// The folder the app's bundle is served from, at <c>https://{VirtualHost}/</c>. Nothing outside it is reachable.
    /// Required unless every page comes from <see cref="DevUrl"/>.
    /// </summary>
    public string? ContentRoot { get; init; }

    /// <summary>
    /// The pages in development (a dev server such as Vite). Set, it also turns off Chromium's local-network check on
    /// WebSockets, which would otherwise refuse the dev server's hot-reload socket (D83). Ignored outside development.
    /// </summary>
    public string? DevUrl { get; init; }

    /// <summary>The host of the app's own origin. The pages are served from <c>https://{VirtualHost}/</c>.</summary>
    public string VirtualHost { get; init; } = "app.local";

    /// <summary>Development: CEF reads command-line switches, and <see cref="DevToolsPort"/> opens. Null = the app's
    /// environment.</summary>
    public bool? IsDevelopment { get; init; }

    /// <summary>Chrome DevTools' port, in development only. 0 = none.</summary>
    public int DevToolsPort { get; init; }

    /// <summary>Where CEF keeps its cache, profile and log. Null = the app's data area <c>chromium</c>.</summary>
    public string? CachePath { get; init; }

    /// <summary>What the ready handshake tells each page this host is and can do (D36).</summary>
    public ShellInfo? Shell { get; init; }
}

/// <summary>
/// Chromium for a host that owns its own UI thread, a WinForms app above all (D83): CEF runs its message loop on a
/// thread of its own, the host's thread stays the host's, and each page's IPC is dispatched on it. Pages are
/// <see cref="ChromiumChildBrowser"/>s. The Chromium shell (<c>UseChromium</c>) does not use this; it runs CEF's loop
/// on the main thread itself.
/// <para>
/// ⚠ <b>Three calls, in order, on the app's main thread.</b> <see cref="RunIfSubprocess"/> first, before anything else
/// the app does; <see cref="Start"/> once, before any browser; and <see cref="Stop"/> last, once every browser has
/// closed, on the thread that called <see cref="Start"/>. CEF cannot start again in the same process.
/// </para>
/// </summary>
public sealed class ChromiumEngine
{
    private readonly ChromiumEngineOptions _options;
    private readonly ILogger<ChromiumEngine>? _log;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new();
    private ChromiumApp? _cefApp;
    private ShenoraApplication? _app;
    private bool _isDevelopment;
    private ChromiumPages? _pages;
    private readonly HashSet<ChromiumChildBrowser> _browsers = [];
    private int _state;   // 0 new, 1 running, 2 stopped

    /// <summary>How long <see cref="Stop"/> waits for browsers still closing.</summary>
    internal static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(5);

    /// <param name="options">Where the pages come from, where CEF keeps its cache, and development settings.</param>
    /// <param name="log">Diagnostics.</param>
    public ChromiumEngine(ChromiumEngineOptions? options = null, ILogger<ChromiumEngine>? log = null)
    {
        _options = options ?? new ChromiumEngineOptions();
        _log = log;
    }

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool IsRunning => Volatile.Read(ref _state) == 1;

    /// <summary>Completes once CEF's context exists, when a browser can be created. Faults when CEF would not
    /// start.</summary>
    public Task Ready => _ready.Task;

    /// <summary>
    /// When THIS process is one of CEF's subprocesses, run as that and return true: the caller exits at once. False in
    /// the app itself. With the build's layout it is always false: started through CEF's launcher the kit's shim runs
    /// every subprocess without .NET, and started without it (<c>dotnet &lt;App&gt;.App.dll</c>, as an IDE may) CEF
    /// is pointed at that launcher for them. It matters for an app laid out some other way that runs an exe of its own
    /// (an apphost), which CEF then starts for each subprocess.
    /// <para>
    /// ⚠ Call it before anything else the app does. A subprocess that went on to run the app would, among other
    /// things, meet the app's single-instance gate and exit without ever rendering.
    /// </para>
    /// </summary>
    /// <param name="exitCode">The subprocess's exit code, when it was one.</param>
    public static bool RunIfSubprocess(out int exitCode)
    {
        exitCode = CefStartup.ExecuteIfSubprocess(new ChromiumApp(() => { }), log: null);
        return exitCode >= 0;
    }

    /// <summary>Start CEF on a message loop of its own. Once, on the app's main thread, before any browser.</summary>
    /// <param name="app">The app: its data area and environment, and the services each page's IPC uses (its
    /// <see cref="IMessageDispatcher"/> and <see cref="IUiDispatcher"/>, and when registered its
    /// <see cref="IEventBus"/> and <see cref="IUrlLauncher"/>).</param>
    /// <exception cref="InvalidOperationException">Started twice, or CEF would not start (the message names its log).</exception>
    public void Start(ShenoraApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) throw new InvalidOperationException("The Chromium engine has already been started.");
        _app = app;
        _isDevelopment = _options.IsDevelopment ?? app.Environment.IsDevelopment;
        _cefApp = new ChromiumApp(() => _ready.TrySetResult(), devServer: _isDevelopment && _options.DevUrl is not null);
        try
        {
            CefStartup.Initialize(_cefApp, new CefStartup.Settings(
                _options.CachePath ?? app.Paths.DataArea("chromium"), _isDevelopment, _options.DevToolsPort, BackgroundColor: null,
                MultiThreadedLoop: true));
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _state, 2);
            _ready.TrySetException(ex);
            throw;
        }
        // RunIfSubprocess has no logger to say it, and an app started as `dotnet <App>.App.dll` should hear it.
        if (CefStartup.Sandbox == 0)
            AppCallback.Log(_log, () => "[Shenora.Chromium] Running without CEF's bootstrap launcher: Chromium's sandbox is OFF", LogLevel.Warning);
    }

    /// <summary>
    /// Shut CEF down, on the thread that called <see cref="Start"/>, once its windows are gone. A browser still closing,
    /// as one does just after its parent window was destroyed, is waited for, a few seconds at most. Idempotent, and a
    /// no-op when the engine never started.
    /// <para>
    /// ⚠ It closes no browser itself: close each first (<see cref="ChromiumChildBrowser.Dispose"/>, or by destroying its
    /// parent).
    /// </para>
    /// </summary>
    public void Stop()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 1) != 1) return;
        // Not closed here, by design rather than measurement: a child window's destruction notifies its parent
        // (WM_PARENTNOTIFY), whose thread is very likely the one blocked in this call.
        Task[] closing;
        lock (_lock) closing = [.. _browsers.Select(browser => browser.Closed)];
        if (closing.Length > 0 && !Task.WaitAll(closing, CloseWait))
            AppCallback.Log(_log, () => $"[Shenora.Chromium] Shutting the Chromium engine down with {closing.Count(t => !t.IsCompleted)} browser(s) still open", LogLevel.Warning);
        else
            AppCallback.Log(_log, () => "[Shenora.Chromium] Shutting the Chromium engine down");
        Cef.cef_shutdown();
    }

    /// <summary>
    /// What every page shares, built as the first browser is made: the origins, the serving with the app's pipeline
    /// applied (which freezes it, D64), the services each page's IPC uses, and the modules that act on the page that
    /// asked. Any thread.
    /// </summary>
    internal ChromiumPages Pages()
    {
        lock (_lock)
        {
            if (_pages is not null) return _pages;
            if (!IsRunning || _app is null) throw new InvalidOperationException("The Chromium engine is not running: call Start first.");

            var origins = ChromiumOrigins.For(_options.VirtualHost, _options.DevUrl, _isDevelopment);
            var interceptor = new ChromiumInterceptor();
            _app.Pipeline.ApplyTo(interceptor);
            var serving = new ChromiumServing(_options.ContentRoot, origins, interceptor,
                _isDevelopment && _options.DevUrl is not null ? new HttpClient() : null, _log);
            var services = _app.Services;
            var dispatcher = services.GetRequiredService<IMessageDispatcher>();
            // Mapped ONCE, acting on the page that asked, under the engine's own name: the app's WebView2 module, if it
            // maps one, keeps the page's name for the WebView2 pages.
            dispatcher.TryMapModule(new ChromiumDropZones(() => ChromiumBrowserContext.Current));
            var root = _isDevelopment && _options.DevUrl is not null ? new Uri(_options.DevUrl) : origins.App;
            return _pages = new ChromiumPages(origins, serving, root, dispatcher, services.GetRequiredService<IUiDispatcher>(),
                services.GetService<IEventBus>(), services.GetService<IUrlLauncher>(), _options.Shell, _log);
        }
    }

    internal void BrowserOpened(ChromiumChildBrowser browser) { lock (_lock) _browsers.Add(browser); }

    internal void BrowserClosed(ChromiumChildBrowser browser) { lock (_lock) _browsers.Remove(browser); }
}

/// <summary>What every page of a <see cref="ChromiumEngine"/> shares. <c>Root</c> is where a page's path is resolved:
/// the dev server in development, else the app's origin.</summary>
internal sealed record ChromiumPages(ChromiumOrigins Origins, ChromiumServing Serving, Uri Root, IMessageDispatcher Dispatcher,
    IUiDispatcher Ui, IEventBus? Events, IUrlLauncher? Urls, ShellInfo? Shell, ILogger? Log);
