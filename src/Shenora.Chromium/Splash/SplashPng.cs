using System.Buffers.Binary;
using System.Drawing;

namespace Shenora.Chromium.Host;

/// <summary>A PNG's size, from its header, which is all the layout needs before a painter decodes it.</summary>
internal static class SplashPng
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The width and height in the IHDR chunk, which a PNG must start with; null when it is not a PNG.</summary>
    public static Size? ReadSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || !png[..8].SequenceEqual(Signature) || !png[12..16].SequenceEqual("IHDR"u8)) return null;
        var width = BinaryPrimitives.ReadInt32BigEndian(png[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(png[20..24]);
        return width > 0 && height > 0 ? new Size(width, height) : null;
    }

    /// <summary>The size of a PNG file; null when it cannot be read or is not a PNG.</summary>
    public static Size? ReadSize(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[24];
            using var file = File.OpenRead(Resolve(path));
            return file.ReadAtLeast(header, 24, throwOnEndOfStream: false) == 24 ? ReadSize(header) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>An image's size, from its file or its bytes.</summary>
    public static Size? ReadSize(SplashImage image) => image.Path is { } path ? ReadSize(path) : ReadSize(image.Bytes.Span);

    /// <summary>A relative path is relative to the app's own folder.</summary>
    public static string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
}
