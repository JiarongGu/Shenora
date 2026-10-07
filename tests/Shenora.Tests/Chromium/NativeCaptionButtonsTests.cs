using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The window-painted caption buttons' pure parts: the palette against the pixels it was measured from, the fade's
/// colour mixing, and the glyphs. The Views overlay itself is proven by the e2e probe, which needs CEF.
/// </summary>
public class NativeCaptionButtonsTests
{
    // A wash of `argb` over an opaque `under`, as the compositor draws it.
    private static uint Over(uint argb, uint under)
    {
        var a = (argb >> 24) / 255.0;
        uint Channel(int shift) => (uint)Math.Round(((argb >> shift) & 0xFF) * a + ((under >> shift) & 0xFF) * (1 - a));
        return (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    private static void Near(uint expected, uint actual)
    {
        foreach (var shift in new[] { 16, 8, 0 })
            Assert.InRange((int)((actual >> shift) & 0xFF) - (int)((expected >> shift) & 0xFF), -1, 1);
    }

    // Measured on a real Windows 11 caption (26200): the caption's own colour, then the button's pixel in each state.
    [Theory]
    [InlineData(true, 0x1A2226u, 0x272F33u, 0x242C2Fu, 0xC42B1Cu, 0xB22A1Cu)]
    [InlineData(false, 0xEDF5F9u, 0xE4EBEFu, 0xE7EFF3u, 0xC42B1Cu, 0xC74031u)]
    public void The_palette_reproduces_the_systems_own_caption_pixels(bool dark, uint caption, uint hover, uint pressed, uint closeHover, uint closePressed)
    {
        var palette = dark ? CaptionButtonPalette.Dark : CaptionButtonPalette.Light;

        Near(hover, Over(palette.For(CaptionButtonKind.Minimize, hot: true, pressed: false, active: true).Background, caption));
        Near(pressed, Over(palette.For(CaptionButtonKind.Maximize, hot: true, pressed: true, active: true).Background, caption));
        Near(closeHover, Over(palette.For(CaptionButtonKind.Close, hot: true, pressed: false, active: true).Background, caption));
        Near(closePressed, Over(palette.For(CaptionButtonKind.Close, hot: true, pressed: true, active: true).Background, caption));
    }

    // The same windows' glyphs: opaque white on dark and black on light, and the dark caption's glyph when the window
    // is inactive, #6D6D6D over its #1D1D1D. The light inactive glyph is not measured.
    [Fact]
    public void The_glyph_colours_reproduce_the_systems_own()
    {
        Assert.Equal(0xFFFFFFFFu, CaptionButtonPalette.Dark.Glyph);
        Assert.Equal(0xFF000000u, CaptionButtonPalette.Light.Glyph);
        Assert.Equal(0xFFFFFFFFu, CaptionButtonPalette.Light.CloseGlyphHot);
        Near(0x6D6D6D, Over(CaptionButtonPalette.Dark.InactiveGlyph, 0x1D1D1D));
    }

    [Fact]
    public void An_idle_button_is_transparent_and_a_pressed_glyph_keeps_its_colour()
    {
        var dark = CaptionButtonPalette.Dark;

        Assert.Equal(0u, dark.For(CaptionButtonKind.Close, hot: false, pressed: false, active: true).Background);
        Assert.Equal(dark.Glyph, dark.For(CaptionButtonKind.Minimize, hot: true, pressed: true, active: true).Glyph);
        Assert.Equal(dark.InactiveGlyph, dark.For(CaptionButtonKind.Minimize, hot: false, pressed: false, active: false).Glyph);
        Assert.Equal(dark.CloseGlyphHot, CaptionButtonPalette.Light.For(CaptionButtonKind.Close, hot: true, pressed: false, active: true).Glyph);
    }

    [Fact]
    public void A_fade_from_transparent_keeps_the_hue_and_gains_only_opacity()
    {
        var idle = new CaptionButtonLook(0, 0xFF000000);
        var hot = new CaptionButtonLook(0xFFC42B1C, 0xFFFFFFFF);

        var half = CaptionButtonLook.Mix(idle, hot, 0.5);

        // Mixed straight, the red would pass through a dark, half-transparent brown on the way.
        Assert.Equal(0x80C42B1Cu, half.Background);
        Assert.Equal(idle, CaptionButtonLook.Mix(idle, hot, 0));
        Assert.Equal(hot, CaptionButtonLook.Mix(idle, hot, 1));
    }

    [Fact]
    public void Maximize_shows_restore_while_the_window_is_maximized()
    {
        Assert.Equal(0xE922, NativeCaptionButtons.Glyph(CaptionButtonKind.Maximize, maximized: false));
        Assert.Equal(0xE923, NativeCaptionButtons.Glyph(CaptionButtonKind.Maximize, maximized: true));
        Assert.Equal(0xE921, NativeCaptionButtons.Glyph(CaptionButtonKind.Minimize, maximized: true));
        Assert.Equal(0xE8BB, NativeCaptionButtons.Glyph(CaptionButtonKind.Close, maximized: false));
    }

    [Fact]
    public void A_glyph_is_drawn_with_its_ink_centred()
    {
        // The system's icon font is on every Windows box; only with neither installed may nothing be drawn, and then
        // nothing must be, rather than a wrong glyph.
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var hasFont = File.Exists(Path.Combine(fonts, "SegoeIcons.ttf")) || File.Exists(Path.Combine(fonts, "segmdl2.ttf"));
        var mask = CaptionGlyphs.Rasterize((char)0xE8BB, 20);
        if (!hasFont) { Assert.Null(mask); return; }
        Assert.NotNull(mask);

        int minX = mask.Size, minY = mask.Size, maxX = -1, maxY = -1;
        for (var y = 0; y < mask.Size; y++)
            for (var x = 0; x < mask.Size; x++)
                if (mask.Coverage[y * mask.Size + x] > 0) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }

        Assert.True(maxX >= 0, "the close glyph drew no ink");
        Assert.InRange(minX - (mask.Size - 1 - maxX), -1, 1);
        Assert.InRange(minY - (mask.Size - 1 - maxY), -1, 1);
    }

    [Fact]
    public void The_splash_strip_takes_the_app_s_colours_over_the_theme_and_keeps_close_red()
    {
        var bar = new Shenora.Chromium.SplashTitleBarOptions
        {
            Glyph = System.Drawing.Color.FromArgb(255, 0x10, 0x20, 0x30),
            Hover = System.Drawing.Color.FromArgb(40, 255, 255, 255),
        };
        var palette = CaptionButtonPalette.ForStrip(bar, CaptionButtonPalette.Dark);
        Assert.Equal(0xFF102030u, palette.Glyph);
        Assert.Equal(0x28FFFFFFu, palette.Hover);
        Assert.Equal(CaptionButtonPalette.Dark.Pressed, palette.Pressed);           // not given: the theme's
        Assert.Equal(0x5A102030u, palette.InactiveGlyph);                           // the glyph at the system's inactive opacity
        Assert.Equal(CaptionButtonPalette.Dark.CloseHover, palette.CloseHover);     // close stays the platform's red
        Assert.Equal(CaptionButtonPalette.Dark.ClosePressed, palette.ClosePressed);
    }

    [Fact]
    public void A_splash_strip_with_no_colours_is_the_theme()
    {
        Assert.Equal(CaptionButtonPalette.Light, CaptionButtonPalette.ForStrip(new Shenora.Chromium.SplashTitleBarOptions(), CaptionButtonPalette.Light));
    }
}
