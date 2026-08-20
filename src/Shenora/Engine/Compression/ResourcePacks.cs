using System.Globalization;
using Shenora.Core.WebView;
using Shenora.Engine.Update;

namespace Shenora.Engine.Compression;

/// <summary>
/// Where an app keeps its resource packs.
/// </summary>
public sealed class ResourcePackOptions
{
    /// <summary>
    /// The directory every pack lives under. Each pack gets <c>{Root}/{name}/{version}</c>, so two
    /// versions can exist at once and switching is a path change, never an in-place mutation of files
    /// something may still have open.
    /// </summary>
    public required string Root { get; init; }
}

/// <summary>
/// A named, versioned set of files an app needs ON DISK at runtime — a native binary for the current ABI,
/// a model, a font set, a fixture tree. The kit owns the mechanism; the app supplies the bytes (D42).
/// Containment comes from <see cref="WebViewFiles.ResolveContained"/> and the marker-written-last
/// discipline from <see cref="UpdateStage"/>; neither is re-implemented here.
/// <para>
/// ⚠ <b>The app supplies the archive, and therefore owns its licence</b> (D51) — attribution, relinking
/// and source availability are the app's to discharge, per build.
/// </para>
/// </summary>
public sealed class ResourcePack
{
    /// <summary>
    /// 🔴 Written LAST, after every file is on disk, and its presence is the ONLY thing that means
    /// "usable" — a process killed mid-extraction leaves files and no marker, so the next run re-stages
    /// instead of executing half a binary.
    /// </summary>
    private const string MarkerName = ".ready";

    private readonly ResourcePackOptions _options;

    /// <param name="name">The pack's name. One directory level, so it may not contain a separator.</param>
    /// <param name="version">
    /// The pack's version. Opaque to the kit — a semver string, a build id, a content hash; whatever the
    /// app can compare for equality. Also one directory level.
    /// </param>
    /// <param name="options">Where packs live.</param>
    public ResourcePack(string name, string version, ResourcePackOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Root);

        // Rejected rather than sanitised: rewriting a name would make two different ones collide on
        // one directory.
        if (HasPathSeparator(name)) throw new ArgumentException("A pack name may not contain a path separator.", nameof(name));
        if (HasPathSeparator(version)) throw new ArgumentException("A pack version may not contain a path separator.", nameof(version));

        Name = name;
        Version = version;
    }

    /// <summary>The pack's name.</summary>
    public string Name { get; }

    /// <summary>The pack's version.</summary>
    public string Version { get; }

    /// <summary>Where this exact version lives: <c>{Root}/{name}/{version}</c>.</summary>
    public string Directory => Path.Combine(_options.Root, Name, Version);

    /// <summary>
    /// True when this version is COMPLETE and may be used. False for absent, half-extracted, or
    /// interrupted alike.
    /// </summary>
    public bool IsReady
    {
        get
        {
            try { return File.Exists(Path.Combine(Directory, MarkerName)); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// Resolve a pack-relative path to a real absolute one, or null to refuse.
    /// <para>
    /// ⚠ Refuses when the pack is not <see cref="IsReady"/>, when the file is absent, and when the path
    /// escapes the pack — <b>and it never says which</b>: a distinguishable refusal turns a resolver into
    /// a probe for what exists on the device.
    /// </para>
    /// </summary>
    /// <param name="relativePath">A path inside the pack, e.g. <c>arm64-v8a/libengine.so</c>.</param>
    public string? PathOf(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        if (!IsReady) return null;

        // The kit's ONE containment implementation: refuses `..` before touching the filesystem and
        // compares roots with the separator appended, so `…/pack-evil` cannot pass as a child of `…/pack`.
        var full = WebViewFiles.ResolveContained(Path.Combine(Directory, relativePath), [Directory]);
        if (full is null) return null;

        try { return File.Exists(full) ? full : null; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Put <paramref name="archive"/> on disk as this version, and mark it ready. A no-op returning true
    /// when the version is already <see cref="IsReady"/>, so a caller may call it on every start.
    /// A partially-extracted directory is DISCARDED and re-extracted, never resumed.
    /// </summary>
    /// <param name="archive">
    /// The pack's contents as a zip. The stream is read, not disposed — the caller opened it and owns it.
    /// </param>
    /// <param name="limits">Extraction bounds. Null takes <c>ExtractionLimits</c>' documented defaults.</param>
    /// <param name="cancellationToken">Cancels the extraction; no marker is written, so a cancelled stage
    /// is simply not ready and the next attempt discards what it left.</param>
    /// <returns>True when the pack is ready afterwards.</returns>
    /// <exception cref="InvalidOperationException">
    /// The archive refused entries — a pack that did not fully unpack must not be marked ready.
    /// </exception>
    public async Task<bool> StageAsync(Stream archive, ExtractionLimits? limits = null,
                                       CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (IsReady) return true;

        var directory = Directory;
        Discard(directory);
        System.IO.Directory.CreateDirectory(directory);

        using var zip = new System.IO.Compression.ZipArchive(archive, System.IO.Compression.ZipArchiveMode.Read,
                                                             leaveOpen: true);
        var result = ZipExtraction.ExtractTo(zip, directory, limits, overwrite: true, cancellationToken);

        // REFUSED entries are fatal here, unlike a general-purpose extraction: a pack is used as a UNIT,
        // and a binary whose sibling library was refused is a broken pack that fails later and elsewhere.
        if (result.Refused.Count > 0)
        {
            Discard(directory);
            throw new InvalidOperationException(
                $"Resource pack '{Name}' {Version} refused {result.Refused.Count} entr(y|ies) during extraction; " +
                "nothing was staged. An archive whose paths escape the pack directory is not usable.");
        }

        // LAST, and only now.
        await File.WriteAllTextAsync(Path.Combine(directory, MarkerName),
            string.Create(CultureInfo.InvariantCulture, $"{Name} {Version} {result.Files.Count} {result.Bytes}"),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Delete every OTHER version of this pack, and report how many went. Separate from
    /// <see cref="StageAsync"/> because the old version is usually still LOADED when the new one is
    /// staged — a mapped <c>.so</c>, a running process — so the safe moment to collect is the next start.
    /// Never throws: a version that will not delete is left for the next attempt.
    /// </summary>
    public int PruneOthers()
    {
        var family = Path.Combine(_options.Root, Name);
        var pruned = 0;
        try
        {
            if (!System.IO.Directory.Exists(family)) return 0;
            foreach (var candidate in System.IO.Directory.EnumerateDirectories(family))
            {
                if (string.Equals(Path.GetFileName(candidate), Version, StringComparison.Ordinal)) continue;
                try
                {
                    System.IO.Directory.Delete(candidate, recursive: true);
                    pruned++;
                }
                catch (Exception) { /* in use or not ours to delete — try again next start */ }
            }
        }
        catch (Exception) { /* the family directory vanished under us; nothing to collect */ }
        return pruned;
    }

    /// <summary>Remove a directory if it is there, swallowing the "it was not" case.</summary>
    private static void Discard(string directory)
    {
        try
        {
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (Exception) { /* best effort — CreateDirectory + overwrite extraction covers the rest */ }
    }

    private static bool HasPathSeparator(string value) =>
        value.Contains('/', StringComparison.Ordinal)
        || value.Contains('\\', StringComparison.Ordinal)
        || value.Contains(Path.DirectorySeparatorChar)
        || value.Contains(Path.AltDirectorySeparatorChar);
}
