using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;
#if CEF_WINDOWS
using static Shenora.Chromium.Host.RenderWidgets;
#endif

namespace Shenora.Chromium.Host;

/// <summary>
/// A page's <c>-webkit-app-region</c> areas as its browser last reported them, in order, a later area winning where two
/// overlap, so a <c>no-drag</c> control inside a drag bar is not drag. In the page's DIPs: CSS px at the page's zoom.
/// Written and read on CEF's UI thread.
/// </summary>
internal sealed class DragAreas
{
    private (int X, int Y, int Width, int Height, bool Drag)[] _areas = [];

    public unsafe void Set(nuint count, _cef_draggable_region_t* regions)
    {
        var areas = new (int, int, int, int, bool)[(int)count];
        for (var i = 0; i < areas.Length; i++)
        {
            var r = regions[i];
            areas[i] = (r.bounds.x, r.bounds.y, r.bounds.width, r.bounds.height, r.draggable != 0);
        }
        _areas = areas;
    }

    internal void Set(params (int X, int Y, int Width, int Height, bool Drag)[] areas) => _areas = areas;

    /// <summary>Whether a point, in DIPs, is in the drag area.</summary>
    public bool Contains(double x, double y)
    {
        var drag = false;
        foreach (var a in _areas)
            if (x >= a.X && x < a.X + a.Width && y >= a.Y && y < a.Y + a.Height) drag = a.Drag;
        return drag;
    }

    /// <summary>Whether a mouse message's client point, in the render widget's physical px, is in the drag area.</summary>
    public bool ContainsMessagePoint(nint lParam, double scale) => Contains((short)(lParam & 0xFFFF) / scale, (short)((lParam >> 16) & 0xFFFF) / scale);
}

#if CEF_WINDOWS
/// <summary>
/// A child browser's drag area on Windows: its render widgets are subclassed, and a mouse press there is the host's,
/// not the page's. Held with the capture until the pointer passes the system's drag threshold, and only then handed to
/// the host as a move, as the Chromium shell's caption is (<see cref="CaptionHitTest"/> has what the other ways
/// measured): a still click moves nothing and enters no modal loop. A double click is handed over at once, and a right
/// click as it is released there. A touch or a pen, and anything outside the area, reach the page. CEF's UI thread,
/// which owns the render widgets.
/// <para>
/// Nothing in the window moves it otherwise: the render widget answers HTCLIENT over the whole page, and the host's
/// window is on another thread, which a child's HTTRANSPARENT does not reach.
/// </para>
/// </summary>
internal sealed unsafe class ChildDragArea : IDisposable
{
    private const uint WM_NCDESTROY = 0x0082, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_RBUTTONDBLCLK = 0x0206,
        WM_MBUTTONDBLCLK = 0x0209, WM_CAPTURECHANGED = 0x0215, WM_XBUTTONDOWN = 0x020B, WM_XBUTTONDBLCLK = 0x020D;
    private const nint MK_LBUTTON = 0x0001;
    private const nuint WidgetId = 3;

    private readonly Action<ChromiumDragAreaPress> _pressed;
    private readonly ILogger? _log;
    private readonly HashSet<nint> _widgets = [];
    private GCHandle _self;          // allocated once a window is subclassed, so a browser that never opens holds nothing
    private nint _holder;            // the widget holding a press, until it moves far enough or ends
    private Point _press;            // where that press began, in screen px
    private nint _rightPress;        // the widget a right press in the area went down on, until its release
    private bool _releasing;         // the capture change our own release causes
    private bool _disposed;

    /// <param name="pressed">What the area asks of the host's window. On CEF's UI thread.</param>
    /// <param name="log">Diagnostics.</param>
    public ChildDragArea(Action<ChromiumDragAreaPress> pressed, ILogger? log)
    {
        _pressed = pressed;
        _log = log;
    }

    public DragAreas Areas { get; } = new();

    /// <summary>The page's areas changed. The widgets are looked for too.</summary>
    public void Update(nint browserWindow, nuint count, _cef_draggable_region_t* regions)
    {
        if (_disposed) return;
        Areas.Set(count, regions);
        AdoptWidgets(browserWindow);
    }

    /// <summary>
    /// A document started loading: look for the render widgets, which exist by then (not yet as the browser is created,
    /// measured). A replaced renderer (a crash, a cross-site navigation) brings a new widget, and nothing else announces
    /// it here: the areas CEF reports after a crash may be none, and a child browser's window hears of no new child
    /// (<c>WM_PARENTNOTIFY</c> never arrived, measured, where the Views shell's top-level window does hear of one).
    /// </summary>
    public void DocumentStarted(nint browserWindow)
    {
        if (!_disposed) AdoptWidgets(browserWindow);
    }

    private void AdoptWidgets(nint browserWindow)
    {
        foreach (var widget in Under(browserWindow)) Adopt(widget);
    }

    /// <summary>Subclass <paramref name="window"/> as a render widget. It must be on the calling thread. Idempotent.</summary>
    internal bool Adopt(nint window)
    {
        if (_disposed || _widgets.Contains(window)) return false;
        if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
        if (SetWindowSubclass(window, &WidgetProc, WidgetId, (nuint)GCHandle.ToIntPtr(_self)) == 0) return false;
        _widgets.Add(window);
        return true;
    }

    /// <summary>On CEF's UI thread, which installed the subclasses.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        if (_holder != 0) EndHold();
        _disposed = true;
        foreach (var widget in _widgets) RemoveWindowSubclass(widget, &WidgetProc, WidgetId);
        _widgets.Clear();
        if (_self.IsAllocated) _self.Free();
    }

    private void EndHold()
    {
        _holder = 0;
        _releasing = true;
        try { ReleaseCapture(); }
        finally { _releasing = false; }
    }

    private bool PastDragThreshold(nint hwnd, Point at) => RenderWidgets.PastDragThreshold(hwnd, _press.X, _press.Y, at.X, at.Y);

    private static Point Screen(nint hwnd, nint lParam)
    {
        var point = new POINT { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
        ClientToScreen(hwnd, &point);
        return new Point(point.X, point.Y);
    }

    [UnmanagedCallersOnly]
    private static nint WidgetProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        ChildDragArea? me = null;
        try
        {
            me = (ChildDragArea)GCHandle.FromIntPtr((nint)data).Target!;
            switch (msg)
            {
                case WM_LBUTTONDOWN or WM_LBUTTONDBLCLK when me._holder == 0 && !FromTouchOrPen() && InArea(me, hwnd, lParam):
                    if (msg == WM_LBUTTONDBLCLK)
                    {
                        me._pressed(new ChromiumDragAreaPress(ChromiumDragAreaAction.ToggleMaximize, Screen(hwnd, lParam)));
                        return 0;
                    }
                    me._press = Screen(hwnd, lParam);
                    me._holder = hwnd;
                    SetCapture(hwnd);
                    return 0;
                case WM_MOUSEMOVE or WM_LBUTTONUP when me._holder == hwnd:
                    // A release, or a move with the button already up (a release that never arrived): a click.
                    if (msg == WM_LBUTTONUP || (wParam & MK_LBUTTON) == 0) { me.EndHold(); return 0; }
                    if (me.PastDragThreshold(hwnd, Screen(hwnd, lParam)))
                    {
                        me.EndHold();
                        me._pressed(new ChromiumDragAreaPress(ChromiumDragAreaAction.Move, me._press));
                    }
                    return 0;
                // The other buttons during a held press, as the system's loop ignores them: passed on, they would reach
                // Chromium in the middle of a press it never saw begin.
                case (>= WM_RBUTTONDOWN and <= WM_MBUTTONDBLCLK) or (>= WM_XBUTTONDOWN and <= WM_XBUTTONDBLCLK) when me._holder == hwnd:
                    return 0;
                // A caption's right click opens its menu as the button is released, over the caption.
                case WM_RBUTTONDOWN or WM_RBUTTONDBLCLK:
                    me._rightPress = !FromTouchOrPen() && InArea(me, hwnd, lParam) ? hwnd : 0;
                    if (me._rightPress != 0) return 0;
                    break;
                case WM_RBUTTONUP when me._rightPress == hwnd:
                    // The page never saw this press begin, so it does not see it end.
                    me._rightPress = 0;
                    if (InArea(me, hwnd, lParam)) me._pressed(new ChromiumDragAreaPress(ChromiumDragAreaAction.ShowSystemMenu, Screen(hwnd, lParam)));
                    return 0;
                case WM_CAPTURECHANGED when me._releasing:
                    return 0;
                case WM_CAPTURECHANGED when me._holder == hwnd:   // taken away before it moved: a menu, another window
                    me._holder = 0;
                    break;
                case WM_NCDESTROY:
                    if (me._holder == hwnd) me._holder = 0;
                    RemoveWindowSubclass(hwnd, &WidgetProc, WidgetId);
                    me._widgets.Remove(hwnd);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppCallback.Log(me?._log, () => "[Shenora.Chromium] The page's drag area failed; the message was passed on", LogLevel.Warning, ex);
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }


    // A touch or a pen press arrives as a mouse message too: the page keeps those, so they do not move the window.
    private static bool InArea(ChildDragArea me, nint hwnd, nint lParam) => me.Areas.ContainsMessagePoint(lParam, Scale(hwnd));

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32")] private static extern int ClientToScreen(nint hwnd, POINT* point);
    [DllImport("user32")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32")] private static extern int ReleaseCapture();
}
#else
/// <summary>No drag area for a child browser on this OS: it is Windows only.</summary>
internal sealed unsafe class ChildDragArea : IDisposable
{
    public ChildDragArea(Action<ChromiumDragAreaPress> _, ILogger? __) { }
    public DragAreas Areas { get; } = new();
    public void DocumentStarted(nint browserWindow) { }
    public void Update(nint browserWindow, nuint count, _cef_draggable_region_t* regions) { }
    public void Dispose() { }
}
#endif
