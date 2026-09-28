using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;
using Shenora.Chromium.Serving;
using Shenora.Core.WebView;

namespace Shenora.Chromium.Host;

/// <summary>
/// One Chromium window: CEF's own <c>CefWindow</c> around one <c>CefBrowserView</c> (D82), the handlers its
/// browser needs, and that page's IPC bridge. Everything here runs on CEF's UI thread except the resource
/// callbacks, which CEF raises on its IO thread.
/// <para>
/// Lifetime follows CEF's C API rules: a struct CEF returns or passes into a callback carries a reference
/// that is released (or kept, deliberately, as the browser and window are), and every struct handed TO CEF
/// is handed with a reference added.
/// </para>
/// </summary>
internal sealed unsafe class ChromiumWindow
{
    private readonly ChromiumWindowOptions _options;
    private readonly Action<ChromiumWindow> _destroyed;
    private readonly ILogger? _log;
    private readonly WindowDelegate _delegate;
    private readonly Client _client;
    private _cef_browser_view_t* _browserView;
    private _cef_window_t* _window;
    private _cef_browser_t* _browser;
    // Written on the UI thread, read on CEF's IO thread by the IPC ownership check.
    private volatile int _browserId;

    public ChromiumWindow(string name, ChromiumWindowOptions options, ChromiumServing serving, ChromiumOrigins origins,
        Func<ChromiumWindow, ChromiumIpcBridge> bridge, Action<ChromiumWindow> destroyed, ILogger? log)
    {
        Name = name;
        _options = options;
        Serving = serving;
        Origins = origins;
        _destroyed = destroyed;
        _log = log;
        Bridge = bridge(this);
        _delegate = new WindowDelegate(this);
        _client = new Client(this);
    }

    public string Name { get; }
    public ChromiumServing Serving { get; }
    public ChromiumOrigins Origins { get; }
    public ChromiumIpcBridge Bridge { get; }
    public ILogger? Log => _log;

    /// <summary>True while the page's own browser is this id: the IPC route answers no other.</summary>
    public bool IsOwnBrowser(_cef_browser_t* browser) => browser != null && _browserId != 0 && browser->get_identifier(browser) == _browserId;

    /// <summary>Create the browser view and the window. UI thread, once CEF's context exists.</summary>
    public void Open(Uri url, _cef_browser_settings_t* settings)
    {
        var text = url.AbsoluteUri;
        fixed (char* p = text)
        {
            var s = CefStrings.View(p, text.Length);
            _browserView = Cef.cef_browser_view_create(_client.ForCef(), &s, settings, null, null, null);
        }
        if (_browserView == null) throw new InvalidOperationException($"CEF would not create the browser view for window '{Name}'.");
        Cef.cef_window_create_top_level(_delegate.ForCef());
    }

    // ── what the window commands and the bridge ask of the window ─────────────────────────────────────

    public void Minimize() { if (_window != null) _window->minimize(_window); }
    public void Close() { if (_window != null) _window->close(_window); }
    public bool IsMaximized => _window != null && _window->is_maximized(_window) == 1;

    public void ToggleMaximize()
    {
        if (_window == null) return;
        if (IsMaximized) _window->restore(_window);
        else _window->maximize(_window);
    }

    public void Activate() { if (_window != null) { _window->show(_window); _window->activate(_window); } }

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

    // ── CEF's callbacks, delegated here ───────────────────────────────────────────────────────────────

    private void WindowCreated(_cef_window_t* window)
    {
        _window = window;   // the argument's reference is KEPT, and released when the window goes
        // Passing a library object INTO CEF hands it one reference, so add one first.
        ((_cef_base_ref_counted_t*)_browserView)->add_ref((_cef_base_ref_counted_t*)_browserView);
        window->@base.add_child_view(&window->@base, &_browserView->@base);
        if (_options.Title is { } title) SetTitle(title);
        var size = new _cef_size_t { width = _options.Width, height = _options.Height };
        window->center_window(window, &size);
        window->show(window);
        AppCallback.Log(_log, () => $"[Shenora.Chromium] Window '{Name}' shown");
    }

    private void WindowDestroyed()
    {
        Bridge.Dispose();
        if (_window != null) { using var w = new CefRef<_cef_window_t>(_window); _window = null; }
        if (_browserView != null) { using var v = new CefRef<_cef_browser_view_t>(_browserView); _browserView = null; }
        AppCallback.Run(() => _destroyed(this));
    }

    private void BrowserCreated(_cef_browser_t* browser)
    {
        _browser = browser;   // kept until the browser closes
        _browserId = browser->get_identifier(browser);
        Bridge.Start();
    }

    private void BrowserClosing()
    {
        if (_browser == null) return;
        using var b = new CefRef<_cef_browser_t>(_browser);
        _browser = null;
        _browserId = 0;
    }

    private void SetTitle(string title)
    {
        if (_window == null) return;
        fixed (char* t = title)
        {
            var s = CefStrings.View(t, title.Length);
            _window->set_title(_window, &s);
        }
    }

    // ── the structs CEF sees ──────────────────────────────────────────────────────────────────────────

    private sealed class WindowDelegate : CefObject<_cef_window_delegate_t>
    {
        private readonly ChromiumWindow _owner;

        public WindowDelegate(ChromiumWindow owner)
        {
            _owner = owner;
            Struct->on_window_created = &OnCreated;
            Struct->on_window_destroyed = &OnDestroyed;
            Struct->is_frameless = &IsFrameless;
            Struct->can_close = &CanClose;
            Struct->@base.@base.get_preferred_size = &PreferredSize;
        }

        [UnmanagedCallersOnly]
        private static void OnCreated(_cef_window_delegate_t* self, _cef_window_t* window) =>
            AppCallback.Run(() => From<WindowDelegate>(self)._owner.WindowCreated(window));

        [UnmanagedCallersOnly]
        private static void OnDestroyed(_cef_window_delegate_t* self, _cef_window_t* window)
        {
            using var w = new CefRef<_cef_window_t>(window);
            AppCallback.Run(From<WindowDelegate>(self)._owner.WindowDestroyed);
        }

        [UnmanagedCallersOnly]
        private static int IsFrameless(_cef_window_delegate_t* self, _cef_window_t* window)
        {
            using var w = new CefRef<_cef_window_t>(window);
            return From<WindowDelegate>(self)._owner._options.Frameless ? 1 : 0;
        }

        [UnmanagedCallersOnly]
        private static int CanClose(_cef_window_delegate_t* self, _cef_window_t* window)
        {
            using var w = new CefRef<_cef_window_t>(window);
            return 1;
        }

        [UnmanagedCallersOnly]
        private static _cef_size_t PreferredSize(_cef_view_delegate_t* self, _cef_view_t* view)
        {
            using var v = new CefRef<_cef_view_t>(view);
            var options = From<WindowDelegate>(self)._owner._options;
            return new _cef_size_t { width = options.Width, height = options.Height };
        }
    }

    private sealed class Client : CefObject<_cef_client_t>
    {
        private readonly Requests _requests;
        private readonly LifeSpan _lifeSpan;
        private readonly Load _load;
        private readonly Drag _drag;
        private readonly Display _display;

        public Client(ChromiumWindow owner)
        {
            _requests = new Requests(owner);
            _lifeSpan = new LifeSpan(owner);
            _load = new Load(owner);
            _drag = new Drag(owner);
            _display = new Display(owner);
            Struct->get_request_handler = &GetRequests;
            Struct->get_life_span_handler = &GetLifeSpan;
            Struct->get_load_handler = &GetLoad;
            Struct->get_drag_handler = &GetDrag;
            Struct->get_display_handler = &GetDisplay;
        }

        // A getter handing CEF one of our structs must add the reference CEF takes.
        [UnmanagedCallersOnly] private static _cef_request_handler_t* GetRequests(_cef_client_t* self) => From<Client>(self)._requests.ForCef();
        [UnmanagedCallersOnly] private static _cef_life_span_handler_t* GetLifeSpan(_cef_client_t* self) => From<Client>(self)._lifeSpan.ForCef();
        [UnmanagedCallersOnly] private static _cef_load_handler_t* GetLoad(_cef_client_t* self) => From<Client>(self)._load.ForCef();
        [UnmanagedCallersOnly] private static _cef_drag_handler_t* GetDrag(_cef_client_t* self) => From<Client>(self)._drag.ForCef();
        [UnmanagedCallersOnly] private static _cef_display_handler_t* GetDisplay(_cef_client_t* self) => From<Client>(self)._display.ForCef();
    }

    private sealed class Requests : CefObject<_cef_request_handler_t>
    {
        private readonly ChromiumWindow _owner;

        public Requests(ChromiumWindow owner)
        {
            _owner = owner;
            Struct->get_resource_request_handler = &GetResourceRequestHandler;
            Struct->on_render_process_terminated = &OnRendererGone;
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
            AppCallback.Run(owner.Bridge.RendererGone);
            AppCallback.Log(owner.Log, () => $"[Shenora.Chromium] Window '{owner.Name}': the renderer terminated ({status}, {errorCode})", LogLevel.Warning);
        }
    }

    /// <summary>One request CEF has decided the shell answers, on the route already chosen for it.</summary>
    private sealed class Resource : CefObject<_cef_resource_request_handler_t>
    {
        private readonly ChromiumWindow _owner;
        private readonly ChromiumRoute _route;

        public Resource(ChromiumWindow owner, ChromiumRoute route)
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
        private readonly ChromiumWindow _owner;

        public LifeSpan(ChromiumWindow owner)
        {
            _owner = owner;
            Struct->on_after_created = &AfterCreated;
            Struct->on_before_close = &BeforeClose;
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
        private readonly ChromiumWindow _owner;

        public Load(ChromiumWindow owner)
        {
            _owner = owner;
            Struct->on_load_start = &LoadStart;
        }

        // The main frame's new document: whoever handshook can no longer receive (ContentLoading's counterpart).
        [UnmanagedCallersOnly]
        private static void LoadStart(_cef_load_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, cef_transition_type_t transition)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            if (frame != null && frame->is_main(frame) == 1) AppCallback.Run(From<Load>(self)._owner.Bridge.DocumentReplaced);
        }
    }

    private sealed class Drag : CefObject<_cef_drag_handler_t>
    {
        private readonly ChromiumWindow _owner;

        public Drag(ChromiumWindow owner)
        {
            _owner = owner;
            Struct->on_draggable_regions_changed = &RegionsChanged;
        }

        // Forwarding is what makes the page's drag bar a real caption: HTCAPTION with it, HTCLIENT without
        // (A/B, CEF 154).
        [UnmanagedCallersOnly]
        private static void RegionsChanged(_cef_drag_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, nuint count, _cef_draggable_region_t* regions)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            var window = From<Drag>(self)._owner._window;
            if (window != null) window->set_draggable_regions(window, count, regions);
        }
    }

    private sealed class Display : CefObject<_cef_display_handler_t>
    {
        private readonly ChromiumWindow _owner;

        public Display(ChromiumWindow owner)
        {
            _owner = owner;
            Struct->on_title_change = &TitleChanged;
        }

        [UnmanagedCallersOnly]
        private static void TitleChanged(_cef_display_handler_t* self, _cef_browser_t* browser, _cef_string_utf16_t* title)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            var owner = From<Display>(self)._owner;
            if (owner._options.Title is null) owner.SetTitle(CefStrings.Read(title));
        }
    }
}
