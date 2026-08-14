using System.IO.Compression;
using Shenora.Engine.Files;

namespace Shenora.Modules.Update.Compression;

/// <summary>
/// An <see cref="IUpdateSource"/> over one or more ZIP archives — the release shape GitHub Releases
/// encourages, and the one the first adopter publishes.
///
/// <para>
/// <b>The interface needed no change to admit it</b>, which is what made this worth shipping rather than
/// leaving to every adopter: <see cref="IUpdateSource.OpenAsync"/> is
/// <c>ManifestFile → Task&lt;Stream&gt;</c>, and a zip entry is exactly that. Everything genuinely hard —
/// staging, per-file SHA-256 verification before the stage counts as pending, the journal, resume — is
/// already on <see cref="UpdateStage"/>'s side. What was missing was this bridge, which is boring, and
/// several adopters would have written it identically.
/// </para>
///
/// <para>
/// ⚠ <b>It does not DOWNLOAD anything, deliberately.</b> Where the archives come from is the app's — an
/// endpoint, a file share, a bucket, a USB stick — for the same reason <see cref="IUpdateSource"/> itself
/// ships no client: baking one in would drag an HTTP dependency into <c>Shenora</c> and ship a
/// consumer's decision. Hand it archives you already have.
/// </para>
///
/// <para>
/// ⚠ <b>MULTIPLE archives, not one.</b> A release is commonly published as one zip PER PART (a backend, a
/// frontend, a tool) with a single manifest spanning them, so a single-archive implementation would serve
/// half a release and fail the rest. Entries are indexed across every archive at construction.
/// </para>
///
/// <para>
/// ⚠ <b>NOT thread-safe, and that is a property of <see cref="ZipArchive"/> rather than a choice here.</b>
/// It is safe with <see cref="UpdateStage.FetchAsync"/> today because that opens files SEQUENTIALLY — a
/// plain <c>foreach</c> with an <c>await</c> — so only one entry is ever open. Parallelising that loop
/// without giving each worker its own source would corrupt reads rather than merely slow them, which is
/// why this is stated here instead of left to be discovered.
/// </para>
/// </summary>
public sealed class ZipUpdateSource : IUpdateSource, IDisposable
{
    private readonly UpdateManifest _manifest;
    private readonly List<ZipArchive> _archives = [];
    private readonly List<Stream> _owned = [];
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// Index <paramref name="archives"/> against <paramref name="manifest"/>.
    /// </summary>
    /// <param name="manifest">The release manifest. Served as-is by <see cref="GetManifestAsync"/>.</param>
    /// <param name="archives">
    /// The archive streams. ⚠ <b>Each MUST be seekable.</b> A live HTTP response is forward-only, and
    /// <see cref="ZipArchive"/> cannot find the central directory on one — the failure surfaces as an
    /// unhelpful format error, so it is rejected up front instead. Download to a file or a
    /// <see cref="MemoryStream"/> first.
    /// </param>
    /// <param name="leaveOpen">
    /// False (the default) means <see cref="Dispose"/> closes the streams too — the common case, where the
    /// caller opened files purely to hand them over. Pass true to keep ownership.
    /// </param>
    public ZipUpdateSource(UpdateManifest manifest, IEnumerable<Stream> archives, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(archives);
        _manifest = manifest;

        try
        {
            foreach (var stream in archives)
            {
                ArgumentNullException.ThrowIfNull(stream, nameof(archives));
                if (!stream.CanSeek)
                {
                    throw new ArgumentException(
                        "A ZIP archive stream must be seekable — ZipArchive reads the central directory from " +
                        "the END of the file, which a forward-only stream (a live HTTP response) cannot do. " +
                        "Download to a file or a MemoryStream first.", nameof(archives));
                }

                if (!leaveOpen) _owned.Add(stream);
                var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                _archives.Add(archive);
                Index(archive);
            }
        }
        catch
        {
            // A half-built source owns streams nobody else can reach. Disposing here rather than leaving
            // the caller to guess whether the constructor got far enough to take ownership.
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Open <paramref name="archivePaths"/> as files and index them — the ordinary case, where a release
    /// was downloaded to disk before staging.
    /// </summary>
    public static ZipUpdateSource Open(UpdateManifest manifest, params string[] archivePaths)
    {
        ArgumentNullException.ThrowIfNull(archivePaths);
        var streams = new List<Stream>(archivePaths.Length);
        try
        {
            foreach (var path in archivePaths)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(archivePaths));
                streams.Add(File.OpenRead(path));
            }
            return new ZipUpdateSource(manifest, streams);
        }
        catch
        {
            // Only reached if a LATER path fails — the ones already opened would otherwise leak, because
            // the constructor never ran to take ownership of them.
            foreach (var stream in streams) stream.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public Task<UpdateManifest> GetManifestAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_manifest);
    }

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">
    /// The manifest lists a file no archive carries. THROWN rather than returning an empty stream:
    /// <see cref="UpdateStage.FetchAsync"/> lets it escape precisely so a truncated release cannot be
    /// staged as if it were whole, and an empty stream would fail SHA verification with a far worse message.
    /// </exception>
    public Task<Stream> OpenAsync(ManifestFile file, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(Normalize(file.Path), out var entry))
        {
            throw new FileNotFoundException(
                $"The release manifest lists '{file.Path}', which none of the {_archives.Count} archive(s) " +
                "carries. Either an archive is missing from this source or the manifest and the archives " +
                "came from different builds.", file.Path);
        }

        return Task.FromResult(entry.Open());
    }

    /// <summary>
    /// Record every entry, refusing a duplicate.
    /// </summary>
    /// <remarks>
    /// A path carried by two archives is REJECTED rather than last-wins, the same judgement
    /// <see cref="UpdateManifest"/> makes for a duplicate manifest entry: last-wins makes which bytes get
    /// installed depend on the order the archives were passed, which reproduces on some inputs and not
    /// others. Directory entries (a trailing separator, zero length, empty name) are skipped — a zip may
    /// or may not carry them and a manifest never lists them.
    /// </remarks>
    private void Index(ZipArchive archive)
    {
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length == 0 || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                continue;

            var key = Normalize(entry.FullName);
            if (!_entries.TryAdd(key, entry))
            {
                throw new InvalidOperationException(
                    $"Two archives in this source both carry '{entry.FullName}'. Refusing to guess which " +
                    "one the release meant — pass only the archives that make up one release.");
            }
        }
    }

    /// <summary>
    /// One spelling for a path, so a manifest written with backslashes matches a zip entry written with
    /// forward ones.
    /// </summary>
    /// <remarks>
    /// Separators AND case, the same two rules <see cref="ManifestDiff"/> already normalises by — and for
    /// the same reason: without the first, every file looks missing forever; without the second, a
    /// generator that changes one letter's case turns a whole release into "not carried by any archive".
    /// </remarks>
    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var archive in _archives) archive.Dispose();
        _archives.Clear();
        _entries.Clear();
        foreach (var stream in _owned) stream.Dispose();
        _owned.Clear();
    }
}
