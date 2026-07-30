using System.Drawing;
using Shenora.Tests.TestSupport;
using Shenora.WinForms;

namespace Shenora.Tests.WinForms;

/// <summary>
/// Page-drawn caption buttons (P5.6): the hit-test that buys Windows 11 Snap Layouts, and the
/// click/hover handling that claiming the hit-test makes MANDATORY.
/// <para>
/// The real flyout needs a live interactive window and a human — that part is e2e/manual. What is
/// tested here is everything the OS decides FROM: which code the window reports for a point, that a
/// registered button beats the resize strip, that press-then-release-elsewhere does not activate,
/// and that clearing the regions cannot leave the app painting a hover forever.
/// </para>
/// </summary>
public class CaptionButtonTests
{
    private const int WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0, WM_NCMOUSELEAVE = 0x02A2,
                      WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2;
    private const int HTMINBUTTON = 8, HTMAXBUTTON = 9, HTCLOSE = 20, HTTOP = 12;

    /// <summary>A frameless form with its caption buttons in a known place, realized off-screen.</summary>
    private static OptimizedForm CreateForm()
    {
        var form = new OptimizedForm(new OptimizedFormOptions { FramelessChrome = true })
        {
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(0, 0, 800, 600),
            ShowInTaskbar = false,
        };
        _ = form.Handle; // hit-testing converts screen↔client, which needs a realized window
        form.SetCaptionButtons(
        [
            new CaptionButtonRegion(CaptionButtonKind.Minimize, new Rectangle(700, 0, 30, 30)),
            new CaptionButtonRegion(CaptionButtonKind.Maximize, new Rectangle(730, 0, 30, 30)),
            new CaptionButtonRegion(CaptionButtonKind.Close, new Rectangle(760, 0, 30, 30)),
        ]);
        return form;
    }

    /// <summary>
    /// Ask the window what it reports for a CLIENT point. Sent as a REAL message so the whole
    /// production path runs — WndProc, the base call, and DefWindowProc underneath it. Reflecting
    /// into WndProc would not do: <c>Message</c> is a struct, so an invoked copy never surfaces the
    /// result the OS would actually see.
    /// </summary>
    private static int HitTest(OptimizedForm form, int clientX, int clientY)
    {
        var screen = form.PointToScreen(new Point(clientX, clientY));
        return (int)SendMessage(form.Handle, WM_NCHITTEST, IntPtr.Zero,
            (IntPtr)(((screen.Y & 0xFFFF) << 16) | (screen.X & 0xFFFF)));
    }

    private static void SendNc(OptimizedForm form, int msg, int hitTest) =>
        SendMessage(form.Handle, msg, (IntPtr)hitTest, IntPtr.Zero);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [Fact]
    public void Each_registered_region_reports_its_caption_hit_test_code() => Sta.Run(() =>
    {
        using var form = CreateForm();

        // HTMAXBUTTON is the one that matters most: it is the entire mechanism behind Snap Layouts.
        Assert.Equal(HTMINBUTTON, HitTest(form, 710, 10));
        Assert.Equal(HTMAXBUTTON, HitTest(form, 745, 10));
        Assert.Equal(HTCLOSE, HitTest(form, 775, 10));
    });

    [Fact]
    public void A_caption_button_beats_the_top_resize_strip() => Sta.Run(() =>
    {
        using var form = CreateForm();

        // Both live at y=0. Losing a few px of resize border is a far smaller cost than a close
        // button that resizes the window instead of closing it.
        Assert.Equal(HTCLOSE, HitTest(form, 775, 1));
        // …and a point outside every button still gets the resize strip.
        Assert.Equal(HTTOP, HitTest(form, 400, 1));
    });

    [Fact]
    public void Points_outside_every_region_are_left_to_the_page() => Sta.Run(() =>
    {
        using var form = CreateForm();

        // Below the strip and outside the buttons = client, i.e. the WebView2's mouse events.
        Assert.Equal(1 /* HTCLIENT */, HitTest(form, 400, 300));
        // Just past the last button's right edge.
        Assert.NotEqual(HTCLOSE, HitTest(form, 795, 10));
    });

    [Fact]
    public void With_no_regions_registered_nothing_changes() => Sta.Run(() =>
    {
        using var form = CreateForm();
        form.SetCaptionButtons(null);

        // The whole feature costs nothing until an app opts in.
        Assert.Equal(HTTOP, HitTest(form, 745, 1));
        Assert.Equal(1 /* HTCLIENT */, HitTest(form, 745, 10));
    });

    [Fact]
    public void Hover_is_reported_because_the_page_can_no_longer_see_it() => Sta.Run(() =>
    {
        using var form = CreateForm();
        var states = new List<CaptionButtonState>();
        form.CaptionButtonStateChanged = states.Add;

        SendNc(form, WM_NCMOUSEMOVE, HTMAXBUTTON);
        SendNc(form, WM_NCMOUSELEAVE, 0);

        Assert.Equal(CaptionButtonKind.Maximize, states[0].Hot);
        Assert.Null(states[0].Pressed);
        Assert.Null(states[^1].Hot);
    });

    [Fact]
    public void Repeated_hover_over_the_same_button_does_not_re_notify() => Sta.Run(() =>
    {
        using var form = CreateForm();
        var count = 0;
        form.CaptionButtonStateChanged = _ => count++;

        SendNc(form, WM_NCMOUSEMOVE, HTMAXBUTTON);
        SendNc(form, WM_NCMOUSEMOVE, HTMAXBUTTON);
        SendNc(form, WM_NCMOUSEMOVE, HTMAXBUTTON);

        // WM_NCMOUSEMOVE fires per mouse move; re-pushing an unchanged state would spam the page's
        // IPC channel with a notification per pixel.
        Assert.Equal(1, count);
    });

    [Fact]
    public void A_press_then_release_on_the_same_button_activates_it() => Sta.Run(() =>
    {
        using var form = CreateForm();
        Assert.False(form.IsAppMaximized);

        SendNc(form, WM_NCLBUTTONDOWN, HTMAXBUTTON);
        SendNc(form, WM_NCLBUTTONUP, HTMAXBUTTON);

        // Routed through ToggleMaximize, the SAME member the page's IPC command uses — so the
        // frameless manual-maximize bookkeeping cannot diverge between the two paths (P5.5 H2).
        Assert.True(form.IsAppMaximized);
    });

    [Fact]
    public void A_press_that_releases_on_a_DIFFERENT_button_does_nothing() => Sta.Run(() =>
    {
        using var form = CreateForm();

        SendNc(form, WM_NCLBUTTONDOWN, HTMAXBUTTON);
        SendNc(form, WM_NCLBUTTONUP, HTMINBUTTON); // dragged off before releasing

        // Every other button on the system behaves this way; a maximize that fires anyway would be
        // the kind of thing a user cannot cancel.
        Assert.False(form.IsAppMaximized);
        Assert.Equal(FormWindowState.Normal, form.WindowState);
    });

    [Fact]
    public void Clearing_the_regions_clears_a_hover_the_app_is_painting() => Sta.Run(() =>
    {
        using var form = CreateForm();
        var states = new List<CaptionButtonState>();
        SendNc(form, WM_NCMOUSEMOVE, HTMAXBUTTON);
        form.CaptionButtonStateChanged = states.Add;

        form.SetCaptionButtons(null);

        // Otherwise the app is left rendering a hot button whose hover can never end, because the
        // messages that would have cleared it are no longer claimed.
        var last = Assert.Single(states);
        Assert.Null(last.Hot);
        Assert.Null(last.Pressed);
    });

    [Fact]
    public void A_throwing_state_handler_cannot_take_the_window_down() => Sta.Run(() =>
    {
        using var form = CreateForm();
        form.CaptionButtonStateChanged = _ => throw new InvalidOperationException("app handler blew up");

        // This runs inside WndProc, where an escaping exception has no caller on the stack and
        // surfaces as the bootstrap's crash dialog — the kit-wide guarded-callback rule.
        SendNc(form, WM_NCMOUSEMOVE, HTMAXBUTTON);

        Assert.False(form.IsDisposed);
    });
}
