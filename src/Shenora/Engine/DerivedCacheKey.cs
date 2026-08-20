using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Shenora.Engine;

/// <summary>
/// The cache key for anything DERIVED from a source file — a converted stream, a probe result, a thumbnail,
/// a rendered sheet.
/// <para>
/// 🔴 <b>Identity plus mtime plus LENGTH, never a path alone.</b> A path-only key survives the file being
/// overwritten, so the app serves yesterday's conversion of a file the user has replaced. Length as well as
/// mtime because they fail differently: a copy can preserve an mtime while changing bytes, and an edit can
/// change bytes without moving the length. Not a content hash.
/// </para>
/// <para>
/// 🔴 <b>PUBLIC BECAUSE THE FORMAT IS THE CONTRACT.</b> The value is that an app's key AGREES with the
/// kit's, byte for byte. Separator normalisation, lower-casing, field order, tick precision and the 8-byte
/// truncation each look optional and each produce a <i>valid-looking</i> key that matches nothing — so a
/// drifting copy silently orphans every cache on every device, with no error anywhere.
/// <c>DerivedCacheKeyTests</c> pins the exact output.
/// </para>
/// </summary>
public static class DerivedCacheKey
{
    /// <summary>
    /// A stable, filesystem-safe key for a source file's derived artefact — 16 hex characters of SHA-256
    /// over the inputs.
    /// </summary>
    /// <param name="path">The source path. Case-insensitive and separator-normalised.</param>
    /// <param name="length">The source length in bytes.</param>
    /// <param name="lastWriteUtc">The source's last-write time, UTC.</param>
    /// <param name="variant">
    /// What is being cached, when one source has several derived forms — <c>"probe"</c>, <c>"mp4"</c>,
    /// <c>"thumb-720"</c>. Part of the key. Empty is fine when a source has exactly one derived form.
    /// </param>
    public static string For(string path, long length, DateTime lastWriteUtc, string variant = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var normalised = path.Replace('\\', '/').ToLowerInvariant();
        var material = string.Create(CultureInfo.InvariantCulture,
            $"{normalised}|{length}|{lastWriteUtc.ToUniversalTime().Ticks}|{variant}");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
