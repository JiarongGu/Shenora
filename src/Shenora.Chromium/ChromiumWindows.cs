using System.Net.Http;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Host;
using Shenora.Chromium.Interop;
using Shenora.Chromium.Serving;
using Shenora.Core.Events;
using Shenora.Core.Ipc;

namespace Shenora.Chromium;

/// <summary>
/// The Chromium shell's windows, by name. Every window is CEF's own, on CEF's UI thread, showing a page from
/// the app's origin with its own IPC bridge. Opening a name that is open activates it. The shell quits when
/// the last one closes.
/// </summary>
public sealed unsafe class ChromiumWindows
{
    /// <summary>The window the shell opens at start.</summary>
    public const string MainWindowName = "main";

    private readonly ChromiumHostOptions _options;
    private readonly CefUiDispatcher _ui;
    private readonly IMessageDispatcher _dispatcher;
    private readonly IEventBus? _events;
    private readonly ILogger? _log;
    // Written on CEF's UI thread, read by IsOpen from any thread, so a concurrent collection, not a Dictionary.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ChromiumWindow> _open = new(StringComparer.Ordinal);
    private ChromiumServing? _serving;
    private ChromiumOrigins? _origins;
    private bool _isDevelopment;

    internal ChromiumWindows(ChromiumHostOptions options, CefUiDispatcher ui, IMessageDispatcher dispatcher, IEventBus? events, ILogger? log)
    {
        _options = options;
        _ui = ui;
        _dispatcher = dispatcher;
        _events = events;
        _log = log;
    }

    /// <summary>True while the named window is open.</summary>
    public bool IsOpen(string name) => _open.ContainsKey(name);

    /// <summary>
    /// Open a window, or activate it if the name is open already. Safe from any thread: the work runs on CEF's
    /// UI thread. False when the shell is not running.
    /// </summary>
    /// <param name="name">The window's name.</param>
    /// <param name="options">Its size, title and page.</param>
    public bool Open(string name, ChromiumWindowOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(options);
        return _ui.Post(() => OpenOnUi(name, options));
    }

    /// <summary>Close the named window. No-op when it is not open.</summary>
    /// <param name="name">The window's name.</param>
    public bool Close(string name) => _ui.Post(() => { if (_open.TryGetValue(name, out var w)) w.Close(); });

    /// <summary>Bring the named window to the front. No-op when it is not open.</summary>
    /// <param name="name">The window's name.</param>
    public bool Activate(string name) => _ui.Post(() => { if (_open.TryGetValue(name, out var w)) w.Activate(); });

    /// <summary>The origins and the serving every window shares. UI thread, before the first window.</summary>
    internal void Initialize(ShenoraApplication app, bool isDevelopment)
    {
        _isDevelopment = isDevelopment;
        _origins = ChromiumOrigins.For(_options.VirtualHost, _options.DevUrl, isDevelopment);
        var interceptor = new ChromiumInterceptor();
        app.Pipeline.ApplyTo(interceptor);   // the app's UseFiles and routes reach every window (D64)
        _serving = new ChromiumServing(_options.ContentRoot, _origins, interceptor,
            isDevelopment && _options.DevUrl is not null ? new HttpClient() : null, _log);
    }

    private void OpenOnUi(string name, ChromiumWindowOptions options)
    {
        if (_open.TryGetValue(name, out var existing)) { existing.Activate(); return; }
        if (_serving is null || _origins is null) throw new InvalidOperationException("The Chromium shell has not started.");

        var window = new ChromiumWindow(name, options, _serving, _origins, NewBridge, Closed, _log);
        _open[name] = window;
        // The main window's commands and drop zones, unless the app mapped its own module under a name (it wins).
        if (name == MainWindowName)
        {
            _dispatcher.TryMapModule(new ChromiumWindowCommands(window));
            _dispatcher.TryMapModule(new ChromiumDropZones(window));
        }

        var settings = new _cef_browser_settings_t { size = (nuint)sizeof(_cef_browser_settings_t) };
        if ((options.BackgroundColor ?? _options.Window.BackgroundColor) is { } color) settings.background_color = (uint)color.ToArgb();
        window.Open(PageUrl(options), &settings);
    }

    private Uri PageUrl(ChromiumWindowOptions options)
    {
        var root = _isDevelopment && _options.DevUrl is not null ? new Uri(_options.DevUrl) : _origins!.App;
        return options.Path is { } path ? new Uri(root, path) : root;
    }

    private ChromiumIpcBridge NewBridge(ChromiumWindow window) =>
        new(new ChromiumIpcBridgeOptions { Dispatcher = _dispatcher, EventBus = _events, Shell = _options.Shell, Log = _log },
            _ui, window.Push, (delay, work) => CefTask.PostDelayed(cef_thread_id_t.TID_UI, delay, work));

    private void Closed(ChromiumWindow window)
    {
        _open.TryRemove(window.Name, out _);
        if (_open.Count == 0) Cef.cef_quit_message_loop();
    }
}
