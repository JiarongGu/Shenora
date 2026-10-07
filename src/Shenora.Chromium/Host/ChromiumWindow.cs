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
    private readonly Func<ChromiumWindow, bool>? _mayClose;
    private readonly ILogger? _log;
    private readonly WindowDelegate _delegate;
    private readonly BrowserViewDelegate _viewDelegate;
    private readonly CaptionButtons _captions;
    private _cef_browser_view_t* _browserView;
    private _cef_window_t* _window;
    private CaptionHitTest? _captionHitTest;
    private NativeCaptionButtons? _nativeCaptions;
    private CaptionButtonPalette? _theme;   // the page's, once it has said; else the system's
    private CaptionButtonPalette? _colors;  // the page's own colours, which win over any theme
    private KitStrip? _kitStrip;            // a frameless main window's title strip while its splash is up
    private bool _pageDragRegions;          // the page has laid out drag regions of its own
#if CEF_WINDOWS
    private bool _stripButtonsTemporary;    // the strip made the painted buttons, and takes them down with it
#endif
    private readonly ChromiumWindowGeometry? _geometry;
    private ChromiumWindowGeometry.Plan? _plan;

    // mayClose is asked before the window closes: false keeps it open and hides it instead (the tray's close-to-tray).
    // geometry, for the main window when the app keeps its state: restored as it opens, saved as it closes.
    public ChromiumWindow(string name, ChromiumWindowOptions options, ChromiumServing serving, ChromiumOrigins origins,
        Func<ChromiumBrowser, ChromiumIpcBridge> bridge, Action<ChromiumWindow> destroyed, ILogger? log, IUrlLauncher? urls = null,
        Func<ChromiumWindow, bool>? mayClose = null, ChromiumWindowGeometry? geometry = null)
    {
        _options = options;
        _geometry = geometry;
        _destroyed = destroyed;
        _mayClose = mayClose;
        _log = log;
        Browser = new ChromiumBrowser(name, serving, origins, bridge, log, urls) { Host = this };
        _delegate = new WindowDelegate(this);
        _viewDelegate = new BrowserViewDelegate(this);
        // The click runs AFTER the message that delivered it: closing the window from inside its own subclassed
        // window procedure would destroy it mid-call.
        _captions = new CaptionButtons(CaptionStateChanged, kind => CefTask.Post(cef_thread_id_t.TID_UI, () => InvokeCaptionButton(kind)));
    }

    /// <summary>The page this window shows.</summary>
    public ChromiumBrowser Browser { get; }

    /// <summary>The window exists and is about to show, with its native handle. UI thread.</summary>
    public Action<nint>? Opening { get; set; }

    /// <summary>The window moved or resized. UI thread.</summary>
    public Action? Moved { get; set; }

    /// <summary>Once, when the page has first painted and two frames have followed (or never, if it does not). CEF's UI
    /// thread.</summary>
    public Action? Painted { get; set; }

    /// <summary>The window was hidden (the tray's close). UI thread.</summary>
    public Action? Hidden { get; set; }

    /// <summary>The window is on screen (after its show). UI thread.</summary>
    public Action? Shown { get; set; }

    /// <summary>A frameless window gets the splash's title strip until its page reports a title bar of its own. Set
    /// before <see cref="Open"/>; ignored for a framed window.</summary>
    public SplashTitleBarOptions? SplashStrip { get; set; }

    /// <summary>What the window and its browser view paint until the page draws (the window's or the host's
    /// <see cref="ChromiumWindowOptions.BackgroundColor"/>). Set before <see cref="Open"/>.</summary>
    public System.Drawing.Color? Background { get; set; }

    public string Name => Browser.Name;

    /// <summary>Create the browser view and the window. UI thread, once CEF's context exists. Once: the delegates
    /// are CEF's from here, and freed when it is done with them.</summary>
    public void Open(Uri url, _cef_browser_settings_t* settings)
    {
        _plan = _geometry?.Restore(_options.Width, _options.Height);
        var text = url.AbsoluteUri;
        fixed (char* p = text)
        {
            var s = CefStrings.View(p, text.Length);
            _browserView = Cef.cef_browser_view_create(Browser.ClientForCef(), &s, settings, null, null, _viewDelegate.ForCef());
        }
        // The creator's references: CEF holds its own for as long as it uses them. Kept, the delegates' handles would
        // root this window, its browser and its bridge for the life of the process.
        _viewDelegate.Release();
        if (_browserView == null)
        {
            _delegate.Release();
            Browser.Retire();
            throw new InvalidOperationException($"CEF would not create the browser view for window '{Name}'.");
        }
        // The window CEF returns carries a reference of its own; WindowCreated has kept the one it needs.
        using var window = new CefRef<_cef_window_t>(Cef.cef_window_create_top_level(_delegate.ForCef()));
        _delegate.Release();
        if (window.IsNull)
        {
            using (new CefRef<_cef_browser_view_t>(_browserView)) _browserView = null;
            // As for a view CEF would not make: no browser was made, so nothing would ever retire the client.
            Browser.Retire();
            throw new InvalidOperationException($"CEF would not create the window '{Name}'.");
        }
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

    // Shown if hidden (the tray's close), restored if minimized, then to the front.
    public void Activate()
    {
        if (_window == null) return;
        _window->show(_window);
        if (_window->is_minimized(_window) == 1) _window->restore(_window);
        _window->activate(_window);
    }
    public void Hide()
    {
        if (_window == null) return;
        _window->hide(_window);
        if (Hidden is { } hidden) AppCallback.Run(hidden, ex => AppCallback.Log(_log, () => "[Shenora.Chromium] A window-hidden hook failed", LogLevel.Warning, ex));
    }

    /// <summary>Take or give back the window's input (IUiInteraction). UI thread.</summary>
    public void SetEnabled(bool enabled)
    {
        if (_window == null) return;
        ((_cef_view_t*)_window)->set_enabled((_cef_view_t*)_window, enabled ? 1 : 0);
        if (_browserView != null) ((_cef_view_t*)_browserView)->set_enabled((_cef_view_t*)_browserView, enabled ? 1 : 0);
#if CEF_WINDOWS
        // Views stops the page's input, not the frame's: the caption hit-test would still drag the window and press
        // its caption buttons. A disabled HWND takes neither, as a disabled WinForms form.
        EnableWindow(_window->get_window_handle(_window), enabled ? 1 : 0);
#endif
    }

#if CEF_WINDOWS
    [DllImport("user32")] private static extern int EnableWindow(nint hwnd, int enable);
#endif
    public bool IsVisible => _window != null && ((_cef_view_t*)_window)->is_visible((_cef_view_t*)_window) == 1;

    /// <summary>CEF asks before the window closes. UI thread.</summary>
    private bool MayClose()
    {
        if (_mayClose is null || AppCallback.RunOrDefault(() => _mayClose(this), fallback: true)) return true;
        Hide();
        return false;
    }

    /// <summary>True where the shell can open a window's system menu: Windows.</summary>
    public static bool SupportsSystemMenu =>
#if CEF_WINDOWS
        true;
#else
        false;
#endif

    /// <summary><c>SHOW_SYSTEM_MENU</c>: the window's system menu at the pointer. Queued behind the request that asked,
    /// which answers first, since the menu is a modal loop. UI thread.</summary>
    public void ShowSystemMenu()
    {
#if CEF_WINDOWS
        if (_window == null) return;
        var hwnd = _window->get_window_handle(_window);
        CefTask.Post(cef_thread_id_t.TID_UI, () => SystemMenu.ShowAtPointer(hwnd));
#endif
    }

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

    /// <summary>The page's own colours, while it has set them.</summary>
    internal CaptionButtonPalette? Colors => _colors;

    // What the painted buttons show: the page's colours, else its theme, else the app's colour scheme or the system's.
    private CaptionButtonPalette Palette => _colors ?? _theme ?? CaptionButtonPalette.ForTheme((SchemeDark?.Invoke() ?? SystemTheme.IsDark()) == true);

    /// <summary>The app's colour scheme as light or dark (null: the system's), which the painted buttons follow until the
    /// page sets a theme or colours of its own.</summary>
    internal Func<bool?>? SchemeDark { get; init; }

    /// <summary>The app's colour scheme changed. CEF's UI thread.</summary>
    internal void ColorSchemeChanged()
    {
        if (_colors is null && _theme is null) _nativeCaptions?.SetPalette(Palette);
    }

    /// <summary><c>SET_THEME</c>: the page's theme, which the painted caption buttons follow unless the page has set
    /// colours of its own. UI thread.</summary>
    public void SetTheme(bool dark)
    {
        _theme = CaptionButtonPalette.ForTheme(dark);
        _nativeCaptions?.SetPalette(Palette);
    }

    /// <summary><c>SET_CAPTION_BUTTON_COLORS</c>: the page's own colours, which win over its theme; null goes back to
    /// the theme. UI thread.</summary>
    public void SetCaptionButtonColors(CaptionButtonPalette? colors)
    {
        _colors = colors;
        _nativeCaptions?.SetPalette(Palette);
    }

    /// <summary><c>SET_CAPTION_BUTTONS</c>: the page's button rectangles in CSS px. UI thread.</summary>
    public void SetCaptionButtons(System.Text.Json.JsonElement? payload)
    {
        if (_window == null) return;
        _kitStrip?.PageCaptionButtons();
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

    internal void InvokeCaptionButton(CaptionButtonKind kind)
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
    // A page with none yet (CEF reports an empty set as a document starts) leaves the splash's strip its drag region; the
    // first real set is the page's title bar, which ends the strip.
    void IChromiumBrowserHost.DraggableRegionsChanged(nuint count, _cef_draggable_region_t* regions)
    {
        if (_window == null) return;
        if (count == 0 && _kitStrip is { Active: true }) return;
        if (count > 0)
        {
            _pageDragRegions = true;
            _kitStrip?.PageDragRegions();
        }
        _window->set_draggable_regions(_window, count, regions);
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
        // The splash's strip is the window's, not the old page's: it stays until the new page has a title bar.
        if (_kitStrip is { Active: true }) PlaceStrip();
    }

    // ── the splash's title strip on a frameless window ────────────────────────────────────────────────

    // The traffic lights' span at the left of a macOS window, in DIPs: left out of the strip's drag region.
    private const int TrafficLightsDips = 80;

    /// <summary>The splash lifted: its strip, if still up, ends. Any thread.</summary>
    public void SplashLifted() => CefTask.Post(cef_thread_id_t.TID_UI, () => _kitStrip?.SplashLifted());

    private void ApplyKitStrip(KitStrip.Apply what)
    {
        if (_window == null || SplashStrip is not { } bar) return;
        if (what == KitStrip.Apply.Begin)
        {
#if CEF_WINDOWS
            // Real caption buttons, painted and hit-tested by the window (which is what offers Snap Layouts), even where
            // the app paints none of its own: those go again with the strip.
            // The page's colours or theme if it has said; else what reads on the window's background the strip shows.
            var palette = CaptionButtonPalette.ForStrip(bar, _colors ?? _theme ?? CaptionButtonPalette.ForBackground(_options.BackgroundColor));
            if (_nativeCaptions is not null) _nativeCaptions.SetPalette(palette);
            else if (_captionHitTest is not null)
            {
                _nativeCaptions = new NativeCaptionButtons(_window, palette);
                _stripButtonsTemporary = true;
            }
#endif
            PlaceStrip();
            return;
        }
#if CEF_WINDOWS
        _captions.Set([]);
        if (_stripButtonsTemporary)
        {
            _nativeCaptions?.Dispose();
            _nativeCaptions = null;
            _stripButtonsTemporary = false;
        }
        else
        {
            _nativeCaptions?.Place([], 1.0);
            _nativeCaptions?.SetPalette(Palette);
        }
        _captionHitTest?.Refresh();
#endif
        if (!_pageDragRegions) _window->set_draggable_regions(_window, 0, null);
    }

    // The strip's buttons at their default place and the rest of it a drag region, at the window's current width.
    private void PlaceStrip()
    {
        if (_window == null || SplashStrip is not { } bar) return;
#if CEF_WINDOWS
        var scale = _captionHitTest?.Scale ?? 1.0;
        WindowsSplashNative.GetClientRect(_window->get_window_handle(_window), out var client);
        var width = client.Right - client.Left;
        var stripPx = (int)Math.Round(bar.Height * scale);
        var buttons = SplashGeometry.DefaultCaptionButtons(width, stripPx, scale);
        _captions.Set(buttons);
        _nativeCaptions?.Place(buttons, scale);
        _captionHitTest?.Refresh();
        var dragPx = SplashGeometry.StripDragRect(width, stripPx, buttons);
        var drag = new _cef_draggable_region_t
        {
            bounds = new _cef_rect_t { x = 0, y = 0, width = (int)Math.Round(dragPx.Width / scale), height = (int)Math.Round(bar.Height) },
            draggable = 1,
        };
#elif CEF_MACOS
        // macOS: the caption buttons are the traffic lights, the window's own.
        var size = ((_cef_view_t*)_window)->get_size((_cef_view_t*)_window);
        var drag = new _cef_draggable_region_t
        {
            bounds = new _cef_rect_t { x = TrafficLightsDips, y = 0, width = Math.Max(0, size.width - TrafficLightsDips), height = (int)Math.Round(bar.Height) },
            draggable = 1,
        };
#else
        // Linux: the splash draws its strip over the window and forwards its input; the kit paints no caption buttons.
        return;
#endif
#if CEF_WINDOWS || CEF_MACOS
        _window->set_draggable_regions(_window, 1, &drag);
#endif
    }

    // The window's own callbacks carry its lifetime (WindowCreated, WindowDestroyed), and CEF's own close request
    // reaches it through can_close.
    void IChromiumBrowserHost.BrowserCreated()
    {
        if (Painted is { } painted) Browser.WatchFirstPaint(painted);
    }
    void IChromiumBrowserHost.BrowserClosed() { }
    bool IChromiumBrowserHost.CloseRequested() => false;

    // The page is the window's only control: there is nowhere else for the focus to go, and it stays in the page.
    void IChromiumBrowserHost.MoveFocusRequested(bool forward) { }

    // ── CEF's window callbacks, delegated here ────────────────────────────────────────────────────────

    private void WindowCreated(_cef_window_t* window)
    {
        _window = window;   // the argument's reference is KEPT, and released when the window goes
        // Passing a library object INTO CEF hands it one reference, so add one first.
        ((_cef_base_ref_counted_t*)_browserView)->add_ref((_cef_base_ref_counted_t*)_browserView);
        window->@base.add_child_view(&window->@base, &_browserView->@base);
        if (_options.Title is { } title) SetTitle(title);
        // Views paints a window and its browser view in its own light default until the page draws: a dark app showed a
        // light window for its first frames, and a splash's uncovered title strip and resize band light around it
        // (measured, Windows).
        if (Background is { } background)
        {
            var argb = (uint)background.ToArgb();
            ((_cef_view_t*)window)->set_background_color((_cef_view_t*)window, argb);
            ((_cef_view_t*)_browserView)->set_background_color((_cef_view_t*)_browserView, argb);
        }
        // A restored position was CEF's initial bounds already; anything else is centred, at the restored size if any.
        if (_plan is not { X: not null, Y: not null })
        {
            var size = new _cef_size_t { width = _plan?.Width ?? _options.Width, height = _plan?.Height ?? _options.Height };
            window->center_window(window, &size);
        }
        // Before it shows: a splash must be owned by the window by then, or showing the window raises it over the splash.
        if (Opening is { } opening)
        {
            var handle = (nint)window->get_window_handle(window);   // an HWND, an NSView*, or an X11 window id
            AppCallback.Run(() => opening(handle), ex => AppCallback.Log(_log, () => "[Shenora.Chromium] A window-opening hook failed", LogLevel.Warning, ex));
        }
        window->show(window);
        // At once: the splash shows over the window as soon as it can, not after the frame's own set-up below.
        if (Shown is { } shown)
            AppCallback.Run(shown, ex => AppCallback.Log(_log, () => "[Shenora.Chromium] A window-shown hook failed", LogLevel.Warning, ex));
#if CEF_WINDOWS
        // Before the page can ask for anything: the drag area needs the frame's hit-test from the start.
        var hwnd = window->get_window_handle(window);
        _captionHitTest = CaptionHitTest.Attach(hwnd, _captions, _options.FramelessChrome, _log);
        SystemMenu.Ensure(hwnd);
#endif
        // Without the hit-test, painted buttons would look real and do nothing.
        if (PaintsCaptionButtons && _captionHitTest is not null)
            _nativeCaptions = new NativeCaptionButtons(window, Palette);
        if (SplashStrip is not null && _options.FramelessChrome)
        {
            _kitStrip = new KitStrip(ApplyKitStrip);
            _kitStrip.Begin();
        }
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
            Struct->@base.@base.on_theme_changed = &ThemeChanged;
            Struct->on_window_activation_changed = &ActivationChanged;
            Struct->on_window_bounds_changed = &BoundsChanged;
            if (owner._geometry is not null)
            {
                Struct->get_initial_bounds = &InitialBounds;
                Struct->get_initial_show_state = &InitialShowState;
                Struct->on_window_closing = &Closing;
                // The minimum a restored size is floored at holds while the window runs too, or a window dragged
                // smaller would come back larger than it was left.
                Struct->@base.@base.get_minimum_size = &MinimumSize;
            }
#if CEF_MACOS
            // macOS's own buttons on a frameless window: the traffic lights, over the page's title bar.
            Struct->with_standard_window_buttons = &StandardWindowButtons;
#elif CEF_LINUX
            // Unanswered, the window has no WM_CLASS at all, which is what a dock matches to the app's .desktop file.
            Struct->get_linux_window_properties = &LinuxWindowProperties;
#endif
        }

#if CEF_LINUX
        [UnmanagedCallersOnly]
        private static int LinuxWindowProperties(_cef_window_delegate_t* self, _cef_window_t* window, _cef_linux_window_properties_t* properties)
        {
            using var w = new CefRef<_cef_window_t>(window);
            string name = LinuxPlatform.ProgramName, @class = LinuxPlatform.ProgramClass;
            Copy(name, &properties->wm_class_name);
            Copy(@class, &properties->wm_class_class);
            Copy(name, &properties->wayland_app_id);
            return 1;
        }

        // CEF owns the struct and frees what it holds, so the characters are copied into CEF's allocation.
        private static void Copy(string value, _cef_string_utf16_t* into)
        {
            fixed (char* c = value) Cef.cef_string_utf16_set((ushort*)c, (nuint)value.Length, into, 1);
        }
#endif

#if CEF_MACOS
        [UnmanagedCallersOnly]
        private static int StandardWindowButtons(_cef_window_delegate_t* self, _cef_window_t* window)
        {
            using var w = new CefRef<_cef_window_t>(window);
            return From<WindowDelegate>(self)._owner._options.NativeCaptionButtons ? 1 : 0;
        }
#endif

        [UnmanagedCallersOnly]
        private static void ActivationChanged(_cef_window_delegate_t* self, _cef_window_t* window, int active)
        {
            using var w = new CefRef<_cef_window_t>(window);
            AppCallback.Run(() => From<WindowDelegate>(self)._owner._nativeCaptions?.Activated(active == 1));
        }

        // A maximize or a restore resizes the window: maximize's glyph follows, and the saved state keeps its bounds.
        [UnmanagedCallersOnly]
        private static void BoundsChanged(_cef_window_delegate_t* self, _cef_window_t* window, _cef_rect_t* bounds)
        {
            using var w = new CefRef<_cef_window_t>(window);
            var owner = From<WindowDelegate>(self)._owner;
            AppCallback.Run(() => owner._nativeCaptions?.WindowSized());
            AppCallback.Run(() => { if (owner._kitStrip is { Active: true }) owner.PlaceStrip(); });
            var changed = *bounds;
            AppCallback.Run(() => owner._geometry?.Changed(window, changed));
            if (owner.Moved is { } moved) AppCallback.Run(moved);
        }

        // The restored position, in DIP screen coordinates; empty lets CEF place the window, which is then centred.
        [UnmanagedCallersOnly]
        private static _cef_rect_t InitialBounds(_cef_window_delegate_t* self, _cef_window_t* window)
        {
            using var w = new CefRef<_cef_window_t>(window);
            return From<WindowDelegate>(self)._owner._plan is { X: { } x, Y: { } y } plan
                ? new _cef_rect_t { x = x, y = y, width = plan.Width, height = plan.Height }
                : default;
        }

        [UnmanagedCallersOnly]
        private static cef_show_state_t InitialShowState(_cef_window_delegate_t* self, _cef_window_t* window)
        {
            using var w = new CefRef<_cef_window_t>(window);
            return From<WindowDelegate>(self)._owner._plan is { Maximized: true }
                ? cef_show_state_t.CEF_SHOW_STATE_MAXIMIZED
                : cef_show_state_t.CEF_SHOW_STATE_NORMAL;
        }

        [UnmanagedCallersOnly]
        private static _cef_size_t MinimumSize(_cef_view_delegate_t* self, _cef_view_t* view)
        {
            using var v = new CefRef<_cef_view_t>(view);
            var minimum = From<WindowDelegate>(self)._owner._geometry!.Minimum;
            return new _cef_size_t { width = minimum.Width, height = minimum.Height };
        }

        // Views resets a view's background to its theme's whenever the theme applies, the window's show included: set
        // only before it, it was gone by the first frame (measured, Windows). So again here.
        [UnmanagedCallersOnly]
        private static void ThemeChanged(_cef_view_delegate_t* self, _cef_view_t* view)
        {
            using var v = new CefRef<_cef_view_t>(view);
            try { From<WindowDelegate>(self)._owner.PaintBackground(view); }
            catch { /* nothing may unwind into CEF */ }
        }

        // Still whole: its bounds can be read, and the state is saved for the next launch.
        [UnmanagedCallersOnly]
        private static void Closing(_cef_window_delegate_t* self, _cef_window_t* window)
        {
            using var w = new CefRef<_cef_window_t>(window);
            var owner = From<WindowDelegate>(self)._owner;
            AppCallback.Run(() => owner._geometry?.Save(window));
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
            return From<WindowDelegate>(self)._owner._options.FramelessChrome ? 1 : 0;
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
            return From<WindowDelegate>(self)._owner.MayClose() ? 1 : 0;
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
        private readonly ChromiumWindow _owner;

        public BrowserViewDelegate(ChromiumWindow owner)
        {
            _owner = owner;
            Struct->get_browser_runtime_style = &Style;
            Struct->@base.on_theme_changed = &ThemeChanged;
        }

        [UnmanagedCallersOnly]
        private static cef_runtime_style_t Style(_cef_browser_view_delegate_t* self) => cef_runtime_style_t.CEF_RUNTIME_STYLE_ALLOY;

        // As the window's: Views resets the view's background whenever its theme applies.
        [UnmanagedCallersOnly]
        private static void ThemeChanged(_cef_view_delegate_t* self, _cef_view_t* view)
        {
            using var v = new CefRef<_cef_view_t>(view);
            try { From<BrowserViewDelegate>(self)._owner.PaintBackground(view); }
            catch { /* nothing may unwind into CEF */ }
        }
    }

    // The window's background on a view of it: until the page draws, what shows.
    private void PaintBackground(_cef_view_t* view)
    {
        if (Background is { } background) view->set_background_color(view, (uint)background.ToArgb());
    }
}
