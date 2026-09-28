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
/// handling would run the OS's own caption-button loop against a caption this window does not draw.
/// </para>
/// <para>
/// A render widget is replaced when its renderer is (a crash, a cross-site navigation). The top-level window
/// hears of each new child (<c>WM_PARENTNOTIFY</c>) and subclasses it.
/// </para>
/// </summary>
internal sealed unsafe class CaptionHitTest : IDisposable
{
    private const uint WM_CREATE = 0x0001, WM_NCDESTROY = 0x0082, WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0,
        WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCLBUTTONDBLCLK = 0x00A3, WM_PARENTNOTIFY = 0x0210,
        WM_NCMOUSELEAVE = 0x02A2;
    private const int HTERROR = -2, HTTRANSPARENT = -1, HTNOWHERE = 0, HTCLIENT = 1, HTMINBUTTON = 8, HTMAXBUTTON = 9, HTCLOSE = 20;
    private const nuint TopId = 1, ChildId = 2;
    private const string RenderWidgetClass = "Chrome_RenderWidgetHostHWND";

    private readonly nint _top;
    private readonly CaptionButtons _buttons;
    private readonly ILogger? _log;
    private readonly GCHandle _self;
    private readonly HashSet<nint> _children = [];
    private bool _disposed;
    private bool _snapLayouts;

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

    /// <summary>
    /// Style the window so Windows offers Snap Layouts on the page's maximize button. Once, when the page first
    /// registers caption buttons. Measured against a control window that shows the flyout: CEF's frameless
    /// style never got it, WS_MAXIMIZEBOX alone never did, and exactly these two bits did in every trial.
    /// (Adding WS_CAPTION as well lost it again.) Neither bit adds a frame: the client area stays the whole
    /// window (measured), though Windows 11 now rounds the window's corners.
    /// </summary>
    public void EnableSnapLayouts()
    {
        if (_disposed || _snapLayouts) return;
        _snapLayouts = true;
        SetWindowLongPtrW(_top, GWL_STYLE, GetWindowLongPtrW(_top, GWL_STYLE) | WS_THICKFRAME | WS_MAXIMIZEBOX);
        SetWindowPos(_top, 0, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
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
                    if (FromHitTest(wParam) is not null)
                    {
                        // The leave that ends the hover: WM_NCMOUSELEAVE arrives only while it is being tracked.
                        var track = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE | TME_NONCLIENT, hwndTrack = hwnd };
                        TrackMouseEvent(&track);
                    }
                    break;
                case WM_NCMOUSELEAVE:
                    me._buttons.Leave();
                    break;
                case WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK when FromHitTest(wParam) is { } pressed:
                    me._buttons.Press(pressed);
                    return 0;
                case WM_NCLBUTTONUP when FromHitTest(wParam) is { } released:
                    me._buttons.Release(released);
                    return 0;
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
    private const int GWL_STYLE = -16;
    private const long WS_THICKFRAME = 0x00040000, WS_MAXIMIZEBOX = 0x00010000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    [DllImport("user32")] private static extern long GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32")] private static extern long SetWindowLongPtrW(nint hwnd, int index, long value);
    [DllImport("user32")] private static extern int SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] private static extern int EnumChildWindows(nint parent, delegate* unmanaged<nint, nint, int> proc, nint lParam);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint hwnd, uint* processId);
    [DllImport("user32")] private static extern int GetClassNameW(nint hwnd, char* name, int max);
    [DllImport("user32")] private static extern int ScreenToClient(nint hwnd, POINT* point);
    [DllImport("user32")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32")] private static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] private static extern int TrackMouseEvent(TRACKMOUSEEVENT* track);
}
#else
/// <summary>No caption hit-test on this OS yet: the macOS and Linux shells are not built.</summary>
internal sealed class CaptionHitTest : IDisposable
{
    public static CaptionHitTest? Attach(nint top, CaptionButtons buttons, ILogger? log) => null;
    public double Scale => 1.0;
    public void EnableSnapLayouts() { }
    public void Refresh() { }
    public void Dispose() { }
}
#endif
