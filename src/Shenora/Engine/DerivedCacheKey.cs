using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Shenora.Engine;

/// <summary>
/// The cache key for anything DERIVED from a source file — a converted stream, a probe result, a thumbnail,
/// a rendered sheet.
/// <para>
/// MOVED HERE FROM <c>Shenora.Media</c> on 2026-08-04 (D45) and renamed off "Media". Content middlewares are
/// a FAMILY — <c>Shenora.Modules.Media</c> today, <c>.Image</c> and <c>.Excel</c> foreseen — and a helper they all
/// share cannot live inside one member: <c>.Image</c> would have to depend on media to cache a thumbnail.
/// Nothing about identity-plus-mtime is media-specific.
/// </para>
/// <para>
/// <b>Identity plus mtime plus length, never a path alone.</b> All three surveyed implementations arrived
/// at that independently, with different encodings and the same rule: replaced source bytes must produce a
/// different key. A path-only key survives the file being overwritten, and then the app serves yesterday's
/// conversion of a file the user has replaced — a stale-cache bug that looks like corruption and is
/// invisible in testing, because a test rarely overwrites its fixture in place.
/// </para>
/// <para>
/// Length is in there as well as mtime because the two fail differently: a copy or restore can preserve an
/// mtime while changing the bytes, and an edit can change the bytes without moving the length. Cheap to
/// include, and it closes both.
/// </para>
/// <para>
/// ⚠ <b>Not a content hash, and not trying to be.</b> Hashing a 4 GB video to decide whether to reuse a
/// thumbnail costs more than producing the thumbnail. This is the identity check a cache needs; when real
/// integrity is the question the kit already has the tool — <c>UpdateManifest</c>'s per-file SHA-256.
/// </para>
/// </summary>
public static class DerivedCacheKey
{
    /// <summary>
    /// A stable, filesystem-safe key for a source file's derived artefact.
    /// <para>
    /// 16 hex characters of SHA-256 over the three inputs. Truncated deliberately: this names a cache
    /// entry, so the cost of a collision is one wrong reuse rather than a security failure, and 64 bits is
    /// far past the point where that matters for a media library. The donor that truncates to the same
    /// length has run it in production for years.
    /// </para>
    /// </summary>
    /// <param name="path">
    /// The source path. Case-INSENSITIVE and separator-normalised, because on Windows the same file
    /// legitimately arrives spelled several ways and each spelling would otherwise get its own cache entry
    /// — wasted work that is easy to ship and hard to notice.
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
