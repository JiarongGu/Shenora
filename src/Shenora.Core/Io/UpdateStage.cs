using System.Security.Cryptography;
using System.Text.Json;

namespace Shenora.Core;

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
            var path = Path.Combine(StagedDirectory, file.Path.Replace('\\', Path.DirectorySeparatorChar)
                                                              .Replace('/', Path.DirectorySeparatorChar));
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

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private void Log(string message) => AppCallback.Log(_options.Log, () => message);
}
