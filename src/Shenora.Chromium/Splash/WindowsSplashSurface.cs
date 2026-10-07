#if CEF_WINDOWS
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using static Shenora.Chromium.Host.WindowsSplashNative;

namespace Shenora.Chromium.Host;

/// <summary>
/// The Windows splash: a layered popup on a thread of its own, so it draws and animates while CEF's thread is busy.
/// <c>UpdateLayeredWindow</c> presents each frame whole, with no <c>WM_PAINT</c>. It takes the clicks over it, so none
/// reaches the page loading unseen beneath it, and a click never activates it (<c>WS_EX_NOACTIVATE</c>); it has no
/// taskbar button. Either the card, before the main window exists, with a shadow it draws in a margin of its own; or the
/// splash over the main window's render area, owned by it from its creation, so it stays above that window only and
/// minimises with it, while the window's frame stays the window's.
/// <para>
/// A same-process owned popup does not count as covering the main window for Chromium's occlusion tracking, so the page
/// keeps painting underneath (measured, CEF 154: 106–120 animation frames a second under it, against a hidden page and
/// none under a foreign window).
/// </para>
/// </summary>
internal sealed unsafe class WindowsSplashSurface(ILogger? log) : ISplashSurface
{
    private const uint WM_RUN = WM_APP + 1, WM_CLOSE_SPLASH = WM_APP + 2;
    private const nuint AnimationTimer = 1, FadeTimer = 2;
    private const string ClassName = "ShenoraSplash";
    private static readonly Lock ClassGate = new();
    private static nint _class;

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private Exception? _failure;
    private nint _hwnd;
    private GCHandle _self;
    private WindowsSplashPainter? _painter;
    private SplashRender? _render;
    private const int ShadowDips = 12;
    private Rectangle _bounds;   // where it opens, in physical pixels, the card's shadow margin included
    private bool _isCard;
    private int _shadow;         // the card's shadow margin in pixels; none over the main window
    private Action? _onShown;    // Reveal's callback, run once it shows (or never will)
    private int _revealed;       // Reveal has been asked
    private bool _shownOnce;     // the splash thread's: it has been shown
    private SplashOverlayLayout _layout;
    private nint _owner;
    private byte _alpha = 255;
    private bool _animating;
    private (Size Size, float Scale, OverlayCorners Corners) _presented;   // what the last frame was drawn for
    private long _fadeStart;
    private TimeSpan _fade;
    private Action? _fadeDone;
    private int _renderQueued;
    private int _disposed;
    private WindowsSplashCover? _cover;   // a frameless window's strip and band until it draws, on CEF's UI thread

    // The card's window is its rect and a margin round it, where it draws its own shadow: a layered window gets none
    // from the desktop compositor.
    public void ShowCard(Rectangle dipRect, SplashRender render)
    {
        _isCard = true;
        var screens = WindowsScreens.All();
        var card = WindowsScreens.ToPixels(new ChromiumWindowGeometry.Plan(dipRect.Width, dipRect.Height, dipRect.X, dipRect.Y, false), screens);
        var centre = new Point(card.X + (card.Width / 2), card.Y + (card.Height / 2));
        var scale = screens.FirstOrDefault(s => s.Monitor.Contains(centre)) is { Scale: > 0 } screen ? screen.Scale : 1f;
        _shadow = (int)Math.Round(ShadowDips * scale);
        _bounds = Rectangle.Inflate(card, _shadow, _shadow);
        Open(render);
        if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The splash window did not open within 5 seconds.");
        if (_failure is { } failure) throw new InvalidOperationException("The splash window could not be opened.", failure);
    }

    // CEF's UI thread, before CEF shows the main window. Returns at once: the splash thread makes the window, owned from
    // its creation, and draws its first frame while CEF goes on to show the main window, which this would otherwise hold
    // back. Owner and owned on two threads share an input queue from here on; the splash thread does nothing that waits
    // on input.
    public void ShowOver(nint mainWindow, SplashOverlayLayout layout, SplashRender render)
    {
        if (mainWindow == 0) throw new ArgumentException("The main window has no handle.", nameof(mainWindow));
        _owner = mainWindow;
        _layout = layout;
        _cover = AppCallback.RunOrDefault(() => WindowsSplashCover.Make(mainWindow, layout), null,
            ex => AppCallback.Log(log, () => "[Shenora.Chromium] The window's strip could not be covered until it draws", LogLevel.Warning, ex));
        Open(render);
    }

    // CEF's UI thread, the main window on screen. Never waits: the splash shows once its window and first frame exist,
    // which the splash thread may still be making (its window's owner is CEF's, which it may be waiting on).
    public void Reveal(Action shown)
    {
        if (_isCard)
        {
            shown();
            return;
        }
        _onShown = shown;
        // The cover first, at once on this thread: the splash shows over it when its first frame is drawn.
        if (_cover is { } cover) AppCallback.Run(cover.Show);
        // A full fence, as _ready.Set() is on the splash thread: each side then sees the other's write, so one of them
        // shows it. A plain write before the read could be reordered after it, and neither would.
        Interlocked.Exchange(ref _revealed, 1);
        if (!_ready.IsSet) return;                        // the splash thread shows it once it is made
        if (_failure is not null || !Post(ShowNow)) TakeShown()?.Invoke();
    }

    // The splash thread. Placed now, not as it was made: a window opening maximized is maximized only by its show.
    private void ShowNow()
    {
        if (_shownOnce) return;
        _shownOnce = true;
        Snap();
        if (IsIconic(_owner) == 0) ShowWindow(_hwnd, SW_SHOWNOACTIVATE);   // after the cover, so over it
        if (TakeShown() is { } shown) Guard(shown);
    }

    private Action? TakeShown() => Interlocked.Exchange(ref _onShown, null);

    // The main window's render area in screen pixels: its client area, below the strip on a frameless window. On the
    // splash thread, which is per-monitor aware: read on a thread that is not, the rectangle comes back in that thread's
    // scaled coordinates, and the splash opened at half its size at 200 % (measured).
    private static Rectangle OverlayBounds(nint owner, SplashOverlayLayout layout)
    {
        var dpi = GetDpiForWindow(owner) / 96.0;
        var scale = dpi > 0 ? dpi : 1;
        var area = ClientBounds(owner);
        // A maximized window has no resize band to keep clear.
        var edge = IsZoomed(owner) != 0 ? 0 : (int)Math.Round(SplashGeometry.ResizeBandDips * scale);
        return SplashGeometry.OverlayRect(area, layout.Frameless, (int)Math.Round(layout.StripDips * scale), edge);
    }

    // The main window's client area in screen pixels. The splash thread.
    private static Rectangle ClientBounds(nint owner)
    {
        GetClientRect(owner, out var client);
        var origin = new POINT();
        ClientToScreen(owner, ref origin);
        return new Rectangle(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
    }

    public void Uncover() => _cover?.Remove();

    private void Open(SplashRender render)
    {
        _render = render;
        _thread = new Thread(Run) { IsBackground = true, Name = "Shenora splash" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            SetThreadDpiAwarenessContext(-4);   // per-monitor v2: every rectangle here is physical pixels
            _painter = new WindowsSplashPainter(log);
            var bounds = _owner != 0 ? OverlayBounds(_owner, _layout) : _bounds;
            _self = GCHandle.Alloc(this);
            var hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, Class(), "", WS_POPUP,
                bounds.X, bounds.Y, bounds.Width, bounds.Height, _owner, 0, GetModuleHandleW(null), GCHandle.ToIntPtr(_self));
            if (hwnd == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            // Published with a full fence, then the dispose flag read: a Dispose that saw no window yet (Show gave up
            // waiting) set the flag first, so this side sees it and the window never shows.
            Interlocked.Exchange(ref _hwnd, hwnd);
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(WindowsSplashSurface));
            Present();
            // The splash over the main window stays hidden until Reveal: shown now, it would float alone until CEF showed
            // its owner, for as long as the app's window-opened hooks took.
            if (_isCard)
            {
                ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
                // A card that is not foreground opens UNDER the foreground window (measured: under the app the user was
                // in), where it does no good. A launch the user made may take the foreground, as the window the card
                // stands in for would; one the OS does not allow it (started in the background) is refused, and stays
                // put. The window's splash needs none of this: it is owned, and its owner takes the foreground.
                SetForegroundWindow(_hwnd);
            }
        }
        catch (Exception ex)
        {
            _failure = ex;
            _cover?.Remove();
            if (_hwnd != 0) DestroyWindow(_hwnd);   // before its handle to this object is freed
            Cleanup();
            _ready.Set();
            // Revealed already, and now never to show: whoever waits on it is told.
            if (Volatile.Read(ref _revealed) == 1 && TakeShown() is { } never) Guard(never);
            return;
        }
        _ready.Set();
        // Revealed while it was being made (Reveal saw it not ready, so left the showing to this thread).
        if (_owner != 0 && Volatile.Read(ref _revealed) == 1) Guard(ShowNow);
        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        Cleanup();
    }

    public void Invalidate()
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
            Post(() =>
            {
                Volatile.Write(ref _renderQueued, 0);
                Present();
            });
    }

    public void FollowOwner()
    {
        if (_cover is { } cover) AppCallback.Run(cover.Follow);
        Post(Snap);
    }

    public void FadeOut(TimeSpan duration, Action done) => Post(() =>
    {
        _fadeDone = done;
        _fade = duration;
        _fadeStart = Stopwatch.GetTimestamp();
        SetTimer(_hwnd, FadeTimer, 15, 0);
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cover?.Remove();
        var hwnd = Volatile.Read(ref _hwnd);
        if (hwnd == 0) return;   // not made yet: the splash thread sees the flag and makes none
        if (Environment.CurrentManagedThreadId == _thread?.ManagedThreadId)
        {
            DestroyWindow(hwnd);
            return;
        }
        PostMessageW(hwnd, WM_CLOSE_SPLASH, 0, 0);
        _thread?.Join(TimeSpan.FromSeconds(1));
    }

    private bool Post(Action work)
    {
        if (_hwnd == 0 || Volatile.Read(ref _disposed) != 0) return false;
        _work.Enqueue(work);
        return PostMessageW(_hwnd, WM_RUN, 0, 0) != 0;
    }

    private nint? Handle(uint msg, nint wParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                // Never activated itself; a click on it brings its window forward, as a click on that window would.
                if (_owner != 0) SetForegroundWindow(_owner);
                return MA_NOACTIVATE;
            case WM_CLOSE:
                return 0;   // Alt+F4 while it has the foreground: the session alone closes it
            case WM_RUN:
                while (_work.TryDequeue(out var work)) Guard(work);
                return 0;
            case WM_CLOSE_SPLASH:
                DestroyWindow(_hwnd);
                return 0;
            case WM_TIMER when (nuint)wParam == AnimationTimer:
                Guard(Present);
                return 0;
            case WM_TIMER when (nuint)wParam == FadeTimer:
                Guard(FadeStep);
                return 0;
            case WM_DESTROY:
                _cover?.Remove();   // owned by the main window, not by this one: it goes with the splash
                PostQuitMessage(0);
                return 0;
            default:
                return null;
        }
    }

    private void Guard(Action work) => AppCallback.Run(work, ex => AppCallback.Log(log, () => "[Shenora.Chromium] The splash failed to draw", LogLevel.Warning, ex));

    // The splash thread: render at the window's current size and DPI, draw, present.
    private void Present()
    {
        if (_hwnd == 0 || _painter is null || _render is null) return;
        GetWindowRect(_hwnd, out var r);
        var size = new Size(Math.Max(1, r.Right - r.Left - (2 * _shadow)), Math.Max(1, r.Bottom - r.Top - (2 * _shadow)));
        var scale = GetDpiForWindow(_hwnd) / 96f;
        var corners = Corners();
        var frame = _render(size, scale > 0 ? scale : 1, _painter);
        _painter.Paint(frame, (int)Math.Round(8 * (scale > 0 ? scale : 1)), corners, _shadow);
        _presented = (size, scale, corners);
        var at = new POINT { X = r.Left, Y = r.Top };
        var extent = new SIZE { Cx = _painter.Size.Width, Cy = _painter.Size.Height };
        var origin = new POINT();
        var blend = new BLENDFUNCTION { SourceConstantAlpha = _alpha, AlphaFormat = 1 };
        UpdateLayeredWindow(_hwnd, 0, &at, &extent, _painter.Dc, &origin, 0, &blend, ULW_ALPHA);
        if (frame.Animated != _animating)
        {
            _animating = frame.Animated;
            if (_animating) SetTimer(_hwnd, AnimationTimer, 33, 0);
            else KillTimer(_hwnd, AnimationTimer);
        }
    }

    // Windows 11 rounds a normal top-level window, so the card cuts all four corners, and the splash over the main
    // window the two at its bottom, below the title bar or strip; a maximized window is square.
    private OverlayCorners Corners()
    {
        var rounded = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        if (_isCard) return rounded ? OverlayCorners.Top | OverlayCorners.Bottom : OverlayCorners.None;
        return SplashGeometry.Corners(rounded, _owner != 0 && IsZoomed(_owner) != 0, framed: !_layout.Frameless);
    }

    // The splash thread. Nothing while the owner is minimized: the OS hides an owned window with its owner.
    private void Snap()
    {
        if (_owner == 0 || _hwnd == 0 || IsIconic(_owner) != 0) return;
        var bounds = OverlayBounds(_owner, _layout);
        GetWindowRect(_hwnd, out var current);
        if (bounds.Left != current.Left || bounds.Top != current.Top || bounds.Right != current.Right || bounds.Bottom != current.Bottom)
        {
            SetWindowPos(_hwnd, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE);
            // A layered window keeps its bitmap as it moves: a frame only when what it was drawn for changed, or a drag
            // would draw one per step.
            if (_presented != (bounds.Size, GetDpiForWindow(_hwnd) / 96f, Corners())) Present();
        }
        // Revealed while its window was minimized, it stayed hidden: the OS shows an owned window again with its owner
        // only if it was showing as the owner went.
        if (_shownOnce && IsWindowVisible(_hwnd) == 0) ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    private void FadeStep()
    {
        var progress = Stopwatch.GetElapsedTime(_fadeStart) / _fade;
        if (progress >= 1)
        {
            KillTimer(_hwnd, FadeTimer);
            KillTimer(_hwnd, AnimationTimer);
            DestroyWindow(_hwnd);
            var done = Interlocked.Exchange(ref _fadeDone, null);
            if (done is not null) Guard(done);
            return;
        }
        _alpha = (byte)Math.Round(255 * (1 - progress));
        var blend = new BLENDFUNCTION { SourceConstantAlpha = _alpha, AlphaFormat = 1 };
        UpdateLayeredWindow(_hwnd, 0, null, null, 0, null, 0, &blend, ULW_ALPHA);
    }

    private void Cleanup()
    {
        Interlocked.Exchange(ref _disposed, 1);
        AppCallback.Run(() => _painter?.Dispose());
        _painter = null;
        if (_self.IsAllocated) _self.Free();
        _hwnd = 0;
    }

    private static nint Class()
    {
        lock (ClassGate)
        {
            if (_class != 0) return _class;
            fixed (char* name = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    Size = (uint)sizeof(WNDCLASSEXW),
                    WndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                    Instance = GetModuleHandleW(null),
                    ClassName = name,
                };
                _class = RegisterClassExW(&wc);
            }
            if (_class == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "The splash's window class could not be registered.");
            return _class;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_NCCREATE) SetWindowLongPtrW(hwnd, GWLP_USERDATA, ((CREATESTRUCTW*)lParam)->CreateParams);
            var data = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
            if (data != 0 && GCHandle.FromIntPtr(data).Target is WindowsSplashSurface self && self.Handle(msg, wParam) is { } result) return result;
        }
        catch
        {
            // Nothing may unwind into user32.
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private const uint WM_CLOSE = 0x10;

    [DllImport("user32")] private static extern int IsZoomed(nint hwnd);
    [DllImport("user32")] private static extern int SetForegroundWindow(nint hwnd);

    /// <summary>The window, for tests.</summary>
    internal nint Window => _hwnd;

    /// <summary>The cover over a frameless window's strip and band, for tests; 0 when there is none.</summary>
    internal nint CoverWindow => _cover?.Window ?? 0;
}
#endif
