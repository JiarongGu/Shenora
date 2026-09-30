using System.Drawing;
using Shenora.Chromium.Host;
using Shenora.Core.Shell;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Chromium main window's saved size and place, the parts decided without CEF: what a saved state restores to on
/// the displays there are now, and what a window saves. Restoring and saving through CEF's window were run end to end
/// in the shell.
/// </summary>
public class ChromiumWindowGeometryTests
{
    private static readonly Rectangle Primary = new(0, 0, 1920, 1040);     // a work area: the taskbar excluded
    private static readonly Rectangle Right = new(1920, 0, 1280, 984);
    private static readonly WindowStateOptions Options = new();

    [Fact]
    public void Nothing_saved_opens_at_the_windows_own_size_centred()
    {
        // Centred by the plan itself, so the window has the bounds it restores to from the start.
        var plan = ChromiumWindowGeometry.PlanFor(null, Options, 1000, 760, [Primary, Right]);
        Assert.Equal(new ChromiumWindowGeometry.Plan(1000, 760, 460, 140, false), plan);
    }

    [Fact]
    public void The_windows_own_size_is_floored_at_the_minimum_it_keeps_while_running()
    {
        var plan = ChromiumWindowGeometry.PlanFor(null, Options, 640, 480, [Primary]);
        Assert.Equal((800, 600), (plan.Width, plan.Height));
    }

    [Fact]
    public void A_saved_place_on_a_display_is_restored_as_it_was()
    {
        var saved = new WindowState(1100, 700, 2000, 100, WindowPlacement.Normal);
        Assert.Equal(new ChromiumWindowGeometry.Plan(1100, 700, 2000, 100, false),
            ChromiumWindowGeometry.PlanFor(saved, Options, 1000, 760, [Primary, Right]));
    }

    [Fact]
    public void A_place_no_display_shows_any_more_is_dropped_and_the_window_centred()
    {
        // The right-hand monitor was unplugged since.
        var saved = new WindowState(1100, 700, 2000, 100, WindowPlacement.Normal);
        var plan = ChromiumWindowGeometry.PlanFor(saved, Options, 1000, 760, [Primary]);
        Assert.Equal(new ChromiumWindowGeometry.Plan(1100, 700, 410, 170, false), plan);   // centred on the primary
    }

    [Fact]
    public void A_maximized_window_whose_place_was_dropped_still_has_bounds_to_restore_to()
    {
        // Maximized on a monitor since unplugged: it opens maximized on the primary, over a centred normal rect.
        var saved = new WindowState(1100, 700, 2000, 100, WindowPlacement.Maximized);
        Assert.Equal(new ChromiumWindowGeometry.Plan(1100, 700, 410, 170, true),
            ChromiumWindowGeometry.PlanFor(saved, Options, 1000, 760, [Primary]));
    }

    [Fact]
    public void A_place_with_only_a_sliver_left_on_screen_is_dropped()
    {
        // 50 px of it on the display: less than the strip a person can grab (120 × 60 by default).
        var saved = new WindowState(800, 600, 1870, 100, WindowPlacement.Normal);
        Assert.Equal(560, ChromiumWindowGeometry.PlanFor(saved, Options, 1000, 760, [Primary]).X);   // centred instead
        Assert.Equal(1720, ChromiumWindowGeometry.PlanFor(saved with { X = 1720 }, Options, 1000, 760, [Primary]).X);
    }

    [Fact]
    public void A_size_saved_on_a_bigger_display_shrinks_to_its_display_and_never_below_the_minimum()
    {
        var saved = new WindowState(2400, 1600, 1950, 10, WindowPlacement.Normal);
        var plan = ChromiumWindowGeometry.PlanFor(saved, Options, 1000, 760, [Primary, Right]);
        Assert.Equal((1280, 984), (plan.Width, plan.Height));   // the right-hand display, which holds its corner

        var tiny = new WindowState(300, 200, 10, 10, WindowPlacement.Normal);
        var floored = ChromiumWindowGeometry.PlanFor(tiny, Options, 1000, 760, [Primary]);
        Assert.Equal((800, 600), (floored.Width, floored.Height));

        var unclamped = ChromiumWindowGeometry.PlanFor(saved, new WindowStateOptions { MaxToWorkArea = false }, 1000, 760, [Primary, Right]);
        Assert.Equal((2400, 1600), (unclamped.Width, unclamped.Height));
    }

    [Fact]
    public void A_maximized_window_restores_maximized_over_its_normal_bounds()
    {
        var saved = new WindowState(1100, 700, 100, 100, WindowPlacement.Maximized);
        Assert.Equal(new ChromiumWindowGeometry.Plan(1100, 700, 100, 100, true),
            ChromiumWindowGeometry.PlanFor(saved, Options, 1000, 760, [Primary]));
    }

    [Fact]
    public void A_position_is_a_pair_or_nothing()
    {
        var saved = new WindowState(1100, 700, 100, null, WindowPlacement.Normal);
        var plan = ChromiumWindowGeometry.PlanFor(saved, Options, 1000, 760, [Primary]);
        Assert.Equal((410, 170), (plan.X, plan.Y));   // centred: half a position places nothing
    }

    [Fact]
    public void With_no_displays_known_the_window_is_centred_by_CEF()
    {
        var saved = new WindowState(1100, 700, 100, 100, WindowPlacement.Normal);
        Assert.Null(ChromiumWindowGeometry.PlanFor(saved, Options, 1000, 760, []).X);
    }

    [Fact]
    public void A_normal_window_saves_where_it_is()
    {
        var state = ChromiumWindowGeometry.StateFor(new Rectangle(1, 2, 3, 4), new Rectangle(50, 60, 900, 700), isNormal: true, maximized: false);
        Assert.Equal(new WindowState(900, 700, 50, 60, WindowPlacement.Normal), state);
    }

    [Fact]
    public void A_maximized_window_saves_the_bounds_it_had_before()
    {
        var state = ChromiumWindowGeometry.StateFor(new Rectangle(50, 60, 900, 700), new Rectangle(0, 0, 1920, 1040), isNormal: false, maximized: true);
        Assert.Equal(new WindowState(900, 700, 50, 60, WindowPlacement.Maximized), state);
    }

    [Fact]
    public void A_minimized_window_saves_its_normal_bounds_as_normal()
    {
        var state = ChromiumWindowGeometry.StateFor(new Rectangle(50, 60, 900, 700), new Rectangle(-32000, -32000, 160, 28), isNormal: false, maximized: false);
        Assert.Equal(new WindowState(900, 700, 50, 60, WindowPlacement.Normal), state);
    }

    [Fact]
    public void A_maximized_window_closed_minimized_keeps_maximized()
    {
        // What it last showed as (Save passes the flag tracked while it showed, not a minimized window's answer).
        var state = ChromiumWindowGeometry.StateFor(new Rectangle(50, 60, 900, 700), new Rectangle(-32000, -32000, 160, 28), isNormal: false, maximized: true);
        Assert.Equal(new WindowState(900, 700, 50, 60, WindowPlacement.Maximized), state);
    }

    private static ChromiumWindowGeometry Tracker() => new(new TestSupport.FakeWindowStateStore(), Options, null);

    private static readonly Rectangle Left = new(200, 150, 1100, 700);
    private static readonly Rectangle Filled = new(0, 25, 1680, 951);

    [Fact]
    public void A_zoom_animation_never_becomes_the_size_to_restore_to()
    {
        // macOS animates a zoom and reports each frame as a normal window's bounds until the last (measured: a
        // maximize saved 1673×949, a frame, as the restore size before the bounds had to settle).
        var g = Tracker();
        g.Observe(shows: true, normal: true, maximized: false, Left, now: 0);
        for (var i = 1; i <= 15; i++)
            g.Observe(true, true, false, new Rectangle(200 - 13 * i, 150 - 8 * i, 1100 + 38 * i, 700 + 16 * i), now: 5000 + 16 * i);
        g.Observe(true, normal: false, maximized: true, Filled, now: 5260);

        Assert.Equal(new WindowState(1100, 700, 200, 150, WindowPlacement.Maximized),
            g.Closing(shows: true, normal: false, maximized: true, Filled, now: 9000));
    }

    [Fact]
    public void A_zoom_whose_last_frame_never_says_maximized_is_still_saved_maximized_over_the_bounds_before()
    {
        // The measured macOS failure: the frames of a zoom, the last one full screen, all reported as a normal
        // window's, and no maximized change after them; at the close, which is seconds later, the window is hidden
        // and says it is maximized. It saved the full-screen frame as a Normal size before this.
        var g = Tracker();
        g.Observe(true, true, false, Left, now: 0);
        g.Observe(true, true, false, new Rectangle(25, 41, 1606, 919), now: 5000);
        g.Observe(true, true, false, new Rectangle(9, 31, 1653, 939), now: 5032);
        g.Observe(true, true, false, new Rectangle(0, 25, 1680, 951), now: 5065);   // the last frame, "normal"

        Assert.Equal(new WindowState(1100, 700, 200, 150, WindowPlacement.Maximized),
            g.Closing(shows: false, normal: false, maximized: true, new Rectangle(0, 25, 1680, 951), now: 8000));
    }

    [Fact]
    public void A_maximize_in_one_step_keeps_the_bounds_the_window_had()
    {
        var g = Tracker();
        g.Observe(true, true, false, Left, now: 0);
        g.Observe(true, false, true, Filled, now: 2000);
        Assert.Equal(new WindowState(1100, 700, 200, 150, WindowPlacement.Maximized), g.Closing(true, false, true, Filled, now: 3000));
    }

    [Fact]
    public void A_resize_the_window_was_left_at_is_kept_once_it_settled()
    {
        var g = Tracker();
        g.Observe(true, true, false, Left, now: 0);
        g.Observe(true, true, false, new Rectangle(250, 150, 1000, 650), now: 4000);   // the user's resize
        g.Observe(true, false, true, Filled, now: 7000);                              // maximized seconds later
        Assert.Equal(new WindowState(1000, 650, 250, 150, WindowPlacement.Maximized), g.Closing(true, false, true, Filled, now: 8000));
    }

    [Fact]
    public void Restored_then_minimized_saves_the_restored_bounds_once_they_settled()
    {
        var g = Tracker();
        g.Observe(true, false, true, Filled, now: 0);                                   // opened maximized
        g.Observe(true, true, false, new Rectangle(100, 80, 1500, 900), now: 1000);    // a frame of the restore
        g.Observe(true, true, false, Left, now: 1016);                                 // where it came to rest
        g.Observe(shows: false, normal: false, maximized: false, new Rectangle(-32000, -32000, 160, 28), now: 3000);   // minimized
        Assert.Equal(new WindowState(1100, 700, 200, 150, WindowPlacement.Normal),
            g.Closing(shows: false, normal: false, maximized: false, new Rectangle(-32000, -32000, 160, 28), now: 5000));
    }

    [Fact]
    public void Maximized_then_minimized_or_hidden_still_saves_maximized()
    {
        var g = Tracker();
        g.Observe(true, true, false, Left, now: 0);
        g.Observe(true, false, true, Filled, now: 2000);
        g.Observe(false, false, false, new Rectangle(-32000, -32000, 160, 28), now: 4000);   // minimized: "not maximized"
        Assert.Equal(new WindowState(1100, 700, 200, 150, WindowPlacement.Maximized),
            g.Closing(shows: false, normal: false, maximized: false, Rectangle.Empty, now: 6000));
    }

    [Fact]
    public void Nothing_is_saved_for_a_window_that_never_had_a_size()
    {
        Assert.Null(ChromiumWindowGeometry.StateFor(null, Rectangle.Empty, isNormal: true, maximized: false));
    }
}
