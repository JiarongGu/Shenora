using System.Drawing;
using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>Where the splash's windows go: the card against the main window's plan, the overlay against its client area,
/// and a frameless window's strip with its default caption buttons.</summary>
public class SplashGeometryTests
{
    private static readonly Rectangle Primary = new(0, 0, 1920, 1040), Second = new(1920, 0, 2560, 1400);

    [Fact]
    public void Card_is_centred_on_the_planned_window()
    {
        var card = SplashGeometry.CardRect(new(1000, 700, 100, 50, false), [Primary], 480, 300);
        Assert.Equal(new Rectangle(100 + 260, 50 + 200, 480, 300), card);
    }

    [Fact]
    public void Card_with_no_place_is_centred_on_the_primary_work_area()
    {
        var card = SplashGeometry.CardRect(new(1000, 700, null, null, false), [Primary, Second], 480, 300);
        Assert.Equal(new Rectangle(720, 370, 480, 300), card);
    }

    [Fact]
    public void Card_is_centred_on_the_saved_display_s_work_area_when_the_window_opens_maximized()
    {
        // A real plan carries the RESTORED bounds and the flag: the window opens over the work area of the display the
        // restored bounds are on, and the card centres on that.
        var card = SplashGeometry.CardRect(new(800, 600, 2000, 100, true), [Primary, Second], 480, 300);
        Assert.Equal(new Rectangle(1920 + 1280 - 240, 700 - 150, 480, 300), card);
    }

    [Fact]
    public void Card_larger_than_its_display_is_clamped_into_it()
    {
        var card = SplashGeometry.CardRect(new(800, 600, 0, 0, false), [new Rectangle(0, 0, 400, 300)], 480, 400);
        Assert.Equal(new Rectangle(0, 0, 400, 300), card);
    }

    [Fact]
    public void Card_with_no_known_display_is_centred_on_the_plan()
    {
        var card = SplashGeometry.CardRect(new(1000, 700, null, null, false), [], 480, 300);
        Assert.Equal(new Rectangle(260, 200, 480, 300), card);
    }

    [Fact]
    public void Overlay_covers_the_client_area_of_a_framed_window() =>
        Assert.Equal(new Rectangle(108, 139, 1000, 700), SplashGeometry.OverlayRect(new(108, 139, 1000, 700), frameless: false, stripPx: 48));

    [Fact]
    public void Overlay_leaves_the_strip_of_a_frameless_window() =>
        Assert.Equal(new Rectangle(100, 148, 1000, 652), SplashGeometry.OverlayRect(new(100, 100, 1000, 700), frameless: true, stripPx: 48));

    [Fact]
    public void Overlay_never_goes_negative_when_the_strip_is_taller_than_the_window() =>
        Assert.Equal(1, SplashGeometry.OverlayRect(new(0, 0, 300, 20), frameless: true, stripPx: 48).Height);

    [Fact]
    public void Overlay_leaves_a_frameless_window_s_resize_band_on_its_sides_and_bottom() =>
        // CEF's frameless window resizes from a band INSIDE its edges (measured: 8 px at 200 %), which the splash must
        // not cover; the top band is in the strip already.
        Assert.Equal(new Rectangle(108, 148, 984, 644), SplashGeometry.OverlayRect(new(100, 100, 1000, 700), frameless: true, stripPx: 48, edgePx: 8));

    [Fact]
    public void Overlay_ignores_the_resize_band_of_a_framed_window() =>
        Assert.Equal(new Rectangle(100, 100, 1000, 700), SplashGeometry.OverlayRect(new(100, 100, 1000, 700), frameless: false, stripPx: 48, edgePx: 8));

    [Theory]
    [InlineData(true, false, true, (int)OverlayCorners.Bottom)]      // Windows 11, normal, framed: the frame rounds the top
    [InlineData(true, false, false, (int)OverlayCorners.Bottom)]     // frameless: the strip is above it
    [InlineData(true, true, true, (int)OverlayCorners.None)]         // maximized: square
    [InlineData(false, false, true, (int)OverlayCorners.None)]       // Windows 10: square
    public void Corners_follow_the_window(bool rounded, bool maximized, bool framed, int expected) =>
        Assert.Equal((OverlayCorners)expected, SplashGeometry.Corners(rounded, maximized, framed));

    [Theory]
    [InlineData(1.0, 46)]
    [InlineData(1.5, 69)]
    [InlineData(2.0, 92)]
    public void Default_caption_buttons_are_46_dips_each_right_aligned(double scale, int width)
    {
        var strip = (int)Math.Round(32 * scale);
        var buttons = SplashGeometry.DefaultCaptionButtons(1000, strip, scale);
        Assert.Equal([CaptionButtonKind.Minimize, CaptionButtonKind.Maximize, CaptionButtonKind.Close], buttons.Select(b => b.Kind));
        Assert.All(buttons, b => Assert.Equal((width, strip, 0), (b.Width, b.Height, b.Y)));
        Assert.Equal(1000 - width, buttons[2].X);
        Assert.Equal(1000 - 3 * width, buttons[0].X);
    }

    [Fact]
    public void Drag_rect_is_the_strip_left_of_the_buttons()
    {
        var buttons = SplashGeometry.DefaultCaptionButtons(1000, 32, 1.0);
        Assert.Equal(new Rectangle(0, 0, 1000 - 3 * 46, 32), SplashGeometry.StripDragRect(1000, 32, buttons));
        Assert.Equal(new Rectangle(0, 0, 1000, 32), SplashGeometry.StripDragRect(1000, 32, []));
    }
}
