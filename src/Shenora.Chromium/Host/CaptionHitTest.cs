using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
#if CEF_WINDOWS
using static Shenora.Chromium.Host.RenderWidgets;
#endif

namespace Shenora.Chromium.Host;

#if CEF_WINDOWS
/// <summary>
/// The frame's hit-test on Windows: subclasses on CEF's top-level window and on Chromium's render-widget
/// children, which are in this process on CEF's UI thread (measured), where every call here runs.
/// <para>
/// The OS asks the CHILD first, because <c>WindowFromPoint</c> lands on it, and with an Alloy-style page (D84) the
/// child answers HTCLIENT everywhere, even over a <c>-webkit-app-region: drag</c> area the top-level window
/// answers HTCAPTION for (measured), so the OS would hand a press there to the page. So wherever the top-level
/// window gives a non-client answer, the child answers <c>HTTRANSPARENT</c> and the OS asks the top-level window
/// beneath it on the same thread. That covers the drag area, and the page's caption buttons, where the
/// top-level window answers <c>HTMINBUTTON</c>/<c>HTMAXBUTTON</c>/<c>HTCLOSE</c> for <see cref="CaptionButtons"/>,
/// which is what Snap Layouts attaches to. The non-client press and release there are SWALLOWED: the default
/// handling would run the OS's own caption-button loop against a caption this window does not draw. So the press
/// takes the mouse capture itself, as that loop would, and the pointer's moves and the release reach
/// <see cref="CaptionButtons"/> wherever they happen.
/// </para>
/// <para>
/// A frameless window's press on the drag area is taken the same way, and handed to the system's move loop once the
/// pointer passes the drag threshold, so the window moves under the pointer from the press, and a still click enters
/// no modal loop.
/// </para>
/// <para>
/// A render widget is replaced when its renderer is (a crash, a cross-site navigation). The top-level window
/// hears of each new child (<c>WM_PARENTNOTIFY</c>) and subclasses it.
/// </para>
/// </summary>
internal sealed unsafe class CaptionHitTest : IDisposable
{
    private const uint WM_CREATE = 0x0001, WM_NCDESTROY = 0x0082, WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0,
        WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCLBUTTONDBLCLK = 0x00A3, WM_MOUSEMOVE = 0x0200,
        WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDBLCLK = 0x0209, WM_XBUTTONDOWN = 0x020B,
        WM_XBUTTONDBLCLK = 0x020D, WM_PARENTNOTIFY = 0x0210, WM_MOVING = 0x0216, WM_CAPTURECHANGED = 0x0215,
        WM_EXITSIZEMOVE = 0x0232, WM_NCMOUSELEAVE = 0x02A2;
    private const nint MK_LBUTTON = 0x0001;
    private const int HTERROR = -2, HTTRANSPARENT = -1, HTNOWHERE = 0, HTCLIENT = 1, HTCAPTION = 2, HTMINBUTTON = 8, HTMAXBUTTON = 9, HTCLOSE = 20;
    private const nuint TopId = 1, ChildId = 2;
    private const string RenderWidgetClass = "Chrome_RenderWidgetHostHWND";

    private readonly nint _top;
    private readonly CaptionButtons _buttons;
    private readonly ILogger? _log;
    private readonly GCHandle _self;
    private readonly HashSet<nint> _children = [];
    private bool _disposed;
    private bool _capturing;   // a press on a button holds the mouse capture
    private bool _releasing;   // the capture change our own release causes
    private bool _captionDrag;      // a press on the drag area holds the capture, until it moves far enough or ends
    private POINT? _captionPress;   // where that press began
    private POINT? _movePress;      // that press, while the loop it started has not placed the window yet
    private readonly bool _frameless;

    private CaptionHitTest(nint top, CaptionButtons buttons, bool frameless, ILogger? log)
    {
        _top = top;
        _buttons = buttons;
        _frameless = frameless;
        _log = log;
        _self = GCHandle.Alloc(this);
    }

    /// <summary>Subclass the top-level window and the render widgets it has. Null when it cannot be (no handle, or
    /// the call failed). At window creation, so the page's drag area works before anything else is asked.</summary>
    /// <param name="top">The window.</param>
    /// <param name="buttons">The page's caption buttons.</param>
    /// <param name="frameless">The page draws the title bar: its drag area moves the window as a system caption would.
    /// A framed window's own caption is left to Chromium.</param>
    /// <param name="log">Diagnostics.</param>
    public static CaptionHitTest? Attach(nint top, CaptionButtons buttons, bool frameless, ILogger? log)
    {
        if (top == 0) return null;
        var hitTest = new CaptionHitTest(top, buttons, frameless, log);
        if (SetWindowSubclass(top, &TopProc, TopId, (nuint)GCHandle.ToIntPtr(hitTest._self)) != 0)
        {
            hitTest.Refresh();
            return hitTest;
        }
        AppCallback.Log(log, () => "[Shenora.Chromium] Could not subclass the window for its hit-test", LogLevel.Warning);
        hitTest._self.Free();
        return null;
    }


    /// <summary>Physical pixels per CSS pixel, from the window's own DPI (per monitor).</summary>
    public double Scale => RenderWidgets.Scale(_top);

    /// <summary>Subclass every render-widget child that exists now. Idempotent.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        foreach (var child in Under(_top)) Adopt(child);
    }

    // Only a render widget on this window's own thread: a subclass installs from that thread alone, and
    // HTTRANSPARENT passes a hit-test on only to a window of the same thread.
    private void Adopt(nint child)
    {
        if (_disposed || _children.Contains(child) || !Is(child)) return;
        if (GetWindowThreadProcessId(child, null) != GetWindowThreadProcessId(_top, null)) return;
        if (SetWindowSubclass(child, &ChildProc, ChildId, (nuint)GCHandle.ToIntPtr(_self)) != 0) _children.Add(child);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_capturing) EndCapture();
        if (_captionDrag) EndCaptionDrag();
        _disposed = true;
        RemoveWindowSubclass(_top, &TopProc, TopId);
        foreach (var child in _children) RemoveWindowSubclass(child, &ChildProc, ChildId);
        _children.Clear();
        _self.Free();
    }

    private CaptionButtonKind? ButtonAt(nint lParam)
    {
        if (_buttons.IsEmpty) return null;
        var point = new POINT { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
        return ScreenToClient(_top, &point) != 0 ? _buttons.At(point.X, point.Y) : null;
    }

    // A captured mouse message carries CLIENT coordinates, negative off the window's left or top.
    private CaptionButtonKind? ButtonAtClient(nint lParam) => _buttons.At((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));

    // The leave that ends a hover: WM_NCMOUSELEAVE arrives only while it is being tracked, and at once when the
    // pointer is already off the frame.
    private void TrackLeave()
    {
        var track = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE | TME_NONCLIENT, hwndTrack = _top };
        TrackMouseEvent(&track);
    }

    private void BeginCapture()
    {
        SetCapture(_top);
        _capturing = true;
    }

    // Our own release changes the capture too; that notice is swallowed, since Chromium never held this capture.
    private void EndCapture()
    {
        _capturing = false;
        _releasing = true;
        try { ReleaseCapture(); }
        finally { _releasing = false; }
    }

    private void EndCaptionDrag()
    {
        _captionDrag = false;
        _captionPress = null;
        _releasing = true;
        try { ReleaseCapture(); }
        finally { _releasing = false; }
    }

    // Snapped to an edge or a corner (Windows 10+); false where the call does not exist.
    private static bool IsArranged(nint hwnd)
    {
        try { return IsWindowArranged(hwnd) != 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private bool PastDragThreshold(POINT press, POINT at) => RenderWidgets.PastDragThreshold(_top, press.X, press.Y, at.X, at.Y);

    private static CaptionButtonKind? FromHitTest(nint code) => (int)code switch
    {
        HTMINBUTTON => CaptionButtonKind.Minimize,
        HTMAXBUTTON => CaptionButtonKind.Maximize,
        HTCLOSE => CaptionButtonKind.Close,
        _ => null,
    };

    private static int ToHitTest(CaptionButtonKind kind) => kind switch
    {
        CaptionButtonKind.Minimize => HTMINBUTTON,
        CaptionButtonKind.Maximize => HTMAXBUTTON,
        _ => HTCLOSE,
    };

    [UnmanagedCallersOnly]
    private static nint TopProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        CaptionHitTest? me = null;
        try
        {
            me = (CaptionHitTest)GCHandle.FromIntPtr((nint)data).Target!;
            switch (msg)
            {
                case WM_NCHITTEST when me.ButtonAt(lParam) is { } kind:
                    return ToHitTest(kind);
                case WM_NCMOUSEMOVE:
                    me._buttons.Hover(FromHitTest(wParam));
                    if (FromHitTest(wParam) is not null) me.TrackLeave();
                    break;
                case WM_NCMOUSELEAVE:
                    me._buttons.Leave();
                    break;
                // ── A press on the page's drag area ─────────────────────────────────────────────────────────────
                // Held with the capture until the pointer passes the system's drag threshold, and only then handed to
                // the system's move loop. Each way of doing it otherwise was measured worse:
                // - straight to the loop at the press, a STILL press stalled this thread, and the page's IPC, ~500 ms;
                // - left to Chromium, the loop began only a step or two along, so the window trailed the pointer (5/6
                //   of a 200 px drag), and on a maximized window a pointer leaving the bar fast was lost altogether,
                //   so dragging it down did not restore it (3 of 8).
                // The page's drag area of a frameless window, pressed with a mouse: what was measured. A framed window's
                // own caption, and a press synthesized from touch or pen, stay Chromium's.
                case WM_NCLBUTTONDOWN when (int)wParam == HTCAPTION && me._frameless && !FromTouchOrPen():
                    me._captionPress = new POINT { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
                    SetCapture(hwnd);
                    me._captionDrag = true;
                    return 0;
                case WM_MOUSEMOVE or WM_LBUTTONUP when me._captionDrag:
                    if (msg == WM_LBUTTONUP || (wParam & MK_LBUTTON) == 0) { me.EndCaptionDrag(); return 0; }   // a click
                    var moved = new POINT { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
                    ClientToScreen(hwnd, &moved);
                    if (me._captionPress is { } press && me.PastDragThreshold(press, moved))
                    {
                        me.EndCaptionDrag();
                        // A maximized or snapped window is left to the system, which places the restored one under the
                        // pointer itself.
                        me._movePress = IsZoomed(hwnd) == 0 && !IsArranged(hwnd) ? press : null;
                        DefWindowProcW(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, (nint)(((moved.Y & 0xFFFF) << 16) | (moved.X & 0xFFFF)));
                        // Modal: the loop has run and ended here. If it never ran, a later move must not inherit this one.
                        me._movePress = null;
                    }
                    return 0;
                case WM_CAPTURECHANGED when me._captionDrag && !me._releasing:   // taken away before it moved
                    me._captionDrag = false;
                    me._captionPress = null;
                    break;
                // The loop's first proposal is placed as the system's caption would have placed it: where the window
                // is, moved by the pointer's travel since the PRESS. Once: the loop builds each later proposal from where
                // the window is by then (added to every one, it accumulated: 517 px for a 200 px drag, measured).
                case WM_MOVING when me._movePress is { } from && GetWindowRect(hwnd, out var now) != 0:
                    var proposed = (RECT*)lParam;
                    var pointer = GetMessagePos();
                    int x = now.Left + (short)(pointer & 0xFFFF) - from.X, y = now.Top + (short)((pointer >> 16) & 0xFFFF) - from.Y;
                    int w = proposed->Right - proposed->Left, h = proposed->Bottom - proposed->Top;
                    *proposed = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
                    me._movePress = null;
                    break;
                case WM_EXITSIZEMOVE:
                    me._movePress = null;
                    break;
                case WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK when FromHitTest(wParam) is { } pressed:
                    me._buttons.Press(pressed);
                    me.BeginCapture();
                    return 0;
                case WM_NCLBUTTONUP when FromHitTest(wParam) is { } released:   // a press that began elsewhere
                    me._buttons.Release(released);
                    return 0;
                // The press's own moves and release, wherever the pointer is. A press cancelled meanwhile (the page's
                // buttons cleared) lets the capture go and the message through.
                case WM_MOUSEMOVE or WM_LBUTTONUP when me._capturing:
                    if (!me._buttons.IsPressed) { me.EndCapture(); break; }
                    var at = me.ButtonAtClient(lParam);
                    // A move with the button already up is a release that never arrived: it ends the press there.
                    if (msg == WM_MOUSEMOVE && (wParam & MK_LBUTTON) != 0) { me._buttons.Hover(at); return 0; }
                    me.EndCapture();
                    me._buttons.Release(at);
                    // The capture ended the leave tracking, and a click usually moves the window from under the
                    // pointer (maximize, restore, minimize): without this the button stayed hot (measured).
                    me.TrackLeave();
                    return 0;
                // The other buttons during a held press, as the system's loop ignores them: passed on, they would reach
                // Chromium in the middle of a press it never saw begin.
                case (>= WM_RBUTTONDOWN and <= WM_MBUTTONDBLCLK) or (>= WM_XBUTTONDOWN and <= WM_XBUTTONDBLCLK) when me._capturing || me._captionDrag:
                    return 0;
                case WM_CAPTURECHANGED when me._releasing:
                    return 0;
                case WM_CAPTURECHANGED when me._capturing:   // taken away: a menu, another window, Alt+Tab
                    me._capturing = false;
                    me._buttons.Cancel();
                    break;
                case WM_PARENTNOTIFY when (wParam & 0xFFFF) == WM_CREATE:
                    me.Adopt(lParam);   // a new render widget: a renderer was replaced
                    break;
                case WM_NCDESTROY:
                    RemoveWindowSubclass(hwnd, &TopProc, TopId);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppCallback.Log(me?._log, () => "[Shenora.Chromium] The caption hit-test failed; the message was passed on", LogLevel.Warning, ex);
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static nint ChildProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        CaptionHitTest? me = null;
        try
        {
            me = (CaptionHitTest)GCHandle.FromIntPtr((nint)data).Target!;
            if (msg == WM_NCHITTEST)
            {
                // The top-level window's answer, caption buttons included: anything but the page's own client
                // area is the frame's, so the OS must ask the frame.
                var frame = (int)SendMessageW(me._top, WM_NCHITTEST, wParam, lParam);
                if (frame is not (HTCLIENT or HTNOWHERE or HTTRANSPARENT or HTERROR)) return HTTRANSPARENT;
            }
            if (msg == WM_NCDESTROY)
            {
                RemoveWindowSubclass(hwnd, &ChildProc, ChildId);
                me._children.Remove(hwnd);
            }
        }
        catch (Exception ex)
        {
            AppCallback.Log(me?._log, () => "[Shenora.Chromium] The caption hit-test failed; the message was passed on", LogLevel.Warning, ex);
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    // ── Win32 ────────────────────────────────────────────────────────────────────────────────────────────
    private const uint TME_LEAVE = 0x2, TME_NONCLIENT = 0x10;
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] private static extern int GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32")] private static extern int ClientToScreen(nint hwnd, POINT* point);
    [DllImport("user32")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] private static extern int IsWindowArranged(nint hwnd);
    [DllImport("user32")] private static extern uint GetMessagePos();
    [DllImport("user32")] private static extern int IsZoomed(nint hwnd);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint hwnd, uint* processId);
    [DllImport("user32")] private static extern int ScreenToClient(nint hwnd, POINT* point);
    [DllImport("user32")] private static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] private static extern int TrackMouseEvent(TRACKMOUSEEVENT* track);
    [DllImport("user32")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32")] private static extern int ReleaseCapture();
}
#else
/// <summary>No caption hit-test on this OS yet: the macOS and Linux shells are not built.</summary>
internal sealed class CaptionHitTest : IDisposable
{
    public static CaptionHitTest? Attach(nint top, CaptionButtons buttons, bool frameless, ILogger? log) => null;
    public double Scale => 1.0;
    public void Refresh() { }
    public void Dispose() { }
}
#endif
