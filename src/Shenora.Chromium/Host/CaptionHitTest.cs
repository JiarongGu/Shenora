using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Shenora.Chromium.Host;

#if CEF_WINDOWS
/// <summary>
/// The Win32 half of <see cref="CaptionButtons"/>: subclasses on CEF's top-level window and on Chromium's
/// render-widget children, which are in this process on CEF's UI thread (measured), where every call here runs.
/// <para>
/// The OS asks the CHILD first, because <c>WindowFromPoint</c> lands on it, and it answers with the Views frame's
/// own hit-test, which knows nothing of the page's buttons (measured: HTCLIENT at the buttons, HTCAPTION only on
/// a <c>-webkit-app-region: drag</c> area). So over a
/// button the child answers <c>HTTRANSPARENT</c>, the OS asks the top-level window beneath it on the same
/// thread, and that answers <c>HTMINBUTTON</c>/<c>HTMAXBUTTON</c>/<c>HTCLOSE</c>, which is what Snap Layouts
/// attaches to, provided the window is styled resizable and maximizable, which attaching adds. The non-client
/// press and release are then SWALLOWED: the default handling would run the OS's own caption-button loop
/// against a caption this window does not draw.
/// </para>
/// <para>
/// A render widget is replaced when its renderer is (a crash, a cross-site navigation), so
/// <see cref="Refresh"/> subclasses whatever children exist whenever the page re-sends its buttons, which it
/// must do after every load anyway.
/// </para>
/// </summary>
internal sealed unsafe class CaptionHitTest : IDisposable
{
    private const uint WM_NCDESTROY = 0x0082, WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0,
        WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCLBUTTONDBLCLK = 0x00A3, WM_NCMOUSELEAVE = 0x02A2;
    private const int HTTRANSPARENT = -1, HTMINBUTTON = 8, HTMAXBUTTON = 9, HTCLOSE = 20;
    private const nuint TopId = 1, ChildId = 2;
    private const string RenderWidgetClass = "Chrome_RenderWidgetHostHWND";

    private readonly nint _top;
    private readonly CaptionButtons _buttons;
    private readonly ILogger? _log;
    private readonly GCHandle _self;
    private readonly HashSet<nint> _children = [];
    private bool _disposed;

    private CaptionHitTest(nint top, CaptionButtons buttons, ILogger? log)
    {
        _top = top;
        _buttons = buttons;
        _log = log;
        _self = GCHandle.Alloc(this);
    }

    /// <summary>Subclass the top-level window. Null when it cannot be (no handle, or the call failed).</summary>
    public static CaptionHitTest? Attach(nint top, CaptionButtons buttons, ILogger? log)
    {
        if (top == 0) return null;
        var hitTest = new CaptionHitTest(top, buttons, log);
        if (SetWindowSubclass(top, &TopProc, TopId, (nuint)GCHandle.ToIntPtr(hitTest._self)) != 0)
        {
            // Snap Layouts needs the window styled resizable and maximizable. Measured against a control window
            // that shows the flyout: CEF's frameless style never got it, WS_MAXIMIZEBOX alone never did, and
            // exactly these two bits did in every trial. (Adding WS_CAPTION as well lost it again.) Neither bit
            // adds a frame: the client area stays the whole window (measured), though Windows 11 now rounds the
            // window's corners.
            SetWindowLongPtrW(top, GWL_STYLE, GetWindowLongPtrW(top, GWL_STYLE) | WS_THICKFRAME | WS_MAXIMIZEBOX);
            SetWindowPos(top, 0, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            return hitTest;
        }
        AppCallback.Log(log, () => $"[Shenora.Chromium] Could not subclass the window for its caption buttons (error {Marshal.GetLastPInvokeError()})", LogLevel.Warning);
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

        var thread = GetWindowThreadProcessId(_top, null);
        foreach (var child in found)
        {
            if (_children.Contains(child) || GetWindowThreadProcessId(child, null) != thread || !IsRenderWidget(child)) continue;
            if (SetWindowSubclass(child, &ChildProc, ChildId, (nuint)GCHandle.ToIntPtr(_self)) != 0) _children.Add(child);
        }
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
            if (msg == WM_NCHITTEST && me.ButtonAt(lParam) is not null) return HTTRANSPARENT;
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
    [DllImport("user32")] private static extern int TrackMouseEvent(TRACKMOUSEEVENT* track);
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
