#if CEF_MACOS
using System.Diagnostics;
using System.Drawing;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;
using static Shenora.Chromium.Host.MacSplashNative;

namespace Shenora.Chromium.Host;

/// <summary>
/// The displays as Cocoa states them (points, y up from the primary display's bottom), turned into the top-left
/// device-independent pixels CEF places windows in. On macOS a point IS CEF's DIP, so only the axis flips.
/// </summary>
internal static class MacScreens
{
    internal readonly record struct Screen(CGRect Frame, CGRect Visible, double Scale);

    /// <summary>Every display, the menu-bar one first. Main thread.</summary>
    public static IReadOnlyList<Screen> All()
    {
        var screens = Send(Class("NSScreen"), "screens");
        var count = GetLong(screens, "count");
        var all = new List<Screen>();
        for (var i = 0; i < count; i++)
        {
            var screen = At(screens, i);
            all.Add(new Screen(GetRect(screen, "frame"), GetRect(screen, "visibleFrame"), GetDouble(screen, "backingScaleFactor")));
        }
        return all;
    }

    public static IReadOnlyList<Rectangle> WorkAreasDip(IReadOnlyList<Screen> screens)
    {
        if (screens.Count == 0) return [];
        var top = screens[0].Frame.Height;
        return [.. screens.Select(s => new Rectangle((int)s.Visible.X, (int)(top - s.Visible.Y - s.Visible.Height), (int)s.Visible.Width, (int)s.Visible.Height))];
    }

    /// <summary>
    /// Where a new window with a content of <paramref name="width"/>×<paramref name="height"/> opens when nothing places
    /// it: CEF centres it with AppKit's own [NSWindow center], which sits it above the middle, so this asks AppKit, with a
    /// window that is never shown (titled, or borderless when <paramref name="frameless"/>, so its frame is the same
    /// size). Its content's rect in CEF's top-left DIPs. Main thread.
    /// </summary>
    public static Rectangle CentredContentDip(int width, int height, bool frameless)
    {
        const ulong Titled = 1, Closable = 2, Miniaturizable = 4, Resizable = 8;
        var window = InitWindow(Send(Class("NSWindow"), "alloc"), new CGRect(0, 0, width, height),
            frameless ? 0 : Titled | Closable | Miniaturizable | Resizable);
        if (window == 0) throw new InvalidOperationException("A window could not be made to centre the card on.");
        try
        {
            Send(window, "center");
            var frame = GetRect(window, "frame");
            var titleBar = frame.Height - height;
            var top = All() is { Count: > 0 } screens ? screens[0].Frame.Height : frame.Y + frame.Height;
            return new Rectangle((int)Math.Round(frame.X), (int)Math.Round(top - frame.Y - frame.Height + titleBar), width, height);
        }
        finally
        {
            Send(window, "release");
        }
    }

    /// <summary>The window frame (Cocoa coordinates) for a plan, and its display's backing scale.</summary>
    public static (CGRect Frame, double Scale) ToFrame(ChromiumWindowGeometry.Plan plan, IReadOnlyList<Screen> screens)
    {
        if (screens.Count == 0) return (new CGRect(plan.X ?? 0, plan.Y ?? 0, plan.Width, plan.Height), 1);
        var top = screens[0].Frame.Height;
        var screen = screens[0];
        if (plan is { X: { } x, Y: { } y })
        {
            double cx = x + (plan.Width / 2.0), cy = top - (y + (plan.Height / 2.0));
            screen = screens.FirstOrDefault(s => cx >= s.Frame.X && cx < s.Frame.X + s.Frame.Width && cy >= s.Frame.Y && cy < s.Frame.Y + s.Frame.Height, screens[0]);
        }
        if (plan.Maximized) return (screen.Visible, screen.Scale);
        if (plan is not { X: { } px, Y: { } py })
        {
            var v = screen.Visible;
            return (new CGRect(v.X + ((v.Width - plan.Width) / 2), v.Y + ((v.Height - plan.Height) / 2), plan.Width, plan.Height), screen.Scale);
        }
        return (new CGRect(px, top - py - plan.Height, plan.Width, plan.Height), screen.Scale);
    }
}

/// <summary>
/// The macOS splash: a borderless <c>NSWindow</c> made on the main thread, showing frames a thread of its own renders
/// through CoreGraphics and CoreText. They go into a layer the splash owns (no delegate, so AppKit manages none of it),
/// over the window's own. Either the card, before the main window exists, its frames committed from the render thread in
/// explicit Core Animation transactions (the only way to draw while the main thread composes the app and starts CEF); or
/// the splash over the main window's content below its title bar or strip, its frames posted to the main thread, which
/// CEF's loop runs, joined to the main window as a child once that is on screen, so it stays above it and moves with it.
/// It takes the clicks over it (a borderless window never becomes key).
/// </summary>
internal sealed unsafe class MacSplashSurface(ILogger? log) : ISplashSurface
{
    private const double CornerRadius = 10;   // a macOS window's own, in points; a zoomed window keeps it

    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _gate = new();
    private Thread? _thread;
    private MacSplashPainter? _painter;
    private SplashRender? _render;
    private nint _window, _layer, _parent;
    private SplashOverlayLayout _layout;
    private Size _sizePx;
    private float _scale = 1;
    private volatile bool _closing, _fading, _animating, _attached;
    private long _fadeStart;
    private TimeSpan _fade;
    private Action? _fadeDone;
    private int _disposed;

    public void ShowCard(Rectangle dipRect, SplashRender render)
    {
        // NSApp must be CEF's own before anything makes one, a window included (MacPlatform).
        MacPlatform.Prepare();
        LoadFrameworks();
        if (!IsMainThread) throw new InvalidOperationException("The macOS splash opens on the main thread.");
        var (frame, scale) = MacScreens.ToFrame(new ChromiumWindowGeometry.Plan(dipRect.Width, dipRect.Height, dipRect.X, dipRect.Y, false), MacScreens.All());
        Open(frame, scale, render);
    }

    // CEF's UI thread, which is the main thread on macOS, before CEF shows the main window. CEF hands over the window's
    // content NSView. From here frames go to the main thread, which CEF's loop runs.
    public void ShowOver(nint mainWindow, SplashOverlayLayout layout, SplashRender render)
    {
        LoadFrameworks();
        if (!IsMainThread) throw new InvalidOperationException("The macOS splash opens on the main thread.");
        var parent = GetBool(mainWindow, "isKindOfClass:", Class("NSWindow")) ? mainWindow : Send(mainWindow, "window");
        if (parent == 0) throw new InvalidOperationException("The main window has no NSWindow.");
        _parent = parent;
        _layout = layout;
        _attached = true;
        Open(RenderArea(parent, layout), GetDouble(parent, "backingScaleFactor"), render);
    }

    // Main thread, the main window on screen: placed at its render area as it is now, then joined to it as a child,
    // which orders it in above it. Until then it is made but not on screen, so it never floats alone.
    public void Reveal(Action shown)
    {
        try
        {
            var window = _window;
            if (_parent == 0 || window == 0) return;
            FollowOwner();
            AddChild(_parent, window);
            Send(window, "orderFront:", 0);
        }
        finally
        {
            shown();
        }
    }

    // The main window's render area in screen points: its content rect (below a framed window's title bar), below the
    // strip on a frameless one. AppKit's y grows upward, so the strip comes off the height and the origin stays.
    private static CGRect RenderArea(nint parent, SplashOverlayLayout layout)
    {
        var content = GetRect(parent, "contentRectForFrameRect:", GetRect(parent, "frame"));
        if (layout.Frameless) content.Height = Math.Max(1, content.Height - layout.StripDips);
        return content;
    }

    private void Open(CGRect frame, double scale, SplashRender render)
    {
        _render = render;
        var window = InitWindow(Send(Class("NSWindow"), "alloc"), frame);
        if (window == 0) throw new InvalidOperationException("The splash window could not be made.");
        _window = window;
        SendBool(window, "setReleasedWhenClosed:", false);
        SendBool(window, "setOpaque:", false);
        Send(window, "setBackgroundColor:", Send(Class("NSColor"), "clearColor"));
        // The card's shadow is the system's own, cast from its rounded content; the window's splash has its window's.
        var card = _parent == 0;
        SendBool(window, "setHasShadow:", card);
        var view = Send(window, "contentView");
        SendBool(view, "setWantsLayer:", true);
        // The splash's own layer, +1 from new and held until its thread is done with it: the window's own layer goes
        // with the window, which may close while a frame is still being drawn.
        _layer = Send(Class("CALayer"), "new");
        SendBool(_layer, "setMasksToBounds:", true);
        SendDouble(_layer, "setCornerRadius:", CornerRadius);
        // Over the main window only its bottom corners are the window's: the top ones sit under its title bar or strip.
        // The content view is not flipped, so the bottom corners are the layer's minimum-y ones.
        if (!card) SendLong(_layer, "setMaskedCorners:", 3 /* kCALayerMinXMinYCorner | kCALayerMaxXMinYCorner */);
        Send(Send(view, "layer"), "addSublayer:", _layer);
        Resize(frame, scale);
        _painter = new MacSplashPainter(log);
        Present();
        if (card) Send(window, "orderFrontRegardless");
        Send(Class("CATransaction"), "flush");
        _thread = new Thread(Loop) { IsBackground = true, Name = "Shenora splash" };
        _thread.Start();
    }

    public void Invalidate() => _wake.Set();

    // Main thread.
    public void FollowOwner()
    {
        var window = _window;
        if (_parent == 0 || window == 0) return;
        var frame = RenderArea(_parent, _layout);
        var scale = GetDouble(_parent, "backingScaleFactor");
        SetFrame(window, frame);
        // A move keeps the frame drawn: a new one only when the size or the scale changed, or a drag would draw one per
        // step.
        if (SameSize(frame, scale)) return;
        Resize(frame, scale);
        _wake.Set();
    }

    private bool SameSize(CGRect frame, double scale)
    {
        scale = scale > 0 ? scale : 1;
        var sizePx = new Size((int)Math.Round(frame.Width * scale), (int)Math.Round(frame.Height * scale));
        lock (_gate) return _scale == (float)scale && _sizePx == sizePx;
    }

    public void FadeOut(TimeSpan duration, Action done)
    {
        _fadeDone = done;
        _fade = duration;
        _fadeStart = Stopwatch.GetTimestamp();
        _fading = true;
        _wake.Set();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _closing = true;
        _wake.Set();
        if (_thread is { } thread && thread.ManagedThreadId != Environment.CurrentManagedThreadId) thread.Join(TimeSpan.FromSeconds(1));
        CloseWindow();
        if (_thread is null) ReleaseDrawing();   // with no thread, nothing else ever drew with them
    }

    // Main thread: the window's size, and the splash layer's, with no implicit animation of the change.
    private void Resize(CGRect frame, double scale)
    {
        scale = scale > 0 ? scale : 1;
        lock (_gate)
        {
            _scale = (float)scale;
            _sizePx = new Size((int)Math.Round(frame.Width * scale), (int)Math.Round(frame.Height * scale));
        }
        Transaction(() =>
        {
            SendRect(_layer, "setFrame:", new CGRect(0, 0, frame.Width, frame.Height));
            SendDouble(_layer, "setContentsScale:", scale);
        });
    }

    private void Loop()
    {
        while (!_closing)
        {
            _wake.WaitOne(_fading ? 16 : _animating ? 33 : Timeout.Infinite);
            if (_closing) break;
            // A pool per pass: CoreText and the bridge autorelease, and this thread has no run loop to drain them.
            var pool = objc_autoreleasePoolPush();
            try
            {
                if (_fading)
                {
                    if (FadeStep()) break;
                    continue;   // the content stands still while it fades
                }
                AppCallback.Run(Present, ex => AppCallback.Log(log, () => "[Shenora.Chromium] The splash failed to draw", LogLevel.Warning, ex));
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        }
        ReleaseDrawing();   // here, the one thread that draws with them
    }

    private void Present()
    {
        if (_painter is null || _render is null || _layer == 0) return;
        Size size;
        float scale;
        lock (_gate)
        {
            size = _sizePx;
            scale = _scale;
        }
        var frame = _render(size, scale, _painter);
        var image = _painter.Paint(frame);
        _animating = frame.Animated;
        Commit(() =>
        {
            try { Send(_layer, "setContents:", image); }
            finally { CGImageRelease(image); }
        });
    }

    // True once the fade is over and the window gone.
    private bool FadeStep()
    {
        var progress = Stopwatch.GetElapsedTime(_fadeStart) / _fade;
        var opacity = (float)Math.Max(0, 1 - progress);
        Commit(() => SendFloat(_layer, "setOpacity:", opacity));
        if (progress < 1) return false;
        _fading = false;
        CloseWindow();
        var done = Interlocked.Exchange(ref _fadeDone, null);
        if (done is not null) AppCallback.Run(done);
        return true;
    }

    // A layer change from the render thread: on the main thread once CEF's loop runs there, else committed here in an
    // explicit transaction, the only kind that reaches the screen from a thread with no run loop.
    private void Commit(Action change)
    {
        if (_attached && !IsMainThread && CefTask.Post(cef_thread_id_t.TID_UI, () => Transaction(change))) return;
        Transaction(change);
    }

    private static void Transaction(Action change)
    {
        var transaction = Class("CATransaction");
        Send(transaction, "begin");
        SendBool(transaction, "setDisableActions:", true);
        try { change(); }
        finally { Send(transaction, "commit"); }
    }

    // The painter and the splash's own layer, once nothing draws with them.
    private void ReleaseDrawing()
    {
        AppCallback.Run(() => Interlocked.Exchange(ref _painter, null)?.Dispose());
        var layer = Interlocked.Exchange(ref _layer, 0);
        if (layer == 0) return;
        // A frame already posted to the main thread may still name it: released there, after it.
        if (!IsMainThread && CefTask.Post(cef_thread_id_t.TID_UI, () => Send(layer, "release"))) return;
        Send(layer, "release");
    }

    // AppKit closes a window on the main thread only: here if this is it, else on CEF's UI thread, which is it.
    private void CloseWindow()
    {
        var window = Interlocked.Exchange(ref _window, 0);
        if (window == 0) return;
        void Close()
        {
            if (_parent != 0) Send(_parent, "removeChildWindow:", window);
            Send(window, "orderOut:", 0);
            Send(window, "close");
            Send(window, "release");
        }
        if (IsMainThread) AppCallback.Run(Close);
        else if (!CefTask.Post(cef_thread_id_t.TID_UI, () => AppCallback.Run(Close)))
        {
            // No CEF loop yet: the main run loop, whenever it runs. Never attached, so there is no parent to leave.
            PerformOnMain(window, "close");
            PerformOnMain(window, "release");
        }
    }
}
#endif
