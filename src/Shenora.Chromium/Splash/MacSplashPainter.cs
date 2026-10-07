#if CEF_MACOS
using System.Drawing;
using Microsoft.Extensions.Logging;
using static Shenora.Chromium.Host.MacSplashNative;

namespace Shenora.Chromium.Host;

/// <summary>
/// Draws a splash frame on macOS through CoreGraphics, CoreText (the system UI font, and the system's own fallback for a
/// script it lacks) and ImageIO, into a bitmap the window's layer shows. CoreGraphics counts y up from the bottom, so
/// every top-left rectangle is flipped as it is drawn. One thread.
/// </summary>
internal sealed unsafe class MacSplashPainter(ILogger? log) : ISplashTextMeasurer, IDisposable
{
    private readonly nint _space = CGColorSpaceCreateDeviceRGB();
    private readonly Dictionary<SplashImage, nint> _images = new(SplashImageSource.Comparer);
    private readonly Dictionary<(string? Family, double Size, bool Bold), nint> _fonts = [];
    private nint _context;
    private Size _size;

    public SizeF Measure(string text, SplashFont font, float maxWidth)
    {
        if (text.Length == 0) return SizeF.Empty;
        // Measured unaligned: alignment changes no line break, and a centred paragraph may report the constraint's width.
        var framesetter = Framesetter(text, font, default, centered: false);
        try
        {
            var constraints = new CGSize { Width = maxWidth > 0 ? maxWidth : double.MaxValue, Height = double.MaxValue };
            var size = CTFramesetterSuggestFrameSizeWithConstraints(framesetter, default, 0, constraints, null);
            // Whole pixels, a hair over: measured again at exactly this width, the text must not wrap differently.
            return new SizeF((float)Math.Ceiling(size.Width + 0.5), (float)Math.Ceiling(size.Height));
        }
        finally
        {
            CFRelease(framesetter);
        }
    }

    /// <summary>Draw <paramref name="frame"/> and return it as a CGImage the caller releases.</summary>
    public nint Paint(SplashFrame frame)
    {
        EnsureContext(frame.Size);
        CGContextClearRect(_context, new CGRect(0, 0, _size.Width, _size.Height));
        CGContextSetInterpolationQuality(_context, 3);   // high
        foreach (var op in frame.Ops)
        {
            switch (op)
            {
                case SplashFill fill when fill.Bounds.Width > 0 && fill.Bounds.Height > 0:
                    CGContextSetRGBFillColor(_context, fill.Color.R / 255.0, fill.Color.G / 255.0, fill.Color.B / 255.0, fill.Color.A / 255.0);
                    CGContextFillRect(_context, Flip(fill.Bounds));
                    break;
                case SplashImageDraw image when Image(image.Source) is var cgImage and not 0:
                    CGContextDrawImage(_context, Flip(image.Bounds), cgImage);
                    break;
                case SplashTextRun text:
                    Text(text);
                    break;
            }
        }
        return CGBitmapContextCreateImage(_context);
    }

    private CGRect Flip(RectangleF r) => new(r.X, _size.Height - r.Y - r.Height, r.Width, r.Height);

    private void Text(SplashTextRun run)
    {
        if (run.Text.Length == 0) return;
        var framesetter = Framesetter(run.Text, run.Font, run.Color, centered: true);
        var path = CGPathCreateWithRect(Flip(run.Bounds), 0);
        var ctFrame = CTFramesetterCreateFrame(framesetter, default, path, 0);
        try
        {
            CGContextSetTextMatrix(_context, new CGAffineTransform { A = 1, D = 1 });
            CTFrameDraw(ctFrame, _context);
        }
        finally
        {
            CFRelease(ctFrame);
            CGPathRelease(path);
            CFRelease(framesetter);
        }
    }

    // A framesetter over the text in the font and the colour (none: the default, which a measurement ignores).
    private nint Framesetter(string text, SplashFont font, Color color, bool centered)
    {
        var alignment = centered ? (byte)2 : (byte)0;   // centred, or natural
        var setting = new CTParagraphStyleSetting { Spec = 0, ValueSize = 1, Value = &alignment };
        var paragraph = CTParagraphStyleCreate(&setting, 1);
        var cgColor = CGColorCreateSRGB(color.R / 255.0, color.G / 255.0, color.B / 255.0, color == default ? 1 : color.A / 255.0);
        var keys = stackalloc nint[3]
        {
            CoreTextConstant("kCTFontAttributeName"), CoreTextConstant("kCTForegroundColorAttributeName"), CoreTextConstant("kCTParagraphStyleAttributeName"),
        };
        var values = stackalloc nint[3] { Font(font), cgColor, paragraph };
        var attributes = CFDictionaryCreate(0, keys, values, 3, KeyCallbacks, ValueCallbacks);
        var cfText = CFString(text);
        var attributed = CFAttributedStringCreate(0, cfText, attributes);
        var framesetter = CTFramesetterCreateWithAttributedString(attributed);
        CFRelease(attributed);
        CFRelease(cfText);
        CFRelease(attributes);
        CGColorRelease(cgColor);
        CFRelease(paragraph);
        return framesetter;
    }

    private nint Font(SplashFont font)
    {
        var key = (font.Family, Math.Round(font.Size, 1), font.Bold);
        if (_fonts.TryGetValue(key, out var cached)) return cached;
        nint made = 0;
        if (font.Family is { } family)
        {
            // CoreText substitutes for a family it lacks rather than fail, as GDI and fontconfig do.
            var name = CFString(family);
            made = CTFontCreateWithName(name, key.Item2, 0);
            CFRelease(name);
            if (made != 0 && font.Bold && CTFontCreateCopyWithSymbolicTraits(made, 0, 0, 2, 2) is var bold and not 0)   // the bold trait
            {
                CFRelease(made);
                made = bold;
            }
        }
        if (made == 0) made = CTFontCreateUIFontForLanguage(font.Bold ? 3u /* emphasized system */ : 2u /* system */, key.Item2, 0);
        _fonts[key] = made;
        return made;
    }

    private nint Image(SplashImage source)
    {
        if (_images.TryGetValue(source, out var cached)) return cached;
        if (_images.Count >= SplashImageSource.Capacity) ReleaseImages();
        nint image = 0;
        var input = source.Path is { } path ? FileUrl(SplashPng.Resolve(path)) : Data(source.Bytes.Span);
        if (input != 0)
        {
            var reader = source.Path is not null ? CGImageSourceCreateWithURL(input, 0) : CGImageSourceCreateWithData(input, 0);
            if (reader != 0)
            {
                image = CGImageSourceCreateImageAtIndex(reader, 0, 0);
                CFRelease(reader);
            }
            CFRelease(input);
        }
        if (image == 0)
            AppCallback.Log(log, () => $"[Shenora.Chromium] The splash image {source.Path ?? "(bytes)"} could not be read; it is left out", LogLevel.Warning);
        _images[source] = image;
        return image;
    }

    private void EnsureContext(Size size)
    {
        size = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
        if (size == _size && _context != 0) return;
        if (_context != 0) CGContextRelease(_context);
        // Premultiplied BGRA, the layout a layer takes without converting.
        _context = CGBitmapContextCreate(0, (nuint)size.Width, (nuint)size.Height, 8, 0, _space, 2 /* premultiplied first */ | (2 << 12) /* 32 little */);
        if (_context == 0) throw new InvalidOperationException($"The splash's {size.Width}×{size.Height} bitmap could not be made.");
        _size = size;
    }

    private void ReleaseImages()
    {
        foreach (var image in _images.Values)
            if (image != 0) CGImageRelease(image);
        _images.Clear();
    }

    public void Dispose()
    {
        ReleaseImages();
        foreach (var font in _fonts.Values)
            if (font != 0) CFRelease(font);
        _fonts.Clear();
        if (_context != 0) CGContextRelease(_context);
        _context = 0;
        CGColorSpaceRelease(_space);
    }
}
#endif
