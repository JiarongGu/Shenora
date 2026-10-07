#if CEF_WINDOWS
using System.Drawing;
using System.Runtime.InteropServices;
using static Shenora.Chromium.Host.WindowsSplashNative;

namespace Shenora.Chromium.Host;

/// <summary>
/// The displays, as the splash needs them before CEF can describe them. CEF places windows in device-independent
/// pixels; a display's DIP origin is taken as its pixel origin at the PRIMARY display's scale (exact for the primary,
/// and for a display beside it, whose shared edge it keeps) and its DIP size at its own scale. The splash snaps to the
/// main window's real bounds once that exists, which absorbs what this approximation misses.
/// </summary>
internal static unsafe class WindowsScreens
{
    internal readonly record struct Screen(Rectangle Monitor, Rectangle Work, float Scale, bool Primary);

    /// <summary>Every display, primary first, in physical pixels.</summary>
    public static IReadOnlyList<Screen> All()
    {
        var previous = SetThreadDpiAwarenessContext(-4);   // physical pixels, whatever the caller's awareness
        try
        {
            var found = new List<Screen>();
            var handle = GCHandle.Alloc(found);
            try
            {
                EnumDisplayMonitors(0, 0, &Found, GCHandle.ToIntPtr(handle));
            }
            finally
            {
                handle.Free();
            }
            return [.. found.OrderByDescending(s => s.Primary)];
        }
        finally
        {
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }
    }

    [UnmanagedCallersOnly]
    private static int Found(nint monitor, nint dc, RECT* rect, nint data)
    {
        var info = new MONITORINFO { Size = (uint)sizeof(MONITORINFO) };
        if (GetMonitorInfoW(monitor, &info) == 0) return 1;
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96f : 1f;
        ((List<Screen>)GCHandle.FromIntPtr(data).Target!).Add(new Screen(Rect(info.Monitor), Rect(info.Work), scale, (info.Flags & MONITORINFOF_PRIMARY) != 0));
        return 1;
    }

    private static Rectangle Rect(RECT r) => Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>The displays' work areas in DIP, primary first.</summary>
    public static IReadOnlyList<Rectangle> WorkAreasDip(IReadOnlyList<Screen> screens) =>
        [.. screens.Select(s => Dip(s, s.Work, PrimaryScale(screens)))];

    /// <summary>Where a window planned at <paramref name="plan"/> (DIP) lands, in physical pixels.</summary>
    public static Rectangle ToPixels(ChromiumWindowGeometry.Plan plan, IReadOnlyList<Screen> screens)
    {
        if (screens.Count == 0) return new Rectangle(plan.X ?? 0, plan.Y ?? 0, plan.Width, plan.Height);
        var primaryScale = PrimaryScale(screens);
        Screen screen;
        if (plan is { X: { } x, Y: { } y })
        {
            var centre = new PointF(x + (plan.Width / 2f), y + (plan.Height / 2f));
            screen = screens.FirstOrDefault(s => ((RectangleF)Dip(s, s.Monitor, primaryScale)).Contains(centre), screens[0]);
        }
        else
        {
            screen = screens[0];
        }
        if (plan.Maximized) return screen.Work;
        int width = (int)Math.Round(plan.Width * screen.Scale), height = (int)Math.Round(plan.Height * screen.Scale);
        if (plan is not { X: { } px, Y: { } py })
            return new Rectangle(screen.Work.X + ((screen.Work.Width - width) / 2), screen.Work.Y + ((screen.Work.Height - height) / 2), width, height);
        var origin = Dip(screen, screen.Monitor, primaryScale);
        return new Rectangle(screen.Monitor.X + (int)Math.Round((px - origin.X) * screen.Scale), screen.Monitor.Y + (int)Math.Round((py - origin.Y) * screen.Scale),
            width, height);
    }

    private static float PrimaryScale(IReadOnlyList<Screen> screens) => screens.FirstOrDefault(s => s.Primary, screens.Count > 0 ? screens[0] : default).Scale is > 0 and var s ? s : 1f;

    // A rectangle on this display in DIP: offset from the display's DIP origin at the display's own scale.
    private static Rectangle Dip(Screen screen, Rectangle px, float primaryScale)
    {
        var originX = screen.Monitor.X / primaryScale;
        var originY = screen.Monitor.Y / primaryScale;
        return new Rectangle(
            (int)Math.Round(originX + ((px.X - screen.Monitor.X) / screen.Scale)),
            (int)Math.Round(originY + ((px.Y - screen.Monitor.Y) / screen.Scale)),
            (int)Math.Round(px.Width / screen.Scale),
            (int)Math.Round(px.Height / screen.Scale));
    }
}
#endif
