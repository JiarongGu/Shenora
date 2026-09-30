using System.Drawing;
using System.Runtime.InteropServices;
using Shenora.Chromium.Interop;
using Shenora.Core.Sessions;
using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// A session's browser in a window of its own (D91): an interactive session's, kept out of sight until revealed and
/// taking the main window's input while it shows, or a pool's development window. CEF's own window around a browser view
/// in the session's request context. Everything runs on CEF's UI thread.
/// </summary>
internal sealed unsafe class ChromiumSessionWindow : ISessionWindow
{
    private readonly ChromiumSessionBrowser _browser;
    private readonly string _title;
    private readonly Size _size;
    private readonly Size _minimum;
    private readonly Point? _place;             // where a development window goes; null centres it
    private readonly IUiInteraction? _modalTo;  // the main window's input, taken while this shows; null for a development window
    private readonly WindowDelegate _delegate;
    private readonly ViewDelegate _viewDelegate;
    private _cef_window_t* _window;
    private _cef_browser_view_t* _view;
    private bool _revealed;
    private bool _blocking;
    private bool _pastClosing;
    private bool _windowGone;
    private bool _browserGone;
    private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ChromiumSessionWindow(ChromiumSessionBrowser browser, string title, Size size, Size minimum, bool revealed, IUiInteraction? modalTo,
        Point? place = null)
    {
        _browser = browser;
        _title = title;
        _size = size;
        _minimum = minimum;
        _revealed = revealed;
        _modalTo = modalTo;
        _place = place;
        _delegate = new WindowDelegate(this);
        _viewDelegate = new ViewDelegate(this);
        browser.CloseWindow = () => Close();
        browser.WhenClosed(() =>
        {
            _browserGone = true;
            // A browser gone on its own takes its window with it: the flow ends with the window.
            if (!_windowGone && _window != null) Close();
            Settle();
        });
    }

    public ISessionBrowser Browser => _browser;

    /// <summary>Completes once the browser exists and is set up: the window can be handed over.</summary>
    public Task Opened => SessionCalls.After(_created.Task, () => _browser.Ready);

    public Func<bool, bool>? Closing { get; set; }

    public bool IsRevealed => _revealed;

    public Task Closed => _closed.Task;

    /// <summary>Create the browser view and the window. UI thread, once. The request context's reference is the caller's.</summary>
    public void Open(_cef_request_context_t* context, Color? background)
    {
        var settings = new _cef_browser_settings_t { size = (nuint)sizeof(_cef_browser_settings_t) };
        if (background is { } color) settings.background_color = (uint)color.ToArgb();
        // No first page, as a WebView2 has none: one loading as the window opens completed after the driver's first
        // navigation had begun, which took it for its own (measured: the title read empty).
        var url = default(_cef_string_utf16_t);
        ((_cef_base_ref_counted_t*)context)->add_ref((_cef_base_ref_counted_t*)context);   // the call consumes one
        _view = Cef.cef_browser_view_create(_browser.ClientForCef(), &url, &settings, null, context, _viewDelegate.ForCef());
        _viewDelegate.Release();   // CEF holds its own for as long as it uses it
        if (_view == null)
        {
            _delegate.Release();
            throw new InvalidOperationException("CEF would not create the session window's browser view.");
        }
        using var window = new CefRef<_cef_window_t>(Cef.cef_window_create_top_level(_delegate.ForCef()));
        _delegate.Release();
        if (window.IsNull)
        {
            using (new CefRef<_cef_browser_view_t>(_view)) _view = null;
            throw new InvalidOperationException("CEF would not create the session window.");
        }
    }

    public void Reveal()
    {
        if (_revealed || _window == null) return;
        _revealed = true;
        var client = _window->get_client_area_bounds_in_screen(_window);
        Apply(new Size(client.width, client.height) + _frame, centre: true);   // the size it has, fitted or not, centred
        Show();
    }

    public void FitToContent(int cssWidth, int cssHeight)
    {
        if (_window == null) return;
        // The page's CSS pixels are the window's device-independent ones.
        SizeContent(FitContent(cssWidth, cssHeight, WorkArea().Size), centre: _revealed);
    }

    /// <summary>The content size for a page's <paramref name="cssWidth"/> × <paramref name="cssHeight"/> CSS px, within
    /// the display's work area less a margin for the frame, as the WinForms window fits. Internal: tested.</summary>
    internal static Size FitContent(int cssWidth, int cssHeight, Size workArea) =>
        new(Math.Max(1, Math.Min(cssWidth, workArea.Width - 40)), Math.Max(1, Math.Min(cssHeight, workArea.Height - 60)));

    // What the window adds around its content: learnt as it first sizes, and 0 where a size is the content's.
    private Size _frame;

    /// <summary>
    /// Size the window so its content is <paramref name="content"/>, as a WebView2 form's <c>ClientSize</c> is. A size is
    /// the whole window's on Windows and the content's on Linux and macOS (measured: 640 × 480 asked, 627 × 445 of content
    /// on Windows), so the first sizing, before the window shows, reads the content back and learns the frame
    /// (<paramref name="learn"/>); every later one adds it. Read back from a window on screen, the content did not match
    /// what its page then had (measured: a fit to 500 × 360 came out 487 × 340).
    /// </summary>
    private void SizeContent(Size content, bool centre, bool learn = false)
    {
        Apply(content + _frame, centre);
        if (!learn) return;
        var client = _window->get_client_area_bounds_in_screen(_window);
        _frame = new Size(Math.Max(0, content.Width - client.width), Math.Max(0, content.Height - client.height));
        if (!_frame.IsEmpty) Apply(content + _frame, centre);
    }

    private void Apply(Size size, bool centre)
    {
        var asked = new _cef_size_t { width = size.Width, height = size.Height };
        if (centre) _window->center_window(_window, &asked);
        else ((_cef_view_t*)_window)->set_size((_cef_view_t*)_window, &asked);
    }

    public void Close()
    {
        if (_window == null || _windowGone) return;
        _pastClosing = true;
        _window->close(_window);
    }

    private Rectangle WorkArea()
    {
        using var display = new CefRef<_cef_display_t>(_window->get_display(_window));
        if (display.IsNull) return new Rectangle(0, 0, 1280, 800);
        var area = display.Ptr->get_work_area(display.Ptr);
        return new Rectangle(area.x, area.y, area.width, area.height);
    }

    private void Show()
    {
        _window->show(_window);
        _window->activate(_window);
        _browser.Focus();
        if (_modalTo is not null && !_blocking)
        {
            _blocking = true;
            AppCallback.Run(_modalTo.BlockInteraction);
        }
    }

    // CEF asks before the window closes: a person's close (the X, the OS's own), or Close(), which goes past the hold.
    private bool MayClose()
    {
        if (!_pastClosing && Closing is { } closing && !AppCallback.RunOrDefault(() => closing(true), fallback: true)) return false;
        _browser.BeginClose();
        return true;
    }

    private void WindowCreated(_cef_window_t* window)
    {
        _window = window;   // the argument's reference is KEPT, and released when the window goes
        ((_cef_base_ref_counted_t*)_view)->add_ref((_cef_base_ref_counted_t*)_view);   // add_child_view consumes one
        window->@base.add_child_view(&window->@base, &_view->@base);
        fixed (char* t = _title)
        {
            var s = CefStrings.View(t, _title.Length);
            window->set_title(window, &s);
        }
        if (_place is not null) Show();   // a development window: placed by its initial bounds
        else
        {
            // Its content at the size asked, shown or not: a page kept out of sight lays out as it will when revealed.
            SizeContent(_size, centre: _revealed, learn: true);
            if (_revealed) Show();
            // Otherwise made and kept out of sight: its browser runs, and Reveal() shows it.
        }
    }

    private void WindowDestroyed()
    {
        _windowGone = true;
        if (_blocking)
        {
            _blocking = false;
            AppCallback.Run(_modalTo!.UnblockInteraction);
        }
        if (_window != null) { using var w = new CefRef<_cef_window_t>(_window); _window = null; }
        if (_view != null) { using var v = new CefRef<_cef_browser_view_t>(_view); _view = null; }
        // Gone before its browser was: the open that waits for it ends, and what waits for the browser to go is told now.
        if (_created.TrySetCanceled()) _browser.NeverMade();
        Settle();
    }

    // Closed once the window AND its browser are gone, and with it the hold on the profile.
    private void Settle()
    {
        if (_windowGone && _browserGone) _closed.TrySetResult();
    }

    private void BrowserCreated(_cef_browser_t* browser)
    {
        ((_cef_base_ref_counted_t*)browser)->add_ref((_cef_base_ref_counted_t*)browser);   // Attach keeps it
        _browser.Attach(browser);
        _created.TrySetResult();
    }

    // ── the structs CEF sees ──────────────────────────────────────────────────────────────────────────

    private sealed class WindowDelegate : CefObject<_cef_window_delegate_t>
    {
        private readonly ChromiumSessionWindow _owner;

        public WindowDelegate(ChromiumSessionWindow owner)
        {
            _owner = owner;
            Struct->on_window_created = &OnCreated;
            Struct->on_window_destroyed = &OnDestroyed;
            Struct->can_close = &CanClose;
            Struct->can_resize = &Yes;
            Struct->can_maximize = &Yes;
            Struct->can_minimize = &Yes;
            Struct->@base.@base.get_preferred_size = &PreferredSize;
            Struct->@base.@base.get_minimum_size = &MinimumSize;
            if (owner._place is not null) Struct->get_initial_bounds = &InitialBounds;
#if CEF_LINUX
            Struct->get_linux_window_properties = &LinuxWindowProperties;
#endif
        }

#if CEF_LINUX
        // The app's WM_CLASS, as its own windows have, so a dock shows the session's window as the app's.
        [UnmanagedCallersOnly]
        private static int LinuxWindowProperties(_cef_window_delegate_t* self, _cef_window_t* window, _cef_linux_window_properties_t* properties)
        {
            using var w = new CefRef<_cef_window_t>(window);
            Copy(LinuxPlatform.ProgramName, &properties->wm_class_name);
            Copy(LinuxPlatform.ProgramClass, &properties->wm_class_class);
            Copy(LinuxPlatform.ProgramName, &properties->wayland_app_id);
            return 1;
        }

        private static void Copy(string value, _cef_string_utf16_t* into)
        {
            fixed (char* c = value) Cef.cef_string_utf16_set((ushort*)c, (nuint)value.Length, into, 1);
        }
#endif

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
        private static int CanClose(_cef_window_delegate_t* self, _cef_window_t* window)
        {
            using var w = new CefRef<_cef_window_t>(window);
            return AppCallback.RunOrDefault(From<WindowDelegate>(self)._owner.MayClose, fallback: true) ? 1 : 0;
        }

        [UnmanagedCallersOnly]
        private static int Yes(_cef_window_delegate_t* self, _cef_window_t* window)
        {
            using var w = new CefRef<_cef_window_t>(window);
            return 1;
        }

        [UnmanagedCallersOnly]
        private static _cef_size_t PreferredSize(_cef_view_delegate_t* self, _cef_view_t* view)
        {
            using var v = new CefRef<_cef_view_t>(view);
            var size = From<WindowDelegate>(self)._owner._size;
            return new _cef_size_t { width = size.Width, height = size.Height };
        }

        [UnmanagedCallersOnly]
        private static _cef_size_t MinimumSize(_cef_view_delegate_t* self, _cef_view_t* view)
        {
            using var v = new CefRef<_cef_view_t>(view);
            var minimum = From<WindowDelegate>(self)._owner._minimum;
            return new _cef_size_t { width = minimum.Width, height = minimum.Height };
        }

        [UnmanagedCallersOnly]
        private static _cef_rect_t InitialBounds(_cef_window_delegate_t* self, _cef_window_t* window)
        {
            using var w = new CefRef<_cef_window_t>(window);
            var owner = From<WindowDelegate>(self)._owner;
            var place = owner._place!.Value;
            return new _cef_rect_t { x = place.X, y = place.Y, width = owner._size.Width, height = owner._size.Height };
        }
    }

    /// <summary>The browser ALLOY style, as the app's own pages are (D84), and its creation reported.</summary>
    private sealed class ViewDelegate : CefObject<_cef_browser_view_delegate_t>
    {
        private readonly ChromiumSessionWindow _owner;

        public ViewDelegate(ChromiumSessionWindow owner)
        {
            _owner = owner;
            Struct->get_browser_runtime_style = &Style;
            Struct->on_browser_created = &OnBrowserCreated;
        }

        [UnmanagedCallersOnly]
        private static cef_runtime_style_t Style(_cef_browser_view_delegate_t* self) => cef_runtime_style_t.CEF_RUNTIME_STYLE_ALLOY;

        [UnmanagedCallersOnly]
        private static void OnBrowserCreated(_cef_browser_view_delegate_t* self, _cef_browser_view_t* view, _cef_browser_t* browser)
        {
            using var v = new CefRef<_cef_browser_view_t>(view);
            using var b = new CefRef<_cef_browser_t>(browser);
            var owner = From<ViewDelegate>(self)._owner;
            var made = (nint)browser;
            AppCallback.Run(() => owner.BrowserCreated((_cef_browser_t*)made));
        }
    }
}
