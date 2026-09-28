using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

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
/// A render widget is replaced when its renderer is (a crash, a cross-site navigation). The top-level window
/// hears of each new child (<c>WM_PARENTNOTIFY</c>) and subclasses it.
/// </para>
/// </summary>
internal sealed unsafe class CaptionHitTest : IDisposable
{
    private const uint WM_CREATE = 0x0001, WM_NCDESTROY = 0x0082, WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0,
        WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCLBUTTONDBLCLK = 0x00A3, WM_MOUSEMOVE = 0x0200,
        WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDBLCLK = 0x0209, WM_XBUTTONDOWN = 0x020B,
        WM_XBUTTONDBLCLK = 0x020D, WM_PARENTNOTIFY = 0x0210, WM_CAPTURECHANGED = 0x0215, WM_NCMOUSELEAVE = 0x02A2;
    private const nint MK_LBUTTON = 0x0001;
    private const int HTERROR = -2, HTTRANSPARENT = -1, HTNOWHERE = 0, HTCLIENT = 1, HTMINBUTTON = 8, HTMAXBUTTON = 9, HTCLOSE = 20;
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

    private CaptionHitTest(nint top, CaptionButtons buttons, ILogger? log)
    {
        _top = top;
        _buttons = buttons;
        _log = log;
        _self = GCHandle.Alloc(this);
    }

    /// <summary>Subclass the top-level window and the render widgets it has. Null when it cannot be (no handle, or
    /// the call failed). At window creation, so the page's drag area works before anything else is asked.</summary>
    public static CaptionHitTest? Attach(nint top, CaptionButtons buttons, ILogger? log)
    {
        if (top == 0) return null;
        var hitTest = new CaptionHitTest(top, buttons, log);
        if (SetWindowSubclass(top, &TopProc, TopId, (nuint)GCHandle.ToIntPtr(hitTest._self)) != 0)
        {
            hitTest.Refresh();
            return hitTest;
        }
        AppCallback.Log(log, () => $"[Shenora.Chromium] Could not subclass the window for its hit-test (error {Marshal.GetLastPInvokeError()})", LogLevel.Warning);
        hitTest._self.Free();
        return null;
    }


    /// <summary>Physical pixels per CSS pixel, from the window's own DPI (per monitor).</summary>
    public double Scale
    {
        get
        {
            var dpi = GetDpiForWindow(_top);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
    }

    /// <summary>Subclass every render-widget child that exists now. Idempotent.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        var found = new List<nint>();
        var handle = GCHandle.Alloc(found);
        try { EnumChildWindows(_top, &CollectChild, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }

        foreach (var child in found) Adopt(child);
    }

    // Only a render widget on this window's own thread: a subclass installs from that thread alone, and
    // HTTRANSPARENT passes a hit-test on only to a window of the same thread.
    private void Adopt(nint child)
    {
        if (_disposed || _children.Contains(child) || !IsRenderWidget(child)) return;
        if (GetWindowThreadProcessId(child, null) != GetWindowThreadProcessId(_top, null)) return;
        if (SetWindowSubclass(child, &ChildProc, ChildId, (nuint)GCHandle.ToIntPtr(_self)) != 0) _children.Add(child);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_capturing) EndCapture();
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
                case (>= WM_RBUTTONDOWN and <= WM_MBUTTONDBLCLK) or (>= WM_XBUTTONDOWN and <= WM_XBUTTONDBLCLK) when me._capturing:
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

    [UnmanagedCallersOnly]
    private static int CollectChild(nint hwnd, nint lParam)
    {
        ((List<nint>)GCHandle.FromIntPtr(lParam).Target!).Add(hwnd);
        return 1;
    }

    private static bool IsRenderWidget(nint hwnd)
    {
        var name = stackalloc char[64];
        var length = GetClassNameW(hwnd, name, 64);
        return new ReadOnlySpan<char>(name, length).SequenceEqual(RenderWidgetClass);
    }

    // ── Win32 ────────────────────────────────────────────────────────────────────────────────────────────
    private const uint TME_LEAVE = 0x2, TME_NONCLIENT = 0x10;
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }

    [DllImport("comctl32", SetLastError = true)]
    private static extern int SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);
    [DllImport("comctl32")]
    private static extern int RemoveWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id);
    [DllImport("comctl32")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] private static extern int EnumChildWindows(nint parent, delegate* unmanaged<nint, nint, int> proc, nint lParam);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint hwnd, uint* processId);
    [DllImport("user32")] private static extern int GetClassNameW(nint hwnd, char* name, int max);
    [DllImport("user32")] private static extern int ScreenToClient(nint hwnd, POINT* point);
    [DllImport("user32")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32")] private static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] private static extern int TrackMouseEvent(TRACKMOUSEEVENT* track);
    [DllImport("user32")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32")] private static extern int ReleaseCapture();
}
#else
/// <summary>No caption hit-test on this OS yet: the macOS and Linux shells are not built.</summary>
internal sealed class CaptionHitTest : IDisposable
{
    public static CaptionHitTest? Attach(nint top, CaptionButtons buttons, ILogger? log) => null;
    public double Scale => 1.0;
    public void Refresh() { }
    public void Dispose() { }
}
#endif
