using System.Drawing;
using Shenora.Tests.TestSupport;
using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// Caption buttons (P5.6): the hit-test that buys Windows 11 Snap Layouts, the click/hover handling
/// that claiming the hit-test makes MANDATORY, and the window-region clip that makes any of it reach
/// the OS in the first place.
/// <para>
/// ⚠ READ THIS BEFORE TRUSTING A GREEN RUN HERE. Every test below drives
/// <c>SendMessage(form, …)</c>, which is the one step REAL input never takes — the OS picks the
/// target with <c>WindowFromPoint</c>, and a WebView2 child covering the client area wins. P5.6
/// shipped once with 10 green tests here, two sabotage runs and a live Win32 probe, having never
/// worked: all of them bypassed the routing. So these tests pin the window's DECISIONS, and the
/// routing itself is proven only by the clip tests (which assert the covering control's region is
/// actually cut) plus a live <c>WindowFromPoint</c>/human pass. See
/// <c>.claude/knowledge/winforms-shell.md</c>.
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
        Assert.Equal(WindowPlacement.Normal, form.AppPlacement);

        SendNc(form, WM_NCLBUTTONDOWN, HTMAXBUTTON);
        SendNc(form, WM_NCLBUTTONUP, HTMAXBUTTON);

        // Routed through ToggleMaximize, the SAME member the page's IPC command uses — so the
        // frameless manual-maximize bookkeeping cannot diverge between the two paths (P5.5 H2).
        Assert.Equal(WindowPlacement.Maximized, form.AppPlacement);
    });

    [Fact]
    public void A_press_that_releases_on_a_DIFFERENT_button_does_nothing() => Sta.Run(() =>
    {
        using var form = CreateForm();

        SendNc(form, WM_NCLBUTTONDOWN, HTMAXBUTTON);
        SendNc(form, WM_NCLBUTTONUP, HTMINBUTTON); // dragged off before releasing

        // Every other button on the system behaves this way; a maximize that fires anyway would be
        // the kind of thing a user cannot cancel.
        Assert.Equal(WindowPlacement.Normal, form.AppPlacement);
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

    // ── The clip: what actually makes the OS route input here (P5.6 hybrid) ──────────────────────

    /// <summary>The cluster the clipping tests register, as a union: x 700..790, y 0..30.</summary>
    private static readonly CaptionButtonRegion[] Cluster =
    [
        new(CaptionButtonKind.Minimize, new Rectangle(700, 0, 30, 30)),
        new(CaptionButtonKind.Maximize, new Rectangle(730, 0, 30, 30)),
        new(CaptionButtonKind.Close, new Rectangle(760, 0, 30, 30)),
    ];

    private static readonly Point InsideCluster = new(775, 10);   // over the close button
    private static readonly Point BesideCluster = new(400, 10);   // same strip, left of the cluster

    private static OptimizedForm CreateClippingForm(bool nativeCaptionButtons = true)
    {
        var form = new OptimizedForm(new OptimizedFormOptions
        {
            FramelessChrome = true,
            NativeCaptionButtons = nativeCaptionButtons,
        })
        {
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(0, 0, 800, 600),
            ShowInTaskbar = false,
        };
        _ = form.Handle;
        return form;
    }

    private static Panel AddCover(OptimizedForm form)
    {
        var cover = new Panel { Dock = DockStyle.Fill };
        form.Controls.Add(cover);
        _ = cover.Handle; // a region can only be cut once the control is realized
        return cover;
    }

    [Fact]
    public void The_cluster_is_cut_out_of_a_covering_child() => Sta.Run(() =>
    {
        using var form = CreateClippingForm();
        var cover = AddCover(form);

        form.SetCaptionButtons(Cluster);

        // THE mechanism: a child that does not COVER these pixels cannot receive their input, so the
        // form's WM_NCHITTEST finally runs there and Windows offers Snap Layouts. Without this the
        // hit-test above is answered into a void — which is exactly how P5.6 shipped broken once.
        Assert.False(cover.Region!.IsVisible(InsideCluster));
        Assert.True(cover.Region.IsVisible(BesideCluster));
    });

    [Fact]
    public void EVERY_covering_child_is_cut_not_just_one() => Sta.Run(() =>
    {
        using var form = CreateClippingForm();
        var splash = AddCover(form);   // covers the caption while the app boots
        var webView = AddCover(form);  // covers it afterwards

        form.SetCaptionButtons(Cluster);

        // The reason this is not a single named control: a splash panel owns the caption pixels
        // before any page exists, so naming the web view alone leaves the window unclosable for the
        // whole of startup (user-reported). Both are cut, so the buttons work in both phases.
        Assert.False(splash.Region!.IsVisible(InsideCluster));
        Assert.False(webView.Region!.IsVisible(InsideCluster));
    });

    [Fact]
    public void A_child_added_AFTER_the_rects_were_reported_is_cut_too() => Sta.Run(() =>
    {
        using var form = CreateClippingForm();
        form.SetCaptionButtons(Cluster);

        var late = AddCover(form); // e.g. a drop-zone overlay, or the web view built after startup

        Assert.False(late.Region!.IsVisible(InsideCluster));
    });

    [Fact]
    public void Clearing_the_regions_hands_every_pixel_back() => Sta.Run(() =>
    {
        using var form = CreateClippingForm();
        var cover = AddCover(form);
        form.SetCaptionButtons(Cluster);
        Assert.False(cover.Region!.IsVisible(InsideCluster));

        form.SetCaptionButtons(null);

        // A hole nobody paints into is a dead rectangle, so the clip must be undone with the regions.
        Assert.True(cover.Region is null || cover.Region.IsVisible(InsideCluster));
    });

    [Fact]
    public void A_child_removed_from_the_form_does_not_keep_our_hole() => Sta.Run(() =>
    {
        using var form = CreateClippingForm();
        var cover = AddCover(form);
        form.SetCaptionButtons(Cluster);

        form.Controls.Remove(cover);

        // It may be re-parented or shown elsewhere; carrying our cut-out corner with it would be a
        // hole in someone else's window.
        Assert.True(cover.Region is null || cover.Region.IsVisible(InsideCluster));
        cover.Dispose();
    });

    [Fact]
    public void Without_the_option_nothing_is_clipped() => Sta.Run(() =>
    {
        using var form = CreateClippingForm(nativeCaptionButtons: false);
        var cover = AddCover(form);

        form.SetCaptionButtons(Cluster);

        // The un-clipped mode is a real mode, not a broken one: the app draws the buttons itself and
        // learns hot/pressed from CaptionButtonStateChanged. It must cost nothing here.
        Assert.Null(cover.Region);
    });

    [Fact]
    public void Asking_for_native_buttons_on_a_FRAMED_window_fails_loudly() => Sta.Run(() =>
    {
        // A framed window has real caption buttons and never reaches the custom hit-test, so the
        // option could only ever do nothing — and "the buttons just don't work" with no error and
        // nothing to grep is the failure mode P5.5 H3 exists to stop.
        var ex = Assert.Throws<ArgumentException>(() =>
            new OptimizedForm(new OptimizedFormOptions { NativeCaptionButtons = true }));

        Assert.Contains(nameof(OptimizedFormOptions.FramelessChrome), ex.Message);
    });

    [Fact]
    public void The_hole_spans_the_WHOLE_cluster_not_one_button() => Sta.Run(() =>
    {
        using var form = CreateClippingForm();
        var cover = AddCover(form);

        form.SetCaptionButtons(Cluster);

        // Driven from the UNION of the reported rects, never a constant: the cluster is ~250 physical
        // px at 200% scaling, so a value guessed at 100% cuts straight THROUGH the buttons (measured
        // during the spike). Every button, and the gaps between them, must be inside the hole.
        Assert.False(cover.Region!.IsVisible(new Point(710, 10))); // minimize
        Assert.False(cover.Region.IsVisible(new Point(745, 10)));  // maximize
        Assert.False(cover.Region.IsVisible(new Point(775, 10)));  // close
    });

    [Fact]
    public void Hovering_a_button_repaints_it() => Sta.Run(() =>
    {
        using var form = CreateClippingForm();
        AddCover(form);
        form.SetCaptionButtons(Cluster);

        var invalidated = new List<Rectangle>();
        form.Invalidated += (_, e) => invalidated.Add(e.InvalidRect);

        SendNc(form, WM_NCMOUSEMOVE, HTMAXBUTTON);

        // THE defect that only RUNNING the sample found: the state changed and the callback fired,
        // but nothing ever asked for a repaint — so with the kit owning these pixels the buttons
        // never visibly reacted. Everything else about the chain was already correct, which is why
        // it survived a green suite. (The user reported it as "the hover style is not working".)
        Assert.Contains(invalidated, r => r.Contains(new Point(745, 10)));
    });

    [Fact]
    public void Releasing_a_press_repaints_the_button_it_left() => Sta.Run(() =>
    {
        using var form = CreateClippingForm();
        AddCover(form);
        form.SetCaptionButtons(Cluster);
        SendNc(form, WM_NCLBUTTONDOWN, HTMINBUTTON);

        var invalidated = new List<Rectangle>();
        form.Invalidated += (_, e) => invalidated.Add(e.InvalidRect);
        SendNc(form, WM_NCLBUTTONUP, HTMINBUTTON);

        // Same class as the hover defect: a pressed button that never repaints stays visibly stuck.
        Assert.Contains(invalidated, r => r.Contains(new Point(710, 10)));
    });
}
