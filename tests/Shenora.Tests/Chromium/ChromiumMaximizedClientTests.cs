using System.Runtime.InteropServices;
using Shenora.Chromium.Host;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Chromium;

/// <summary>
/// A maximized frameless Chromium window's client is the monitor's work area, where Chromium's own overhung it by a
/// pixel (measured, CEF 154 at 200 %), a column of it on the next monitor. A form stands in for CEF's window here: its
/// own maximized client keeps a caption, which the frameless hit-test replaces with the work area, less a reveal gap
/// beside an auto-hide app bar.
/// </summary>
public class ChromiumMaximizedClientTests
{
    private const int GWL_STYLE = -16, WS_MAXIMIZE = 0x01000000;
    private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_maximized_frameless_windows_client_is_the_work_area(bool frameless) => Sta.Run(() =>
    {
        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(100, 100, 800, 600) };
        var hwnd = form.Handle;
        using var hitTest = CaptionHitTest.Attach(hwnd, new CaptionButtons(_ => { }, _ => { }), frameless, null);
        Assert.NotNull(hitTest);
        var work = Screen.FromHandle(hwnd).WorkingArea;   // only for placing it: the exact figures come from Win32 below

        // Maximized as the system does it, hidden: the style, then a frame larger than the work area.
        SetWindowLong(hwnd, GWL_STYLE, GetWindowLong(hwnd, GWL_STYLE) | WS_MAXIMIZE);
        SetWindowPos(hwnd, 0, work.Left - 12, work.Top - 12, work.Width + 24, work.Height + 24, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        var client = ClientOnScreen(hwnd);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info);
        var exact = Rectangle.FromLTRB(info.rcWork.L, info.rcWork.T, info.rcWork.R, info.rcWork.B);
        if (frameless)
        {
            // Never outside it, and on each edge exactly on it or within the reveal gap an auto-hide bar there needs.
            Assert.True(exact.Contains(client), $"client {client} overhangs the work area {exact}");
            Assert.InRange(client.Left - exact.Left, 0, 2);
            Assert.InRange(client.Top - exact.Top, 0, 2);
            Assert.InRange(exact.Right - client.Right, 0, 2);
            Assert.InRange(exact.Bottom - client.Bottom, 0, 2);
        }
        else
        {
            Assert.True(client.Top - exact.Top > 2, "a framed window keeps its own caption");   // left to the system
        }
    });

    private static Rectangle ClientOnScreen(nint hwnd)
    {
        GetClientRect(hwnd, out var c);
        var origin = new POINT();
        ClientToScreen(hwnd, ref origin);
        return new Rectangle(origin.X, origin.Y, c.R, c.B);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] private static extern bool GetClientRect(nint hwnd, out RECT r);
    [DllImport("user32")] private static extern bool ClientToScreen(nint hwnd, ref POINT p);
    [DllImport("user32")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32")] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
}
