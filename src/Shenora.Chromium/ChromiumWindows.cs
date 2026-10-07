using System.Drawing;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Host;
using Shenora.Chromium.Interop;
using Shenora.Chromium.Serving;
using Shenora.Core.Events;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;

namespace Shenora.Chromium;

/// <summary>
/// The Chromium shell's windows, by name. Every window is CEF's own, on CEF's UI thread, showing a page from
/// the app's origin with its own IPC bridge, and a page's window commands and drop zones act on its own window.
/// Opening a name that is open activates it. The shell quits when the last one closes.
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
    private readonly IUrlLauncher _urls;
    // A name is reserved by Open on any thread, as null, and gets its window on CEF's UI thread, where every later post
    // for it runs after that one. Read by HasWindow from any thread, so a concurrent collection, not a Dictionary.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ChromiumWindow?> _open = new(StringComparer.Ordinal);
    private ChromiumServing? _serving;
    private ChromiumOrigins? _origins;
    private bool _isDevelopment;
    private IWindowStateStore? _windowStore;   // the main window's saved state, when the app keeps it

    internal ChromiumWindows(ChromiumHostOptions options, CefUiDispatcher ui, IMessageDispatcher dispatcher, IEventBus? events, ILogger? log,
        IUrlLauncher urls)
    {
        _options = options;
        _ui = ui;
        _dispatcher = dispatcher;
        _events = events;
        _log = log;
        _urls = urls;
    }

    /// <summary>True while the named window is open (or opening).</summary>
    public bool HasWindow(string name) => _open.ContainsKey(name);

    /// <summary>
    /// Open the named window. Safe from any thread: the work runs on CEF's UI thread. Returns false (and activates the
    /// existing window) when the name is already open, and false when the shell is not running.
    /// </summary>
    /// <param name="name">The window's name.</param>
    /// <param name="options">Its size, title and page.</param>
    public bool Open(string name, ChromiumWindowOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(nameof(options));   // here, since the window itself is made on CEF's thread
        if (name == MainWindowName && options.StateStore is not null)
            throw new ArgumentException($"The main window keeps its state through {nameof(ChromiumHostOptions)}.{nameof(ChromiumHostOptions.WindowState)}.", nameof(options));
        if (Volatile.Read(ref _serving) is null) return false;   // not started: the post would only log
        if (!_open.TryAdd(name, null))
        {
            Activate(name);
            return false;
        }
        if (_ui.Post(() => OpenOnUi(name, options))) return true;
        _open.TryRemove(KeyValuePair.Create(name, (ChromiumWindow?)null));
        return false;
    }

    /// <summary>Close the named window. No-op when it is not open. False when the shell is not running.</summary>
    /// <param name="name">The window's name.</param>
    public bool Close(string name) => _ui.Post(() => WhenOpen(name, w => w.Close()));

    /// <summary>Bring the named window to the front. No-op when it is not open. False when the shell is not
    /// running.</summary>
    /// <param name="name">The window's name.</param>
    public bool Activate(string name) => _ui.Post(() => WhenOpen(name, w => w.Activate()));

    // A name reserved and not yet made — its Open posted from another thread and still queued — takes the request once
    // it is: queued again behind that open, where it was dropped.
    private void WhenOpen(string name, Action<ChromiumWindow> act, int tries = 0)
    {
        if (!_open.TryGetValue(name, out var window)) return;
        if (window is not null) act(window);
        else if (tries < 3) _ui.Queue(() => WhenOpen(name, act, tries + 1));
    }

    /// <summary>Asked, by window name, before a window closes: false hides it instead (the tray's close-to-tray).
    /// UI thread.</summary>
    internal Func<string, bool>? CloseGuard { get; set; }

    /// <summary>The splash over the main window, while the app starts: told as the main window opens, moves, hides or
    /// goes, and when its page is ready.</summary>
    internal SplashSession? Splash { get; set; }

    /// <summary>
    /// Where the main window will open, for a splash that shows before CEF can say: the state it restores (from the same
    /// store the window then uses, against <paramref name="workAreas"/>, the displays' work areas in DIP, primary first),
    /// else only its size, with no place: CEF centres such a window by each OS's own rule (on macOS, above the middle),
    /// so the splash centres itself the same way rather than guess.
    /// </summary>
    internal ChromiumWindowGeometry.Plan MainWindowPlan(IServiceProvider services, IReadOnlyList<Rectangle> workAreas)
    {
        int width = _options.Window.Width, height = _options.Window.Height;
        if (_options.WindowState is { } state)
        {
            _windowStore ??= state.Store(services);
            WindowState? saved = null;
            try { saved = _windowStore.Load(); }
            catch (Exception ex) { AppCallback.Log(_log, () => "[Shenora.Chromium] The window state could not be read for the splash", LogLevel.Warning, ex); }
            return ChromiumWindowGeometry.PlanFor(saved, state.Options ?? new WindowStateOptions(), width, height, workAreas);
        }
        return new(width, height, null, null, false);
    }

    /// <summary>Close every open window; the shell quits as the last one goes. Any thread.</summary>
    internal bool CloseAll() => _ui.Post(() => { foreach (var w in _open.Values) w?.Close(); });

    /// <summary>Is the named window showing? False while it is hidden or not open. UI thread.</summary>
    internal bool IsShowing(string name) => _open.TryGetValue(name, out var w) && w is { IsVisible: true };

    // IUiInteraction's state for the main window, kept so a main window opened while blocked opens blocked.
    private volatile bool _mainEnabled = true;

    /// <summary>Whether the main window takes input: what it opens with, and what the last change asked.</summary>
    internal bool MainEnabled => _mainEnabled;

    /// <summary>Take or give back the main window's input (IUiInteraction). Any thread; applied on the UI thread, in
    /// the order asked.</summary>
    internal void SetMainEnabled(bool enabled)
    {
        _mainEnabled = enabled;
        // The LATEST state, read when the post runs, not the value captured here: Post runs inline on the UI thread, so
        // an unblock there overtook a block posted from elsewhere, which then landed last and left the window disabled.
        _ui.Post(() => { if (_open.TryGetValue(MainWindowName, out var w)) w?.SetEnabled(_mainEnabled); });
    }

    /// <summary>
    /// Show a file dialog over the main window, or over any open window when the main one is not. UI thread.
    /// False when no window is open to own it.
    /// </summary>
    internal bool RunFileDialog(cef_file_dialog_mode_t mode, string title, string folder, string? fileName, IReadOnlyList<string> filters, Action<string[]> done)
    {
        // A VISIBLE window owns it: on macOS the dialog is a sheet on its owner, and on a main window hidden in the tray
        // it was never seen while the page waited on it. With none visible, the main window comes back to show it.
        var main = _open.TryGetValue(MainWindowName, out var named) ? named : null;
        var owner = (main is { IsVisible: true } ? main : null) ?? _open.Values.FirstOrDefault(w => w is { IsVisible: true });
        if (owner is null)
        {
            owner = main ?? _open.Values.FirstOrDefault(w => w is not null);
            if (owner is null) return false;
            owner.Activate();
        }
        owner.Browser.RunFileDialog(mode, title, folder, fileName, filters, done);
        return true;
    }

    /// <summary>The origins and the serving every window shares. UI thread, before the first window.</summary>
    internal void Initialize(ShenoraApplication app, bool isDevelopment)
    {
        _isDevelopment = isDevelopment;
        _windowStore ??= _options.WindowState?.Store(app.Services);   // the splash may have asked for it first
        _origins = ChromiumOrigins.For(_options.VirtualHost, _options.DevUrl, isDevelopment);
        var interceptor = new ChromiumInterceptor();
        app.Pipeline.ApplyTo(interceptor);   // the app's UseFiles and routes reach every window (D64)
        _serving = new ChromiumServing(_options.ContentRoot, _origins, interceptor,
            isDevelopment && _options.DevUrl is not null ? new HttpClient() : null, _log);
        // Chromium takes ~140 ms after navigation starts to route the first request here, and .NET then spends ~50 ms on
        // first calls serving it (measured). Serving the root document once now, off the UI thread, spends them while
        // Chromium is busy; the answer is discarded.
        var serving = _serving;
        var root = _origins.App;
        _ = Task.Run(() => AppCallback.Run(() => serving.Warm(root)));
        // Every page's window commands and drop zones, mapped ONCE: each acts on the page that asked, and its
        // window. An app that mapped its own SHENORA.WINDOW wins. The drop zones are under the engine's own name,
        // where each page's requests are addressed, so they always answer this engine's pages: their protocol is
        // not the WebView2 module's.
        _dispatcher.TryMapModule(new ChromiumWindowCommands(() => ChromiumBrowserContext.Current?.Host as ChromiumWindow, () => Splash));
        _dispatcher.TryMapModule(new ChromiumDropZones(() => ChromiumBrowserContext.Current));
    }

    private void OpenOnUi(string name, ChromiumWindowOptions options)
    {
        ChromiumWindow? window = null;
        try
        {
            if (_serving is null || _origins is null) throw new InvalidOperationException("The Chromium shell has not started.");
            window = new ChromiumWindow(name, options, _serving, _origins, NewBridge, Closed, _log, _urls,
                w => CloseGuard?.Invoke(w.Name) ?? true, GeometryFor(name, options));
            if (name == MainWindowName && Splash is { } splash)
            {
                window.Opening = splash.WindowOpened;
                window.Moved = splash.OwnerMoved;
                window.Hidden = splash.Abort;   // the tray's close: a splash left over the desktop would cover it
            }
            _open[name] = window;

            var settings = new _cef_browser_settings_t { size = (nuint)sizeof(_cef_browser_settings_t) };
            if ((options.BackgroundColor ?? _options.Window.BackgroundColor) is { } color) settings.background_color = (uint)color.ToArgb();
            window.Open(PageUrl(options), &settings);
            if (name == MainWindowName && !_mainEnabled) window.SetEnabled(false);
        }
        catch
        {
            // A window CEF would not make frees its name, and a shell left with none quits rather than wait for a
            // window that will never close.
            _open.TryRemove(name, out _);
            window?.Browser.Bridge.Dispose();
            if (_open.IsEmpty) Cef.cef_quit_message_loop();
            throw;
        }
    }

    /// <summary>A window keeps its size and place across launches when the app has a store for it: the main window's
    /// from the host's options, any other's from its own. Null when it has none.</summary>
    internal ChromiumWindowGeometry? GeometryFor(string name, ChromiumWindowOptions options) =>
        name == MainWindowName
            ? _windowStore is { } store ? new ChromiumWindowGeometry(store, _options.WindowState!.Options ?? new WindowStateOptions(), _log) : null
            : options.StateStore is { } own ? new ChromiumWindowGeometry(own, options.StateOptions ?? new WindowStateOptions(), _log) : null;

    private Uri PageUrl(ChromiumWindowOptions options)
    {
        var root = _isDevelopment && _options.DevUrl is not null ? new Uri(_options.DevUrl) : _origins!.App;
        return options.Path is { } path ? new Uri(root, path) : root;
    }

    internal ChromiumIpcBridge NewBridge(ChromiumBrowser browser) =>
        new(new ChromiumIpcBridgeOptions
            {
                Dispatcher = _dispatcher, EventBus = _events, Shell = _options.Shell, Log = _log,
                EnterWindow = () => ChromiumBrowserContext.Enter(browser),
                OnClientReady = browser.Name == MainWindowName ? () => Splash?.PageReady() : null,
            },
            _ui, browser.Push, (delay, work) => CefTask.PostDelayed(cef_thread_id_t.TID_UI, delay, work));

    private void Closed(ChromiumWindow window)
    {
        if (window.Name == MainWindowName) Splash?.Abort();
        _open.TryRemove(KeyValuePair.Create(window.Name, (ChromiumWindow?)window));
        if (_open.Count == 0) Cef.cef_quit_message_loop();
    }
}
