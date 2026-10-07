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
/// over the window's own: committed from the render thread in explicit Core Animation transactions until the main window
/// exists, the only way to draw while the main thread composes the app and starts CEF, and posted to the main thread
/// once CEF's loop runs it. It takes the clicks over it (a borderless window never becomes key), and joins the main
/// window as a child, so it stays above it and moves with it.
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
    private Size _sizePx;
    private float _scale = 1;
    private volatile bool _closing, _fading, _animating, _attached;
    private long _fadeStart;
    private TimeSpan _fade;
    private Action? _fadeDone;
    private int _disposed;

    public void Show(ChromiumWindowGeometry.Plan placement, SplashRender render)
    {
        // NSApp must be CEF's own before anything makes one, a window included (MacPlatform).
        MacPlatform.Prepare();
        LoadFrameworks();
        if (!IsMainThread) throw new InvalidOperationException("The macOS splash opens on the main thread.");
        _render = render;
        var (frame, scale) = MacScreens.ToFrame(placement, MacScreens.All());
        var window = InitWindow(Send(Class("NSWindow"), "alloc"), frame);
        if (window == 0) throw new InvalidOperationException("The splash window could not be made.");
        _window = window;
        // A window with no saved place is centred the way Chromium centres the main one, by AppKit's own [NSWindow
        // center], which sits it above the middle: centred exactly, the splash jumped 86 points as it snapped; centred
        // this way it moves 1 point (measured, macOS 15).
        if (placement is { X: null } and { Maximized: false })
        {
            Send(window, "center");
            frame = GetRect(window, "frame");
        }
        SendBool(window, "setReleasedWhenClosed:", false);
        SendBool(window, "setOpaque:", false);
        Send(window, "setBackgroundColor:", Send(Class("NSColor"), "clearColor"));
        SendBool(window, "setHasShadow:", false);
        var view = Send(window, "contentView");
        SendBool(view, "setWantsLayer:", true);
        // The splash's own layer, +1 from new and held until its thread is done with it: the window's own layer goes
        // with the window, which may close while a frame is still being drawn.
        _layer = Send(Class("CALayer"), "new");
        SendBool(_layer, "setMasksToBounds:", true);
        SendDouble(_layer, "setCornerRadius:", CornerRadius);
        Send(Send(view, "layer"), "addSublayer:", _layer);
        Resize(frame, scale);
        _painter = new MacSplashPainter(log);
        Present();
        Send(window, "orderFrontRegardless");
        Send(Class("CATransaction"), "flush");
        _thread = new Thread(Loop) { IsBackground = true, Name = "Shenora splash" };
        _thread.Start();
    }

    public void Invalidate() => _wake.Set();

    // CEF's UI thread, which is the main thread on macOS. CEF hands over the window's content NSView.
    public void Attach(nint mainWindow)
    {
        var window = _window;
        if (window == 0 || mainWindow == 0 || Volatile.Read(ref _disposed) != 0) return;
        var parent = GetBool(mainWindow, "isKindOfClass:", Class("NSWindow")) ? mainWindow : Send(mainWindow, "window");
        if (parent == 0) return;
        _parent = parent;
        AddChild(parent, window);
        _attached = true;   // from here frames go to the main thread, which CEF's loop now runs
        FollowOwner();
    }

    // Main thread.
    public void FollowOwner()
    {
        var window = _window;
        if (_parent == 0 || window == 0) return;
        var frame = GetRect(_parent, "frame");
        SetFrame(window, frame);
        Resize(frame, GetDouble(_parent, "backingScaleFactor"));
        _wake.Set();
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
