using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;
using Shenora.Chromium.Serving;
using Shenora.Core.Shell;
using Shenora.Core.WebView;

namespace Shenora.Chromium.Host;

/// <summary>What only the window hosting a page's browser can do; the browser asks it.</summary>
internal unsafe interface IChromiumBrowserHost
{
    /// <summary>The page's <c>-webkit-app-region</c> areas changed. CEF's UI thread.</summary>
    void DraggableRegionsChanged(nuint count, _cef_draggable_region_t* regions);

    /// <summary>The page's title changed. CEF's UI thread.</summary>
    void TitleChanged(string title);

    /// <summary>The main frame started a new document, after the browser's own per-page resets. UI thread.</summary>
    void DocumentStarted();

    /// <summary>The browser exists. CEF's UI thread.</summary>
    void BrowserCreated();

    /// <summary>
    /// CEF is ready to close the browser (<c>do_close</c>). True when the host closes the browser's window itself;
    /// false has CEF ask the browser's top-level window to close (<c>WM_CLOSE</c>), which a window CEF owns answers.
    /// CEF's UI thread.
    /// </summary>
    bool CloseRequested();

    /// <summary>The browser has closed. CEF's UI thread.</summary>
    void BrowserClosed();

    /// <summary>The keyboard focus is leaving the page: Tab past its last element (<paramref name="forward"/>) or
    /// Shift+Tab past its first. A host with other controls moves the focus to its next one. CEF's UI thread.</summary>
    void MoveFocusRequested(bool forward);
}

/// <summary>
/// One page's browser, whatever window hosts it: the client and every handler CEF asks, the page's IPC bridge,
/// its serving and routing, drops, file dialogs, permissions, popups and crash recovery. A host window
/// (<see cref="ChromiumWindow"/> on CEF's Views) creates the browser from <see cref="ClientForCef"/> and answers
/// <see cref="IChromiumBrowserHost"/>. Everything here runs on CEF's UI thread except the resource callbacks, which
/// CEF raises on its IO thread, and what is marked "any thread": a host that owns its UI thread dispatches the
/// page's IPC there.
/// <para>
/// Lifetime follows CEF's C API rules: a struct CEF returns or passes into a callback carries a reference that is
/// released (or kept, deliberately, as the browser is), and every struct handed TO CEF is handed with a reference
/// added.
/// </para>
/// </summary>
internal sealed unsafe class ChromiumBrowser
{
    private readonly ILogger? _log;
    private readonly IUrlLauncher? _urls;
    private readonly ChromiumPageHooks? _hooks;
    private readonly Client _client;
    private readonly RendererRecovery _recovery;
    private _cef_browser_t* _browser;
    // Written on the UI thread, read on CEF's IO thread by the IPC ownership check.
    private volatile int _browserId;
    private string[] _draggedFiles = [];

    /// <param name="name">The page's name, in the log.</param>
    /// <param name="serving">The app's origin: the bundle, the interceptor pipeline, the dev server.</param>
    /// <param name="origins">The app's origins, which the IPC route and the permissions answer.</param>
    /// <param name="bridge">Builds this page's IPC bridge.</param>
    /// <param name="log">Diagnostics.</param>
    /// <param name="urls">Where popups go; none drops them.</param>
    /// <param name="hooks">The app's answers to downloads, permissions and a renderer's end; none leaves the defaults.</param>
    /// <param name="allowZoom">The user may zoom the page; false, the default, keeps every zoom input from it (<see cref="ChromiumZoom"/>).</param>
    public ChromiumBrowser(string name, ChromiumServing serving, ChromiumOrigins origins, Func<ChromiumBrowser, ChromiumIpcBridge> bridge,
        ILogger? log, IUrlLauncher? urls = null, ChromiumPageHooks? hooks = null, bool allowZoom = false)
    {
        AllowZoom = allowZoom;
        Name = name;
        Serving = serving;
        Origins = origins;
        _log = log;
        _urls = urls;
        _hooks = hooks;
        Bridge = bridge(this);
        _client = new Client(this);
        _recovery = new RendererRecovery(message => AppCallback.Log(_log, () => $"[Shenora.Chromium] Window '{Name}': {message}", LogLevel.Warning));
    }

    public string Name { get; }
    public bool AllowZoom { get; }
    public ChromiumServing Serving { get; }

    /// <summary>Told when one of this page's loads found nothing and the not-found page was answered. The main window's
    /// lifts its splash, held or not: that page never sends the ready handshake, nor calls <c>closeSplash()</c>. Set
    /// before the browser exists; read on CEF's IO thread.</summary>
    public Action? PageMissed { get; set; }

    /// <summary>Serve one of this page's requests on <paramref name="route"/>.</summary>
    internal Task<WebViewResourceResponse> ServeAsync(ChromiumRoute route, WebViewResourceRequest request, CancellationToken cancellationToken) =>
        Serving.ServeAsync(route, request, cancellationToken, PageMissed);
    public ChromiumOrigins Origins { get; }
    public ChromiumIpcBridge Bridge { get; }
    public ILogger? Log => _log;

    /// <summary>The window hosting this browser, which answers what only a window can.</summary>
    public IChromiumBrowserHost? Host { get; set; }

    // The page's drop zones (ChromiumDropZones), any thread: the page's requests run where the IPC dispatches, and
    // a new document clears them on CEF's.
    private readonly HashSet<string> _dropZones = new(StringComparer.Ordinal);

    public void AddDropZone(string zoneId) { lock (_dropZones) _dropZones.Add(zoneId); }
    public void RemoveDropZone(string zoneId) { lock (_dropZones) _dropZones.Remove(zoneId); }
    public bool HasDropZone(string zoneId) { lock (_dropZones) return _dropZones.Contains(zoneId); }

    /// <summary>The browser's own window while the browser exists, else 0: a child of the host's window, for a host
    /// that places it. Any thread.</summary>
    public nint WindowHandle => Volatile.Read(ref _windowHandle);

    private nint _windowHandle;

    /// <summary>Close the browser; <see cref="IChromiumBrowserHost.BrowserClosed"/> follows. Any thread: it runs on
    /// CEF's UI thread, where the browser is only ever touched. False when CEF is gone.</summary>
    /// <param name="force">Skip the page's <c>beforeunload</c>.</param>
    public bool CloseBrowser(bool force) => OnBrowserHost(host => host->close_browser(host, force ? 1 : 0));

    /// <summary>Give the page the keyboard focus. Any thread.</summary>
    public bool Focus() => OnBrowserHost(host => host->set_focus(host, 1));

    private bool OnBrowserHost(BrowserHostAction work) => CefTask.Post(cef_thread_id_t.TID_UI, () =>
    {
        if (_browser == null) return;
        using var host = new CefRef<_cef_browser_host_t>(_browser->get_host(_browser));
        if (!host.IsNull) work(host.Ptr);
    });

    private delegate void BrowserHostAction(_cef_browser_host_t* host);

    /// <summary>The client to create this browser with, with the reference CEF takes added. Once.</summary>
    public _cef_client_t* ClientForCef() => _client.ForCef();

    private int _retired;

    /// <summary>
    /// Give up this browser's own reference to its client, once CEF has closed the browser or would not create it:
    /// CEF frees the client, and its handlers, as it lets go of its own. Kept, the client's handle would root this
    /// browser for the life of the process. Idempotent.
    /// </summary>
    public void Retire()
    {
        if (Interlocked.Exchange(ref _retired, 1) == 0) _client.Release();
    }

    /// <summary>
    /// The main frame started a new document, and everything the old page set up goes with it: whoever handshook
    /// can no longer receive (<c>ContentLoading</c>'s counterpart), and its drop zones and drag would otherwise keep
    /// answering for a page that never made them; then the host's own (the window's caption buttons). The new page
    /// sends its own. UI thread.
    /// </summary>
    public void DocumentStarted()
    {
        Bridge.DocumentReplaced();
        Interlocked.Exchange(ref _draggedFiles, []);
        lock (_dropZones) _dropZones.Clear();
        Host?.DocumentStarted();
    }

    /// <summary>True while the page's own browser is this id: the IPC route answers no other.</summary>
    public bool IsOwnBrowser(_cef_browser_t* browser) => browser != null && _browserId != 0 && browser->get_identifier(browser) == _browserId;

    /// <summary>
    /// The paths of the files in the drag that last entered this page, handed out ONCE: the page's drop asks for
    /// them, and a later drop with no new drag must not receive them again. Any thread.
    /// </summary>
    public string[] TakeDraggedFiles() => Interlocked.Exchange(ref _draggedFiles, []);

    /// <summary>A drag carrying these files entered the page. Any thread.</summary>
    public void FilesDraggedIn(string[] files) => Interlocked.Exchange(ref _draggedFiles, files);

    /// <summary>Run <see cref="ChromiumTransport.PushScript"/> in the page's main frame. UI thread.</summary>
    public void Push(string message)
    {
        if (_browser == null) return;
        using var frame = new CefRef<_cef_frame_t>(_browser->get_main_frame(_browser));
        if (frame.IsNull) return;
        var code = ChromiumTransport.PushScript(message);
        fixed (char* c = code)
        {
            var script = CefStrings.View(c, code.Length);
            var empty = default(_cef_string_utf16_t);
            frame.Ptr->execute_java_script(frame.Ptr, &script, &empty, 0);
        }
    }

    /// <summary>
    /// Show CEF's own file dialog, native on each OS, over this page's window, in <paramref name="folder"/> and with a
    /// save's <paramref name="fileName"/> filled in. UI thread. <paramref name="done"/> gets the picked paths, none when
    /// the user cancelled or the page's browser is gone.
    /// </summary>
    public void RunFileDialog(cef_file_dialog_mode_t mode, string title, string folder, string? fileName, IReadOnlyList<string> filters, Action<string[]> done)
    {
        if (_browser == null) { done([]); return; }
        using var host = new CefRef<_cef_browser_host_t>(_browser->get_host(_browser));
        if (host.IsNull) { done([]); return; }
        StartIn(host.Ptr, folder);
        var defaultPath = string.IsNullOrWhiteSpace(fileName) ? folder : Path.Combine(folder, fileName);
        var list = Cef.cef_string_list_alloc();
        try
        {
            foreach (var filter in filters)
                fixed (char* f = filter)
                {
                    var s = CefStrings.View(f, filter.Length);
                    Cef.cef_string_list_append(list, &s);
                }
            var callback = new FileDialogCallback(done);
            var given = callback.ForCef();
            callback.Release();
            fixed (char* t = title)
            fixed (char* p = defaultPath)
            {
                var titleString = CefStrings.View(t, title.Length);
                var pathString = CefStrings.View(p, defaultPath.Length);
                host.Ptr->run_file_dialog(host.Ptr, mode, &titleString, &pathString, list, given);
            }
        }
        finally { Cef.cef_string_list_free(list); }
    }

    // CEF passes none of a dialog's default path on but a save's file name: Chromium starts every dialog in the
    // profile's last-selected directory, so the folder goes there, where it is read.
    private void StartIn(_cef_browser_host_t* host, string folder)
    {
        using var context = new CefRef<_cef_request_context_t>(host->get_request_context(host));
        if (context.IsNull) return;
        var preferences = &context.Ptr->@base;
        var value = Cef.cef_value_create();
        var error = default(_cef_string_utf16_t);
        const string name = "selectfile.last_directory";
        fixed (char* f = folder)
        fixed (char* n = name)
        {
            var folderString = CefStrings.View(f, folder.Length);
            var nameString = CefStrings.View(n, name.Length);
            value->set_string(value, &folderString);
            // The call consumes the value's one reference.
            if (preferences->set_preference(preferences, &nameString, value, &error) != 1)
            {
                var reason = CefStrings.Read(&error);
                AppCallback.Log(_log, () => $"[Shenora.Chromium] The file dialog cannot start in its folder: {reason}", LogLevel.Warning);
            }
        }
        Cef.cef_string_utf16_clear(&error);
    }

    // ── what CEF reports, delegated here ──────────────────────────────────────────────────────────────

    private void BrowserCreated(_cef_browser_t* browser)
    {
        _browser = browser;   // kept until the browser closes
        _browserId = browser->get_identifier(browser);
        using (var host = new CefRef<_cef_browser_host_t>(browser->get_host(browser)))
            if (!host.IsNull) Volatile.Write(ref _windowHandle, (nint)host.Ptr->get_window_handle(host.Ptr));   // per OS: HWND, X11 id, NSView*
        if (_hooks is { HandlesAuth: true } && _auth is null)
            using (var host = new CefRef<_cef_browser_host_t>(browser->get_host(browser)))
                if (!host.IsNull) _auth = new ChromiumPageAuth(host.Ptr, _hooks, Name, _log);
        if (!AllowZoom) GuardZoom(browser);
        Bridge.Start();
        Host?.BrowserCreated();
    }

    // The page's HTTP challenges, heard through the browser's DevTools only when the app set OnAuthRequest.
    private ChromiumPageAuth? _auth;

    // Chromium keeps a page's zoom per host in the profile, so a zoom an earlier run allowed (or a person made before the app
    // stopped allowing it) comes back with the page: a page that may not be zoomed is brought back to the default as it loads.
    private static void ResetZoom(_cef_browser_t* browser)
    {
        using var host = new CefRef<_cef_browser_host_t>(browser->get_host(browser));
        if (!host.IsNull && host.Ptr->get_zoom_level(host.Ptr) != 0) host.Ptr->set_zoom_level(host.Ptr, 0);
    }

    // The zoom guard's script is registered through the browser's DevTools, in process, only when the app has not allowed zoom
    // (ChromiumZoom): the channel is kept for the browser's life, as the registration ends with it.
    private DevToolsChannel? _zoomGuard;

    private void GuardZoom(_cef_browser_t* browser)
    {
        using var host = new CefRef<_cef_browser_host_t>(browser->get_host(browser));
        if (host.IsNull) return;
        var channel = _zoomGuard = new DevToolsChannel(host.Ptr);
        _ = channel.CallAsync("Page.addScriptToEvaluateOnNewDocument", ChromiumZoom.AddScriptParameters()).ContinueWith(
            t => AppCallback.Log(_log, () => $"[Shenora.Chromium] Window '{Name}': the zoom guard could not be set up, so the wheel and a pinch may zoom the page", LogLevel.Warning, t.Exception?.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        ChromiumZoom.GuardFrames(channel, ex => AppCallback.Log(_log, () => $"[Shenora.Chromium] Window '{Name}': a frame of another site could not be given the zoom guard, so its wheel may zoom the page", LogLevel.Warning, ex));
    }

    // Asked once: the page's first paint, from Chromium itself through the in-process DevTools protocol (no port), and
    // two animation frames after it, by which time the frame is on screen (measured: the paint event lands within
    // ~35 ms of the pixels). The channel goes after: nothing else here needs it.
    private DevToolsChannel? _paintWatch;

    /// <summary>Run <paramref name="painted"/> once the page has painted and two frames have followed. UI thread, once
    /// the browser exists.</summary>
    internal void WatchFirstPaint(Action painted)
    {
        if (_browser == null || _paintWatch is not null) return;
        using var host = new CefRef<_cef_browser_host_t>(_browser->get_host(_browser));
        if (host.IsNull) return;
        var channel = _paintWatch = new DevToolsChannel(host.Ptr);
        IDisposable? subscription = null;
        subscription = channel.Subscribe("Page.lifecycleEvent", json =>
        {
            if (!json.Contains("\"name\":\"firstPaint\"", StringComparison.Ordinal) || subscription is null) return;
            subscription.Dispose();
            subscription = null;
            _ = channel.CallAsync("Runtime.evaluate",
                    "{\"expression\":\"new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r(true))))\",\"awaitPromise\":true}")
                .ContinueWith(_ => CefTask.Post(cef_thread_id_t.TID_UI, () =>
                {
                    StopWatchingPaint();
                    AppCallback.Run(painted);
                }), TaskScheduler.Default);
        });
        _ = channel.CallAsync("Page.enable", "{}");
        _ = channel.CallAsync("Page.setLifecycleEventsEnabled", "{\"enabled\":true}");
    }

    private void StopWatchingPaint()
    {
        _paintWatch?.Dispose();
        _paintWatch = null;
    }

    /// <summary>Whether a navigation CEF reports from a tab asks for another tab or window, which the shell never opens itself.</summary>
    internal static bool OpensAnotherTabOrWindow(cef_window_open_disposition_t disposition) =>
        disposition is cef_window_open_disposition_t.CEF_WOD_NEW_FOREGROUND_TAB or cef_window_open_disposition_t.CEF_WOD_NEW_BACKGROUND_TAB
            or cef_window_open_disposition_t.CEF_WOD_NEW_WINDOW or cef_window_open_disposition_t.CEF_WOD_NEW_POPUP;

    // A window the page or the user asked for: never a bare Chromium window onto whatever was named. The app's hook may take it;
    // otherwise an http/https URL goes to the user's browser, as on the WebView2 shell, and anything else is dropped.
    private void RoutePopup(string url, string frameName, bool userGesture, cef_window_open_disposition_t disposition, _cef_popup_features_t? features)
    {
        if (_hooks?.Popup(url, frameName, userGesture, disposition, features, Log) == true) return;   // the app took it
        if (_urls is null) return;
        try { _urls.OpenUrl(url); }
        catch (ArgumentException) { AppCallback.Log(Log, () => $"[Shenora.Chromium] Window '{Name}': dropped a popup to a non-web URL"); }
    }

    private void BrowserClosing()
    {
        StopWatchingPaint();
        _auth?.Dispose();
        _auth = null;
        _zoomGuard?.Dispose();
        _zoomGuard = null;
        if (_browser == null) return;
        using (new CefRef<_cef_browser_t>(_browser))
        {
            _browser = null;
            _browserId = 0;
            Volatile.Write(ref _windowHandle, 0);
        }
        Host?.BrowserClosed();
        Retire();
    }

    // From the renderer-terminated callback. The reload is posted, so it runs after CEF has finished reporting
    // the dead renderer.
    private void RendererTerminated(cef_termination_status_t status, int errorCode)
    {
        Bridge.RendererGone();
        if (_hooks?.ProcessFailed(status, errorCode, _log) == true) return;
        if (!_recovery.ShouldReload(DateTime.UtcNow)) return;
        CefTask.Post(cef_thread_id_t.TID_UI, () => { if (_browser != null) _browser->reload(_browser); });
    }

    // As an external drag enters: the only moment the engine hands over real paths, because the page's own drop
    // event carries `File` objects with none.
    private void DragEntered(_cef_browser_t* browser, _cef_drag_data_t* data)
    {
        Interlocked.Exchange(ref _draggedFiles, []);
        if (!IsOwnBrowser(browser) || data == null || data->is_file(data) != 1) return;
        var list = Cef.cef_string_list_alloc();
        try
        {
            if (data->get_file_paths(data, list) != 1) return;
            var count = (int)Cef.cef_string_list_size(list);
            var files = new string[count];
            for (var i = 0; i < count; i++)
            {
                var value = default(_cef_string_utf16_t);
                Cef.cef_string_list_value(list, (nuint)i, &value);
                files[i] = CefStrings.Read(&value);
                Cef.cef_string_utf16_clear(&value);
            }
            FilesDraggedIn(files);
        }
        finally { Cef.cef_string_list_free(list); }
    }

    // ── the structs CEF sees ──────────────────────────────────────────────────────────────────────────

    private sealed class FileDialogCallback : CefObject<_cef_run_file_dialog_callback_t>
    {
        private readonly Action<string[]> _done;

        public FileDialogCallback(Action<string[]> done)
        {
            _done = done;
            Struct->on_file_dialog_dismissed = &Dismissed;
        }

        [UnmanagedCallersOnly]
        private static void Dismissed(_cef_run_file_dialog_callback_t* self, _cef_string_list_t* paths)
        {
            var files = new string[paths == null ? 0 : (int)Cef.cef_string_list_size(paths)];
            for (var i = 0; i < files.Length; i++)
            {
                var value = default(_cef_string_utf16_t);
                Cef.cef_string_list_value(paths, (nuint)i, &value);
                files[i] = CefStrings.Read(&value);
                Cef.cef_string_utf16_clear(&value);
            }
            var done = From<FileDialogCallback>(self)._done;
            AppCallback.Run(() => done(files));
        }
    }

    private sealed class Client : CefObject<_cef_client_t>
    {
        private readonly Requests _requests;
        private readonly LifeSpan _lifeSpan;
        private readonly Load _load;
        private readonly Drag _drag;
        private readonly Display _display;
        private readonly Permissions _permissions;
        private readonly Downloads? _downloads;   // only with the app's hook: with none, CEF cancels every download itself
        private readonly FileDialogs? _dialogs;   // only with the app's hook: with none, CEF shows its own dialog
        private readonly ScriptDialogs? _scriptDialogs;   // only with the app's hook: with none, CEF shows its own dialogs
        private readonly FocusHandler _focus;
        private readonly ZoomKeys? _zoomKeys;   // only with zoom off: with it allowed, every key is Chromium's

        public Client(ChromiumBrowser owner)
        {
            _requests = new Requests(owner);
            _lifeSpan = new LifeSpan(owner);
            _load = new Load(owner);
            _drag = new Drag(owner);
            _display = new Display(owner);
            _permissions = new Permissions(owner);
            _downloads = owner._hooks is { HandlesDownloads: true } ? new Downloads(owner) : null;
            _dialogs = owner._hooks is { HandlesFileDialogs: true } ? new FileDialogs(owner) : null;
            _scriptDialogs = owner._hooks is { HandlesScriptDialogs: true } ? new ScriptDialogs(owner) : null;
            _focus = new FocusHandler(owner);
            _zoomKeys = owner.AllowZoom ? null : new ZoomKeys();
            Struct->get_request_handler = &GetRequests;
            Struct->get_life_span_handler = &GetLifeSpan;
            Struct->get_load_handler = &GetLoad;
            Struct->get_drag_handler = &GetDrag;
            Struct->get_display_handler = &GetDisplay;
            Struct->get_permission_handler = &GetPermissions;
            Struct->get_download_handler = &GetDownloads;
            Struct->get_dialog_handler = &GetDialogs;
            Struct->get_jsdialog_handler = &GetScriptDialogs;
            Struct->get_focus_handler = &GetFocus;
            Struct->get_keyboard_handler = &GetKeyboard;
        }

        // The client's references to its handlers: each is freed once CEF has let go of it too.
        private protected override void OnFreed()
        {
            _requests.Release();
            _lifeSpan.Release();
            _load.Release();
            _drag.Release();
            _display.Release();
            _permissions.Release();
            _downloads?.Release();
            _dialogs?.Release();
            _scriptDialogs?.Release();
            _focus.Release();
            _zoomKeys?.Release();
        }

        // A getter handing CEF one of our structs must add the reference CEF takes.
        [UnmanagedCallersOnly] private static _cef_request_handler_t* GetRequests(_cef_client_t* self) => From<Client>(self)._requests.ForCef();
        [UnmanagedCallersOnly] private static _cef_life_span_handler_t* GetLifeSpan(_cef_client_t* self) => From<Client>(self)._lifeSpan.ForCef();
        [UnmanagedCallersOnly] private static _cef_load_handler_t* GetLoad(_cef_client_t* self) => From<Client>(self)._load.ForCef();
        [UnmanagedCallersOnly] private static _cef_drag_handler_t* GetDrag(_cef_client_t* self) => From<Client>(self)._drag.ForCef();
        [UnmanagedCallersOnly] private static _cef_display_handler_t* GetDisplay(_cef_client_t* self) => From<Client>(self)._display.ForCef();
        [UnmanagedCallersOnly] private static _cef_permission_handler_t* GetPermissions(_cef_client_t* self) => From<Client>(self)._permissions.ForCef();
        [UnmanagedCallersOnly] private static _cef_download_handler_t* GetDownloads(_cef_client_t* self) => From<Client>(self)._downloads is { } downloads ? downloads.ForCef() : null;
        [UnmanagedCallersOnly] private static _cef_dialog_handler_t* GetDialogs(_cef_client_t* self) => From<Client>(self)._dialogs is { } dialogs ? dialogs.ForCef() : null;
        [UnmanagedCallersOnly] private static _cef_jsdialog_handler_t* GetScriptDialogs(_cef_client_t* self) => From<Client>(self)._scriptDialogs is { } dialogs ? dialogs.ForCef() : null;
        [UnmanagedCallersOnly] private static _cef_focus_handler_t* GetFocus(_cef_client_t* self) => From<Client>(self)._focus.ForCef();
        [UnmanagedCallersOnly] private static _cef_keyboard_handler_t* GetKeyboard(_cef_client_t* self) => From<Client>(self)._zoomKeys is { } keys ? keys.ForCef() : null;
    }

    /// <summary>Handles a zoom shortcut itself, before Chromium or the page hears it, when the app has not allowed zoom
    /// (<see cref="ChromiumZoom.BlocksKey"/>).</summary>
    private sealed class ZoomKeys : CefObject<_cef_keyboard_handler_t>
    {
        public ZoomKeys() => Struct->on_pre_key_event = &PreKeyEvent;

        [UnmanagedCallersOnly]
#if CEF_WINDOWS
        private static int PreKeyEvent(_cef_keyboard_handler_t* self, _cef_browser_t* browser, _cef_key_event_t* key, nint osEvent, int* isShortcut)
#else
        private static int PreKeyEvent(_cef_keyboard_handler_t* self, _cef_browser_t* browser, _cef_key_event_t* key, void* osEvent, int* isShortcut)
#endif
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            return key != null && ChromiumZoom.BlocksKey(key->type, key->modifiers, key->windows_key_code) ? 1 : 0;
        }
    }

    /// <summary>The page's <c>alert</c>, <c>confirm</c>, <c>prompt</c> and <c>beforeunload</c> dialogs, answered by the app's
    /// <see cref="ChromiumHostOptions.OnScriptDialog"/> when it takes them; given to CEF only when that hook is set.</summary>
    private sealed class ScriptDialogs : CefObject<_cef_jsdialog_handler_t>
    {
        private readonly ChromiumBrowser _owner;

        public ScriptDialogs(ChromiumBrowser owner)
        {
            _owner = owner;
            Struct->on_jsdialog = &OnJsDialog;
            Struct->on_before_unload_dialog = &OnBeforeUnload;
        }

        // UI thread. Returning 1 takes the dialog and answers the callback before returning; 0 is CEF's own dialog.
        [UnmanagedCallersOnly]
        private static int OnJsDialog(_cef_jsdialog_handler_t* self, _cef_browser_t* browser, _cef_string_utf16_t* originUrl, cef_jsdialog_type_t type,
            _cef_string_utf16_t* messageText, _cef_string_utf16_t* defaultPromptText, _cef_jsdialog_callback_t* callback, int* suppressMessage)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var c = new CefRef<_cef_jsdialog_callback_t>(callback);
            var owner = From<ScriptDialogs>(self)._owner;
            if (callback == null || owner._hooks is null) return 0;
            var kind = type switch
            {
                cef_jsdialog_type_t.JSDIALOGTYPE_CONFIRM => ChromiumScriptDialogKind.Confirm,
                cef_jsdialog_type_t.JSDIALOGTYPE_PROMPT => ChromiumScriptDialogKind.Prompt,
                _ => ChromiumScriptDialogKind.Alert,
            };
            return Answer(owner, callback, kind, CefStrings.Read(originUrl), CefStrings.Read(messageText), CefStrings.Read(defaultPromptText), false);
        }

        [UnmanagedCallersOnly]
        private static int OnBeforeUnload(_cef_jsdialog_handler_t* self, _cef_browser_t* browser, _cef_string_utf16_t* messageText, int isReload,
            _cef_jsdialog_callback_t* callback)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var c = new CefRef<_cef_jsdialog_callback_t>(callback);
            var owner = From<ScriptDialogs>(self)._owner;
            if (callback == null || owner._hooks is null) return 0;
            return Answer(owner, callback, ChromiumScriptDialogKind.BeforeUnload, string.Empty, CefStrings.Read(messageText), string.Empty, isReload != 0);
        }

        private static int Answer(ChromiumBrowser owner, _cef_jsdialog_callback_t* callback, ChromiumScriptDialogKind kind, string origin, string message,
            string defaultText, bool isReload)
        {
            var answer = owner._hooks!.ScriptDialog(kind, origin, message, defaultText, isReload, owner.Log);
            if (answer is not { } result) return 0;
            fixed (char* t = result.Text)
            {
                var input = CefStrings.View(t, result.Text.Length);
                callback->cont(callback, result.Accept ? 1 : 0, &input);
            }
            AppCallback.Log(owner.Log, () => $"[Shenora.Chromium] Window '{owner.Name}': the app answered a {kind} dialog ({(result.Accept ? "accepted" : "not accepted")})");
            return 1;
        }
    }

    /// <summary>The page's own file dialog (<c>&lt;input type="file"&gt;</c>), answered by the app's
    /// <see cref="ChromiumHostOptions.OnFileDialog"/> when it takes it; given to CEF only when that hook is set.</summary>
    private sealed class FileDialogs : CefObject<_cef_dialog_handler_t>
    {
        private readonly ChromiumBrowser _owner;

        public FileDialogs(ChromiumBrowser owner)
        {
            _owner = owner;
            Struct->on_file_dialog = &OnFileDialog;
        }

        // UI thread. Returning 1 takes the dialog and answers the callback before returning; 0 is CEF's own dialog.
        [UnmanagedCallersOnly]
        private static int OnFileDialog(_cef_dialog_handler_t* self, _cef_browser_t* browser, cef_file_dialog_mode_t mode, _cef_string_utf16_t* title,
            _cef_string_utf16_t* defaultPath, _cef_string_list_t* acceptFilters, _cef_string_list_t* acceptExtensions, _cef_string_list_t* acceptDescriptions,
            _cef_file_dialog_callback_t* callback)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var c = new CefRef<_cef_file_dialog_callback_t>(callback);
            var owner = From<FileDialogs>(self)._owner;
            if (callback == null || owner._hooks is null) return 0;
            // The page's accept="" arrives as MIME patterns and extensions: the list the page wrote, in both.
            var accept = Strings(acceptFilters).Concat(Strings(acceptExtensions)).Distinct().ToArray();
            var kind = mode switch
            {
                cef_file_dialog_mode_t.FILE_DIALOG_OPEN_MULTIPLE => ChromiumFileDialogMode.OpenMultiple,
                cef_file_dialog_mode_t.FILE_DIALOG_OPEN_FOLDER => ChromiumFileDialogMode.OpenFolder,
                cef_file_dialog_mode_t.FILE_DIALOG_SAVE => ChromiumFileDialogMode.Save,
                _ => ChromiumFileDialogMode.Open,
            };
            var paths = owner._hooks.FileDialog(kind, CefStrings.Read(title), CefStrings.Read(defaultPath), accept, owner.Log);
            if (paths is null) return 0;
            if (paths.Length == 0) callback->cancel(callback);
            else
            {
                var list = Cef.cef_string_list_alloc();
                try
                {
                    foreach (var path in paths)
                        fixed (char* p = path)
                        {
                            var s = CefStrings.View(p, path.Length);
                            Cef.cef_string_list_append(list, &s);
                        }
                    callback->cont(callback, list);
                }
                finally { Cef.cef_string_list_free(list); }
            }
            AppCallback.Log(owner.Log, () => $"[Shenora.Chromium] Window '{owner.Name}': the app answered a {kind} file dialog with {paths.Length} path(s)");
            return 1;
        }

        private static string[] Strings(_cef_string_list_t* list)
        {
            if (list == null) return [];
            var texts = new string[(int)Cef.cef_string_list_size(list)];
            for (var i = 0; i < texts.Length; i++)
            {
                var value = default(_cef_string_utf16_t);
                Cef.cef_string_list_value(list, (nuint)i, &value);
                texts[i] = CefStrings.Read(&value);
                Cef.cef_string_utf16_clear(&value);
            }
            return texts;
        }
    }

    /// <summary>CEF hands the keyboard focus back to the host when Tab leaves the page; left unanswered, it stayed in
    /// the page with nowhere to go.</summary>
    private sealed class FocusHandler : CefObject<_cef_focus_handler_t>
    {
        private readonly ChromiumBrowser _owner;

        public FocusHandler(ChromiumBrowser owner)
        {
            _owner = owner;
            Struct->on_take_focus = &TakeFocus;
        }

        [UnmanagedCallersOnly]
        private static void TakeFocus(_cef_focus_handler_t* self, _cef_browser_t* browser, int next)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            var owner = From<FocusHandler>(self)._owner;
            AppCallback.Run(() => owner.Host?.MoveFocusRequested(next != 0));
        }
    }

    /// <summary>A page-started download. No handler takes one, which CEF cancels (D84), unless the app set
    /// <see cref="ChromiumHostOptions.OnDownloadStarting"/> and it names a path.</summary>
    private sealed class Downloads : CefObject<_cef_download_handler_t>
    {
        private readonly ChromiumBrowser _owner;

        public Downloads(ChromiumBrowser owner)
        {
            _owner = owner;
            Struct->can_download = &CanDownload;
            Struct->on_before_download = &BeforeDownload;
            Struct->on_download_updated = &DownloadUpdated;
        }

        // Left unset, CEF never asks the hook (measured): it is OnBeforeDownload that decides.
        [UnmanagedCallersOnly]
        private static int CanDownload(_cef_download_handler_t* self, _cef_browser_t* browser, _cef_string_utf16_t* url, _cef_string_utf16_t* method)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            return 1;
        }

        // Returning 1 with the callback continued saves it there, with no dialog; returning 0 is CEF's default, which
        // cancels in Alloy style.
        [UnmanagedCallersOnly]
        private static int BeforeDownload(_cef_download_handler_t* self, _cef_browser_t* browser, _cef_download_item_t* item,
            _cef_string_utf16_t* suggestedName, _cef_before_download_callback_t* callback)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var i = new CefRef<_cef_download_item_t>(item);
            using var c = new CefRef<_cef_before_download_callback_t>(callback);
            var owner = From<Downloads>(self)._owner;
            var url = CefStrings.TakeUserFree(item->get_url(item));
            var path = owner._hooks?.Download(url, CefStrings.Read(suggestedName), CefStrings.TakeUserFree(item->get_mime_type(item)), owner.Log);
            if (path is null)
            {
                AppCallback.Log(owner.Log, () => $"[Shenora.Chromium] Window '{owner.Name}': download canceled by policy: {url}");
                return 0;
            }
            fixed (char* p = path)
            {
                var target = CefStrings.View(p, path.Length);
                callback->cont(callback, &target, 0);
            }
            return 1;
        }

        // Every download reports, the ones the app cancelled too (Canceled, no path), and the first update can precede BeforeDownload (measured).
        [UnmanagedCallersOnly]
        private static void DownloadUpdated(_cef_download_handler_t* self, _cef_browser_t* browser, _cef_download_item_t* item,
            _cef_download_item_callback_t* callback)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var i = new CefRef<_cef_download_item_t>(item);
            using var c = new CefRef<_cef_download_item_callback_t>(callback);
            var downloads = From<Downloads>(self);
            var owner = downloads._owner;
            if (owner._hooks is null) return;
            var id = item->get_id(item);
            var state = ChromiumPageHooks.DownloadState(item->is_canceled(item) != 0, item->is_complete(item) != 0, item->is_interrupted(item) != 0);
            var control = downloads.Control(id, state, c);
            var update = new ChromiumDownloadProgress(id, CefStrings.TakeUserFree(item->get_url(item)),
                CefStrings.TakeUserFree(item->get_full_path(item)), state, item->get_received_bytes(item), item->get_total_bytes(item),
                item->is_paused(item) != 0, control);
            owner._hooks.DownloadUpdated(update, owner.Log);
        }

        // One control per download in flight, on CEF's UI thread only: the item callback of its latest update, kept until it ends.
        private readonly Dictionary<uint, DownloadControl> _controls = [];

        private DownloadControl Control(uint id, ChromiumDownloadState state, CefRef<_cef_download_item_callback_t> callback)
        {
            if (!_controls.TryGetValue(id, out var control)) control = new DownloadControl();
            if (state == ChromiumDownloadState.InProgress)
            {
                control.Hold(callback.ForCef());
                _controls[id] = control;
            }
            else
            {
                control.End();
                _controls.Remove(id);
            }
            return control;
        }

        // Whatever is still in flight when CEF lets the handler go gives its reference back (a release is thread-safe).
        private protected override void OnFreed()
        {
            foreach (var control in _controls.Values) control.End();
            _controls.Clear();
        }
    }

    /// <summary>The app's way to cancel, pause or resume a download: CEF's item callback, run on CEF's UI thread whichever thread asks.</summary>
    private sealed class DownloadControl : IChromiumDownloadControl
    {
        private _cef_download_item_callback_t* _callback;   // UI thread only; null once the download ended

        public void Hold(_cef_download_item_callback_t* callback)
        {
            Release();
            _callback = callback;
        }

        public void End() => Release();

        private enum Command { Cancel, Pause, Resume }

        public void Cancel() => OnUi(Command.Cancel);

        public void Pause() => OnUi(Command.Pause);

        public void Resume() => OnUi(Command.Resume);

        private void OnUi(Command command)
        {
            if (Cef.cef_currently_on(cef_thread_id_t.TID_UI) == 1) Run(command);
            else CefTask.Post(cef_thread_id_t.TID_UI, () => Run(command));
        }

        private void Run(Command command)
        {
            var callback = _callback;
            if (callback == null) return;
            switch (command)
            {
                case Command.Cancel: callback->cancel(callback); break;
                case Command.Pause: callback->pause(callback); break;
                default: callback->resume(callback); break;
            }
        }

        private void Release()
        {
            var callback = _callback;
            _callback = null;
            if (callback != null) new CefRef<_cef_download_item_callback_t>(callback).Dispose();
        }
    }

    /// <summary>Answers every permission prompt (<see cref="ChromiumRouting.AllowsPermission"/>, or the app's own). Media access has its
    /// own callback, left to Alloy's default, which denies and settles (measured).</summary>
    private sealed class Permissions : CefObject<_cef_permission_handler_t>
    {
        private readonly ChromiumBrowser _owner;

        public Permissions(ChromiumBrowser owner)
        {
            _owner = owner;
            Struct->on_show_permission_prompt = &ShowPrompt;
        }

        // Returning 1 says the callback is ours to run, which happens before returning.
        [UnmanagedCallersOnly]
        private static int ShowPrompt(_cef_permission_handler_t* self, _cef_browser_t* browser, ulong promptId, _cef_string_utf16_t* origin,
            uint requested, _cef_permission_prompt_callback_t* callback)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var c = new CefRef<_cef_permission_prompt_callback_t>(callback);
            var owner = From<Permissions>(self)._owner;
            var from = CefStrings.Read(origin);
            var allow = owner._hooks?.Permission(requested, from, owner.Log)
                ?? AppCallback.RunOrDefault(() => ChromiumRouting.AllowsPermission(requested, from, owner.Origins), fallback: false);
            callback->cont(callback, allow ? cef_permission_request_result_t.CEF_PERMISSION_RESULT_ACCEPT : cef_permission_request_result_t.CEF_PERMISSION_RESULT_DENY);
            if (!allow) AppCallback.Log(owner.Log, () => $"[Shenora.Chromium] Window '{owner.Name}': denied a permission prompt (flags {requested:X})");
            return 1;
        }
    }

    private sealed class Requests : CefObject<_cef_request_handler_t>
    {
        private readonly ChromiumBrowser _owner;

        public Requests(ChromiumBrowser owner)
        {
            _owner = owner;
            Struct->get_resource_request_handler = &GetResourceRequestHandler;
            Struct->on_render_process_terminated = &OnRendererGone;
            Struct->on_before_browse = &BeforeBrowse;
            Struct->on_open_urlfrom_tab = &OpenUrlFromTab;
            // Only with the app's hook: with none CEF is not asked and fails the request itself.
            if (owner._hooks is { HandlesCertificates: true }) Struct->on_certificate_error = &CertificateError;
            if (owner._hooks is { HandlesClientCertificates: true }) Struct->on_select_client_certificate = &SelectClientCertificate;
        }

        // UI thread. Returning 0 leaves it to CEF's default (its own pick from the list); 1 means the callback is answered before returning, with the app's
        // choice or none. Each certificate offered carries a reference this callback releases, as every pointer parameter does.
        [UnmanagedCallersOnly]
        private static int SelectClientCertificate(_cef_request_handler_t* self, _cef_browser_t* browser, int isProxy, _cef_string_utf16_t* host, int port,
            nuint certificatesCount, _cef_x509_certificate_t** certificates, _cef_select_client_certificate_callback_t* callback)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var c = new CefRef<_cef_select_client_certificate_callback_t>(callback);
            var held = new List<CefRef<_cef_x509_certificate_t>>((int)certificatesCount);
            try
            {
                for (nuint i = 0; i < certificatesCount; i++) held.Add(new CefRef<_cef_x509_certificate_t>(certificates[i]));
                var owner = From<Requests>(self)._owner;
                if (callback == null || owner._hooks is null) return 0;
                var offered = new List<ChromiumClientCertificate>((int)certificatesCount);
                foreach (var certificate in held)
                {
                    try
                    {
                        using var subject = new CefRef<_cef_x509_cert_principal_t>(certificate.Ptr->get_subject(certificate.Ptr));
                        using var issuer = new CefRef<_cef_x509_cert_principal_t>(certificate.Ptr->get_issuer(certificate.Ptr));
                        offered.Add(new ChromiumClientCertificate(
                            subject.IsNull ? string.Empty : CefStrings.TakeUserFree(subject.Ptr->get_display_name(subject.Ptr)),
                            issuer.IsNull ? string.Empty : CefStrings.TakeUserFree(issuer.Ptr->get_display_name(issuer.Ptr)),
                            Fingerprint(certificate.Ptr->get_derencoded(certificate.Ptr)),
                            Time(certificate.Ptr->get_valid_start(certificate.Ptr)), Time(certificate.Ptr->get_valid_expiry(certificate.Ptr))));
                    }
                    catch (Exception ex)
                    {
                        // Keep the list's positions: the app's index must stay the one CEF's callback takes.
                        AppCallback.Log(owner.Log, () => "[Shenora.Chromium] Reading a client certificate failed; it is offered without its details", LogLevel.Warning, ex);
                        offered.Add(new ChromiumClientCertificate(string.Empty, string.Empty, string.Empty, null, null));
                    }
                }
                var hostName = CefStrings.Read(host);
                var chosen = owner._hooks.ClientCertificate(hostName, port, isProxy != 0, offered, owner.Log);
                if (chosen is not { } index) return 0;
                if (index >= 0)
                {
                    var picked = certificates[index];
                    ((_cef_base_ref_counted_t*)picked)->add_ref((_cef_base_ref_counted_t*)picked);   // select consumes one
                    callback->select(callback, picked);
                }
                else callback->select(callback, null);
                AppCallback.Log(owner.Log, () => $"[Shenora.Chromium] Window '{owner.Name}': {(index >= 0 ? $"presented client certificate {index}" : "presented no client certificate")} to {hostName}:{port}");
                return 1;
            }
            finally
            {
                foreach (var certificate in held) certificate.Dispose();
            }
        }

        // CEF's base time: microseconds since 1601-01-01 UTC; zero is no date.
        private static DateTimeOffset? Time(_cef_basetime_t time) =>
            time.val == 0 ? null : new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(time.val * 10);

        // UI thread. Returning 0 fails the request; 1 hands CEF's callback to us, answered before returning. No callback: the error
        // cannot be overridden, and the app is not asked.
        [UnmanagedCallersOnly]
        private static int CertificateError(_cef_request_handler_t* self, _cef_browser_t* browser, cef_errorcode_t error, _cef_string_utf16_t* requestUrl,
            _cef_sslinfo_t* sslInfo, _cef_callback_t* callback)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var s = new CefRef<_cef_sslinfo_t>(sslInfo);
            using var c = new CefRef<_cef_callback_t>(callback);
            var owner = From<Requests>(self)._owner;
            if (callback == null || owner._hooks is null) return 0;
            var url = CefStrings.Read(requestUrl);
            string subject = string.Empty, issuer = string.Empty, sha256 = string.Empty;
            try
            {
                if (sslInfo != null)
                {
                    using var certificate = new CefRef<_cef_x509_certificate_t>(sslInfo->get_x509_certificate(sslInfo));
                    if (!certificate.IsNull)
                    {
                        subject = DisplayName(certificate.Ptr->get_subject(certificate.Ptr));
                        issuer = DisplayName(certificate.Ptr->get_issuer(certificate.Ptr));
                        sha256 = Fingerprint(certificate.Ptr->get_derencoded(certificate.Ptr));
                    }
                }
            }
            catch (Exception ex)
            {
                AppCallback.Log(owner.Log, () => "[Shenora.Chromium] Reading a server certificate failed; the app is asked without it", LogLevel.Warning, ex);
            }
            var allow = owner._hooks.CertificateError(url, (int)error, subject, issuer, sha256, owner.Log);
            if (allow) callback->cont(callback); else callback->cancel(callback);
            AppCallback.Log(owner.Log, () => $"[Shenora.Chromium] Window '{owner.Name}': {(allow ? "allowed" : "failed")} a navigation with a certificate error ({ChromiumPageHooks.ErrorName((int)error)})");
            return 1;
        }

        private static string DisplayName(_cef_x509_cert_principal_t* principal)
        {
            using var held = new CefRef<_cef_x509_cert_principal_t>(principal);
            return held.IsNull ? string.Empty : CefStrings.TakeUserFree(principal->get_display_name(principal));
        }

        private static string Fingerprint(_cef_binary_value_t* der)
        {
            using var held = new CefRef<_cef_binary_value_t>(der);
            if (held.IsNull) return string.Empty;
            var bytes = new byte[(int)der->get_size(der)];
            fixed (byte* data = bytes) der->get_data(der, data, (nuint)bytes.Length, 0);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        }

        // A link the user clicked with a modifier (ctrl: NEW_FOREGROUND_TAB, shift: NEW_WINDOW, as measured) does not come through
        // OnBeforePopup but here, and CEF's own answer to it was to navigate THIS window to the address (measured: the page was gone, the
        // server saw a browser-initiated request). It takes the popup's route instead. 1 cancels the navigation; a click in this tab goes on.
        [UnmanagedCallersOnly]
        private static int OpenUrlFromTab(_cef_request_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, _cef_string_utf16_t* targetUrl,
            cef_window_open_disposition_t disposition, int userGesture)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            if (!OpensAnotherTabOrWindow(disposition)) return 0;
            var owner = From<Requests>(self)._owner;
            var url = CefStrings.Read(targetUrl);
            AppCallback.Run(() => owner.RoutePopup(url, "", userGesture != 0, disposition, null));
            return 1;
        }

        // Answering 1 cancels the navigation.
        [UnmanagedCallersOnly]
        private static int BeforeBrowse(_cef_request_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, _cef_request_t* request, int userGesture, int isRedirect)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            using var r = new CefRef<_cef_request_t>(request);
            var owner = From<Requests>(self)._owner;
            try
            {
                var url = CefStrings.TakeUserFree(request->get_url(request));
                if (!ChromiumRouting.RefusesNavigation(url, frame != null && frame->is_main(frame) == 1)) return 0;
                AppCallback.Log(owner.Log, () => $"[Shenora.Chromium] Window '{owner.Name}': refused navigating the page to a local file");
                return 1;
            }
            catch (Exception ex)
            {
                AppCallback.Log(owner.Log, () => "[Shenora.Chromium] Checking a navigation failed; it was allowed", LogLevel.Warning, ex);
                return 0;
            }
        }

        // CEF's IO thread. `Network` answers null, which leaves the request to Chromium's own stack.
        [UnmanagedCallersOnly]
        private static _cef_resource_request_handler_t* GetResourceRequestHandler(_cef_request_handler_t* self, _cef_browser_t* browser,
            _cef_frame_t* frame, _cef_request_t* request, int isNavigation, int isDownload, _cef_string_utf16_t* initiator, int* disableDefault)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            using var r = new CefRef<_cef_request_t>(request);
            var owner = From<Requests>(self)._owner;
            try
            {
                var url = new Uri(CefStrings.TakeUserFree(request->get_url(request)));
                var method = CefStrings.TakeUserFree(request->get_method(request));
                var main = frame != null && frame->is_main(frame) == 1;
                var route = ChromiumRouting.Classify(url, method, owner.IsOwnBrowser(browser), main, isNavigation == 1,
                    CefStrings.Read(initiator), owner.Origins);
                if (route == ChromiumRoute.Network) return null;
                var handler = new Resource(owner, route);
                var given = handler.ForCef();
                handler.Release();
                return given;
            }
            catch (Exception ex)
            {
                AppCallback.Log(owner.Log, () => "[Shenora.Chromium] Routing a request failed; left to the network", LogLevel.Warning, ex);
                return null;
            }
        }

        [UnmanagedCallersOnly]
        private static void OnRendererGone(_cef_request_handler_t* self, _cef_browser_t* browser, cef_termination_status_t status, int errorCode, _cef_string_utf16_t* errorText)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            var owner = From<Requests>(self)._owner;
            AppCallback.Log(owner.Log, () => $"[Shenora.Chromium] Window '{owner.Name}': the renderer terminated ({status}, {errorCode})", LogLevel.Warning);
            AppCallback.Run(() => owner.RendererTerminated(status, errorCode));
        }
    }

    /// <summary>One request CEF has decided the shell answers, on the route already chosen for it.</summary>
    private sealed class Resource : CefObject<_cef_resource_request_handler_t>
    {
        private readonly ChromiumBrowser _owner;
        private readonly ChromiumRoute _route;

        public Resource(ChromiumBrowser owner, ChromiumRoute route)
        {
            _owner = owner;
            _route = route;
            Struct->get_resource_handler = &GetHandler;
        }

        [UnmanagedCallersOnly]
        private static _cef_resource_handler_t* GetHandler(_cef_resource_request_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, _cef_request_t* request)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            using var r = new CefRef<_cef_request_t>(request);
            var me = From<Resource>(self);
            ChromiumResourceHandler handler;
            try
            {
                if (me._route == ChromiumRoute.Ipc)
                {
                    me._owner.Bridge.Incoming(PostBody(request));
                    handler = new ChromiumResourceHandler(ChromiumServing.Accepted(), me._owner.Log);
                }
                else
                {
                    var snapshot = Snapshot(request);
                    var owner = me._owner;
                    var route = me._route;
                    handler = new ChromiumResourceHandler(cancel => owner.ServeAsync(route, snapshot, cancel), owner.Log);
                }
            }
            catch (Exception ex)
            {
                AppCallback.Log(me._owner.Log, () => "[Shenora.Chromium] Answering a request failed", LogLevel.Warning, ex);
                handler = new ChromiumResourceHandler(Task.FromException<WebViewResourceResponse>(ex), me._owner.Log);
            }
            var given = handler.ForCef();
            handler.Release();
            return given;
        }

        private static WebViewResourceRequest Snapshot(_cef_request_t* request)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var map = Cef.cef_string_multimap_alloc();
            try
            {
                request->get_header_map(request, map);
                var count = Cef.cef_string_multimap_size(map);
                for (nuint i = 0; i < count; i++)
                {
                    var key = default(_cef_string_utf16_t);
                    var value = default(_cef_string_utf16_t);
                    Cef.cef_string_multimap_key(map, i, &key);
                    Cef.cef_string_multimap_value(map, i, &value);
                    headers[CefStrings.Read(&key)] = CefStrings.Read(&value);
                    Cef.cef_string_utf16_clear(&key);
                    Cef.cef_string_utf16_clear(&value);
                }
            }
            finally { Cef.cef_string_multimap_free(map); }

            return new WebViewResourceRequest
            {
                Uri = new Uri(CefStrings.TakeUserFree(request->get_url(request))),
                Method = CefStrings.TakeUserFree(request->get_method(request)),
                Headers = headers,
            };
        }

        private static string PostBody(_cef_request_t* request)
        {
            using var post = new CefRef<_cef_post_data_t>(request->get_post_data(request));
            if (post.IsNull) return "";
            var count = post.Ptr->get_element_count(post.Ptr);
            if (count == 0) return "";
            var elements = new _cef_post_data_element_t*[(int)count];
            using var bytes = new MemoryStream();
            fixed (_cef_post_data_element_t** e = elements) post.Ptr->get_elements(post.Ptr, &count, e);
            // CEF lowers count to what it filled in: past it the array is null.
            for (var i = 0; i < (int)count && i < elements.Length; i++)
            {
                var element = elements[i];
                if (element == null) continue;
                using var el = new CefRef<_cef_post_data_element_t>(element);
                var size = element->get_bytes_count(element);
                var buffer = new byte[(int)size];
                fixed (byte* p = buffer) element->get_bytes(element, size, p);
                bytes.Write(buffer);
            }
            return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        }
    }

    private sealed class LifeSpan : CefObject<_cef_life_span_handler_t>
    {
        private readonly ChromiumBrowser _owner;

        public LifeSpan(ChromiumBrowser owner)
        {
            _owner = owner;
            Struct->on_after_created = &AfterCreated;
            Struct->do_close = &DoClose;
            Struct->on_before_close = &BeforeClose;
            Struct->on_before_popup = &BeforePopup;
        }

        // Answering 1 says the host closes the browser's window itself; 0, CEF's default, sends the top-level window
        // a close request.
        [UnmanagedCallersOnly]
        private static int DoClose(_cef_life_span_handler_t* self, _cef_browser_t* browser)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            var host = From<LifeSpan>(self)._owner.Host;
            return AppCallback.RunOrDefault(() => host?.CloseRequested() == true, fallback: false) ? 1 : 0;
        }

        // window.open and target=_blank, routed by RoutePopup. Returning 1 cancels.
        [UnmanagedCallersOnly]
        private static int BeforePopup(_cef_life_span_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, int popupId,
            _cef_string_utf16_t* targetUrl, _cef_string_utf16_t* targetFrameName, cef_window_open_disposition_t disposition, int userGesture,
            _cef_popup_features_t* features, _cef_window_info_t* windowInfo, _cef_client_t** client, _cef_browser_settings_t* settings,
            _cef_dictionary_value_t** extraInfo, int* noJavascriptAccess)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            // The client CEF hands in carries a reference the callee owns (CEF's own C++ wrapper releases it): kept, every
            // window.open held the window's client alive for good.
            using var c = new CefRef<_cef_client_t>(client == null ? null : *client);
            var owner = From<LifeSpan>(self)._owner;
            var url = CefStrings.Read(targetUrl);
            var frameName = CefStrings.Read(targetFrameName);
            _cef_popup_features_t? popupFeatures = features == null ? null : *features;
            AppCallback.Run(() => owner.RoutePopup(url, frameName, userGesture != 0, disposition, popupFeatures));
            return 1;
        }

        [UnmanagedCallersOnly]
        private static void AfterCreated(_cef_life_span_handler_t* self, _cef_browser_t* browser) =>
            AppCallback.Run(() => From<LifeSpan>(self)._owner.BrowserCreated(browser));   // the argument's reference is kept

        [UnmanagedCallersOnly]
        private static void BeforeClose(_cef_life_span_handler_t* self, _cef_browser_t* browser)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            AppCallback.Run(From<LifeSpan>(self)._owner.BrowserClosing);
        }
    }

    private sealed class Load : CefObject<_cef_load_handler_t>
    {
        private readonly ChromiumBrowser _owner;

        public Load(ChromiumBrowser owner)
        {
            _owner = owner;
            Struct->on_load_start = &LoadStart;
            Struct->on_load_end = &LoadEnd;
        }

        // Only a SUCCESSFUL main-frame load restores the crash-reload budget; an error page must not.
        [UnmanagedCallersOnly]
        private static void LoadEnd(_cef_load_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, int httpStatusCode)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            if (frame == null || frame->is_main(frame) != 1 || httpStatusCode is < 200 or >= 400) return;
            From<Load>(self)._owner._recovery.LoadSucceeded();
        }

        [UnmanagedCallersOnly]
        private static void LoadStart(_cef_load_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, cef_transition_type_t transition)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            if (frame == null || frame->is_main(frame) != 1) return;
            if (!From<Load>(self)._owner.AllowZoom) ResetZoom(browser);
            AppCallback.Run(From<Load>(self)._owner.DocumentStarted);
        }
    }

    private sealed class Drag : CefObject<_cef_drag_handler_t>
    {
        private readonly ChromiumBrowser _owner;

        public Drag(ChromiumBrowser owner)
        {
            _owner = owner;
            Struct->on_draggable_regions_changed = &RegionsChanged;
            Struct->on_drag_enter = &DragEnter;
        }

        // Answering 0 lets the drag continue into the page, whose own drop event names the zone.
        [UnmanagedCallersOnly]
        private static int DragEnter(_cef_drag_handler_t* self, _cef_browser_t* browser, _cef_drag_data_t* data, cef_drag_operations_mask_t mask)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var d = new CefRef<_cef_drag_data_t>(data);
            var owner = From<Drag>(self)._owner;
            AppCallback.Run(() => owner.DragEntered(browser, data));
            return 0;
        }

        [UnmanagedCallersOnly]
        private static void RegionsChanged(_cef_drag_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, nuint count, _cef_draggable_region_t* regions)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            var host = From<Drag>(self)._owner.Host;
            AppCallback.Run(() => host?.DraggableRegionsChanged(count, regions));
        }
    }

    private sealed class Display : CefObject<_cef_display_handler_t>
    {
        private readonly ChromiumBrowser _owner;

        public Display(ChromiumBrowser owner)
        {
            _owner = owner;
            Struct->on_title_change = &TitleChanged;
        }

        [UnmanagedCallersOnly]
        private static void TitleChanged(_cef_display_handler_t* self, _cef_browser_t* browser, _cef_string_utf16_t* title)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            var host = From<Display>(self)._owner.Host;
            var text = CefStrings.Read(title);
            AppCallback.Run(() => host?.TitleChanged(text));
        }
    }
}
