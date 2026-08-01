using Shenora.WinForms;

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
}
