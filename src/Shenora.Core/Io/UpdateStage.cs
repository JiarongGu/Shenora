using System.Security.Cryptography;
using System.Text.Json;

namespace Shenora.Core;

/// <summary>
/// Where releases come from — the SEAM, and the kit ships no implementation of it.
/// <para>
/// Both donor apps fetch from GitHub releases, and that is one instance of "somewhere to get a
/// manifest and some files from", not the shape. An app may serve updates from its own endpoint, a
/// file share, an S3 bucket or a USB stick; baking a client for any of them in would ship a
/// consumer's decision and drag an HTTP dependency into <c>Shenora.Core</c>
/// (`generic-library.md`: ship the mechanism, leave the product).
/// </para>
/// <para>
/// It is deliberately two methods. Anything more — release notes, channels, signatures, rollout
/// percentages — is a product decision the kit has no business having.
/// </para>
/// </summary>
public interface IUpdateSource
{
    /// <summary>The manifest describing the release this source offers.</summary>
    Task<UpdateManifest> GetManifestAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Open one file's content for reading. The caller disposes the stream. Throwing is the right
    /// answer for a file that cannot be fetched — <see cref="UpdateStage.FetchAsync"/> lets it
    /// escape, because a partial download must not be staged as if it were whole.
    /// </summary>
    Task<Stream> OpenAsync(ManifestFile file, CancellationToken cancellationToken = default);
}

/// <summary>Inputs for <see cref="UpdateStage"/>.</summary>
public sealed class UpdateStageOptions
{
    /// <summary>
    /// The staging root, conventionally <c>{installRoot}/.update</c>. It must be somewhere the app
    /// can write and the APPLIER can read — on the desktop that is the install root, never a temp
    /// folder the applier has no reason to look in.
    /// </summary>
    public required string Root { get; init; }

    /// <summary>Diagnostics sink.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>Whether a verified update is staged and waiting to be applied.</summary>
public sealed class UpdateStageStatus
{
    /// <summary>True when a complete, verified stage is waiting.</summary>
    public required bool Pending { get; init; }

    /// <summary>The staged version, when <see cref="Pending"/>.</summary>
    public string? Version { get; init; }

    /// <summary>When the stage was completed, when <see cref="Pending"/>.</summary>
    public DateTimeOffset? StagedAt { get; init; }
}

/// <summary>
/// The staging half of a two-phase update: a running process cannot replace its own executable, so
/// the app downloads and VERIFIES while it is alive and something that runs before it applies the
/// result (<c>docs/2026-08-02-shenora-app-update-design.md</c>).
/// <para>
/// The kit owns the PROTOCOL, not the download. An app fetches the changed files
/// (<see cref="ManifestDiff"/>) into <see cref="StagedDirectory"/> however it likes — HTTP, a
/// share, a USB stick — and then calls <see cref="CommitAsync"/>, which verifies every file and only
/// then publishes the marker.
/// </para>
/// <para>
/// <b>The ordering IS the property.</b> <c>ready.json</c> is written LAST, after every file in the
/// manifest has matched its hash, so the marker means "this stage is complete and verified" and an
/// applier never has to re-check. A crash mid-download leaves files but no marker, and the next run
/// restages — which is why the marker is the only thing an applier may trust.
/// </para>
/// </summary>
public sealed class UpdateStage
{
    private const string MarkerName = "ready.json";
    private const string StagedFolder = "staged";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly UpdateStageOptions _options;

    /// <summary>Wrap a staging root. Nothing touches the disk until a method is called.</summary>
    public UpdateStage(UpdateStageOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Root);
    }

    /// <summary>Where the app writes downloaded files, mirroring their manifest-relative paths.</summary>
    public string StagedDirectory => Path.Combine(_options.Root, StagedFolder);

    private string MarkerPath => Path.Combine(_options.Root, MarkerName);

    /// <summary>
    /// Is a verified stage waiting? Reads the marker only — deliberately cheap, because a UI asks
    /// this on every settings screen. A marker that cannot be parsed reports NOT pending: a stage
    /// nobody can describe is not one an applier should act on.
    /// </summary>
    public UpdateStageStatus GetStatus()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return new UpdateStageStatus { Pending = false };
            var marker = JsonSerializer.Deserialize<UpdateStageStatus>(File.ReadAllText(MarkerPath), Json);
            if (marker is null || string.IsNullOrWhiteSpace(marker.Version))
            {
                Log($"[Shenora.Core] Ignoring an unreadable staging marker at '{MarkerPath}'.");
                return new UpdateStageStatus { Pending = false };
            }
            return marker;
        }
        catch (Exception ex)
        {
            Log($"[Shenora.Core] Could not read the staging marker: {ex.GetType().Name}");
            return new UpdateStageStatus { Pending = false };
        }
    }

    /// <summary>
    /// Clear any previous stage and create an empty <see cref="StagedDirectory"/>.
    /// <para>
    /// Call before downloading. Starting from a CLEAN directory is what stops a half-finished
    /// earlier attempt from being verified as part of this one — leftovers from a stage that failed
    /// after three of ten files would otherwise sit there looking exactly like success.
    /// </para>
    /// </summary>
    public void Begin()
    {
        Clear();
        Directory.CreateDirectory(StagedDirectory);
    }

    /// <summary>Delete the whole staging area, marker included. Safe when nothing is staged.</summary>
    public void Clear()
    {
        if (Directory.Exists(_options.Root)) Directory.Delete(_options.Root, recursive: true);
    }

    /// <summary>
    /// Verify every file the manifest lists against what is on disk, then publish the marker.
    /// Returns the resulting status; on ANY failure nothing is published and the stage stays
    /// unusable rather than half-usable.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The manifest lists no files. This is <see cref="ManifestDiff"/>'s deferred guard arriving:
    /// an empty manifest tells an applier to delete every tracked path, so a manifest that "loaded"
    /// to nothing would destroy the install as the successful outcome of an update. Refused HERE,
    /// where a manifest first meets the disk.
    /// </exception>
    public async Task<UpdateStageStatus> CommitAsync(UpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Files.Count == 0)
        {
            throw new ArgumentException(
                "The staged manifest lists no files. An empty manifest tells an applier to remove every " +
                "tracked path, so this is refused rather than staged — check that the manifest actually " +
                "parsed.", nameof(manifest));
        }

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(StagedDirectory, Relative(file.Path));
            if (!File.Exists(path))
            {
                Log($"[Shenora.Core] Stage incomplete: '{file.Path}' is missing.");
                return new UpdateStageStatus { Pending = false };
            }

            var actual = await Sha256Async(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // The hash is authoritative, not the size — a truncated download can coincidentally
                // match a size and never matches a hash. The message names the file and NOT the
                // hashes: a mismatch is a corrupt download, and dumping 128 hex characters into a log
                // buries that.
                Log($"[Shenora.Core] Stage corrupt: '{file.Path}' does not match its manifest hash.");
                return new UpdateStageStatus { Pending = false };
            }
        }

        // LAST, and only now. Everything above verified, so the marker's existence is the promise
        // that an applier can act without re-checking.
        var status = new UpdateStageStatus
        {
            Pending = true,
            Version = manifest.Version,
            StagedAt = DateTimeOffset.UtcNow,
        };
        Directory.CreateDirectory(_options.Root);
        File.WriteAllText(MarkerPath, JsonSerializer.Serialize(status, Json));
        Log($"[Shenora.Core] Staged {manifest.Files.Count} file(s) for version {manifest.Version}.");
        return status;
    }

    /// <summary>
    /// The whole download-and-stage phase: ask the source what it has, diff it against what is
    /// installed, fetch only the changed files, and commit. Returns a non-pending status when there
    /// is nothing to do.
    /// <para>
    /// <b>Only the CHANGESET is staged, and that is why <see cref="CommitAsync"/> takes the manifest
    /// of what is IN the stage rather than the release manifest.</b> A differential update downloads
    /// added and updated files only; verifying the full release manifest against a partial stage
    /// would fail on every unchanged file. The full release manifest is written into the stage as
    /// <c>manifest.json</c> instead — an applier needs it to compute REMOVALS, and overlaying the
    /// stage makes it the newly-installed manifest, which is exactly how both donor apps carry the
    /// baseline forward.
    /// </para>
    /// <para>
    /// A fetch that throws is left to escape: a partial download must not be staged as though it
    /// were whole, and the absent marker already means "unusable".
    /// </para>
    /// </summary>
    public async Task<UpdateStageStatus> FetchAsync(IUpdateSource source, UpdateManifest installed,
                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(installed);

        var release = await source.GetManifestAsync(cancellationToken).ConfigureAwait(false);
        if (release is null || release.Files.Count == 0)
        {
            // The same refusal CommitAsync makes, moved as early as it can be made: an empty release
            // manifest diffs to "remove everything installed".
            throw new InvalidOperationException(
                "The release manifest is empty or missing, which would diff to removing every installed " +
                "file. Refusing to stage — check the source.");
        }

        var diff = ManifestDiff.Compute(installed, release);
        if (diff.Added.Count == 0 && diff.Updated.Count == 0)
        {
            // Removals alone still need an apply pass, but nothing to DOWNLOAD means nothing to
            // verify, and staging an empty directory would trip CommitAsync's own guard. Report
            // honestly rather than manufacturing a stage.
            Log($"[Shenora.Core] Nothing to download for version {release.Version}" +
                (diff.Removed.Count > 0 ? $" ({diff.Removed.Count} removal(s) only)." : "."));
            return new UpdateStageStatus { Pending = false };
        }

        Begin();
        var staged = new List<ManifestFile>(diff.Added.Count + diff.Updated.Count);
        foreach (var file in diff.Added.Concat(diff.Updated))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(StagedDirectory, Relative(file.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using (var content = await source.OpenAsync(file, cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(destination))
            {
                await content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
            staged.Add(file);
        }

        // The applier's copy of the new baseline. Not part of the staged manifest, so CommitAsync
        // does not verify it — it is not a payload file, it is the record of what the payload means.
        await File.WriteAllTextAsync(Path.Combine(StagedDirectory, "manifest.json"), release.ToJson(),
            cancellationToken).ConfigureAwait(false);

        return await CommitAsync(new UpdateManifest { Version = release.Version, Files = staged },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Manifest paths are forward-slashed; the disk wants this platform's separator.</summary>
    private static string Relative(string manifestPath) =>
        manifestPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private void Log(string message) => AppCallback.Log(_options.Log, () => message);
}
