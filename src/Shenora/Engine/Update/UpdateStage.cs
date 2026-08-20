using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shenora.Core.WebView;
using Shenora.Engine.Files;

namespace Shenora.Engine.Update;

/// <summary>
/// Where releases come from — the SEAM. The kit ships ONE implementation,
/// <see cref="ZipUpdateSource"/>, over archives you already have; there is no downloader.
/// </summary>
public interface IUpdateSource
{
    /// <summary>The manifest describing the release this source offers.</summary>
    Task<UpdateManifest> GetManifestAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Open one file's content for reading; the caller disposes the stream.
    /// <para>
    /// ⚠ <b>THROW for a file that cannot be fetched.</b> Returning an empty stream stages a truncated
    /// release as if it were whole.
    /// </para>
    /// </summary>
    Task<Stream> OpenAsync(ManifestFile file, CancellationToken cancellationToken = default);
}

/// <summary>Inputs for <see cref="UpdateStage"/>.</summary>
public sealed class UpdateStageOptions
{
    /// <summary>
    /// The staging root, conventionally <c>{installRoot}/.update</c>. Must be somewhere the app can write
    /// and the APPLIER can read.
    /// </summary>
    public required string Root { get; init; }

    /// <summary>
    /// Which staged paths a clean release legitimately carries that the manifest does NOT index —
    /// receives a manifest-relative, forward-slashed path and returns true to exempt it from the
    /// intrusion check. Default: nothing is exempt beyond the kit's own <c>manifest.json</c>.
    /// <para>⚠ Too loose lets an injected file through; too STRICT rejects every honest download.</para>
    /// </summary>
    public Func<string, bool>? IsUnindexed { get; init; }

    /// <summary>
    /// Where <see cref="UpdateStage.ApplyAsync"/> keeps the baseline manifest — the record of what is
    /// currently installed, which the next apply diffs against to compute REMOVALS. Null (default) means
    /// <c>{installRoot}/manifest.json</c>; a relative path resolves against the install root.
    /// <para>
    /// ⚠ Lose the file and the next apply removes nothing: stale files stay behind, silently. Point it
    /// outside the root and <b>whatever reads the baseline must look here too</b>.
    /// </para>
    /// </summary>
    public string? BaselinePath { get; init; }

    /// <summary>Diagnostics sink.</summary>
    public ILogger? Log { get; init; }
}

/// <summary>What an <see cref="UpdateStage.ApplyAsync"/> pass did, or why it did nothing.</summary>
public sealed class UpdateOutcome
{
    /// <summary>True when the stage was overlaid onto the install and cleared.</summary>
    public required bool Applied { get; init; }

    /// <summary>The version applied, when <see cref="Applied"/>.</summary>
    public string? Version { get; init; }

    /// <summary>Manifest-relative paths written (added or replaced).</summary>
    public IReadOnlyList<string> Written { get; init; } = [];

    /// <summary>Manifest-relative paths deleted because the new manifest dropped them.</summary>
    public IReadOnlyList<string> Removed { get; init; } = [];

    /// <summary>Why nothing was applied, when <see cref="Applied"/> is false. Null on success.</summary>
    public string? Failure { get; init; }
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
/// The staging half of a two-phase update (D57): an app fetches the changed files
/// (<see cref="ManifestDiff"/>) into <see cref="StagedDirectory"/>, then calls
/// <see cref="CommitAsync"/>. The on-disk layout, the write order and the failure grading are a
/// supported contract, stated in <c>docs/design/update.md</c>.
/// <para>
/// 🔴 <b>DESKTOP ONLY.</b> This manages the install tree beside <c>Shenora.Launcher</c>; a mobile app
/// updates through its store. The scope is load-bearing: <see cref="ManifestDiff"/> compares paths
/// CASE-INSENSITIVELY, mirroring the launcher's C++ <c>manifest.hpp</c> — one side alone cannot change
/// it — and on a case-sensitive filesystem that widens the "nothing unlisted" check, so a staged
/// <c>Foo.dll</c> reads as listed when the manifest says <c>foo.dll</c>.
/// </para>
/// </summary>
public sealed class UpdateStage
{
    private const string MarkerName = "ready.json";
    private const string StagedFolder = "staged";

    /// <summary>
    /// The full release manifest carried inside the stage, for the applier's removals. Lower-case: it is
    /// compared through <see cref="ManifestDiff.Normalize"/>.
    /// </summary>
    private const string StagedManifestName = "manifest.json";

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
    /// Is a verified stage waiting? Reads the marker only, so it is cheap enough for a UI to poll. A
    /// marker that cannot be parsed reports NOT pending.
    /// </summary>
    public UpdateStageStatus GetStatus()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return new UpdateStageStatus { Pending = false };
            var marker = JsonSerializer.Deserialize<UpdateStageStatus>(File.ReadAllText(MarkerPath), Json);
            if (marker is null || string.IsNullOrWhiteSpace(marker.Version))
            {
                Log($"[Shenora.Engine.Update] Ignoring an unreadable staging marker at '{MarkerPath}'.");
                return new UpdateStageStatus { Pending = false };
            }
            return marker;
        }
        catch (Exception ex)
        {
            Log($"[Shenora.Engine.Update] Could not read the staging marker: {ex.GetType().Name}");
            return new UpdateStageStatus { Pending = false };
        }
    }

    /// <summary>
    /// Clear any previous stage and create an empty <see cref="StagedDirectory"/>. ⚠ Call before
    /// downloading: leftovers from a failed attempt would be verified as part of this one and pass.
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
    /// Verify every file <paramref name="manifest"/> lists against what is on disk, then publish the
    /// marker. On ANY failure nothing is published and the stage stays unusable rather than half-usable.
    /// <para>
    /// ⚠ <paramref name="manifest"/> is the CHANGESET, not the release: an empty one is legitimate and is
    /// accepted, because it is what a release whose only change is DELETING files stages.
    /// </para>
    /// </summary>
    public async Task<UpdateStageStatus> CommitAsync(UpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveTracked(StagedDirectory, file.Path);
            if (path is null)
            {
                Log($"[Shenora.Engine.Update] Stage refused: '{file.Path}' is not a contained relative path.");
                return new UpdateStageStatus { Pending = false };
            }

            if (!File.Exists(path))
            {
                Log($"[Shenora.Engine.Update] Stage incomplete: '{file.Path}' is missing.");
                return new UpdateStageStatus { Pending = false };
            }

            var actual = await Sha256Async(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Log($"[Shenora.Engine.Update] Stage corrupt: '{file.Path}' does not match its manifest hash.");
                return new UpdateStageStatus { Pending = false };
            }
        }

        // INTRUSION: ApplyAsync overlays the staged TREE, not the manifest, so without this a file
        // nothing verified reaches the install root.
        if (!VerifyNothingUnlisted(manifest, cancellationToken))
            return new UpdateStageStatus { Pending = false };

        // A CHECK, never a write: `manifest` is the changeset, the file is the FULL release manifest.
        // Writing the changeset there tells the applier everything else was removed.
        if (!StagedManifestIsUsable())
            return new UpdateStageStatus { Pending = false };

        // LAST: the marker's existence is the promise that an applier need not re-check.
        var status = new UpdateStageStatus
        {
            Pending = true,
            Version = manifest.Version,
            StagedAt = DateTimeOffset.UtcNow,
        };
        Directory.CreateDirectory(_options.Root);
        File.WriteAllText(MarkerPath, JsonSerializer.Serialize(status, Json));
        Log($"[Shenora.Engine.Update] Staged {manifest.Files.Count} file(s) for version {manifest.Version}.");
        return status;
    }

    /// <summary>
    /// The whole download-and-stage phase: ask the source what it has, diff it against what is
    /// installed, fetch only the changed files, and commit. Returns a non-pending status when there
    /// is nothing to do; a fetch that throws is left to escape and nothing is staged.
    /// </summary>
    public async Task<UpdateStageStatus> FetchAsync(IUpdateSource source, UpdateManifest installed,
                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(installed);

        var release = await source.GetManifestAsync(cancellationToken).ConfigureAwait(false);
        if (release is null || release.Files.Count == 0)
        {
            throw new InvalidOperationException(
                "The release manifest is empty or missing, which would diff to removing every installed " +
                "file. Refusing to stage — check the source.");
        }

        var diff = ManifestDiff.Compute(installed, release);
        if (diff.Added.Count == 0 && diff.Updated.Count == 0 && diff.Removed.Count == 0)
        {
            Log($"[Shenora.Engine.Update] Already at version {release.Version}; nothing to stage.");
            return new UpdateStageStatus { Pending = false };
        }

        // 🔴 REMOVALS ALONE STILL STAGE, and downloading nothing is not the same as having nothing to do.
        // The apply pass is driven by `staged/manifest.json`, written below, not by the payload.
        if (diff.Added.Count == 0 && diff.Updated.Count == 0)
        {
            Log($"[Shenora.Engine.Update] Nothing to download for version {release.Version} " +
                $"({diff.Removed.Count} removal(s) only) — staging the apply pass anyway.");
        }

        Begin();
        var staged = new List<ManifestFile>(diff.Added.Count + diff.Updated.Count);
        foreach (var file in diff.Added.Concat(diff.Updated))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Re-checked at the line that creates a file: a write path must not rely on a caller's checks.
            var destination = ResolveTracked(StagedDirectory, file.Path)
                ?? throw new InvalidOperationException(
                    $"The release manifest lists '{file.Path}', which is not a contained relative path.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using (var content = await source.OpenAsync(file, cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(destination))
            {
                await content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
            staged.Add(file);
        }

        // The applier's copy of the new baseline — not a payload file, so CommitAsync does not verify it.
        await File.WriteAllTextAsync(Path.Combine(StagedDirectory, StagedManifestName), release.ToJson(),
            cancellationToken).ConfigureAwait(false);

        return await CommitAsync(new UpdateManifest { Version = release.Version, Files = staged },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Apply a staged update: overlay it onto <paramref name="installRoot"/>, delete what the new
    /// manifest dropped, and clear the stage.
    /// <para>
    /// 🔴 <b>Run this from OUTSIDE <paramref name="installRoot"/>, with the app not running</b> (D50).
    /// </para>
    /// </summary>
    public async Task<UpdateOutcome> ApplyAsync(string installRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        var status = GetStatus();
        if (!status.Pending) return new UpdateOutcome { Applied = false, Failure = "nothing is staged" };

        // Read BOTH manifests first: the overlay overwrites the installed one, and the removal set is
        // the difference between them.
        var stagedManifestPath = Path.Combine(StagedDirectory, StagedManifestName);
        UpdateManifest? release = null;
        try
        {
            if (File.Exists(stagedManifestPath)) release = UpdateManifest.Parse(File.ReadAllText(stagedManifestPath));
        }
        catch (Exception ex)
        {
            Log($"[Shenora.Engine.Update] The staged manifest could not be read: {ex.GetType().Name}");
        }

        if (release is null || release.Files.Count == 0)
        {
            return new UpdateOutcome
            {
                Applied = false,
                Failure = "the staged manifest is missing or empty, so removals cannot be computed safely",
            };
        }

        var baselinePath = ResolveBaselinePath(installRoot);
        UpdateManifest installed = new() { Version = "", Files = [] };
        try
        {
            if (File.Exists(baselinePath))
                installed = UpdateManifest.Parse(File.ReadAllText(baselinePath));
        }
        catch (Exception ex)
        {
            // A first install, or a corrupt baseline. Either way: overlay, remove NOTHING.
            Log($"[Shenora.Engine.Update] No usable installed manifest ({ex.GetType().Name}) — applying without removals.");
        }

        var written = new List<string>();
        foreach (var source in Directory.EnumerateFiles(StagedDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(StagedDirectory, source);
            // Skipped here and written EXPLICITLY below: a configured BaselinePath would otherwise leave
            // a stray second copy at {installRoot}/manifest.json.
            if (ManifestDiff.Normalize(relative) == StagedManifestName) continue;
            var destination = Path.Combine(installRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            written.Add(relative.Replace('\\', '/'));
        }

        // The new baseline, written even outside the tree since the next apply computes removals from it.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            File.Copy(stagedManifestPath, baselinePath, overwrite: true);
            if (IsUnder(baselinePath, installRoot))
                written.Add(Path.GetRelativePath(installRoot, baselinePath).Replace('\\', '/'));
        }
        catch (Exception ex)
        {
            // The payload is already overlaid; a missing baseline degrades to "remove nothing next time".
            Log($"[Shenora.Engine.Update] The payload applied but the baseline could not be written to " +
                $"'{baselinePath}' ({ex.GetType().Name}) — the next apply will compute no removals.");
        }

        var removed = new List<string>();
        foreach (var path in ManifestDiff.Compute(installed, release).Removed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The BASELINE drives this loop and was itself written from a staged manifest, so a manifest
            // that escaped once would delete outside the install root on the NEXT update. Skipped rather
            // than thrown: abandoning here would leave a half-applied install.
            var target = ResolveTracked(installRoot, path);
            if (target is null)
            {
                Log($"[Shenora.Engine.Update] Removal refused: '{path}' is not a contained relative path.");
                continue;
            }

            try
            {
                if (File.Exists(target)) { File.Delete(target); removed.Add(path); }
            }
            catch (Exception ex)
            {
                Log($"[Shenora.Engine.Update] Could not remove '{path}': {ex.GetType().Name}");
            }
        }

        Clear();
        Log($"[Shenora.Engine.Update] Applied version {release.Version}: {written.Count} written, {removed.Count} removed.");
        return new UpdateOutcome
        {
            Applied = true,
            Version = release.Version,
            Written = written,
            Removed = removed,
        };
    }

    /// <summary>
    /// Where the baseline manifest lives for this install root — <see cref="UpdateStageOptions.BaselinePath"/>
    /// or the <c>{installRoot}/manifest.json</c> default.
    /// <para>
    /// ⚠ <see cref="Path.GetFullPath(string, string)"/>, never <see cref="Path.Combine(string, string)"/>:
    /// Combine SILENTLY DISCARDS its first argument when the second is rooted.
    /// </para>
    /// </summary>
    internal string ResolveBaselinePath(string installRoot) =>
        _options.BaselinePath is { Length: > 0 } configured
            ? Path.GetFullPath(configured, Path.GetFullPath(installRoot))
            : Path.Combine(Path.GetFullPath(installRoot), StagedManifestName);

    /// <summary>
    /// Whether <paramref name="path"/> sits inside <paramref name="root"/>. Only decides whether the
    /// baseline is reported in <see cref="UpdateOutcome.Written"/>; it is not a security boundary.
    /// The separator is appended before comparing, as <see cref="WebViewFiles.ResolveContained"/> does:
    /// without it <c>/app-old</c> passes as a child of <c>/app</c>.
    /// </summary>
    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(root);
        var prefix = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, PathComparison.ForPaths);
    }

    /// <summary>
    /// Is the full release manifest present in the stage, readable, and non-empty? Empty counts as
    /// unusable: an applier reads it as "everything was removed".
    /// </summary>
    private bool StagedManifestIsUsable()
    {
        var path = Path.Combine(StagedDirectory, StagedManifestName);
        if (!File.Exists(path))
        {
            Log($"[Shenora.Engine.Update] Stage incomplete: '{StagedManifestName}' is not in the stage. It is the full " +
                "release manifest, and ApplyAsync computes REMOVALS from it. FetchAsync writes it for you; " +
                "an app staging by other means must write it itself.");
            return false;
        }

        try
        {
            var release = UpdateManifest.Parse(File.ReadAllText(path));
            if (release.Files.Count != 0) return true;
            Log($"[Shenora.Engine.Update] Stage rejected: '{StagedManifestName}' lists no files, which an applier reads " +
                "as 'the release removed everything'.");
        }
        catch (Exception ex)
        {
            Log($"[Shenora.Engine.Update] Stage rejected: '{StagedManifestName}' could not be read ({ex.GetType().Name}).");
        }
        return false;
    }

    /// <summary>
    /// The staged tree must contain NOTHING the manifest does not list. Returns false (and logs the
    /// offending path) when it does; the kit's own <c>manifest.json</c> is always exempt.
    /// </summary>
    private bool VerifyNothingUnlisted(UpdateManifest manifest, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(StagedDirectory)) return true;

        var listed = manifest.Files
            .Select(f => ManifestDiff.Normalize(f.Path))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(StagedDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = ManifestDiff.Normalize(Path.GetRelativePath(StagedDirectory, file));
            if (listed.Contains(relative)) continue;
            if (relative == StagedManifestName) continue;         // the kit's own, never listed
            if (_options.IsUnindexed?.Invoke(relative) == true) continue;

            Log($"[Shenora.Engine.Update] Stage rejected: '{relative}' is present but the manifest does not " +
                "list it. If a clean release legitimately carries it, exempt it with " +
                $"{nameof(UpdateStageOptions)}.{nameof(UpdateStageOptions.IsUnindexed)}.");
            return false;
        }
        return true;
    }

    /// <summary>Manifest paths are forward-slashed; the disk wants this platform's separator.</summary>
    private static string Relative(string manifestPath) =>
        manifestPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// Turn a manifest path into an absolute one under <paramref name="root"/>, or null to refuse.
    /// <para>
    /// 🔴 <b>The ONE place a manifest path becomes a filesystem path</b> — every write, existence check
    /// and delete in this type goes through here. <see cref="ManifestDiff.IsSafeRelativePath"/> refuses
    /// at diff time; this is the second line, for paths reaching a file operation WITHOUT a diff (a
    /// manifest handed straight to <see cref="CommitAsync"/>). Containment is
    /// <see cref="PathClaims.IsContained"/>, under its platform case rule.
    /// </para>
    /// </summary>
    private static string? ResolveTracked(string root, string manifestPath)
    {
        if (!ManifestDiff.IsSafeRelativePath(manifestPath)) return null;

        string full;
        try
        {
            full = Path.GetFullPath(Relative(manifestPath), Path.GetFullPath(root));
        }
        catch (Exception)
        {
            return null;   // malformed: invalid characters, too long, or a reserved name
        }

        return PathClaims.IsContained(root, full) ? full : null;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private void Log(string message) => AppCallback.Log(_options.Log, () => message);
}
