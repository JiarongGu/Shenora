using System.IO.Compression;

namespace Shenora.Engine.Update;

/// <summary>
/// An <see cref="IUpdateSource"/> over one or more ZIP archives, indexed across every archive at
/// construction. It does not DOWNLOAD anything — hand it archives you already have.
/// <para>
/// ⚠ <b>NOT thread-safe</b>, a property of <see cref="ZipArchive"/>. Safe with
/// <see cref="UpdateStage.FetchAsync"/> because that opens files SEQUENTIALLY; parallelising that loop
/// without giving each worker its own source CORRUPTS reads.
/// </para>
/// </summary>
public sealed class ZipUpdateSource : IUpdateSource, IDisposable
{
    private readonly UpdateManifest _manifest;
    private readonly List<ZipArchive> _archives = [];
    private readonly List<Stream> _owned = [];
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Index <paramref name="archives"/> against <paramref name="manifest"/>.</summary>
    /// <param name="manifest">The release manifest. Served as-is by <see cref="GetManifestAsync"/>.</param>
    /// <param name="archives">
    /// The archive streams. ⚠ <b>Each MUST be seekable</b> — <see cref="ZipArchive"/> reads the central
    /// directory from the END of the file.
    /// </param>
    /// <param name="leaveOpen">
    /// False (the default) means <see cref="Dispose"/> closes the streams too. Pass true to keep ownership.
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
            // A half-built source owns streams nobody else can reach.
            Dispose();
            throw;
        }
    }

    /// <summary>Open <paramref name="archivePaths"/> as files and index them.</summary>
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
            // A later path failed, so the constructor never ran to take ownership of the earlier ones.
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
    /// The manifest lists a file no archive carries. <see cref="UpdateStage.FetchAsync"/> lets it escape,
    /// so a truncated release cannot be staged.
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

    /// <summary>Record every entry, refusing a duplicate.</summary>
    /// <remarks>
    /// A path carried by two archives is REJECTED rather than last-wins, which would make the installed
    /// bytes depend on the order the archives were passed. Directory entries are skipped — a zip may or
    /// may not carry them and a manifest never lists them.
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
    /// One spelling for a path — separators AND case, the same two rules <see cref="ManifestDiff"/>
    /// normalises by. Diverge from them and a whole release reads as "not carried by any archive".
    /// </summary>
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
