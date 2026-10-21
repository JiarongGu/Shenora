using System.Drawing;
using System.Runtime.InteropServices;
using Shenora.Chromium.Interop;

namespace Shenora.Chromium.Host;

/// <summary>
/// A web-content browser embedded in one of the shell's windows (D97): a CEF <c>CefBrowserView</c> in the caller's
/// profile, floated over the host window's page as a CUSTOM-docked OVERLAY (<c>CefWindow::AddOverlayView</c>), placed by
/// the overlay controller it returns. This is the CEF-idiomatic way to host a second browser inside a Views window:
/// the overlay is a real view in the window's own widget, so it composites and routes keyboard and mouse like the
/// window's main <c>CefBrowserView</c> and the caption-button overlays already do — unlike a raw <c>WS_CHILD</c> HWND
/// layered over the window, which the Views compositor never presents and the Views focus system never reaches.
/// <para>
/// The browser view is ALLOY style, matching the host window (D84): an overlay browser view's runtime style must match
/// its window's, and the shell's windows resolve to Alloy through their own Alloy main browser view. Everything runs on
/// CEF's UI thread.
/// </para>
/// </summary>
internal sealed unsafe class ChromiumWebPanelBrowser
{
    private readonly ChromiumSessionBrowser _browser;
    private readonly ChromiumWindow _host;
    private readonly Color? _background;
    private readonly ViewDelegate _viewDelegate;
    private _cef_browser_view_t* _view;
    private _cef_overlay_controller_t* _overlay;
    private Rectangle _bounds;       // last requested, in the host window's CLIENT PIXELS
    private bool _torn;
    private bool _closing;
    private bool _hidden;            // SetVisible(false) hides until SetVisible(true) or the next SetBounds
    private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ChromiumWebPanelBrowser(ChromiumSessionBrowser browser, ChromiumWindow host, Rectangle bounds, Color? background)
    {
        _browser = browser;
        _host = host;
        _bounds = bounds;
        _background = background;
        _viewDelegate = new ViewDelegate(this);
        // The panel owns no window: its overlay is removed from the host window as its browser goes, however it goes.
        browser.WhenClosed(TearDownOverlay);
    }

    /// <summary>The embedded browser, driven as any session browser is (D91).</summary>
    public ChromiumSessionBrowser Browser => _browser;

    /// <summary>Completes once the browser exists (attached) and is set up, and its first page has settled.</summary>
    public Task Ready => SessionCalls.After(_created.Task, () => Task.WhenAll(_browser.Ready, _browser.FirstPage));

    /// <summary>
    /// Create the browser view in the profile's request context and float it over the host window's page. UI thread,
    /// once. The context's reference is the caller's; the view-create call consumes one this adds.
    /// </summary>
    public void Open(_cef_request_context_t* context)
    {
        var settings = new _cef_browser_settings_t { size = (nuint)sizeof(_cef_browser_settings_t) };
        if (_background is { } color) settings.background_color = (uint)color.ToArgb();
        // On about:blank, as the window paths are and for the same reason (ChromiumSessionWindow.Open): a first page
        // loading as the view is made can finish after the host's first navigation has begun and take it for its own.
        const string blank = "about:blank";
        fixed (char* u = blank)
        {
            var url = CefStrings.View(u, blank.Length);
            ((_cef_base_ref_counted_t*)context)->add_ref((_cef_base_ref_counted_t*)context);   // the call consumes one
            _view = Cef.cef_browser_view_create(_browser.ClientForCef(), &url, &settings, null, context, _viewDelegate.ForCef());
        }
        _viewDelegate.Release();   // CEF holds its own for as long as it uses it
        if (_view == null)
        {
            _browser.NeverMade();   // its client, which nothing would ever retire
            throw new InvalidOperationException("CEF would not create the web panel's browser view.");
        }
        _overlay = _host.AddCustomOverlay(&_view->@base, canActivate: true);
        if (_overlay == null)
        {
            using (new CefRef<_cef_browser_view_t>(_view)) _view = null;
            _browser.NeverMade();
            throw new InvalidOperationException("The host window is gone; a web panel cannot be placed in it.");
        }
        ApplyBounds(_bounds);
    }

    /// <summary>Place the panel in the host window's client area, in the window's CLIENT PIXELS. Placing it also shows
    /// it, winning over an earlier <see cref="SetVisible"/>(false). UI thread.</summary>
    public void SetBounds(Rectangle bounds)
    {
        _bounds = bounds;
        _hidden = false;   // placing the panel shows it, as SetBounds moving a WS_CHILD on-screen did
        if (_overlay != null && !_torn) ApplyBounds(bounds);
    }

    /// <summary>
    /// Show or hide the panel without moving it. A hide wins until the next <see cref="SetVisible"/>(true) or
    /// <see cref="SetBounds"/> — <see cref="ApplyBounds"/> leaves a hidden panel hidden. UI thread.
    /// </summary>
    public void SetVisible(bool visible)
    {
        _hidden = !visible;
        if (_overlay != null && !_torn) _overlay->set_visible(_overlay, visible ? 1 : 0);
    }

    // The overlay controller takes Views DIP, as the caption-button overlays do; the caller's client pixels are the
    // window's device pixels, so divide by its scale. A moved window reaches another display's scale on the next
    // SetBounds, which the host drives on every resize. A hidden panel keeps its bounds but is not shown.
    private void ApplyBounds(Rectangle bounds)
    {
        var scale = _host.DeviceScaleFactor;
        if (scale <= 0) scale = 1f;
        var rect = new _cef_rect_t
        {
            x = (int)Math.Round(bounds.X / scale),
            y = (int)Math.Round(bounds.Y / scale),
            width = Math.Max(1, (int)Math.Round(bounds.Width / scale)),
            height = Math.Max(1, (int)Math.Round(bounds.Height / scale)),
        };
        _overlay->set_bounds(_overlay, &rect);
        if (!_hidden && _overlay->is_visible(_overlay) == 0) _overlay->set_visible(_overlay, 1);
    }

    /// <summary>
    /// Close the panel. UI thread; idempotent. An existing browser closes through its own host, as a windowless session
    /// browser does, and its close removes the overlay (<see cref="TearDownOverlay"/>, run as the browser goes). One that
    /// was never made has nothing to close it, so its overlay and client are retired here.
    /// </summary>
    public void Close()
    {
        if (_closing) return;
        _closing = true;
        if (_created.Task.IsCompletedSuccessfully)
        {
            _browser.Close();
        }
        else
        {
            TearDownOverlay();
            _browser.NeverMade();
        }
    }

    // The browser was made (on_browser_created, after the view was added to the window): keep it and let the open through.
    private void BrowserCreated(_cef_browser_t* browser)
    {
        ((_cef_base_ref_counted_t*)browser)->add_ref((_cef_base_ref_counted_t*)browser);   // Attach keeps it
        _browser.Attach(browser);
        _created.TrySetResult();
    }

    // The browser left the view for good (on_browser_destroyed): the open that waited for it ends, and the overlay goes.
    private void BrowserDestroyed()
    {
        _created.TrySetCanceled();
        TearDownOverlay();
    }

    // Remove the overlay from the host window and let the view go. Run as the browser closes (WhenClosed) and when the
    // view is destroyed; idempotent, and safe once the browser is gone from the view.
    private void TearDownOverlay()
    {
        if (_torn) return;
        _torn = true;
        if (_overlay != null)
        {
            if (_overlay->is_valid(_overlay) == 1) _overlay->destroy(_overlay);
            using (new CefRef<_cef_overlay_controller_t>(_overlay)) { }
            _overlay = null;
        }
        if (_view != null) { using (new CefRef<_cef_browser_view_t>(_view)) { } _view = null; }
    }

    /// <summary>The browser ALLOY style, matching the host window (D84), with its creation and destruction reported.</summary>
    private sealed class ViewDelegate : CefObject<_cef_browser_view_delegate_t>
    {
        private readonly ChromiumWebPanelBrowser _owner;

        public ViewDelegate(ChromiumWebPanelBrowser owner)
        {
            _owner = owner;
            Struct->get_browser_runtime_style = &Style;
            Struct->on_browser_created = &OnBrowserCreated;
            Struct->on_browser_destroyed = &OnBrowserDestroyed;
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

        [UnmanagedCallersOnly]
        private static void OnBrowserDestroyed(_cef_browser_view_delegate_t* self, _cef_browser_view_t* view, _cef_browser_t* browser)
        {
            using var v = new CefRef<_cef_browser_view_t>(view);
            using var b = new CefRef<_cef_browser_t>(browser);
            AppCallback.Run(From<ViewDelegate>(self)._owner.BrowserDestroyed);
        }
    }
}
