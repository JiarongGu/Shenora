#if CEF_WINDOWS
using System.Drawing;
using Microsoft.Extensions.Logging;
using static Shenora.Chromium.Host.WindowsSplashNative;

namespace Shenora.Chromium.Host;

/// <summary>
/// Draws a splash frame into a 32-bit DIB on Windows. Text goes through GDI's <c>DrawTextW</c>, which font-links a
/// script the font lacks (Chinese, Japanese and Korean from the system's message font, seen rendered); fills and images
/// through GDI+. The frame is opaque, so every pixel's alpha is set after drawing, then the corners are cut round where
/// Windows 11 rounds the window under it. One thread.
/// </summary>
internal sealed unsafe class WindowsSplashPainter : ISplashTextMeasurer, IDisposable
{
    private const uint TextFormat = DT_CENTER | DT_WORDBREAK | DT_NOPREFIX | DT_EDITCONTROL;
    private const int Unbounded = 1 << 20;

    private readonly ILogger? _log;
    private readonly nint _gdiplus;
    private readonly nint _measure;
    private readonly string _systemFace;
    private readonly Dictionary<(string Face, int Px, bool Bold), nint> _fonts = [];
    private readonly Dictionary<SplashImage, (nint Image, nint Stream)> _images = new(SplashImageSource.Comparer);
    private nint _dc, _bitmap, _previous, _imageAttributes;
    private uint* _bits;
    private Size _size;

    public WindowsSplashPainter(ILogger? log)
    {
        _log = log;
        var input = new GdiplusStartupInput { Version = 1 };
        if (GdiplusStartup(out _gdiplus, &input, 0) != 0) throw new InvalidOperationException("GDI+ would not start.");
        _measure = CreateCompatibleDC(0);
        _dc = CreateCompatibleDC(0);
        _systemFace = SystemFace();
    }

    /// <summary>The DC holding the last frame, for <c>UpdateLayeredWindow</c>.</summary>
    public nint Dc => _dc;

    public SizeF Measure(string text, SplashFont font, float maxWidth)
    {
        if (text.Length == 0) return SizeF.Empty;
        var rect = new RECT { Right = maxWidth > 0 ? Math.Max(1, (int)Math.Floor(maxWidth)) : Unbounded };
        var previous = SelectObject(_measure, Font(font));
        DrawTextW(_measure, text, text.Length, &rect, TextFormat | DT_CALCRECT);
        SelectObject(_measure, previous);
        return new SizeF(rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>The bitmap's size: the frame's, and the shadow's margin round it.</summary>
    public Size Size => _size;

    /// <summary>Draw <paramref name="frame"/>; <paramref name="cornerRadius"/> pixels of rounding (0 for square) on the
    /// <paramref name="corners"/> asked, inside a margin of <paramref name="shadow"/> pixels that holds a soft shadow.</summary>
    public void Paint(SplashFrame frame, int cornerRadius, OverlayCorners corners = OverlayCorners.Top | OverlayCorners.Bottom, int shadow = 0)
    {
        shadow = Math.Max(0, shadow);
        EnsureBitmap(new Size(frame.Size.Width + (2 * shadow), frame.Size.Height + (2 * shadow)));
        new Span<uint>(_bits, _size.Width * _size.Height).Clear();
        nint graphics = 0;
        try
        {
            foreach (var original in frame.Ops)
            {
                var op = shadow == 0 ? original : Offset(original, shadow);
                switch (op)
                {
                    case SplashFill fill:
                        Begin(ref graphics);
                        Fill(graphics, fill);
                        break;
                    case SplashImageDraw image when Image(image.Source) is var bitmap and not 0:
                        Begin(ref graphics);
                        DrawImage(graphics, bitmap, image.Bounds);
                        break;
                    case SplashTextRun text:
                        End(ref graphics);   // GDI and GDI+ take turns on the DC
                        Text(text);
                        break;
                }
            }
        }
        finally
        {
            End(ref graphics);
        }
        GdiFlush();
        Opaque(cornerRadius, corners, shadow);
    }

    private static SplashDrawOp Offset(SplashDrawOp op, int by) => op switch
    {
        SplashFill fill => fill with { Bounds = Moved(fill.Bounds, by) },
        SplashImageDraw image => image with { Bounds = Moved(image.Bounds, by) },
        SplashTextRun text => text with { Bounds = Moved(text.Bounds, by) },
        _ => op,
    };

    private static RectangleF Moved(RectangleF bounds, int by) => bounds with { X = bounds.X + by, Y = bounds.Y + by };

    private void Begin(ref nint graphics)
    {
        if (graphics != 0) return;
        GdipCreateFromHDC(_dc, out graphics);
        GdipSetSmoothingMode(graphics, 4);         // anti-alias
        GdipSetInterpolationMode(graphics, 7);     // high-quality bicubic
        GdipSetPixelOffsetMode(graphics, 4);       // half: integer rects fill whole pixels
    }

    private static void End(ref nint graphics)
    {
        if (graphics == 0) return;
        GdipDeleteGraphics(graphics);
        graphics = 0;
    }

    private static void Fill(nint graphics, SplashFill fill)
    {
        if (fill.Bounds.Width <= 0 || fill.Bounds.Height <= 0) return;
        GdipCreateSolidFill((uint)fill.Color.ToArgb(), out var brush);
        GdipFillRectangle(graphics, brush, fill.Bounds.X, fill.Bounds.Y, fill.Bounds.Width, fill.Bounds.Height);
        GdipDeleteBrush(brush);
    }

    // Tile-flip wrapping: scaled, GDI+ samples past the image's edge, and the default blends those samples with
    // transparency, which leaves every image with a faded rim (a blue 1×1 image scaled up came out #3308C8 over red).
    private void DrawImage(nint graphics, nint image, RectangleF bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        GdipGetImageWidth(image, out var width);
        GdipGetImageHeight(image, out var height);
        if (_imageAttributes == 0)
        {
            GdipCreateImageAttributes(out _imageAttributes);
            GdipSetImageAttributesWrapMode(_imageAttributes, 3 /* TileFlipXY */, 0, 0);
        }
        GdipDrawImageRectRect(graphics, image, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0, 0, width, height, 2 /* pixels */,
            _imageAttributes, 0, 0);
    }

    private void Text(SplashTextRun run)
    {
        var left = (int)Math.Round(run.Bounds.X);
        var top = (int)Math.Round(run.Bounds.Y);
        var rect = new RECT { Left = left, Top = top, Right = left + (int)Math.Ceiling(run.Bounds.Width), Bottom = top + (int)Math.Ceiling(run.Bounds.Height) };
        var previous = SelectObject(_dc, Font(run.Font));
        SetBkMode(_dc, 1);   // transparent
        SetTextColor(_dc, (uint)(run.Color.R | (run.Color.G << 8) | (run.Color.B << 16)));
        DrawTextW(_dc, run.Text, run.Text.Length, &rect, TextFormat);
        SelectObject(_dc, previous);
    }

    private nint Font(SplashFont font)
    {
        var key = (font.Family ?? _systemFace, Math.Max(1, (int)Math.Round(font.Size)), font.Bold);
        if (_fonts.TryGetValue(key, out var handle)) return handle;
        handle = CreateFontW(-key.Item2, 0, 0, 0, font.Bold ? 700 : 400, 0, 0, 0, 1, 0, 0, 5 /* ClearType */, 0, key.Item1);
        _fonts[key] = handle;
        return handle;
    }

    private nint Image(SplashImage source)
    {
        if (_images.TryGetValue(source, out var cached)) return cached.Image;
        if (_images.Count >= SplashImageSource.Capacity) ReleaseImages();   // a render that makes a new image every frame
        nint image = 0, stream = 0;
        if (source.Path is { } path)
        {
            if (GdipCreateBitmapFromFile(SplashPng.Resolve(path), out image) != 0) image = 0;
        }
        else
        {
            fixed (byte* data = source.Bytes.Span) stream = SHCreateMemStream(data, (uint)source.Bytes.Length);
            if (stream != 0 && GdipCreateBitmapFromStream(stream, out image) != 0) image = 0;
        }
        if (image == 0)
            AppCallback.Log(_log, () => $"[Shenora.Chromium] The splash image {source.Path ?? "(bytes)"} could not be read; it is left out", LogLevel.Warning);
        _images[source] = (image, stream);
        return image;
    }

    private void ReleaseImages()
    {
        foreach (var (image, stream) in _images.Values)
        {
            if (image != 0) GdipDisposeImage(image);
            Release(stream);
        }
        _images.Clear();
    }

    private void EnsureBitmap(Size size)
    {
        if (size == _size && _bitmap != 0) return;
        if (_bitmap != 0)
        {
            SelectObject(_dc, _previous);
            DeleteObject(_bitmap);
        }
        var header = new BITMAPINFOHEADER { Size = (uint)sizeof(BITMAPINFOHEADER), Width = Math.Max(1, size.Width), Height = -Math.Max(1, size.Height), Planes = 1, BitCount = 32 };
        void* bits;
        _bitmap = CreateDIBSection(_dc, &header, 0, &bits, 0, 0);
        if (_bitmap == 0) throw new InvalidOperationException($"The splash's {size.Width}×{size.Height} bitmap could not be made.");
        _previous = SelectObject(_dc, _bitmap);
        _bits = (uint*)bits;
        _size = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
    }

    // GDI leaves alpha 0 wherever it draws; the frame is opaque, so every pixel of it takes 255. Then each corner asked
    // for is cut to a quarter circle, anti-aliased by coverage, its colour premultiplied as UpdateLayeredWindow's
    // per-pixel alpha wants. A margin round the frame holds a black shadow that fades outward.
    private void Opaque(int radius, OverlayCorners corners, int shadow)
    {
        int stride = _size.Width, w = _size.Width - (2 * shadow), h = _size.Height - (2 * shadow);
        var pixels = new Span<uint>(_bits, stride * _size.Height);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            pixels[((y + shadow) * stride) + x + shadow] |= 0xFF000000;
        if (shadow > 0) Shadow(pixels, stride, w, h, shadow, radius);
        radius = Math.Min(radius, Math.Min(w, h) / 2);
        for (var y = 0; y < radius; y++)
        for (var x = 0; x < radius; x++)
        {
            var dx = radius - (x + 0.5);
            var dy = radius - (y + 0.5);
            var coverage = Math.Clamp(radius - Math.Sqrt((dx * dx) + (dy * dy)) + 0.5, 0, 1);
            if (coverage >= 1) continue;
            int left = shadow + x, right = shadow + w - 1 - x, top = shadow + y, bottom = shadow + h - 1 - y;
            if (corners.HasFlag(OverlayCorners.Top))
            {
                Cut(ref pixels[(top * stride) + left], coverage);
                Cut(ref pixels[(top * stride) + right], coverage);
            }
            if (corners.HasFlag(OverlayCorners.Bottom))
            {
                Cut(ref pixels[(bottom * stride) + left], coverage);
                Cut(ref pixels[(bottom * stride) + right], coverage);
            }
        }
    }

    // A soft black shadow in the margin: its alpha falls with the distance from the frame's rounded edge, squared, from
    // about a fifth at the edge to nothing at the margin's outer edge.
    private static void Shadow(Span<uint> pixels, int stride, int w, int h, int shadow, int radius)
    {
        const double EdgeAlpha = 0x30;
        int height = h + (2 * shadow);
        double r = Math.Min(radius, Math.Min(w, h) / 2);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < stride; x++)
        {
            // Distance to the frame's rounded rectangle, in pixels; inside it is zero or less.
            double fx = x + 0.5 - shadow, fy = y + 0.5 - shadow;
            var qx = Math.Max(Math.Abs(fx - (w / 2.0)) - ((w / 2.0) - r), 0);
            var qy = Math.Max(Math.Abs(fy - (h / 2.0)) - ((h / 2.0) - r), 0);
            var distance = Math.Sqrt((qx * qx) + (qy * qy)) - r;
            if (distance <= 0 || distance >= shadow) continue;
            var fade = 1 - (distance / shadow);
            var alpha = (uint)Math.Round(EdgeAlpha * fade * fade);
            ref var pixel = ref pixels[(y * stride) + x];
            if ((pixel >> 24) < alpha) pixel = alpha << 24;
        }
    }

    private static void Cut(ref uint pixel, double coverage)
    {
        var value = pixel;
        uint Scale(int shift) => (uint)Math.Round(((value >> shift) & 0xFF) * coverage) << shift;
        pixel = Scale(24) | Scale(16) | Scale(8) | Scale(0);
    }

    private static string SystemFace()
    {
        var metrics = new NONCLIENTMETRICSW { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NONCLIENTMETRICSW>() };
        return SystemParametersInfoW(0x29, metrics.Size, ref metrics, 0) != 0 && !string.IsNullOrEmpty(metrics.MessageFont.FaceName)
            ? metrics.MessageFont.FaceName
            : "Segoe UI";
    }

    public void Dispose()
    {
        foreach (var font in _fonts.Values) DeleteObject(font);
        _fonts.Clear();
        ReleaseImages();
        if (_imageAttributes != 0) GdipDisposeImageAttributes(_imageAttributes);
        _imageAttributes = 0;
        if (_bitmap != 0)
        {
            SelectObject(_dc, _previous);
            DeleteObject(_bitmap);
            _bitmap = 0;
        }
        if (_dc != 0) DeleteDC(_dc);
        if (_measure != 0) DeleteDC(_measure);
        _dc = 0;
        GdiplusShutdown(_gdiplus);
    }

    /// <summary>The pixel at (<paramref name="x"/>, <paramref name="y"/>) of the last frame, as premultiplied ARGB. For
    /// tests.</summary>
    internal uint Pixel(int x, int y) => _bits[(y * _size.Width) + x];
}
#endif
