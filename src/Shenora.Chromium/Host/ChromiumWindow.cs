using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;
using Shenora.Chromium.Serving;
using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// One Chromium window: CEF's own <c>CefWindow</c> around one <c>CefBrowserView</c> (D82), hosting one page's
/// <see cref="ChromiumBrowser"/>. What is the window's own lives here: its commands, its page-drawn caption buttons
/// and the frame's hit-test, the page's drag areas, its title. Everything runs on CEF's UI thread.
/// </summary>
internal sealed unsafe class ChromiumWindow : IChromiumBrowserHost
{
    private readonly ChromiumWindowOptions _options;
    private readonly Action<ChromiumWindow> _destroyed;
    private readonly ILogger? _log;
    private readonly WindowDelegate _delegate;
    private readonly BrowserViewDelegate _viewDelegate = new();
    private readonly CaptionButtons _captions;
    private _cef_browser_view_t* _browserView;
    private _cef_window_t* _window;
    private CaptionHitTest? _captionHitTest;
    private NativeCaptionButtons? _nativeCaptions;
    private CaptionButtonPalette? _theme;   // the page's, once it has said; else the system's

    public ChromiumWindow(string name, ChromiumWindowOptions options, ChromiumServing serving, ChromiumOrigins origins,
        Func<ChromiumBrowser, ChromiumIpcBridge> bridge, Action<ChromiumWindow> destroyed, ILogger? log, IUrlLauncher? urls = null)
    {
        _options = options;
        _destroyed = destroyed;
        _log = log;
        Browser = new ChromiumBrowser(name, serving, origins, bridge, log, urls) { Host = this };
        _delegate = new WindowDelegate(this);
        // The click runs AFTER the message that delivered it: closing the window from inside its own subclassed
        // window procedure would destroy it mid-call.
        _captions = new CaptionButtons(CaptionStateChanged, kind => CefTask.Post(cef_thread_id_t.TID_UI, () => InvokeCaptionButton(kind)));
    }

    /// <summary>The page this window shows.</summary>
    public ChromiumBrowser Browser { get; }

    public string Name => Browser.Name;

    /// <summary>Create the browser view and the window. UI thread, once CEF's context exists.</summary>
    public void Open(Uri url, _cef_browser_settings_t* settings)
    {
        var text = url.AbsoluteUri;
        fixed (char* p = text)
        {
            var s = CefStrings.View(p, text.Length);
            _browserView = Cef.cef_browser_view_create(Browser.ClientForCef(), &s, settings, null, null, _viewDelegate.ForCef());
        }
        if (_browserView == null) throw new InvalidOperationException($"CEF would not create the browser view for window '{Name}'.");
        Cef.cef_window_create_top_level(_delegate.ForCef());
    }

    // ── what the window commands ask of the window ────────────────────────────────────────────────────

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

    /// <summary>True where the shell can make page-drawn caption buttons real ones: Windows, today.</summary>
    public static bool SupportsCaptionButtons =>
#if CEF_WINDOWS
        true;
#else
        false;
#endif

    /// <summary>The window paints the caption buttons itself (<see cref="ChromiumWindowOptions.NativeCaptionButtons"/>).</summary>
    public bool PaintsCaptionButtons => _options.NativeCaptionButtons && SupportsCaptionButtons;

    /// <summary>The page's theme, once it has sent one.</summary>
    internal CaptionButtonPalette? Theme => _theme;

    /// <summary><c>SET_THEME</c>: the page's theme, which the painted caption buttons follow. UI thread.</summary>
    public void SetTheme(bool dark)
    {
        _theme = CaptionButtonPalette.ForTheme(dark);
        _nativeCaptions?.SetPalette(_theme);
    }

    /// <summary><c>SET_CAPTION_BUTTONS</c>: the page's button rectangles in CSS px. UI thread.</summary>
    public void SetCaptionButtons(System.Text.Json.JsonElement? payload)
    {
        if (_window == null) return;
        var scale = _captionHitTest?.Scale ?? 1.0;
        var regions = CaptionButtons.Parse(payload, scale);
        _captions.Set(regions);
        _nativeCaptions?.Place(regions, scale);
        _captionHitTest?.Refresh();
    }

    private void CaptionStateChanged(CaptionButtonState state)
    {
        _nativeCaptions?.Show(state);
        Browser.Bridge.Notify(new Shenora.Core.Ipc.IpcNotification
        {
            Module = ChromiumWindowCommands.Module,
            Type = ChromiumWindowCommands.CaptionButtonStateEvent,
            Payload = state,
            // Each state is the whole state, so an undelivered one is superseded by the next.
            CoalesceKey = ChromiumWindowCommands.CaptionButtonStateEvent,
        }, immediate: true);
    }

    private void InvokeCaptionButton(CaptionButtonKind kind)
    {
        switch (kind)
        {
            case CaptionButtonKind.Minimize: Minimize(); break;
            case CaptionButtonKind.Maximize: ToggleMaximize(); break;
            case CaptionButtonKind.Close: Close(); break;
        }
    }

    // ── what the page's browser asks of its window ────────────────────────────────────────────────────

    // Forwarding is what makes the page's drag bar a real caption: HTCAPTION with it, HTCLIENT without (A/B, CEF 154).
    void IChromiumBrowserHost.DraggableRegionsChanged(nuint count, _cef_draggable_region_t* regions)
    {
        if (_window != null) _window->set_draggable_regions(_window, count, regions);
    }

    void IChromiumBrowserHost.TitleChanged(string title)
    {
        if (_options.Title is null) SetTitle(title);
    }

    // The old page's caption buttons are gone with it, or their rectangles would keep stealing clicks from a page
    // that never drew them.
    void IChromiumBrowserHost.DocumentStarted()
    {
        _captions.Set([]);
        _nativeCaptions?.Place([], 1.0);
    }

    // The window's own callbacks carry its lifetime (WindowCreated, WindowDestroyed), and CEF's own close request
    // reaches it through can_close.
    void IChromiumBrowserHost.BrowserCreated() { }
    void IChromiumBrowserHost.BrowserClosed() { }
    bool IChromiumBrowserHost.CloseRequested() => false;

    // ── CEF's window callbacks, delegated here ────────────────────────────────────────────────────────

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
#if CEF_WINDOWS
        // Before the page can ask for anything: the drag area needs the frame's hit-test from the start.
        _captionHitTest = CaptionHitTest.Attach(window->get_window_handle(window), _captions, _options.Frameless, _log);
#endif
        // Without the hit-test, painted buttons would look real and do nothing.
        if (PaintsCaptionButtons && _captionHitTest is not null)
            _nativeCaptions = new NativeCaptionButtons(window, _theme ?? CaptionButtonPalette.SystemTheme());
        AppCallback.Log(_log, () => $"[Shenora.Chromium] Window '{Name}' shown");
    }

    private void WindowDestroyed()
    {
        _captionHitTest?.Dispose();
        _captionHitTest = null;
        _nativeCaptions?.Dispose();
        _nativeCaptions = null;
        Browser.Bridge.Dispose();
        if (_window != null) { using var w = new CefRef<_cef_window_t>(_window); _window = null; }
        if (_browserView != null) { using var v = new CefRef<_cef_browser_view_t>(_browserView); _browserView = null; }
        AppCallback.Run(() => _destroyed(this));
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
            // Left unanswered, CEF treats a frameless window as fixed: its edges answer HTBORDER and it gets
            // none of WS_THICKFRAME / WS_MAXIMIZEBOX, without which Windows offers no Snap Layouts either.
            // Answered, Chromium styles the window itself and the edges resize (both measured).
            Struct->can_resize = &Yes;
            Struct->can_maximize = &Yes;
            Struct->can_minimize = &Yes;
            Struct->@base.@base.get_preferred_size = &PreferredSize;
            Struct->on_window_activation_changed = &ActivationChanged;
            Struct->on_window_bounds_changed = &BoundsChanged;
        }

        [UnmanagedCallersOnly]
        private static void ActivationChanged(_cef_window_delegate_t* self, _cef_window_t* window, int active)
        {
            using var w = new CefRef<_cef_window_t>(window);
            AppCallback.Run(() => From<WindowDelegate>(self)._owner._nativeCaptions?.Activated(active == 1));
        }

        // A maximize or a restore resizes the window: maximize's glyph follows.
        [UnmanagedCallersOnly]
        private static void BoundsChanged(_cef_window_delegate_t* self, _cef_window_t* window, _cef_rect_t* bounds)
        {
            using var w = new CefRef<_cef_window_t>(window);
            AppCallback.Run(() => From<WindowDelegate>(self)._owner._nativeCaptions?.WindowSized());
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
        private static int Yes(_cef_window_delegate_t* self, _cef_window_t* window)
        {
            using var w = new CefRef<_cef_window_t>(window);
            return 1;
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

    /// <summary>
    /// The page's browser is ALLOY style (D84): Chromium's content layer without Chrome's own UI, which is what the
    /// shell's client callbacks need. Measured: in Chrome style CEF never called <c>on_drag_enter</c>, so a drop's
    /// real paths were unreachable. The window stays Chrome style, which may host an Alloy view.
    /// </summary>
    private sealed class BrowserViewDelegate : CefObject<_cef_browser_view_delegate_t>
    {
        public BrowserViewDelegate() => Struct->get_browser_runtime_style = &Style;

        [UnmanagedCallersOnly]
        private static cef_runtime_style_t Style(_cef_browser_view_delegate_t* self) => cef_runtime_style_t.CEF_RUNTIME_STYLE_ALLOY;
    }
}
