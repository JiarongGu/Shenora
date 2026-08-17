using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Shenora.Engine;

/// <summary>
/// The cache key for anything DERIVED from a source file — a converted stream, a probe result, a thumbnail,
/// a rendered sheet.
/// <para>
/// 🔴 <b>Identity plus mtime plus LENGTH, never a path alone.</b> A path-only key survives the file being
/// overwritten, so the app serves yesterday's conversion of a file the user has replaced — a stale-cache
/// bug that looks like corruption and that testing rarely catches, because a test seldom overwrites its
/// fixture in place. Length is there as well as mtime because they fail differently: a copy can preserve
/// an mtime while changing bytes, and an edit can change bytes without moving the length.
/// </para>
/// <para>
/// ⚠ <b>Not a content hash, and not trying to be</b> — hashing a 4 GB video to decide whether to reuse a
/// thumbnail costs more than producing it. For real integrity use <c>UpdateManifest</c>'s per-file SHA-256.
/// </para>
/// </summary>
internal static class DerivedCacheKey
{
    /// <summary>
    /// A stable, filesystem-safe key for a source file's derived artefact.
    /// 16 hex characters of SHA-256 over the inputs — truncated deliberately, since a collision costs one
    /// wrong reuse rather than a security failure.
    /// </summary>
    /// <param name="path">
    /// The source path. Case-insensitive and separator-normalised, so one file spelled several ways does
    /// not get several cache entries.
    /// </param>
    /// <param name="length">The source length in bytes.</param>
    /// <param name="lastWriteUtc">The source's last-write time, UTC.</param>
    /// <param name="variant">
    /// What is being cached, when one source has several derived forms — <c>"probe"</c>, <c>"mp4"</c>,
    /// <c>"thumb-720"</c>. Part of the key, so a thumbnail can never be served as a converted stream.
    /// Empty is fine when a source has exactly one derived form.
    /// </param>
    public static string For(string path, long length, DateTime lastWriteUtc, string variant = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var normalised = path.Replace('\\', '/').ToLowerInvariant();
        // Ticks rather than a formatted date: no culture, no precision lost to a format string, and a
        // change of one tick is still a different key.
        var material = string.Create(CultureInfo.InvariantCulture,
            $"{normalised}|{length}|{lastWriteUtc.ToUniversalTime().Ticks}|{variant}");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
