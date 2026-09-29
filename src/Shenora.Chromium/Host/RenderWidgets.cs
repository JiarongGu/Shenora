#if CEF_WINDOWS
using System.Runtime.InteropServices;

namespace Shenora.Chromium.Host;

/// <summary>
/// What the shell's subclasses of Chromium's windows share (<see cref="CaptionHitTest"/>, <see cref="ChildDragArea"/>):
/// finding Chromium's render widgets, telling a mouse from a touch or a pen, the system's drag threshold, the DPI.
/// </summary>
internal static unsafe class RenderWidgets
{
    /// <summary>The window class of the window Chromium's page input arrives at.</summary>
    public const string ClassName = "Chrome_RenderWidgetHostHWND";

    private const int SM_CXDRAG = 68, SM_CYDRAG = 69;
    // A mouse message the system synthesized from touch or pen: MI_WP_SIGNATURE in the top bits of its extra info.
    private const ulong SignatureMask = 0xFFFFFF00, TouchOrPenSignature = 0xFF515700;

    /// <summary>Every render widget under <paramref name="parent"/>, at any depth.</summary>
    public static List<nint> Under(nint parent)
    {
        var found = new List<nint>();
        if (parent == 0) return found;
        var handle = GCHandle.Alloc(found);
        try { EnumChildWindows(parent, &Collect, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }
        found.RemoveAll(window => !Is(window));
        return found;
    }

    public static bool Is(nint hwnd)
    {
        var name = stackalloc char[64];
        var length = GetClassNameW(hwnd, name, 64);
        return new ReadOnlySpan<char>(name, length).SequenceEqual(ClassName);
    }

    /// <summary>Whether the mouse message being handled came from a touch or a pen.</summary>
    public static bool FromTouchOrPen() => ((ulong)GetMessageExtraInfo() & SignatureMask) == TouchOrPenSignature;

    /// <summary>Physical pixels per DIP at the window's DPI (per monitor).</summary>
    public static double Scale(nint hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    /// <summary>Past the system's drag rectangle: <c>SM_CXDRAG</c> by <c>SM_CYDRAG</c> centred on the press, as
    /// <c>DragDetect</c> uses it, at the window's DPI. Screen or client pixels, the same for both points.</summary>
    public static bool PastDragThreshold(nint hwnd, int pressX, int pressY, int x, int y)
    {
        var dpi = GetDpiForWindow(hwnd);
        var width = dpi > 0 ? GetSystemMetricsForDpi(SM_CXDRAG, dpi) : GetSystemMetrics(SM_CXDRAG);
        var height = dpi > 0 ? GetSystemMetricsForDpi(SM_CYDRAG, dpi) : GetSystemMetrics(SM_CYDRAG);
        return Math.Abs(x - pressX) > width / 2 || Math.Abs(y - pressY) > height / 2;
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint lParam)
    {
        ((List<nint>)GCHandle.FromIntPtr(lParam).Target!).Add(hwnd);
        return 1;
    }

    // SetWindowSubclass sets no last error: a failure has no code to report.
    [DllImport("comctl32")]
    public static extern int SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);
    [DllImport("comctl32")]
    public static extern int RemoveWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id);
    [DllImport("comctl32")] public static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] private static extern int EnumChildWindows(nint parent, delegate* unmanaged<nint, nint, int> proc, nint lParam);
    [DllImport("user32")] private static extern int GetClassNameW(nint hwnd, char* name, int max);
    [DllImport("user32")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32")] private static extern nint GetMessageExtraInfo();
}
#endif
