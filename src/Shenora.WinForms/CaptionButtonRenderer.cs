namespace Shenora.WinForms;

/// <summary>
/// Draws the caption-button cluster <see cref="OptimizedForm"/> owns when
/// <see cref="OptimizedFormOptions.NativeCaptionButtons"/> is on: the surface behind the cluster, each
/// button's hover/pressed background, and the Windows chrome glyph on top.
/// <para>
/// Split out of <see cref="OptimizedForm"/> in the 0.2.0 design pass, and the SPLIT LINE is the point.
/// Everything here is input → pixels: it never sees a window message, never hit-tests, and never
/// touches the window region. That is exactly why it could be moved — the message-loop half
/// (<c>WM_NCCALCSIZE</c>, <c>WM_NCHITTEST</c>, <c>WM_SYSCOMMAND</c>, the manual maximize, the child
/// region clipping) deliberately stayed in the form, because OS input routing is the area where a
/// green unit suite has twice been the wrong answer in this repo (P5.6, see
/// <c>docs/REVIEW-GUIDE.md</c> §6) and re-verifying it buys nothing. Cohesion where it is free; no
/// gambling where it is not.
/// </para>
/// <para>
/// Internal, and per-form: it caches a <see cref="Font"/> keyed on the monitor scale, so the owning
/// form disposes it. Stateless apart from that cache — <see cref="Glyph"/> and
/// <see cref="FallbackColors"/> are pure and unit-tested directly.
/// </para>
/// </summary>
internal sealed class CaptionButtonRenderer : IDisposable
{
    private Font? _glyphFont;

    // Process-wide: which of the two icon fonts this machine actually has. Resolving it costs a failed
    // FontFamily construction, and the answer cannot change while the process runs.
    private static string? _glyphFamily;

    /// <summary>
    /// Paint the cluster. <paramref name="union"/> is the bounding box of every region — the whole of
    /// it is filled, so the GAPS between buttons are covered too: the web view no longer renders any
    /// of those pixels (they were cut out of it), and an unpainted gap shows as a tear beside the
    /// buttons.
    /// </summary>
    internal void Paint(Graphics graphics, IReadOnlyList<CaptionButtonRegion> regions, Rectangle union,
                        CaptionButtonKind? hot, CaptionButtonKind? pressed, bool maximized,
                        int deviceDpi, Color formBackColor, CaptionButtonColors? colors)
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

            if (isHot || isPressed)
            {
                var back = isPressed
                    ? (isClose ? palette.ClosePressed : palette.Pressed)
                    : (isClose ? palette.CloseHover : palette.Hover);
                using var brush = new SolidBrush(back);
                graphics.FillRectangle(brush, region.Bounds);
            }

            var glyph = isClose && (isHot || isPressed) ? palette.CloseGlyphHot ?? palette.Glyph : palette.Glyph;
            TextRenderer.DrawText(graphics, Glyph(region.Kind, maximized), font, region.Bounds, glyph,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
        }
    }

    /// <summary>
    /// The Windows chrome glyphs — the same codepoints the OS draws in a real caption, so the buttons
    /// match every other window on the desktop. Maximize swaps to RESTORE while maximized, which is
    /// behaviour rather than styling: a maximize glyph on a maximized window is simply wrong.
    /// </summary>
    /// <remarks>
    /// ESCAPE SEQUENCES, never the literal characters. These are Private Use Area codepoints, and a
    /// BOM-less UTF-8 source on this repo's CJK-locale build machine is a documented mojibake trap
    /// (the CodePage note in <c>src/Directory.Build.props</c>). An escape is plain ASCII in the file,
    /// so nothing between an editor and the compiler can mangle it. Unlike a mangled glyph, a mangled
    /// escape fails to COMPILE instead of silently painting an empty button.
    /// </remarks>
    internal static string Glyph(CaptionButtonKind kind, bool maximized) => kind switch
    {
        CaptionButtonKind.Minimize => "\uE921",                          // ChromeMinimize
        CaptionButtonKind.Maximize => maximized ? "\uE923" : "\uE922",   // ChromeRestore / ChromeMaximize
        _ => "\uE8BB",                                                   // ChromeClose
    };

    /// <summary>
    /// The icon font at this monitor's scale, cached until the scale changes.
    /// <para>
    /// "Segoe Fluent Icons" is Windows 11's; Windows 10 ships only "Segoe MDL2 Assets". Both carry
    /// these four glyphs at the SAME codepoints, so the fallback is exact rather than approximate, and
    /// one of the two is present on every Windows this package targets.
    /// </para>
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
    /// <see cref="OptimizedForm.CaptionButtonColors"/>. Refusing to paint would be worse: the clip has
    /// already taken those pixels away from the page, so the buttons would silently vanish — the same
    /// "degrades to silence" failure the resource-prefix check exists to prevent.
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
            // Close goes red on hover on every Windows app; that is the platform convention users read
            // as "this closes", not a design choice of ours.
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
