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
    void FocusLeaving(bool forward);
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
    public ChromiumBrowser(string name, ChromiumServing serving, ChromiumOrigins origins, Func<ChromiumBrowser, ChromiumIpcBridge> bridge,
        ILogger? log, IUrlLauncher? urls = null)
    {
        Name = name;
        Serving = serving;
        Origins = origins;
        _log = log;
        _urls = urls;
        Bridge = bridge(this);
        _client = new Client(this);
        _recovery = new RendererRecovery(message => AppCallback.Log(_log, () => $"[Shenora.Chromium] Window '{Name}': {message}", LogLevel.Warning));
    }

    public string Name { get; }
    public ChromiumServing Serving { get; }
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

    /// <summary>The client to create this browser with, with the reference CEF takes added.</summary>
    public _cef_client_t* ClientForCef() => _client.ForCef();

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
    /// Show CEF's own file dialog, native on each OS, over this page's window. UI thread. <paramref name="done"/> gets
    /// the picked paths, none when the user cancelled or the page's browser is gone.
    /// </summary>
    public void RunFileDialog(cef_file_dialog_mode_t mode, string title, string defaultPath, IReadOnlyList<string> filters, Action<string[]> done)
    {
        if (_browser == null) { done([]); return; }
        using var host = new CefRef<_cef_browser_host_t>(_browser->get_host(_browser));
        if (host.IsNull) { done([]); return; }
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

    // ── what CEF reports, delegated here ──────────────────────────────────────────────────────────────

    private void BrowserCreated(_cef_browser_t* browser)
    {
        _browser = browser;   // kept until the browser closes
        _browserId = browser->get_identifier(browser);
        using (var host = new CefRef<_cef_browser_host_t>(browser->get_host(browser)))
            if (!host.IsNull) Volatile.Write(ref _windowHandle, (nint)host.Ptr->get_window_handle(host.Ptr));   // per OS: HWND, X11 id, NSView*
        Bridge.Start();
        Host?.BrowserCreated();
    }

    private void BrowserClosing()
    {
        if (_browser == null) return;
        using (new CefRef<_cef_browser_t>(_browser))
        {
            _browser = null;
            _browserId = 0;
            Volatile.Write(ref _windowHandle, 0);
        }
        Host?.BrowserClosed();
    }

    // From the renderer-terminated callback. The reload is posted, so it runs after CEF has finished reporting
    // the dead renderer.
    private void RendererTerminated()
    {
        Bridge.RendererGone();
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
        private readonly FocusHandler _focus;

        public Client(ChromiumBrowser owner)
        {
            _requests = new Requests(owner);
            _lifeSpan = new LifeSpan(owner);
            _load = new Load(owner);
            _drag = new Drag(owner);
            _display = new Display(owner);
            _permissions = new Permissions(owner);
            _focus = new FocusHandler(owner);
            Struct->get_request_handler = &GetRequests;
            Struct->get_life_span_handler = &GetLifeSpan;
            Struct->get_load_handler = &GetLoad;
            Struct->get_drag_handler = &GetDrag;
            Struct->get_display_handler = &GetDisplay;
            Struct->get_permission_handler = &GetPermissions;
            Struct->get_focus_handler = &GetFocus;
        }

        // A getter handing CEF one of our structs must add the reference CEF takes.
        [UnmanagedCallersOnly] private static _cef_request_handler_t* GetRequests(_cef_client_t* self) => From<Client>(self)._requests.ForCef();
        [UnmanagedCallersOnly] private static _cef_life_span_handler_t* GetLifeSpan(_cef_client_t* self) => From<Client>(self)._lifeSpan.ForCef();
        [UnmanagedCallersOnly] private static _cef_load_handler_t* GetLoad(_cef_client_t* self) => From<Client>(self)._load.ForCef();
        [UnmanagedCallersOnly] private static _cef_drag_handler_t* GetDrag(_cef_client_t* self) => From<Client>(self)._drag.ForCef();
        [UnmanagedCallersOnly] private static _cef_display_handler_t* GetDisplay(_cef_client_t* self) => From<Client>(self)._display.ForCef();
        [UnmanagedCallersOnly] private static _cef_permission_handler_t* GetPermissions(_cef_client_t* self) => From<Client>(self)._permissions.ForCef();
        [UnmanagedCallersOnly] private static _cef_focus_handler_t* GetFocus(_cef_client_t* self) => From<Client>(self)._focus.ForCef();
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
            AppCallback.Run(() => owner.Host?.FocusLeaving(next != 0));
        }
    }

    /// <summary>Answers every permission prompt (<see cref="ChromiumRouting.AllowsPermission"/>). Media access has its
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
            var allow = AppCallback.RunOrDefault(() => ChromiumRouting.AllowsPermission(requested, CefStrings.Read(origin), owner.Origins), fallback: false);
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
            AppCallback.Run(owner.RendererTerminated);
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
                    handler = new ChromiumResourceHandler(me._owner.Serving.ServeAsync(me._route, Snapshot(request), CancellationToken.None), me._owner.Log);
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
            foreach (var element in elements)
            {
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

        // window.open and target=_blank: never a bare Chromium window onto whatever the page named. An http/https
        // URL goes to the user's browser, as on the WebView2 shell; anything else is dropped. Returning 1 cancels.
        [UnmanagedCallersOnly]
        private static int BeforePopup(_cef_life_span_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, int popupId,
            _cef_string_utf16_t* targetUrl, _cef_string_utf16_t* targetFrameName, cef_window_open_disposition_t disposition, int userGesture,
            _cef_popup_features_t* features, _cef_window_info_t* windowInfo, _cef_client_t** client, _cef_browser_settings_t* settings,
            _cef_dictionary_value_t** extraInfo, int* noJavascriptAccess)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            var owner = From<LifeSpan>(self)._owner;
            var url = CefStrings.Read(targetUrl);
            AppCallback.Run(() =>
            {
                if (owner._urls is null) return;
                try { owner._urls.OpenUrl(url); }
                catch (ArgumentException) { AppCallback.Log(owner.Log, () => $"[Shenora.Chromium] Window '{owner.Name}': dropped a popup to a non-web URL"); }
            });
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
