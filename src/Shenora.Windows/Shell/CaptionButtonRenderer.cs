namespace Shenora.Windows;

/// <summary>
/// Draws the caption-button cluster <see cref="OptimizedForm"/> owns when
/// <see cref="OptimizedFormOptions.NativeCaptionButtons"/> is on: the surface behind the cluster, each
/// button's hover/pressed background, and the Windows chrome glyph on top.
/// <para>
/// Everything here is input → pixels: it never sees a window message, never hit-tests and never touches
/// the window region — the message-loop half stays in the form. Internal and per-form, because it caches
/// a <see cref="Font"/> keyed on the monitor scale that the owning form disposes.
/// </para>
/// </summary>
internal sealed class CaptionButtonRenderer : IDisposable
{
    private Font? _glyphFont;

    // Process-wide: which of the two icon fonts this machine has. Resolving it costs a failed
    // FontFamily construction, and the answer cannot change while the process runs.
    private static string? _glyphFamily;

    /// <summary>
    /// Paint the cluster. <paramref name="union"/> is the bounding box of every region, and the whole of
    /// it is filled so the GAPS between buttons are covered too — the web view no longer renders any of
    /// those pixels, and an unpainted gap shows as a tear beside the buttons.
    /// </summary>
    internal void Paint(Graphics graphics, IReadOnlyList<CaptionButtonRegion> regions, Rectangle union,
                        CaptionButtonKind? hot, CaptionButtonKind? pressed, bool maximized,
                        int deviceDpi, Color formBackColor, CaptionButtonColors? colors, bool active = true)
    {
        if (regions.Count == 0 || union.IsEmpty) return;
        var palette = colors ?? FallbackColors(formBackColor);

        using (var surface = new SolidBrush(palette.Surface))
            graphics.FillRectangle(surface, union);

        var font = GlyphFont(deviceDpi);
        foreach (var region in regions)
        {
            var isHot = hot == region.Kind;
            var isPressed = pressed == region.Kind;
            var isClose = region.Kind == CaptionButtonKind.Close;
            var back = palette.Surface;

            if (isHot || isPressed)
            {
                var fill = isPressed
                    ? (isClose ? palette.ClosePressed : palette.Pressed)
                    : (isClose ? palette.CloseHover : palette.Hover);
                using var brush = new SolidBrush(fill);
                graphics.FillRectangle(brush, region.Bounds);
                back = Over(fill, palette.Surface);
            }

            var glyph = isClose && (isHot || isPressed) ? palette.CloseGlyphHot ?? palette.Glyph
                : !active && !isHot && !isPressed ? palette.InactiveGlyph ?? Color.FromArgb(palette.Glyph.A * InactiveAlpha / 255, palette.Glyph)
                : palette.Glyph;
            // GDI text has no alpha, so a translucent glyph is blended over what is behind it here.
            TextRenderer.DrawText(graphics, Glyph(region.Kind, maximized), font, region.Bounds, Over(glyph, back),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
        }
    }

    /// <summary>An inactive window's idle glyph opacity, as the system draws its own (0x5A of 0xFF, measured on a dark
    /// caption).</summary>
    private const int InactiveAlpha = 0x5A;

    /// <summary><paramref name="color"/> over an opaque <paramref name="back"/>.</summary>
    internal static Color Over(Color color, Color back)
    {
        if (color.A == 255) return color;
        int Mix(int c, int b) => (c * color.A + b * (255 - color.A) + 127) / 255;
        return Color.FromArgb(255, Mix(color.R, back.R), Mix(color.G, back.G), Mix(color.B, back.B));
    }

    /// <summary>
    /// The Windows chrome glyphs — the same codepoints the OS draws in a real caption. Maximize swaps to
    /// RESTORE while maximized, which is behaviour rather than styling.
    /// </summary>
    /// <remarks>
    /// 🔴 ESCAPE SEQUENCES, never the literal characters. These are Private Use Area codepoints, and a
    /// BOM-less UTF-8 source on a CJK-locale build machine is a mojibake trap (the CodePage note in
    /// <c>src/Directory.Build.props</c>). A mangled escape fails to COMPILE; a mangled literal silently
    /// paints an empty button.
    /// </remarks>
    internal static string Glyph(CaptionButtonKind kind, bool maximized) => kind switch
    {
        CaptionButtonKind.Minimize => "\uE921",                          // ChromeMinimize
        CaptionButtonKind.Maximize => maximized ? "\uE923" : "\uE922",   // ChromeRestore / ChromeMaximize
        _ => "\uE8BB",                                                   // ChromeClose
    };

    /// <summary>
    /// The icon font at this monitor's scale, cached until the scale changes. "Segoe Fluent Icons" is
    /// Windows 11's and "Segoe MDL2 Assets" is Windows 10's; both carry these four glyphs at the SAME
    /// codepoints, so the fallback is exact.
    /// </summary>
    internal Font GlyphFont(int deviceDpi)
    {
        // 10 logical px is the size Windows itself draws caption glyphs at.
        var size = (float)(10 * DpiHelper.ScaleFromDeviceDpi(deviceDpi));
        if (_glyphFont is { } cached && Math.Abs(cached.Size - size) < 0.01f) return cached;
        _glyphFont?.Dispose();
        _glyphFont = new Font(GlyphFamily(), size, GraphicsUnit.Pixel);
        return _glyphFont;
    }

    private static string GlyphFamily()
    {
        if (_glyphFamily is not null) return _glyphFamily;
        foreach (var candidate in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            // FontFamily throws when the family is not installed; there is no TryGet.
            try
            {
                using var family = new FontFamily(candidate);
                return _glyphFamily = candidate;
            }
            catch (ArgumentException)
            {
                // Not on this machine — try the older name.
            }
        }
        return _glyphFamily = FontFamily.GenericSansSerif.Name;
    }

    /// <summary>
    /// A last-resort palette derived from the form's own fill, used only when an app set
    /// <see cref="OptimizedFormOptions.NativeCaptionButtons"/> without
    /// <see cref="OptimizedForm.CaptionButtonColors"/>. ⚠ Refusing to paint would be worse: the clip has
    /// already taken those pixels from the page, so the buttons would silently vanish.
    /// </summary>
    internal static CaptionButtonColors FallbackColors(Color formBackColor)
    {
        var dark = formBackColor.GetBrightness() <= 0.5;
        return new CaptionButtonColors
        {
            Surface = formBackColor,
            Hover = dark ? ControlPaint.Light(formBackColor, 0.4f) : ControlPaint.Dark(formBackColor, 0.06f),
            Pressed = dark ? ControlPaint.Light(formBackColor, 0.8f) : ControlPaint.Dark(formBackColor, 0.12f),
            Glyph = dark ? Color.White : Color.Black,
            // Close goes red on hover on every Windows app — a platform convention, not a design choice.
            CloseHover = Color.FromArgb(196, 43, 28),
            ClosePressed = Color.FromArgb(163, 36, 23),
            CloseGlyphHot = Color.White,
        };
    }

    public void Dispose()
    {
        _glyphFont?.Dispose();
        _glyphFont = null;
    }
}
