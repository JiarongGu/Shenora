using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// The caption-button RENDERING, split out of <see cref="OptimizedForm"/> in the 0.2.0 design pass.
/// <para>
/// These tests are the payoff for that split and the reason it stopped where it did. Glyph choice,
/// palette fallback and font scaling are pure input → output, so they can be asserted directly — no
/// STA thread, no window handle, no message pump. Everything the renderer deliberately does NOT do
/// (hit-testing, the window region, maximize) still needs a real window and stays covered by
/// <c>CaptionButtonTests</c>/<c>OptimizedFormTests</c> plus the live probes
/// <c>docs/REVIEW-GUIDE.md</c> §6 describes.
/// </para>
/// </summary>
public class CaptionButtonRendererTests
{
    /// <summary>
    /// The maximize button is the only one whose glyph depends on STATE, and it is behaviour rather
    /// than styling: a maximize glyph on an already-maximized window is simply wrong. Pinned because
    /// the swap is easy to drop in a refactor and impossible to notice in a unit-green suite.
    /// </summary>
    [Theory]
    [InlineData(CaptionButtonKind.Minimize, false, 0xE921)]  // ChromeMinimize
    [InlineData(CaptionButtonKind.Minimize, true,  0xE921)]  // …unchanged when maximized
    [InlineData(CaptionButtonKind.Maximize, false, 0xE922)]  // ChromeMaximize
    [InlineData(CaptionButtonKind.Maximize, true,  0xE923)]  // ChromeRestore
    [InlineData(CaptionButtonKind.Close,    false, 0xE8BB)]  // ChromeClose
    [InlineData(CaptionButtonKind.Close,    true,  0xE8BB)]  // …unchanged when maximized
    public void Each_kind_maps_to_its_windows_chrome_codepoint(CaptionButtonKind kind, bool maximized, int expected)
    {
        // Asserted as a CODEPOINT, not as a literal glyph in this file. A test that pastes the
        // Private Use Area character carries exactly the mojibake exposure the production code
        // avoids by using escapes — it would be re-encoded alongside the source it is guarding and
        // agree with it while both were wrong.
        var glyph = CaptionButtonRenderer.Glyph(kind, maximized);
        Assert.Equal(1, glyph.Length);
        Assert.Equal(expected, (int)glyph[0]);
    }

    /// <summary>
    /// The codepoints must be the OS's own, or the buttons stop matching every other window on the
    /// desktop. This also guards the documented mojibake trap directly: these are Private Use Area
    /// characters written as ESCAPES in the source, and a source-encoding accident (a BOM-less UTF-8
    /// file read as ANSI on this repo's CJK-locale build machine) is exactly how they would silently
    /// become something else. A glyph that is not in the PUA range is that accident.
    /// </summary>
    [Fact]
    public void Every_glyph_is_a_single_private_use_area_codepoint()
    {
        foreach (var kind in Enum.GetValues<CaptionButtonKind>())
        {
            foreach (var maximized in new[] { false, true })
            {
                var glyph = CaptionButtonRenderer.Glyph(kind, maximized);
                Assert.True(glyph.Length == 1, $"{kind} (maximized: {maximized}) is not a single char: {glyph.Length}");
                Assert.InRange((int)glyph[0], 0xE000, 0xF8FF);   // the BMP Private Use Area
            }
        }
    }

    /// <summary>
    /// The fallback exists so a half-wired app sees BUTTONS rather than an empty rectangle — the clip
    /// has already taken those pixels away from the page, so refusing to paint would make them vanish.
    /// It has to stay legible against whatever fill the form has, which is why it branches on
    /// brightness rather than picking one palette.
    /// </summary>
    [Theory]
    [InlineData(20, 20, 20)]        // a dark app
    [InlineData(250, 250, 250)]     // a light app
    public void The_fallback_palette_keeps_the_glyph_legible_against_the_forms_own_fill(int r, int g, int b)
    {
        var back = Color.FromArgb(r, g, b);

        var palette = CaptionButtonRenderer.FallbackColors(back);

        Assert.Equal(back, palette.Surface);   // the cluster must not show a seam against the form
        // The glyph contrasts with the surface it sits on: dark fill → white glyph, and vice versa.
        Assert.NotEqual(back.GetBrightness() > 0.5, palette.Glyph.GetBrightness() > 0.5);
        // Hover/pressed have to be VISIBLE against the surface, not merely different in principle.
        Assert.NotEqual(back, palette.Hover);
        Assert.NotEqual(palette.Hover, palette.Pressed);
    }

    /// <summary>
    /// Close going red on hover is the platform convention users read as "this closes" — not a design
    /// choice of ours, and the one colour the fallback must not derive from the app's fill.
    /// </summary>
    [Fact]
    public void The_fallback_keeps_the_platforms_red_close_affordance()
    {
        var palette = CaptionButtonRenderer.FallbackColors(Color.FromArgb(31, 31, 31));

        Assert.True(palette.CloseHover.R > 150 && palette.CloseHover.G < 80 && palette.CloseHover.B < 80,
            $"close hover should read as the platform red, was {palette.CloseHover}");
        Assert.Equal(Color.White, palette.CloseGlyphHot);
    }

    /// <summary>
    /// The glyph font scales with the MONITOR, not a constant: the cluster is ~250 physical px at 200%,
    /// and a size picked at 100% is the same class of bug as sizing the clip hole from a constant
    /// (which cut through the buttons). 10 logical px is what Windows itself draws caption glyphs at.
    /// </summary>
    [Theory]
    [InlineData(96, 10f)]
    [InlineData(144, 15f)]
    [InlineData(192, 20f)]
    public void The_glyph_font_scales_with_the_monitors_dpi(int deviceDpi, float expectedSize)
    {
        using var renderer = new CaptionButtonRenderer();

        var font = renderer.GlyphFont(deviceDpi);

        Assert.Equal(expectedSize, font.Size, 2);
        Assert.Equal(GraphicsUnit.Pixel, font.Unit);
    }

    /// <summary>
    /// The cache is keyed on the resolved SIZE, so repeated paints at one scale reuse a font while a
    /// DPI change produces a new one. Worth pinning both halves: a cache that never hits allocates a
    /// font per paint, and one that never misses paints the old scale forever after a monitor move.
    /// </summary>
    [Fact]
    public void The_glyph_font_is_cached_per_scale_and_replaced_when_the_scale_changes()
    {
        using var renderer = new CaptionButtonRenderer();

        var first = renderer.GlyphFont(96);
        var again = renderer.GlyphFont(96);
        var rescaled = renderer.GlyphFont(192);

        Assert.Same(first, again);
        Assert.NotSame(first, rescaled);
        Assert.Equal(20f, rescaled.Size, 2);
    }

    /// <summary>An empty cluster paints nothing and must not reach for a Graphics it was not given.</summary>
    [Fact]
    public void Painting_no_regions_is_a_no_op()
    {
        using var renderer = new CaptionButtonRenderer();
        using var bitmap = new Bitmap(10, 10);
        using var graphics = Graphics.FromImage(bitmap);

        // No exception, and nothing to assert about pixels — the point is that it returns early
        // rather than filling an empty union across the whole surface.
        renderer.Paint(graphics, [], Rectangle.Empty, null, null, false, 96, Color.Black, null);
    }

    /// <summary>
    /// The whole UNION is filled, gaps between buttons included: the web view no longer renders any of
    /// those pixels (they were cut out of it), so an unpainted gap shows as a tear beside the buttons.
    /// Asserted on real pixels rather than on the call, because "did it fill" is the actual contract.
    /// </summary>
    [Fact]
    public void Painting_fills_the_whole_cluster_union_including_the_gaps_between_buttons()
    {
        using var renderer = new CaptionButtonRenderer();
        using var bitmap = new Bitmap(60, 20);
        using var graphics = Graphics.FromImage(bitmap);
        var surface = Color.FromArgb(255, 40, 44, 52);
        var colors = CaptionButtonRenderer.FallbackColors(surface);   // Surface is the fill it was derived from

        // Two buttons with a deliberate 20px gap between them.
        CaptionButtonRegion[] regions =
        [
            new(CaptionButtonKind.Minimize, new Rectangle(0, 0, 20, 20)),
            new(CaptionButtonKind.Close, new Rectangle(40, 0, 20, 20)),
        ];

        renderer.Paint(graphics, regions, new Rectangle(0, 0, 60, 20), null, null, false, 96, surface, colors);

        // A pixel in the GAP — no button covers it, and it must still be painted with the surface.
        Assert.Equal(surface.ToArgb(), bitmap.GetPixel(30, 10).ToArgb());
    }

    /// <summary>
    /// The row is as tall as the page's rectangles, so a taller title bar gets taller buttons: a hot button's fill
    /// reaches its top and bottom edges, with the app's colours.
    /// </summary>
    [Fact]
    public void A_taller_row_is_filled_to_its_full_height_in_the_apps_colours()
    {
        using var renderer = new CaptionButtonRenderer();
        using var bitmap = new Bitmap(46, 96);
        using var graphics = Graphics.FromImage(bitmap);
        var colors = new CaptionButtonColors
        {
            Surface = Color.FromArgb(255, 48, 80, 128), Hover = Color.FromArgb(255, 70, 100, 150),
            Pressed = Color.FromArgb(255, 90, 120, 170), Glyph = Color.White,
            CloseHover = Color.FromArgb(255, 200, 30, 30), ClosePressed = Color.FromArgb(255, 160, 20, 20),
        };
        CaptionButtonRegion[] regions = [new(CaptionButtonKind.Close, new Rectangle(0, 0, 46, 96))];

        renderer.Paint(graphics, regions, new Rectangle(0, 0, 46, 96), CaptionButtonKind.Close, null, false, 96, Color.Black, colors);

        Assert.Equal(colors.CloseHover.ToArgb(), bitmap.GetPixel(2, 1).ToArgb());
        Assert.Equal(colors.CloseHover.ToArgb(), bitmap.GetPixel(2, 94).ToArgb());
    }

    /// <summary>
    /// An inactive window's idle glyphs are dimmed, as the system dims its own: by default the glyph at about a third of
    /// its opacity, blended over the surface since GDI text has none. A hovered button keeps its full glyph.
    /// </summary>
    [Fact]
    public void An_inactive_windows_idle_glyphs_are_dimmed()
    {
        var colors = new CaptionButtonColors
        {
            Surface = Color.Black, Hover = Color.FromArgb(255, 60, 60, 60), Pressed = Color.FromArgb(255, 90, 90, 90),
            Glyph = Color.White, CloseHover = Color.Red, ClosePressed = Color.DarkRed,
        };
        CaptionButtonRegion[] regions = [new(CaptionButtonKind.Minimize, new Rectangle(0, 0, 46, 32))];
        int Brightest(CaptionButtonKind? hot, bool active)
        {
            using var renderer = new CaptionButtonRenderer();
            using var bitmap = new Bitmap(46, 32);
            using (var graphics = Graphics.FromImage(bitmap))
                renderer.Paint(graphics, regions, new Rectangle(0, 0, 46, 32), hot, null, false, 96, Color.Black, colors, active);
            var max = 0;
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                    max = Math.Max(max, bitmap.GetPixel(x, y).R);
            return max;
        }

        // Against the active glyph, not against 255: in a process that has also run the Windows splash's window tests,
        // GDI draws this one-pixel stroke unhinted, about 0.72 of a pixel (brightest 184, where it is otherwise 255; one run
        // in two of the suite, never alone). The ratios are the renderer's; the stroke's coverage is GDI's.
        int active = Brightest(null, active: true), inactive = Brightest(null, active: false), hovered = Brightest(CaptionButtonKind.Minimize, active: false);
        Assert.True(active > 150, $"the active glyph draws (brightest {active})");
        Assert.Equal(active, hovered);   // a hovered button keeps its full glyph
        // 0x5A of 0xFF (0.353) exactly at full coverage; GDI's blend of a partly covered pixel is not quite linear (0.364
        // measured at 184), so a band that still tells a dimmed glyph from an undimmed one or a wrong opacity.
        Assert.Equal(0x5A, CaptionButtonRenderer.Over(Color.FromArgb(0x5A, Color.White), Color.Black).R);
        Assert.InRange(inactive / (double)active, 0.32, 0.39);
    }
}
