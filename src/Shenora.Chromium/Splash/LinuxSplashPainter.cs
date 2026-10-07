#if CEF_LINUX
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using static Shenora.Chromium.Host.LinuxSplashNative;

namespace Shenora.Chromium.Host;

/// <summary>
/// Draws a splash frame on Linux through cairo (fills, PNGs) and pango (text, wrapped, with fontconfig's fallback for a
/// script the font lacks), into an ARGB32 image surface whose bytes are what X's 24-bit TrueColor visual takes. One
/// thread.
/// </summary>
internal sealed unsafe class LinuxSplashPainter(ILogger? log) : ISplashTextMeasurer, IDisposable
{
    private readonly Dictionary<SplashImage, nint> _images = new(SplashImageSource.Comparer);
    private readonly nint _measureSurface = cairo_image_surface_create(0, 1, 1);
    private nint _measure, _surface, _cr;
    private Size _size;
    private IReadOnlyList<SplashDrawOp> _last = [];

    /// <summary>The last frame's pixels.</summary>
    public byte* Data => cairo_image_surface_get_data(_surface);

    public int Stride => cairo_image_surface_get_stride(_surface);

    public Size Size => _size;

    public SizeF Measure(string text, SplashFont font, float maxWidth)
    {
        if (text.Length == 0) return SizeF.Empty;
        if (_measure == 0) _measure = cairo_create(_measureSurface);
        var layout = Layout(_measure, text, font, maxWidth > 0 ? (int)Math.Floor(maxWidth) : -1, center: false);
        pango_layout_get_pixel_size(layout, out var width, out var height);
        g_object_unref(layout);
        return new SizeF(width, height);
    }

    /// <summary>Draw <paramref name="frame"/>; returns the pixels that differ from the last frame, which is all an X
    /// server need be sent (an indeterminate bar's slide is a few rows, where the whole window would be megabytes).</summary>
    public Rectangle Paint(SplashFrame frame)
    {
        var damage = Damage(frame);
        _last = frame.Ops;
        EnsureSurface(frame.Size);
        cairo_set_operator(_cr, 0);   // clear
        cairo_paint(_cr);
        cairo_set_operator(_cr, 2);   // over
        foreach (var op in frame.Ops)
        {
            switch (op)
            {
                case SplashFill fill when fill.Bounds.Width > 0 && fill.Bounds.Height > 0:
                    cairo_set_source_rgba(_cr, fill.Color.R / 255.0, fill.Color.G / 255.0, fill.Color.B / 255.0, fill.Color.A / 255.0);
                    cairo_rectangle(_cr, fill.Bounds.X, fill.Bounds.Y, fill.Bounds.Width, fill.Bounds.Height);
                    cairo_fill(_cr);
                    break;
                case SplashImageDraw image when Image(image.Source) is var png and not 0:
                    DrawImage(png, image.Bounds);
                    break;
                case SplashTextRun text when text.Text.Length > 0:
                    var layout = Layout(_cr, text.Text, text.Font, (int)Math.Floor(text.Bounds.Width), center: true);   // as measured
                    cairo_set_source_rgba(_cr, text.Color.R / 255.0, text.Color.G / 255.0, text.Color.B / 255.0, 1);
                    cairo_move_to(_cr, text.Bounds.X, text.Bounds.Y);
                    pango_cairo_show_layout(_cr, layout);
                    g_object_unref(layout);
                    break;
            }
        }
        cairo_surface_flush(_surface);
        return damage;
    }

    // The ops that changed place or content since the last frame (by position in the list), old and new bounds both;
    // the whole frame when the size or the number of ops changed.
    private Rectangle Damage(SplashFrame frame)
    {
        var whole = new Rectangle(Point.Empty, frame.Size);
        if (frame.Size != _size || frame.Ops.Count != _last.Count) return whole;
        RectangleF? dirty = null;
        for (var i = 0; i < frame.Ops.Count; i++)
        {
            if (frame.Ops[i].Equals(_last[i])) continue;
            var both = RectangleF.Union(Bounds(frame.Ops[i]), Bounds(_last[i]));
            dirty = dirty is { } d ? RectangleF.Union(d, both) : both;
        }
        if (dirty is not { } r) return Rectangle.Empty;
        var area = Rectangle.FromLTRB((int)Math.Floor(r.Left) - 1, (int)Math.Floor(r.Top) - 1, (int)Math.Ceiling(r.Right) + 1, (int)Math.Ceiling(r.Bottom) + 1);
        return Rectangle.Intersect(area, whole);   // a pixel of margin for anti-aliased edges
    }

    private static RectangleF Bounds(SplashDrawOp op) => op switch
    {
        SplashFill f => f.Bounds,
        SplashTextRun t => t.Bounds,
        SplashImageDraw i => i.Bounds,
        _ => RectangleF.Empty,
    };

    private void DrawImage(nint png, RectangleF bounds)
    {
        int width = cairo_image_surface_get_width(png), height = cairo_image_surface_get_height(png);
        if (width <= 0 || height <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
        cairo_save(_cr);
        cairo_translate(_cr, bounds.X, bounds.Y);
        cairo_scale(_cr, bounds.Width / width, bounds.Height / height);
        cairo_set_source_surface(_cr, png, 0, 0);
        var source = cairo_get_source(_cr);
        cairo_pattern_set_filter(source, 2);   // best
        cairo_pattern_set_extend(source, 3);   // pad: no faded rim at the edges
        cairo_paint(_cr);
        cairo_restore(_cr);
    }

    // Wrapped at width (-1: one line), centred within it when drawn; measured left-aligned, which gives the natural width.
    private static nint Layout(nint cr, string text, SplashFont font, int width, bool center)
    {
        var layout = pango_cairo_create_layout(cr);
        var description = pango_font_description_from_string(font.Family ?? "Sans");
        pango_font_description_set_absolute_size(description, font.Size * PangoScale);
        pango_font_description_set_weight(description, font.Bold ? 700 : 400);
        pango_layout_set_font_description(layout, description);
        pango_font_description_free(description);
        pango_layout_set_width(layout, width > 0 ? width * PangoScale : -1);
        pango_layout_set_wrap(layout, 2);   // word, then character
        if (center) pango_layout_set_alignment(layout, 1);
        var utf8 = Encoding.UTF8.GetBytes(text);
        pango_layout_set_text(layout, utf8, utf8.Length);
        return layout;
    }

    private nint Image(SplashImage source)
    {
        if (_images.TryGetValue(source, out var cached)) return cached;
        if (_images.Count >= SplashImageSource.Capacity) ReleaseImages();
        nint png;
        if (source.Path is { } path) png = cairo_image_surface_create_from_png(SplashPng.Resolve(path));
        else
        {
            var reader = new PngReader(source.Bytes);
            var handle = GCHandle.Alloc(reader);
            try { png = cairo_image_surface_create_from_png_stream(&PngReader.Read, GCHandle.ToIntPtr(handle)); }
            finally { handle.Free(); }
        }
        if (png != 0 && cairo_surface_status(png) != 0)
        {
            cairo_surface_destroy(png);
            png = 0;
        }
        if (png == 0)
            AppCallback.Log(log, () => $"[Shenora.Chromium] The splash image {source.Path ?? "(bytes)"} could not be read; it is left out", LogLevel.Warning);
        _images[source] = png;
        return png;
    }

    private sealed class PngReader(ReadOnlyMemory<byte> bytes)
    {
        private readonly ReadOnlyMemory<byte> _bytes = bytes;
        private int _at;

        [UnmanagedCallersOnly]
        public static int Read(nint closure, byte* data, uint length)
        {
            var reader = (PngReader)GCHandle.FromIntPtr(closure).Target!;
            if (reader._at + length > reader._bytes.Length) return 10;   // read error
            reader._bytes.Span.Slice(reader._at, (int)length).CopyTo(new Span<byte>(data, (int)length));
            reader._at += (int)length;
            return 0;
        }
    }

    private void EnsureSurface(Size size)
    {
        size = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
        if (size == _size && _surface != 0) return;
        if (_cr != 0) cairo_destroy(_cr);
        if (_surface != 0) cairo_surface_destroy(_surface);
        _surface = cairo_image_surface_create(0, size.Width, size.Height);   // ARGB32
        _cr = cairo_create(_surface);
        _size = size;
    }

    private void ReleaseImages()
    {
        foreach (var png in _images.Values)
            if (png != 0) cairo_surface_destroy(png);
        _images.Clear();
    }

    public void Dispose()
    {
        ReleaseImages();
        if (_cr != 0) cairo_destroy(_cr);
        if (_surface != 0) cairo_surface_destroy(_surface);
        if (_measure != 0) cairo_destroy(_measure);
        cairo_surface_destroy(_measureSurface);
        _cr = _surface = _measure = 0;
    }
}
#endif
