using System.Drawing;
using System.Runtime.InteropServices;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Windows splash against real GDI and real windows: what the painter leaves in its pixels, where a plan lands on a
/// mixed-DPI desktop, and the window's styles, owner and teardown. No CEF.
/// </summary>
public class WindowsSplashTests
{
    private static readonly Color Red = Color.FromArgb(255, 200, 30, 40);

    private static SplashFrame Frame(Size size, params SplashDrawOp[] ops) =>
        new(size, [new SplashFill(new RectangleF(0, 0, size.Width, size.Height), Red), .. ops], Animated: false);

    [Fact]
    public void The_painter_draws_an_opaque_frame_with_the_text_on_it()
    {
        using var painter = new WindowsSplashPainter(null);
        var font = new SplashFont(null, 20, Bold: true);
        var text = "正在加载 Loading";
        var size = painter.Measure(text, font, 0);
        Assert.True(size.Width > 0 && size.Height > 0);

        painter.Paint(Frame(new Size(400, 120), new SplashTextRun(new RectangleF(10, 10, size.Width, size.Height), text, font, Color.White)), cornerRadius: 0);

        Assert.Equal(0xFFC81E28u, painter.Pixel(1, 1));   // the fill, opaque
        Assert.Equal(0xFFC81E28u, painter.Pixel(399, 119));
        var inked = 0;
        for (var y = 10; y < 10 + (int)size.Height; y++)
        for (var x = 10; x < 10 + (int)size.Width; x++)
        {
            var pixel = painter.Pixel(x, y);
            Assert.Equal(0xFFu, pixel >> 24);   // GDI's text left no transparent hole
            if (pixel != 0xFFC81E28u) inked++;
        }
        Assert.True(inked > 50, $"only {inked} pixels of text were drawn");
    }

    [Fact]
    public void A_wrapped_measure_is_narrower_and_taller()
    {
        using var painter = new WindowsSplashPainter(null);
        var font = new SplashFont(null, 14, Bold: false);
        var text = "one two three four five six seven eight nine ten";
        var line = painter.Measure(text, font, 0);
        var wrapped = painter.Measure(text, font, line.Width / 3);
        Assert.True(wrapped.Width <= line.Width / 3);
        Assert.True(wrapped.Height >= line.Height * 2);
    }

    [Fact]
    public void Rounded_corners_are_cut_and_the_middle_stays_opaque()
    {
        using var painter = new WindowsSplashPainter(null);
        painter.Paint(Frame(new Size(100, 100)), cornerRadius: 8);
        Assert.Equal(0u, painter.Pixel(0, 0) >> 24);
        Assert.Equal(0u, painter.Pixel(99, 99) >> 24);
        Assert.Equal(0xFFu, painter.Pixel(50, 50) >> 24);
        Assert.Equal(0xFFu, painter.Pixel(8, 0) >> 24);
    }

    [Fact]
    public void Only_the_corners_asked_for_are_cut()
    {
        // The splash in a window's render area: the title bar above it is square to it, the window's bottom is round.
        using var painter = new WindowsSplashPainter(null);
        painter.Paint(Frame(new Size(100, 100)), cornerRadius: 8, OverlayCorners.Bottom);
        Assert.Equal(0xFFu, painter.Pixel(0, 0) >> 24);
        Assert.Equal(0xFFu, painter.Pixel(99, 0) >> 24);
        Assert.Equal(0u, painter.Pixel(0, 99) >> 24);
        Assert.Equal(0u, painter.Pixel(99, 99) >> 24);
    }

    [Fact]
    public void A_shadow_rings_the_frame_which_lands_inside_it()
    {
        using var painter = new WindowsSplashPainter(null);
        painter.Paint(Frame(new Size(100, 60)), cornerRadius: 8, OverlayCorners.Top | OverlayCorners.Bottom, shadow: 12);
        Assert.Equal(new Size(124, 84), painter.Size);
        Assert.Equal(0xFFC81E28u, painter.Pixel(12 + 50, 12 + 30));      // the frame, opaque, inside the margin
        var ring = painter.Pixel(6, 42);                                 // halfway out on the left, mid-height
        Assert.InRange(ring >> 24, 1u, 0x40u);                           // a faint shadow
        Assert.Equal(0u, ring & 0x00FFFFFF);                             // black, premultiplied
        Assert.True((painter.Pixel(1, 42) >> 24) < (ring >> 24), "the shadow fades outward");
        Assert.True((painter.Pixel(12, 12) >> 24) < 0xFF, "the frame's own corner is cut");
    }

    [Fact]
    public void A_missing_image_is_left_out_and_the_rest_draws()
    {
        using var painter = new WindowsSplashPainter(null);
        painter.Paint(Frame(new Size(50, 50), new SplashImageDraw(new RectangleF(0, 0, 50, 50), new SplashImage(Guid.NewGuid() + ".png"))), cornerRadius: 0);
        Assert.Equal(0xFFC81E28u, painter.Pixel(25, 25));
    }

    [Fact]
    public void A_png_from_memory_is_drawn()
    {
        using var painter = new WindowsSplashPainter(null);
        byte[] png;
        using (var bitmap = new Bitmap(1, 1))
        using (var stream = new MemoryStream())
        {
            bitmap.SetPixel(0, 0, Color.Blue);
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            png = stream.ToArray();
        }
        painter.Paint(Frame(new Size(20, 20), new SplashImageDraw(new RectangleF(0, 0, 20, 20), new SplashImage(png))), cornerRadius: 0);
        Assert.Equal(0xFF0000FFu, painter.Pixel(10, 10));
    }

    // A 150 % primary 2560×1440 with its taskbar, and a 100 % 1920×1080 to its right.
    private static readonly IReadOnlyList<WindowsScreens.Screen> Desk =
    [
        new(new Rectangle(0, 0, 2560, 1440), new Rectangle(0, 0, 2560, 1392), 1.5f, true),
        new(new Rectangle(2560, 0, 1920, 1080), new Rectangle(2560, 0, 1920, 1080), 1f, false),
    ];

    [Fact]
    public void A_plan_with_no_place_is_centred_on_the_primary_work_area_at_its_scale() =>
        Assert.Equal(new Rectangle(530, 126, 1500, 1140), WindowsScreens.ToPixels(new ChromiumWindowGeometry.Plan(1000, 760, null, null, false), Desk));

    [Fact]
    public void A_placed_plan_lands_on_its_display_at_that_displays_scale()
    {
        Assert.Equal(new Rectangle(150, 75, 1500, 1140), WindowsScreens.ToPixels(new ChromiumWindowGeometry.Plan(1000, 760, 100, 50, false), Desk));
        Assert.Equal(new Rectangle(2653, 100, 1000, 760), WindowsScreens.ToPixels(new ChromiumWindowGeometry.Plan(1000, 760, 1800, 100, false), Desk));
    }

    [Fact]
    public void A_maximized_plan_covers_its_displays_work_area() =>
        Assert.Equal(new Rectangle(2560, 0, 1920, 1080), WindowsScreens.ToPixels(new ChromiumWindowGeometry.Plan(1000, 760, 1800, 100, true), Desk));

    [Fact]
    public void The_work_areas_in_DIP_keep_the_shared_edge_and_put_the_primary_first() =>
        Assert.Equal([new Rectangle(0, 0, 1707, 928), new Rectangle(1707, 0, 1920, 1080)], WindowsScreens.WorkAreasDip([Desk[1], Desk[0]]).OrderBy(r => r.X));

    [Fact]
    public void The_real_desktop_has_a_primary_display() => Assert.Contains(WindowsScreens.All(), s => s.Primary);

    [Fact]
    public void The_surface_shows_without_focus_follows_its_owner_and_fades_away()
    {
        Sta.Run(() =>
        {
            // Borderless, as the Chromium shell's window is: a framed form's DWM bounds leave out its invisible borders.
            using var owner = new Form
            {
                StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(120, 140, 640, 420), ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
            };
            owner.Show();
            using var surface = new WindowsSplashSurface(null);
            var frames = 0;
            surface.ShowOver(owner.Handle, new SplashOverlayLayout(false, 32, new SplashTitleBarOptions(), null, null), (size, scale, measurer) =>
            {
                Interlocked.Increment(ref frames);
                return SplashLayout.Build(new SplashText("splash"), Color.Black, size, scale, measurer, 0, _ => null);
            });
            RevealAndWait(surface);
            var hwnd = surface.Window;
            Assert.NotEqual(0, hwnd);
            Assert.True(frames >= 1);
            var ex = (long)GetWindowLongPtrW(hwnd, -20);
            // Layered, never activated by a click, no taskbar button, and NOT click-through: a click must not reach the page
            // loading unseen beneath it. Whether it took the foreground depends on whether this process may (a focused
            // terminal may; a background runner may not), so that is not asserted.
            Assert.Equal(0x80000L | 0x08000000 | 0x80, ex & (0x80000L | 0x20 | 0x08000000 | 0x80));
            Assert.True(IsWindowVisible(hwnd) != 0);

            Assert.Equal(owner.Handle, GetWindow(hwnd, 4));   // GW_OWNER, from its creation
            Assert.Equal(Rect(owner.Handle), Rect(hwnd));      // a borderless owner: its client area is all of it

            owner.Bounds = new Rectangle(200, 160, 500, 380);
            surface.FollowOwner();
            Wait(() => Rect(hwnd) == Rect(owner.Handle), "the splash followed its owner");

            var done = 0;
            surface.FadeOut(TimeSpan.FromMilliseconds(60), () => Interlocked.Increment(ref done));
            Wait(() => IsWindow(hwnd) == 0 && Volatile.Read(ref done) == 1, "the splash faded and was destroyed");
            surface.Dispose();
            surface.Dispose();
        });
    }

    // Per-monitor aware, as the splash's own thread and CEF's windows are: every rectangle read here is physical pixels.
    [DllImport("user32")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32")] private static extern uint GetDpiForWindow(nint hwnd);

    private static SplashOverlayLayout Layout(bool frameless) => new(frameless, 32, new SplashTitleBarOptions(), null, null);

    private static SplashFrame Blank(Size size, float scale, ISplashTextMeasurer measurer) =>
        SplashLayout.Build(new SplashStack(), Color.Black, size, scale, measurer, 0, _ => null);

    [Fact]
    public void The_window_s_splash_covers_a_framed_owner_s_client_area_and_follows_it()
    {
        Sta.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var owner = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(200, 150, 800, 600), ShowInTaskbar = false };
            owner.Show();
            using var surface = new WindowsSplashSurface(null);
            surface.ShowOver(owner.Handle, Layout(frameless: false), Blank);
            RevealAndWait(surface);
            Assert.Equal(owner.RectangleToScreen(owner.ClientRectangle), Rect(surface.Window));   // the frame stays uncovered

            owner.Bounds = new Rectangle(300, 200, 900, 700);
            surface.FollowOwner();
            Wait(() => Rect(surface.Window) == owner.RectangleToScreen(owner.ClientRectangle), "the splash followed its owner's client area");
        });
    }

    [Fact]
    public void The_window_s_splash_stays_hidden_until_revealed_then_takes_the_owner_s_area_as_it_is_then()
    {
        Sta.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var owner = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(200, 150, 800, 600), ShowInTaskbar = false };
            owner.Show();
            using var surface = new WindowsSplashSurface(null);
            surface.ShowOver(owner.Handle, Layout(frameless: false), Blank);   // returns without waiting for the window
            Wait(() => surface.Window != 0, "the splash's window was made");
            Thread.Sleep(100);
            Assert.Equal(0, IsWindowVisible(surface.Window));                  // nothing floats before the owner shows

            // As a window opening maximized from saved state: maximized between the splash's creation and its reveal.
            owner.WindowState = FormWindowState.Maximized;
            RevealAndWait(surface);
            Assert.NotEqual(0, IsWindowVisible(surface.Window));
            Assert.Equal(owner.RectangleToScreen(owner.ClientRectangle), Rect(surface.Window));
        });
    }

    [Fact]
    public void The_window_s_splash_leaves_a_frameless_owner_s_strip()
    {
        Sta.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var owner = new Form
            {
                StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(200, 150, 800, 600), ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
            };
            owner.Show();
            using var surface = new WindowsSplashSurface(null);
            surface.ShowOver(owner.Handle, Layout(frameless: true), Blank);
            RevealAndWait(surface);
            var scale = GetDpiForWindow(owner.Handle) / 96.0;
            var strip = (int)Math.Round(32 * scale);
            var edge = (int)Math.Round(SplashGeometry.ResizeBandDips * scale);   // its resize band stays the window's
            Assert.Equal(SplashGeometry.OverlayRect(owner.RectangleToScreen(owner.ClientRectangle), true, strip, edge), Rect(surface.Window));
        });
    }

    [Fact]
    public void The_card_opens_unowned_at_its_rect_inside_a_shadow_margin()
    {
        Sta.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var surface = new WindowsSplashSurface(null);
            var dip = new Rectangle(100, 100, 480, 300);
            surface.ShowCard(dip, Blank);
            Assert.Equal(0, GetWindow(surface.Window, 4));   // GW_OWNER: none
            var card = WindowsScreens.ToPixels(new ChromiumWindowGeometry.Plan(dip.Width, dip.Height, dip.X, dip.Y, false), WindowsScreens.All());
            var window = Rect(surface.Window);
            var margin = card.Left - window.Left;
            Assert.True(margin > 0, "the card has a shadow margin");
            Assert.Equal(Rectangle.Inflate(card, margin, margin), window);
        });
    }

    [Fact]
    public void A_surface_disposed_before_its_window_exists_never_shows_one()
    {
        // As after Show gave up waiting: the session disposes it with no window made yet, and the splash thread, which
        // makes it later, must not show it.
        var surface = new WindowsSplashSurface(null);
        surface.Dispose();
        Assert.Throws<InvalidOperationException>(() => surface.ShowCard(new Rectangle(0, 0, 200, 100),
            (size, scale, measurer) => SplashLayout.Build(new SplashStack(), Color.Black, size, scale, measurer, 0, _ => null)));
        Assert.Equal(0, surface.Window);
    }

    [Fact]
    public void Disposing_destroys_the_window_at_once()
    {
        using var surface = new WindowsSplashSurface(null);
        surface.ShowCard(new Rectangle(0, 0, 200, 100),
            (size, scale, measurer) => SplashLayout.Build(new SplashStack(), Color.Black, size, scale, measurer, 0, _ => null));
        var hwnd = surface.Window;
        surface.Dispose();
        Assert.Equal(0, IsWindow(hwnd));
    }

    private static Rectangle Rect(nint hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    // The owner's thread is this one, so its messages are pumped while waiting.
    // Reveal returns at once; this thread owns the owner, so it pumps while it waits, as CEF's thread does.
    private static void RevealAndWait(WindowsSplashSurface surface)
    {
        var shown = 0;
        surface.Reveal(() => Interlocked.Exchange(ref shown, 1));
        Wait(() => Volatile.Read(ref shown) == 1, "the splash showed over its owner");
    }

    private static void Wait(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow > until) Assert.Fail($"timed out: {what}");
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32")] private static extern int IsWindowVisible(nint hwnd);
    [DllImport("user32")] private static extern int IsWindow(nint hwnd);
    [DllImport("user32")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32")] private static extern int GetWindowRect(nint hwnd, out RECT rect);
}
