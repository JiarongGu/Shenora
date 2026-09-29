using System.Runtime.InteropServices;

namespace Shenora.Chromium.Host;

/// <summary>
/// The system's caption glyphs as pixels, drawn from its own icon font. A Views label cannot show them as text: it
/// draws a private-use code point, which is where the icon font keeps them, as U+FFFD, whatever its font list
/// (measured: plain text in the same label draws).
/// </summary>
internal static unsafe class CaptionGlyphs
{
    /// <summary>A glyph's coverage, 0–255, centred in a square.</summary>
    internal sealed record Mask(int Size, byte[] Coverage);

#if CEF_WINDOWS
    // Segoe Fluent Icons is Windows 11's, Segoe MDL2 Assets Windows 10's, with the glyphs at the same code points.
    private static readonly string[] Families = ["Segoe Fluent Icons", "Segoe MDL2 Assets"];

    /// <summary>
    /// <paramref name="glyph"/> at <paramref name="em"/> pixels, its ink centred in a square a little larger than the
    /// em. Null when neither icon font is installed.
    /// </summary>
    public static Mask? Rasterize(char glyph, int em)
    {
        var box = em * 2;
        var dc = CreateCompatibleDC(0);
        if (dc == 0) return null;
        nint bitmap = 0, font = 0, oldBitmap = 0, oldFont = 0;
        try
        {
            var info = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = box, biHeight = -box, biPlanes = 1, biBitCount = 32 };
            bitmap = CreateDIBSection(dc, &info, 0, out var bits, 0, 0);
            if (bitmap == 0) return null;
            oldBitmap = SelectObject(dc, bitmap);
            font = FontFor(dc, em, out oldFont);
            if (font == 0) return null;
            SetTextColor(dc, 0x00FFFFFF);
            SetBkMode(dc, 1 /* TRANSPARENT */);
            var rect = new RECT { Right = box, Bottom = box };
            DrawTextW(dc, &glyph, 1, &rect, 0x1 | 0x4 | 0x20 | 0x800 /* CENTER | VCENTER | SINGLELINE | NOPREFIX */);
            GdiFlush();
            return Centre(new ReadOnlySpan<uint>(bits, box * box), box, em + 4);
        }
        finally
        {
            if (oldFont != 0) SelectObject(dc, oldFont);
            if (font != 0) DeleteObject(font);
            if (oldBitmap != 0) SelectObject(dc, oldBitmap);
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    // The first family GDI really has: it substitutes another face for a missing one rather than failing.
    private static nint FontFor(nint dc, int em, out nint old)
    {
        old = 0;
        var face = stackalloc char[64];
        foreach (var family in Families)
        {
            var font = CreateFontW(-em, 0, 0, 0, 400, 0, 0, 0, 1 /* DEFAULT_CHARSET */, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, family);
            if (font == 0) continue;
            var previous = SelectObject(dc, font);
            var length = GetTextFaceW(dc, 64, face);
            if (length > 1 && new ReadOnlySpan<char>(face, length - 1).Equals(family, StringComparison.OrdinalIgnoreCase))
            {
                old = previous;
                return font;
            }
            SelectObject(dc, previous);
            DeleteObject(font);
        }
        return 0;
    }

    // The ink moved to the middle of a size-by-size square. White on black, so any channel is the coverage.
    private static Mask? Centre(ReadOnlySpan<uint> pixels, int box, int size)
    {
        int minX = box, minY = box, maxX = -1, maxY = -1;
        for (var y = 0; y < box; y++)
            for (var x = 0; x < box; x++)
                if ((pixels[y * box + x] & 0xFF) != 0) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
        if (maxX < 0) return null;
        var coverage = new byte[size * size];
        var dx = (size - (maxX - minX + 1)) / 2 - minX;
        var dy = (size - (maxY - minY + 1)) / 2 - minY;
        for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                int tx = x + dx, ty = y + dy;
                if (tx >= 0 && ty >= 0 && tx < size && ty < size) coverage[ty * size + tx] = (byte)(pixels[y * box + x] & 0xFF);
            }
        return new Mask(size, coverage);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [DllImport("gdi32")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32")] private static extern int DeleteDC(nint dc);
    [DllImport("gdi32")] private static extern nint CreateDIBSection(nint dc, BITMAPINFOHEADER* info, uint usage, out void* bits, nint section, uint offset);
    [DllImport("gdi32")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32")] private static extern int DeleteObject(nint obj);
    [DllImport("gdi32", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
        uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32")] private static extern int GetTextFaceW(nint dc, int count, char* face);
    [DllImport("gdi32")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32")] private static extern int GdiFlush();
    [DllImport("user32")] private static extern int DrawTextW(nint dc, char* text, int count, RECT* rect, uint format);
#else
    public static Mask? Rasterize(char glyph, int em) => null;
#endif
}
