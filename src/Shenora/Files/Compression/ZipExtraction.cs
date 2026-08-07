using System.IO.Compression;
using Shenora;

namespace Shenora.IO.Compression;

/// <summary>What an extraction produced, and what it refused.</summary>
/// <param name="Files">Absolute paths written, in archive order.</param>
/// <param name="Bytes">Total uncompressed bytes written.</param>
/// <param name="Refused">
/// Entries skipped because they would have escaped the destination.
/// <b>Non-empty means the archive tried something</b>; an app that wants to treat that as fatal has the
/// names to say which.
/// </param>
public sealed record ExtractionResult(
    IReadOnlyList<string> Files, long Bytes, IReadOnlyList<string> Refused);

/// <summary>Bounds for one extraction. Every one is a REFUSAL, never a truncation.</summary>
public sealed class ExtractionLimits
{
    /// <summary>
    /// Largest total uncompressed size to write. Default 1 GiB.
    /// <para>
    /// ⚠ This is the zip-bomb bound, and it is on the TOTAL rather than per entry: a bomb is usually many
    /// small entries, or one entry that only looks small until it is inflated. Exceeding it throws — a
    /// partial extraction that stopped quietly would leave the caller believing it had everything.
    /// </para>
    /// </summary>
    public long MaxTotalBytes { get; init; } = 1L << 30;

    /// <summary>Largest number of entries to write. Default 100,000.</summary>
    public int MaxEntries { get; init; } = 100_000;
}

/// <summary>
/// Extracting a ZIP <b>safely</b> — which is the whole reason this exists, because the extraction itself
/// is one framework call.
///
/// <para>
/// ⚠ <b>The danger is the entry NAME, not the bytes.</b> An archive is a list of paths chosen by whoever
/// built it, and nothing stops one of them being <c>../../autoexec.bat</c> or an absolute path — the
/// "zip slip" family. `ZipFile.ExtractToDirectory` has guarded this since .NET 4.5.1, but a hand-rolled
/// loop over `archive.Entries` (the shape anyone writing progress reporting or filtering ends up with)
/// does not, and neither does a third-party native extractor unless it says so. **The donor this was
/// harvested from leans on its 7-Zip library's behaviour and has no check of its own** — the gap
/// `extraction-sources.md` says to fix during the port rather than carry.
/// </para>
///
/// <para>
/// <b>Zip only, and no native engine.</b> `System.IO.Compression` is in the shared framework, so this
/// package adds no dependency and works on every shell. 7z, rar and friends need a native library, which
/// the kit will not vendor for the same reason it ships no media encoder (D42) — those arrive as a seam
/// an app fills, if a second consumer ever asks.
/// </para>
/// </summary>
public static class ZipExtraction
{
    /// <summary>
    /// Extract <paramref name="archivePath"/> into <paramref name="destinationDirectory"/>, refusing any
    /// entry that would land outside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A refused entry is SKIPPED and named in <see cref="ExtractionResult.Refused"/> rather than
    /// throwing. That is deliberate and it is the one judgement here worth arguing with: an archive with
    /// one hostile entry is usually still an archive you want the rest of, and a caller who disagrees can
    /// treat a non-empty list as fatal in one line. Throwing would deny that choice; silently dropping it
    /// would hide an attack.
    /// </para>
    /// <para>
    /// Limits are the opposite — they THROW, because exceeding one means the caller's assumption about the
    /// archive was wrong and continuing would write an unknown amount to their disk.
    /// </para>
    /// </remarks>
    /// <param name="archivePath">The .zip to read.</param>
    /// <param name="destinationDirectory">Where entries land. Created if absent; nothing may escape it.</param>
    /// <param name="limits">Bounds on the result. Null uses <see cref="ExtractionLimits"/>' defaults.</param>
    /// <param name="overwrite">
    /// Replace files that already exist. False (the default) throws on a collision, which is the safer
    /// answer when extracting into a directory that holds anything else.
    /// </param>
    /// <param name="cancellationToken">Checked per entry.</param>
    public static ExtractionResult ExtractTo(string archivePath, string destinationDirectory,
        ExtractionLimits? limits = null, bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        using var archive = ZipFile.OpenRead(archivePath);
        return ExtractTo(archive, destinationDirectory, limits, overwrite, cancellationToken);
    }

    /// <summary>
    /// Extract an already-open <paramref name="archive"/>, refusing any entry that would land outside
    /// <paramref name="destinationDirectory"/>. See the string overload for the judgement calls.
    /// </summary>
    /// <param name="archive">An open archive. The caller keeps ownership.</param>
    /// <param name="destinationDirectory">Where entries land. Created if absent; nothing may escape it.</param>
    /// <param name="limits">Bounds on the result. Null uses <see cref="ExtractionLimits"/>' defaults.</param>
    /// <param name="overwrite">Replace files that already exist; false throws on a collision.</param>
    /// <param name="cancellationToken">Checked per entry.</param>
    public static ExtractionResult ExtractTo(ZipArchive archive, string destinationDirectory,
        ExtractionLimits? limits = null, bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        limits ??= new ExtractionLimits();

        // The root is resolved ONCE and compared against, rather than re-derived per entry: a check that
        // recomputes its own baseline is a check that can be argued into agreeing with the attacker.
        var root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);

        var written = new List<string>();
        var refused = new List<string>();
        long bytes = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A directory entry: no bytes, and nothing to place. Created lazily by the files inside it,
            // so an archive that omits directory entries entirely still extracts correctly.
            if (entry.Name.Length == 0) continue;

            if (Resolve(root, entry.FullName) is not { } target)
            {
                refused.Add(entry.FullName);
                continue;
            }

            if (written.Count >= limits.MaxEntries)
            {
                throw new InvalidOperationException(
                    $"The archive holds more than {limits.MaxEntries} entries. Refusing to continue — raise " +
                    $"{nameof(ExtractionLimits)}.{nameof(ExtractionLimits.MaxEntries)} if that is expected.");
            }

            bytes += entry.Length;
            if (bytes > limits.MaxTotalBytes)
            {
                throw new InvalidOperationException(
                    $"The archive expands to more than {limits.MaxTotalBytes} bytes. Refusing to continue — " +
                    "this is the zip-bomb bound, so raise it deliberately rather than by reflex.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite);
            written.Add(target);
        }

        return new ExtractionResult(written, bytes, refused);
    }

    /// <summary>
    /// Where an entry may be written, or null if it escapes <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Compared with a separator APPENDED to the root</b> — without it, <c>/data-evil</c> passes as a
    /// child of <c>/data</c>, which is the same prefix-matching bug `WebViewFiles.ResolveContained` already
    /// documents. Two features needing the identical rule is why the reasoning is repeated here rather than
    /// left implicit.
    /// </para>
    /// <para>
    /// Separators are normalised first, because a zip written on Linux uses <c>/</c> and one written by a
    /// careless tool may use <c>\</c> — and on Windows both are separators, so a check that only knew about
    /// one would resolve <c>..\..\x</c> as a FILE NAME and let it through.
    /// </para>
    /// </remarks>
    private static string? Resolve(string root, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return null;

        var relative = entryName.Replace('\\', '/');
        // An absolute or rooted name is refused outright rather than "made relative": stripping the root
        // and continuing is a guess at what the archive meant, and the honest answer is that a
        // well-formed archive does not contain one.
        if (Path.IsPathRooted(relative) || relative.StartsWith('/')) return null;

        string full;
        try { full = Path.GetFullPath(Path.Combine(root, relative)); }
        catch (Exception) { return null; }   // reserved names, invalid characters, a path too long

        var fence = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(fence, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
