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
/// The Windows splash: a layered popup on a thread of its own, so it draws and animates while the main thread composes
/// the app and starts CEF. <c>UpdateLayeredWindow</c> presents each frame whole, with no <c>WM_PAINT</c>. It takes the
/// clicks over it, so none reaches the page loading unseen beneath it, and a click never activates it
/// (<c>WS_EX_NOACTIVATE</c>); it has no taskbar button.
/// <para>
/// Once owned by the main window it stays above it and minimises with it. A same-process owned popup does not count as
/// covering the main window for Chromium's occlusion tracking, so the page keeps painting underneath (measured, CEF 154:
/// 106–120 animation frames a second under it, against a hidden page and none under a foreign window).
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
    private ChromiumWindowGeometry.Plan _plan;
    private nint _owner;
    private byte _alpha = 255;
    private bool _animating;
    private long _fadeStart;
    private TimeSpan _fade;
    private Action? _fadeDone;
    private int _renderQueued;
    private int _disposed;

    public void Show(ChromiumWindowGeometry.Plan placement, SplashRender render)
    {
        _render = render;
        _plan = placement;
        _thread = new Thread(Run) { IsBackground = true, Name = "Shenora splash" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The splash window did not open within 5 seconds.");
        if (_failure is { } failure) throw new InvalidOperationException("The splash window could not be opened.", failure);
    }

    private void Run()
    {
        try
        {
            SetThreadDpiAwarenessContext(-4);   // per-monitor v2: every rectangle here is physical pixels
            _painter = new WindowsSplashPainter(log);
            var bounds = WindowsScreens.ToPixels(_plan, WindowsScreens.All());
            _self = GCHandle.Alloc(this);
            var hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, Class(), "", WS_POPUP,
                bounds.X, bounds.Y, bounds.Width, bounds.Height, 0, 0, GetModuleHandleW(null), GCHandle.ToIntPtr(_self));
            if (hwnd == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            // Published with a full fence, then the dispose flag read: a Dispose that saw no window yet (Show gave up
            // waiting) set the flag first, so this side sees it and the window never shows.
            Interlocked.Exchange(ref _hwnd, hwnd);
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(WindowsSplashSurface));
            Present();
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            // A window that is not foreground opens UNDER the foreground window (measured: under the app the user was
            // in), where a splash does no good. A launch the user made may take the foreground, as the window the splash
            // stands in for would; one the OS does not allow it (started in the background) is refused, and stays put.
            SetForegroundWindow(_hwnd);
        }
        catch (Exception ex)
        {
            _failure = ex;
            if (_hwnd != 0) DestroyWindow(_hwnd);   // before its handle to this object is freed
            Cleanup();
            _ready.Set();
            return;
        }
        _ready.Set();
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

    // Synchronous on the caller's thread for the owner itself: CEF shows the main window right after, and an owner set
    // later would let that raise the main window over the splash. Owner and owned on two threads share an input queue
    // from here on; the splash thread does nothing that waits on input.
    public void Attach(nint mainWindow)
    {
        if (_hwnd == 0 || mainWindow == 0 || Volatile.Read(ref _disposed) != 0) return;
        SetWindowLongPtrW(_hwnd, GWLP_HWNDPARENT, mainWindow);
        Post(() =>
        {
            _owner = mainWindow;
            Snap();
        });
    }

    public void FollowOwner() => Post(Snap);

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

    private void Post(Action work)
    {
        if (_hwnd == 0 || Volatile.Read(ref _disposed) != 0) return;
        _work.Enqueue(work);
        PostMessageW(_hwnd, WM_RUN, 0, 0);
    }

    private nint? Handle(uint msg, nint wParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
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
        var size = new Size(Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
        var scale = GetDpiForWindow(_hwnd) / 96f;
        var frame = _render(size, scale > 0 ? scale : 1, _painter);
        _painter.Paint(frame, Rounded() ? (int)Math.Round(8 * scale) : 0);
        var at = new POINT { X = r.Left, Y = r.Top };
        var extent = new SIZE { Cx = size.Width, Cy = size.Height };
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

    // Windows 11 rounds a normal top-level window, the main window included, so the splash cuts its corners to match;
    // a maximized one is square.
    private bool Rounded() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && !(_owner != 0 ? IsZoomed(_owner) != 0 : _plan.Maximized);

    private void Snap()
    {
        if (_owner == 0 || _hwnd == 0) return;
        RECT bounds;
        if (DwmGetWindowAttribute(_owner, DWMWA_EXTENDED_FRAME_BOUNDS, &bounds, sizeof(RECT)) != 0 || bounds.Right <= bounds.Left)
            GetWindowRect(_owner, out bounds);
        GetWindowRect(_hwnd, out var current);
        if (bounds.Left == current.Left && bounds.Top == current.Top && bounds.Right == current.Right && bounds.Bottom == current.Bottom) return;
        SetWindowPos(_hwnd, 0, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, SWP_NOZORDER | SWP_NOACTIVATE);
        Present();
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
}
#endif
