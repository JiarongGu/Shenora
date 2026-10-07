namespace Shenora.Chromium.Host;

/// <summary>
/// Two <see cref="SplashImage"/>s are the same image when they name the same file or hold the same bytes, whatever their
/// size or place: the key every cache of decoded images uses. A record's own equality would count each size an image is
/// animated through, and each fresh array of the same bytes (a resource read again every render), as a new image.
/// </summary>
internal sealed class SplashImageSource : IEqualityComparer<SplashImage>
{
    public static readonly SplashImageSource Comparer = new();

    /// <summary>The most decoded images a cache keeps; past it, it starts again rather than grow.</summary>
    public const int Capacity = 32;

    public bool Equals(SplashImage? x, SplashImage? y) =>
        ReferenceEquals(x, y)
        || (x is not null && y is not null && (x.Path is not null || y.Path is not null
            ? string.Equals(x.Path, y.Path, StringComparison.Ordinal)
            : x.Bytes.Span.SequenceEqual(y.Bytes.Span)));

    public int GetHashCode(SplashImage image)
    {
        if (image.Path is { } path) return StringComparer.Ordinal.GetHashCode(path);
        var bytes = image.Bytes.Span;
        var hash = new HashCode();
        hash.Add(bytes.Length);
        hash.AddBytes(bytes[..Math.Min(bytes.Length, 256)]);
        hash.AddBytes(bytes[Math.Max(0, bytes.Length - 256)..]);
        return hash.ToHashCode();
    }
}
