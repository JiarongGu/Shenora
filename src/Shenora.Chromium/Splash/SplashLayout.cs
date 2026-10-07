using System.Drawing;

namespace Shenora.Chromium.Host;

/// <summary>A font as the painter needs it: its size in physical pixels.</summary>
internal readonly record struct SplashFont(string? Family, float Size, bool Bold);

/// <summary>The platform's text engine, measuring as it will draw: wrapped at <c>maxWidth</c>, in physical pixels.</summary>
internal interface ISplashTextMeasurer
{
    SizeF Measure(string text, SplashFont font, float maxWidth);
}

/// <summary>One thing a painter draws, in physical pixels from the window's top-left.</summary>
internal abstract record SplashDrawOp;

internal sealed record SplashFill(RectangleF Bounds, Color Color) : SplashDrawOp;

/// <summary>Text, wrapped at its width and centred in its bounds.</summary>
internal sealed record SplashTextRun(RectangleF Bounds, string Text, SplashFont Font, Color Color) : SplashDrawOp;

/// <summary>An image scaled into its bounds.</summary>
internal sealed record SplashImageDraw(RectangleF Bounds, SplashImage Source) : SplashDrawOp;

/// <summary>What to draw, back to front, and whether it changes with time (an indeterminate bar).</summary>
internal sealed record SplashFrame(Size Size, IReadOnlyList<SplashDrawOp> Ops, bool Animated);

/// <summary>
/// The splash's layout, the same on every platform: an element tree, the window's pixel size and scale in, draw
/// operations out. A container at the root fills the window; any other element sits in it by its own alignment. A
/// stack larger than its children centres them as a group along its axis; across it, each child takes its own
/// alignment. Negative sizes and spacing count as zero.
/// </summary>
internal static class SplashLayout
{
    private const double ProgressWidth = 280, ProgressHeight = 4, SegmentShare = 0.3;

    public static SplashFrame Build(SplashElement root, Color background, Size windowPx, float scale,
        ISplashTextMeasurer measurer, double phase, Func<SplashImage, Size?> imageSize)
    {
        var pass = new Pass(scale, measurer, phase, imageSize);
        var window = new RectangleF(0, 0, windowPx.Width, windowPx.Height);
        pass.Ops.Add(new SplashFill(window, background));
        if (root.Visible)
        {
            if (root is SplashStack or SplashLayer) pass.Draw(root, Deflate(window, pass.Insets(root.Margin)), background);
            else pass.Place(root, window, background);
        }
        return new SplashFrame(windowPx, pass.Ops, pass.Animated);
    }

    /// <summary>A text colour readable on <paramref name="background"/>.</summary>
    internal static Color Foreground(Color background) =>
        Luminance(background) > 0.5 ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xF0, 0xF0, 0xF0);

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    private static Color Blend(Color under, Color over, double amount) => Color.FromArgb(
        (int)Math.Round(under.R + ((over.R - under.R) * amount)),
        (int)Math.Round(under.G + ((over.G - under.G) * amount)),
        (int)Math.Round(under.B + ((over.B - under.B) * amount)));

    private static RectangleF Deflate(RectangleF r, Inset i) =>
        new(r.X + i.Left, r.Y + i.Top, Math.Max(0, r.Width - i.Left - i.Right), Math.Max(0, r.Height - i.Top - i.Bottom));

    private readonly record struct Inset(float Left, float Top, float Right, float Bottom)
    {
        public float Horizontal => Left + Right;
        public float Vertical => Top + Bottom;
    }

    private sealed class Pass(float scale, ISplashTextMeasurer measurer, double phase, Func<SplashImage, Size?> imageSize)
    {
        public List<SplashDrawOp> Ops { get; } = [];
        public bool Animated { get; private set; }

        public float Px(double? dip) => dip is { } d ? (float)Math.Max(0, d * scale) : 0;

        public Inset Insets(SplashInsets i) => new(Px(i.Left), Px(i.Top), Px(i.Right), Px(i.Bottom));

        /// <summary>The element's own size (margins excluded) in the space given, already deflated by its margin.</summary>
        public SizeF Measure(SplashElement e, float availableWidth, float availableHeight)
        {
            var fixedWidth = e.Width is not null ? Px(e.Width) : (float?)null;
            var fixedHeight = e.Height is not null ? Px(e.Height) : (float?)null;
            switch (e)
            {
                case SplashText text:
                {
                    var size = measurer.Measure(text.Text, Font(text), fixedWidth ?? availableWidth);
                    return new SizeF(fixedWidth ?? size.Width, fixedHeight ?? size.Height);
                }
                case SplashImage image:
                {
                    var natural = imageSize(image) ?? Size.Empty;
                    if (fixedWidth is { } w && fixedHeight is { } h) return new SizeF(w, h);
                    if (natural.Width <= 0 || natural.Height <= 0) return new SizeF(fixedWidth ?? 0, fixedHeight ?? 0);
                    if (fixedWidth is { } fw) return new SizeF(fw, fw * natural.Height / natural.Width);
                    if (fixedHeight is { } fh) return new SizeF(fh * natural.Width / natural.Height, fh);
                    return new SizeF(natural.Width * scale, natural.Height * scale);
                }
                case SplashProgress:
                    return new SizeF(fixedWidth ?? Px(ProgressWidth), fixedHeight ?? Px(ProgressHeight));
                case SplashStack stack:
                {
                    var content = MeasureStack(stack, (fixedWidth ?? availableWidth) - Insets(stack.Padding).Horizontal,
                        (fixedHeight ?? availableHeight) - Insets(stack.Padding).Vertical);
                    return new SizeF(fixedWidth ?? content.Width + Insets(stack.Padding).Horizontal, fixedHeight ?? content.Height + Insets(stack.Padding).Vertical);
                }
                case SplashLayer layer:
                {
                    var padding = Insets(layer.Padding);
                    float w = 0, h = 0;
                    foreach (var child in layer.Children.Where(c => c.Visible))
                    {
                        var margin = Insets(child.Margin);
                        var size = Measure(child, (fixedWidth ?? availableWidth) - padding.Horizontal - margin.Horizontal,
                            (fixedHeight ?? availableHeight) - padding.Vertical - margin.Vertical);
                        w = Math.Max(w, size.Width + margin.Horizontal);
                        h = Math.Max(h, size.Height + margin.Vertical);
                    }
                    return new SizeF(fixedWidth ?? w + padding.Horizontal, fixedHeight ?? h + padding.Vertical);
                }
                default:
                    return SizeF.Empty;
            }
        }

        private SizeF MeasureStack(SplashStack stack, float availableWidth, float availableHeight)
        {
            var vertical = stack.Orientation == SplashOrientation.Vertical;
            float along = 0, across = 0;
            var count = 0;
            foreach (var child in stack.Children.Where(c => c.Visible))
            {
                var margin = Insets(child.Margin);
                var size = Measure(child, availableWidth - margin.Horizontal, availableHeight - margin.Vertical);
                along += vertical ? size.Height + margin.Vertical : size.Width + margin.Horizontal;
                across = Math.Max(across, vertical ? size.Width + margin.Horizontal : size.Height + margin.Vertical);
                count++;
            }
            if (count > 1) along += Px(stack.Spacing) * (count - 1);
            return vertical ? new SizeF(across, along) : new SizeF(along, across);
        }

        /// <summary>Place <paramref name="e"/> in <paramref name="slot"/> (margin included) by its own alignment, then draw it.</summary>
        public void Place(SplashElement e, RectangleF slot, Color behind)
        {
            var inner = Deflate(slot, Insets(e.Margin));
            var size = Measure(e, inner.Width, inner.Height);
            var w = e.HorizontalAlign == SplashAlign.Stretch && e.Width is null ? inner.Width : size.Width;
            var h = e.VerticalAlign == SplashAlign.Stretch && e.Height is null ? inner.Height : size.Height;
            var x = Align(e.HorizontalAlign, inner.X, inner.Width, w);
            var y = Align(e.VerticalAlign, inner.Y, inner.Height, h);
            Draw(e, new RectangleF(x, y, w, h), behind);
        }

        private static float Align(SplashAlign align, float start, float space, float size) => align switch
        {
            SplashAlign.Center => start + ((space - size) / 2),
            SplashAlign.End => start + space - size,
            _ => start,
        };

        public void Draw(SplashElement e, RectangleF r, Color behind)
        {
            switch (e)
            {
                case SplashText text:
                {
                    // Translucent text is blended over what is behind it here, once, so every platform's text engine
                    // gets the same opaque colour (GDI's has no alpha at all).
                    var color = text.Color ?? Foreground(behind);
                    if (color.A < 255) color = Blend(behind, Color.FromArgb(255, color), color.A / 255.0);
                    Ops.Add(new SplashTextRun(r, text.Text, Font(text), color));
                    break;
                }
                case SplashImage image:
                    Ops.Add(new SplashImageDraw(r, image));
                    break;
                case SplashProgress progress:
                    DrawProgress(progress, r, behind);
                    break;
                case SplashStack stack:
                {
                    var under = Fill(stack.Background, r, behind);
                    DrawStack(stack, Deflate(r, Insets(stack.Padding)), under);
                    break;
                }
                case SplashLayer layer:
                {
                    var under = Fill(layer.Background, r, behind);
                    var content = Deflate(r, Insets(layer.Padding));
                    foreach (var child in layer.Children.Where(c => c.Visible)) Place(child, content, under);
                    break;
                }
            }
        }

        private Color Fill(Color? color, RectangleF r, Color behind)
        {
            if (color is not { } c) return behind;
            Ops.Add(new SplashFill(r, c));
            return c;
        }

        private void DrawStack(SplashStack stack, RectangleF content, Color behind)
        {
            var vertical = stack.Orientation == SplashOrientation.Vertical;
            var group = MeasureStack(stack, content.Width, content.Height);
            var spacing = Px(stack.Spacing);
            var cursor = vertical ? content.Y + ((content.Height - group.Height) / 2) : content.X + ((content.Width - group.Width) / 2);
            foreach (var child in stack.Children.Where(c => c.Visible))
            {
                var margin = Insets(child.Margin);
                var size = Measure(child, content.Width - margin.Horizontal, content.Height - margin.Vertical);
                if (vertical)
                {
                    Place(child with { VerticalAlign = SplashAlign.Start }, new RectangleF(content.X, cursor, content.Width, size.Height + margin.Vertical), behind);
                    cursor += size.Height + margin.Vertical + spacing;
                }
                else
                {
                    Place(child with { HorizontalAlign = SplashAlign.Start }, new RectangleF(cursor, content.Y, size.Width + margin.Horizontal, content.Height), behind);
                    cursor += size.Width + margin.Horizontal + spacing;
                }
            }
        }

        private void DrawProgress(SplashProgress progress, RectangleF r, Color behind)
        {
            var fill = progress.Fill ?? Foreground(behind);
            Ops.Add(new SplashFill(r, progress.Track ?? Blend(behind, fill, 0.2)));
            if (progress.Value is { } value)
            {
                Ops.Add(new SplashFill(r with { Width = (float)(r.Width * Math.Clamp(value, 0, 1)) }, fill));
                return;
            }
            // Indeterminate: a segment that travels the track and back, never leaving it.
            Animated = true;
            var segment = (float)(r.Width * SegmentShare);
            var t = phase - Math.Floor(phase);
            var travel = t < 0.5 ? t * 2 : 2 - (t * 2);
            Ops.Add(new SplashFill(new RectangleF(r.X + (float)((r.Width - segment) * travel), r.Y, segment, r.Height), fill));
        }

        private SplashFont Font(SplashText text) => new(text.FontFamily, Px(text.FontSize), text.Bold);
    }
}
