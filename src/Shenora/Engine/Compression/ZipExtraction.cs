using System.IO.Compression;
using Shenora.Engine.Files;

namespace Shenora.Engine.Compression;

/// <summary>What an extraction produced, and what it refused.</summary>
/// <param name="Files">Absolute paths written, in archive order.</param>
/// <param name="Bytes">Total uncompressed bytes written.</param>
/// <param name="Refused">
/// Entries skipped because they would have escaped the destination. ⚠ <b>Non-empty means the archive
/// tried something.</b>
/// </param>
public sealed record ExtractionResult(
    IReadOnlyList<string> Files, long Bytes, IReadOnlyList<string> Refused);

/// <summary>Bounds for one extraction. Every one is a REFUSAL, never a truncation.</summary>
public sealed class ExtractionLimits
{
    /// <summary>
    /// Largest total uncompressed size to write. Default 1 GiB.
    /// <para>
    /// ⚠ The zip-bomb bound, on the TOTAL rather than per entry: a bomb is many small entries, or one that
    /// only looks small until inflated. Exceeding it THROWS.
    /// </para>
    /// </summary>
    public long MaxTotalBytes { get; init; } = 1L << 30;

    /// <summary>Largest number of entries to write. Default 100,000.</summary>
    public int MaxEntries { get; init; } = 100_000;
}

/// <summary>
/// Extracting a ZIP <b>safely</b> — the extraction itself is one framework call. Zip only:
/// <c>System.IO.Compression</c> is in the shared framework, and a native engine for 7z or rar is not
/// something the kit vendors (D42).
/// <para>
/// ⚠ <b>The danger is the entry NAME, not the bytes.</b> An archive is a list of paths chosen by whoever
/// built it, and nothing stops one being <c>../../autoexec.bat</c> or an absolute path — the "zip slip"
/// family. <c>ZipFile.ExtractToDirectory</c> guards this; a hand-rolled loop over <c>archive.Entries</c>
/// (the shape anyone writing progress reporting or filtering ends up with) does not.
/// </para>
/// </summary>
public static class ZipExtraction
{
    /// <summary>
    /// Extract <paramref name="archivePath"/> into <paramref name="destinationDirectory"/>, refusing any
    /// entry that would land outside it.
    /// </summary>
    /// <remarks>
    /// ⚠ A refused entry is SKIPPED and named in <see cref="ExtractionResult.Refused"/>; a caller that
    /// wants an escaping entry to be fatal treats a non-empty list as such. Limits are the opposite —
    /// they THROW, because continuing would write an unknown amount to the caller's disk.
    /// </remarks>
    /// <param name="archivePath">The .zip to read.</param>
    /// <param name="destinationDirectory">Where entries land. Created if absent; nothing may escape it.</param>
    /// <param name="limits">Bounds on the result. Null uses <see cref="ExtractionLimits"/>' defaults.</param>
    /// <param name="overwrite">Replace files that already exist; false (the default) throws on a
    /// collision.</param>
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
    /// <paramref name="destinationDirectory"/>. See the string overload for refusal and limit behaviour.
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

        // The root is resolved ONCE and compared against, never re-derived per entry.
        var root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);

        var written = new List<string>();
        var refused = new List<string>();
        long bytes = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A directory entry: nothing to place. Directories are created by the files inside them, so
            // an archive that omits directory entries still extracts correctly.
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
    /// child of <c>/data</c>.
    /// </para>
    /// <para>
    /// ⚠ Separators are normalised first: a zip written on Linux uses <c>/</c> and a careless tool may use
    /// <c>\</c>, and a check that knew only one would resolve <c>..\..\x</c> as a FILE NAME and let it
    /// through.
    /// </para>
    /// </remarks>
    private static string? Resolve(string root, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return null;

        var relative = entryName.Replace('\\', '/');
        // An absolute or rooted name is refused outright, never "made relative" — stripping the root is a
        // guess at what the archive meant.
        if (Path.IsPathRooted(relative) || relative.StartsWith('/')) return null;

        string full;
        try { full = Path.GetFullPath(Path.Combine(root, relative)); }
        catch (Exception) { return null; }   // reserved names, invalid characters, a path too long

        var fence = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        // Platform-correct (PathComparison): a case-insensitive fence is WIDER than a case-sensitive
        // filesystem, so on Android an entry named `Foo/x` would pass a fence of `…/foo`. An entry name is
        // attacker-influenced, so the fence must never be looser than the OS it protects.
        return full.StartsWith(fence, PathComparison.ForPaths) ? full : null;
    }
}
