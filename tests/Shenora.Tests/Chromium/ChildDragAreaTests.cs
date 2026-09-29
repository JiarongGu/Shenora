using System.Runtime.InteropServices;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Chromium.Interop;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Chromium;

/// <summary>
/// A child browser's <c>-webkit-app-region</c> areas, without CEF: the areas as CEF reports them, and the subclass
/// that takes a press there away from the page, over a plain window standing in for Chromium's render widget. The whole
/// path, CEF's report included, is measured in the WinForms probe with presses posted to the real render widget.
/// </summary>
public class ChildDragAreaTests
{
    private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;
    private const nint MK_LBUTTON = 1;

    [Fact]
    public void A_later_area_wins_where_two_overlap()
    {
        var areas = new DragAreas();
        areas.Set((0, 0, 400, 32, true), (100, 5, 50, 20, false));

        Assert.True(areas.Contains(10, 10));     // the bar
        Assert.False(areas.Contains(110, 10));   // a no-drag button inside it
        Assert.False(areas.Contains(10, 40));    // below the bar
        Assert.False(areas.Contains(400, 10));   // the right edge is outside
    }

    [Fact]
    public unsafe void The_areas_are_read_as_CEF_reports_them()
    {
        var regions = new[]
        {
            new _cef_draggable_region_t { bounds = new _cef_rect_t { x = 0, y = 0, width = 400, height = 32 }, draggable = 1 },
            new _cef_draggable_region_t { bounds = new _cef_rect_t { x = 100, y = 5, width = 50, height = 20 }, draggable = 0 },
        };
        var areas = new DragAreas();
        fixed (_cef_draggable_region_t* p = regions) areas.Set((nuint)regions.Length, p);

        Assert.True(areas.Contains(10, 10));
        Assert.False(areas.Contains(110, 10));

        // A page that drops its last area is reported with none.
        areas.Set(0, null);
        Assert.False(areas.Contains(10, 10));
    }

    // A message's client point is physical px; the areas are DIPs, so a 200 % display halves it.
    [Fact]
    public void A_message_point_is_read_at_the_windows_scale()
    {
        var areas = new DragAreas();
        areas.Set((0, 0, 100, 30, true));

        Assert.True(areas.ContainsMessagePoint(At(150, 50), 2.0));    // (75, 25) in DIPs
        Assert.False(areas.ContainsMessagePoint(At(150, 50), 1.0));   // (150, 50)
    }

    // Held until the pointer passes the drag threshold, as the shell's caption is: a still click asks nothing, and enters
    // no modal loop. The page sees none of it.
    [Fact]
    public void A_press_in_the_area_is_held_until_it_drags_and_a_still_click_asks_nothing()
    {
        Sta.Run(() =>
        {
            using var form = new Form();
            var page = new Control { Bounds = new Rectangle(0, 0, 400, 300) };
            form.Controls.Add(page);
            _ = form.Handle;
            _ = page.Handle;
            var pageDowns = 0;
            page.MouseDown += (_, _) => pageDowns++;
            var presses = new List<ChromiumDragAreaPress>();
            var area = new ChildDragArea(presses.Add, null);
            area.Areas.Set((0, 0, 200, 30, true));
            Assert.True(area.Adopt(page.Handle));
            var scale = page.DeviceDpi / 96.0;
            int X(double dip) => (int)Math.Round(dip * scale);

            // A still click.
            Send(page, WM_LBUTTONDOWN, MK_LBUTTON, X(10), X(10));
            Assert.Equal(page.Handle, GetCapture());
            Send(page, WM_LBUTTONUP, 0, X(10), X(10));
            Assert.Empty(presses);
            Assert.NotEqual(page.Handle, GetCapture());

            // A drag: a step inside the threshold asks nothing, one past it asks a move from the press.
            Send(page, WM_LBUTTONDOWN, MK_LBUTTON, X(10), X(10));
            Send(page, WM_MOUSEMOVE, MK_LBUTTON, X(10) + 1, X(10));
            Assert.Empty(presses);
            Send(page, WM_MOUSEMOVE, MK_LBUTTON, X(60), X(20));
            var moved = Assert.Single(presses);
            Assert.False(moved.DoubleClick);
            Assert.Equal(page.PointToScreen(new Point(X(10), X(10))), moved.Press);
            Assert.NotEqual(page.Handle, GetCapture());
            Send(page, WM_LBUTTONUP, 0, X(60), X(20));

            // A double click asks at once.
            Send(page, WM_LBUTTONDBLCLK, MK_LBUTTON, X(10), X(10));
            Assert.True(presses[^1].DoubleClick);
            Assert.Equal(2, presses.Count);
            Assert.Equal(0, pageDowns);

            Send(page, WM_LBUTTONDOWN, MK_LBUTTON, X(10), X(50));   // outside the area
            Assert.Equal(1, pageDowns);

            // Disposed, the page has every press again.
            area.Dispose();
            Send(page, WM_LBUTTONDOWN, MK_LBUTTON, X(10), X(10));
            Assert.Equal(2, pageDowns);
            Assert.Equal(2, presses.Count);
        });
    }

    // The system's drag rectangle is SM_CXDRAG wide CENTRED on the press, as DragDetect uses it: half each way. A press
    // that has moved exactly that far is still a click; one pixel more is a drag. The shell's caption uses the same.
    [Fact]
    public void The_drag_threshold_is_the_systems_rectangle_centred_on_the_press()
    {
        Sta.Run(() =>
        {
            using var form = new Form();
            var page = new Control { Bounds = new Rectangle(0, 0, 400, 300) };
            form.Controls.Add(page);
            _ = form.Handle;
            _ = page.Handle;
            var presses = new List<ChromiumDragAreaPress>();
            var area = new ChildDragArea(presses.Add, null);
            area.Areas.Set((0, 0, 400, 300, true));
            Assert.True(area.Adopt(page.Handle));
            var half = GetSystemMetricsForDpi(68 /* SM_CXDRAG */, (uint)page.DeviceDpi) / 2;

            Send(page, WM_LBUTTONDOWN, MK_LBUTTON, 100, 100);
            Send(page, WM_MOUSEMOVE, MK_LBUTTON, 100 + half, 100);
            Assert.Empty(presses);
            Send(page, WM_MOUSEMOVE, MK_LBUTTON, 100 + half + 1, 100);
            Assert.Single(presses);
            Send(page, WM_LBUTTONUP, 0, 100 + half + 1, 100);
            area.Dispose();
        });
    }

    private static nint At(int x, int y) => (y << 16) | (x & 0xFFFF);

    [DllImport("user32")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    private static void Send(Control window, uint msg, nint keys, int x, int y) => SendMessage(window.Handle, msg, keys, At(x, y));

    [DllImport("user32")] private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] private static extern nint GetCapture();
}
