using System.Security.Cryptography;
using System.Text.Json;
using Shenora.Core.WebView;
using Shenora.Engine.Files;

using Shenora;

namespace Shenora.Modules.Update;

/// <summary>
/// Where releases come from — the SEAM, and the kit ships no implementation of it.
/// <para>
/// Both donor apps fetch from GitHub releases, and that is one instance of "somewhere to get a
/// manifest and some files from", not the shape. An app may serve updates from its own endpoint, a
/// file share, an S3 bucket or a USB stick; baking a client for any of them in would ship a
/// consumer's decision and drag an HTTP dependency into <c>Shenora</c>
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

    /// <summary>
    /// Which staged paths a clean release legitimately carries that the manifest deliberately does NOT
    /// index — receives a manifest-relative, forward-slashed path and returns true to exempt it from
    /// the intrusion check. Default: nothing is exempt beyond the kit's own <c>manifest.json</c>.
    /// <para>
    /// <b>This is a predicate rather than a list because the answer is a property of whatever GENERATED
    /// the manifest, not of the kit.</b> Real examples from an adopter: a bundled data folder, a seeded
    /// checksum stamp (indexing it would be circular), a version file that changes every release. Baking
    /// that set in would freeze one app's packaging policy into everyone's verifier —
    /// <c>generic-library.md</c>'s rule, applied to a case where it is tempting to just hardcode it.
    /// </para>
    /// <para>
    /// ⚠ <b>Getting this wrong fails in the INVERTED direction, so validate it against a real
    /// release.</b> Too loose lets an injected file through; too STRICT rejects every honest download —
    /// and the second is worse, because it breaks for every user at once rather than for an attacker.
    /// Synthetic fixtures will not catch it: the tester writes both sides and they agree by
    /// construction. Stage an actual published release and check the counts.
    /// </para>
    /// </summary>
    public Func<string, bool>? IsUnindexed { get; init; }

    /// <summary>
    /// Where <see cref="UpdateStage.ApplyAsync"/> keeps the baseline manifest — the record of what is
    /// currently installed, which the next apply diffs against to compute REMOVALS. Null (default) means
    /// <c>{installRoot}/manifest.json</c>, so an ordinary app install needs nothing here. A relative path is
    /// resolved against the install root; a rooted one is used as given.
    /// <para>
    /// <b>It is a parameter because "the baseline belongs with the thing it describes" is only true of an
    /// INSTALL TREE.</b> Filed by the first adopter, whose targets are deploy INPUTS: two directories whose
    /// aggregate content hash decides what gets re-uploaded, hashed with no exclusions on purpose so the
    /// figure agrees with the build's own. A per-release <c>manifest.json</c> inside such a tree changes that
    /// hash on every release even when the payload is byte-identical — so *"did this actually change?"*
    /// answers yes forever and an unchanged part takes the slow path every time. Their invariant is that a
    /// part's content is a pure function of SOURCE, never of build HISTORY, and the kit was writing history
    /// into it.
    /// </para>
    /// <para>
    /// ⚠ Point it outside the root and the install tree no longer carries its own baseline, so **whatever
    /// reads it must look here too** — the apply pass is the only thing in the kit that does, and
    /// <see cref="UpdateStage.FetchAsync"/> is handed the installed manifest by the caller rather than
    /// reading one. Lose the file and the next apply removes nothing (it cannot know what to remove), which
    /// is the safe direction but leaves stale files behind.
    /// </para>
    /// </summary>
    public string? BaselinePath { get; init; }

    /// <summary>Diagnostics sink.</summary>
    public Action<string>? Log { get; init; }
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

    /// <summary>
    /// Why nothing was applied, when <see cref="Applied"/> is false. Null on success. A REASON
    /// rather than an exception because "there was no update" is the common case, not a fault.
    /// </summary>
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
/// The staging half of a two-phase update: a running process cannot replace its own executable, so
/// the app downloads and VERIFIES while it is alive and something that runs before it applies the
/// result (<c>docs/DECISIONS.md</c> D57).
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
/// <para><b>THE ON-DISK LAYOUT IS A SUPPORTED CONTRACT, not an implementation detail.</b></para>
/// <code>
/// {UpdateStageOptions.Root}/
///   ready.json          ← the MARKER. Written LAST. Its existence means "complete and verified".
///   staged/
///     manifest.json     ← the full release manifest, for the applier's REMOVALS
///     &lt;every changed file, at its manifest-relative path&gt;
/// </code>
/// <para>
/// <c>ready.json</c> is <see cref="UpdateStageStatus"/> as camelCase JSON —
/// <c>{"pending":true,"version":"1.4.0","stagedAt":"2026-08-06T09:12:33.4+00:00"}</c>. An applier that
/// only needs "is there an update" may test for the file's existence and nothing more.
/// </para>
/// <para>
/// It is stated because <b>staging and applying are SEPARATELY adoptable, and for some apps only one of
/// them ever will be</b> (first adopter, 2026-08-06). An app whose applier is a native launcher living
/// beside the install — never replaced by its own updates, so every copy in the field keeps the applier it
/// shipped with — can adopt this half only down to the marker. That app answered "can our frozen applier
/// consume a kit-produced stage?" by reading these three names out of the source, which is a question that
/// should be answerable from the documentation. It can now.
/// </para>
/// <para>
/// ⚠ <b>What that means for changing any of it.</b> These three names and the write ORDER are now a
/// compatibility surface with appliers this repo did not write and cannot recompile. Renaming
/// <c>ready.json</c>, moving <c>staged/</c>, or publishing the marker before verification completes are
/// breaking changes to an audience the API baseline cannot see — treat them as such.
/// </para>
/// </summary>
public sealed class UpdateStage
{
    private const string MarkerName = "ready.json";
    private const string StagedFolder = "staged";

    /// <summary>
    /// The release manifest that rides along inside the stage for the applier's removals. Named once
    /// because THREE things depend on it agreeing: <see cref="FetchAsync"/> writes it,
    /// <see cref="ApplyAsync"/> reads it, and the intrusion check must exempt it. Lower-case, because
    /// it is compared through <see cref="ManifestDiff.Normalize"/>.
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
                Log($"[Shenora.Modules.Update] Ignoring an unreadable staging marker at '{MarkerPath}'.");
                return new UpdateStageStatus { Pending = false };
            }
            return marker;
        }
        catch (Exception ex)
        {
            Log($"[Shenora.Modules.Update] Could not read the staging marker: {ex.GetType().Name}");
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
            var path = ResolveTracked(StagedDirectory, file.Path);
            if (path is null)
            {
                // Not "incomplete" — REFUSED. A manifest handed straight to CommitAsync never passed a
                // diff, so this is the first thing that has looked at its paths.
                Log($"[Shenora.Modules.Update] Stage refused: '{file.Path}' is not a contained relative path.");
                return new UpdateStageStatus { Pending = false };
            }

            if (!File.Exists(path))
            {
                Log($"[Shenora.Modules.Update] Stage incomplete: '{file.Path}' is missing.");
                return new UpdateStageStatus { Pending = false };
            }

            var actual = await Sha256Async(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // The hash is authoritative, not the size — a truncated download can coincidentally
                // match a size and never matches a hash. The message names the file and NOT the
                // hashes: a mismatch is a corrupt download, and dumping 128 hex characters into a log
                // buries that.
                Log($"[Shenora.Modules.Update] Stage corrupt: '{file.Path}' does not match its manifest hash.");
                return new UpdateStageStatus { Pending = false };
            }
        }

        // THE THIRD FAILURE MODE. The loop above covers TRUNCATION (listed but missing) and TAMPER
        // (present but wrong hash); this covers INTRUSION (present but unlisted), and without it the
        // marker's promise was bigger than what was actually checked — because ApplyAsync overlays the
        // staged TREE, not the manifest, so a file nothing verified was copied into the install root.
        // Both halves were individually defensible, which is exactly why the gap survived: enumerating
        // in ApplyAsync is right (a differential stage holds only the changeset, and manifest.json is in
        // the tree but not in the manifest), and verifying the manifest is right; it is the PAIR that
        // left a hole. An adopter shipped the same asymmetry — its native launcher rejected all three
        // from the start and its managed verifier only two, one threat model enforced two ways.
        if (!VerifyNothingUnlisted(manifest, cancellationToken))
            return new UpdateStageStatus { Pending = false };

        // THE FOURTH THING THE MARKER PROMISES, and it was missing until a real-tree probe hit it
        // (2026-08-05, `devtools/update-probe`). ApplyAsync REFUSES without a readable, non-empty
        // `staged/manifest.json` — it is what removals are computed from. FetchAsync writes it; an app
        // that stages by its own means has no reason to know that, and nothing said so. So a manually
        // staged tree passed every check above, got the marker, and then failed at apply time.
        //
        // ⚠ WHERE that failed is the reason this is a guard and not a doc note: ApplyAsync runs in the
        // APPLIER — typically a launcher, after the app has exited. A stage that verifies clean and then
        // refuses on next start reports its failure with no app running to show it, which is the worst
        // possible moment to discover a contract nobody stated.
        //
        // ⚠ It is a CHECK, never a write. The manifest passed here is the STAGED changeset; the file is
        // the FULL release manifest, and a differential update makes them genuinely different. Writing
        // the changeset there would tell the applier that every file NOT in the changeset was removed
        // from the release — a data-destroying "fix" that looks obvious.
        if (!StagedManifestIsUsable())
            return new UpdateStageStatus { Pending = false };

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
        Log($"[Shenora.Modules.Update] Staged {manifest.Files.Count} file(s) for version {manifest.Version}.");
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
            Log($"[Shenora.Modules.Update] Nothing to download for version {release.Version}" +
                (diff.Removed.Count > 0 ? $" ({diff.Removed.Count} removal(s) only)." : "."));
            return new UpdateStageStatus { Pending = false };
        }

        Begin();
        var staged = new List<ManifestFile>(diff.Added.Count + diff.Updated.Count);
        foreach (var file in diff.Added.Concat(diff.Updated))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Unreachable in practice — the diff this loop walks came from ManifestDiff.Compute, which
            // already refused the whole manifest. Kept because THIS is the line that creates a file, and
            // a write path defended only by a caller is one refactor away from being undefended.
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

        // The applier's copy of the new baseline. Not part of the staged manifest, so CommitAsync
        // does not verify it — it is not a payload file, it is the record of what the payload means.
        await File.WriteAllTextAsync(Path.Combine(StagedDirectory, StagedManifestName), release.ToJson(),
            cancellationToken).ConfigureAwait(false);

        return await CommitAsync(new UpdateManifest { Version = release.Version, Files = staged },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Apply a staged update: overlay it onto <paramref name="installRoot"/>, delete what the new
    /// manifest dropped, and clear the stage. Portable — no native code and nothing platform-specific.
    /// <para>
    /// <b>Run this from OUTSIDE <paramref name="installRoot"/>, with the app not running.</b> That is
    /// the topology the design picked (`docs/DECISIONS.md` D50): a launcher
    /// at <c>{root}/</c> overlaying <c>{root}/app/</c> can never overwrite or delete itself, which
    /// makes four separate self-exclusion guards UNREACHABLE rather than merely handled. Overlay a
    /// tree that contains the running process and you are signing up for all of them.
    /// </para>
    /// <para>
    /// A self-contained app needs nothing more than this. A framework-dependent one still wants a
    /// native launcher, because something has to run when the runtime may be absent — but that
    /// launcher's job shrinks to bootstrapping the runtime and calling this.
    /// </para>
    /// </summary>
    public async Task<UpdateOutcome> ApplyAsync(string installRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        var status = GetStatus();
        if (!status.Pending) return new UpdateOutcome { Applied = false, Failure = "nothing is staged" };

        // Read BOTH manifests before the overlay — the overlay overwrites the installed one, and the
        // removal set is the difference between them. Both donors read them first for this reason.
        var stagedManifestPath = Path.Combine(StagedDirectory, StagedManifestName);
        UpdateManifest? release = null;
        try
        {
            if (File.Exists(stagedManifestPath)) release = UpdateManifest.Parse(File.ReadAllText(stagedManifestPath));
        }
        catch (Exception ex)
        {
            Log($"[Shenora.Modules.Update] The staged manifest could not be read: {ex.GetType().Name}");
        }

        // THE GUARD ONE DONOR HAS AND THE OTHER DOES NOT. Removals are driven by "installed minus
        // release", so an unreadable or empty release manifest would delete every tracked path —
        // including the files just overlaid — turning a SUCCESSFUL copy into a corrupt install. No
        // manifest means no removals, and here it means no apply at all.
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
            // A first install, or a corrupt baseline. Either way: overlay, remove NOTHING. Guessing
            // at removals without a trustworthy baseline is the destructive direction.
            Log($"[Shenora.Modules.Update] No usable installed manifest ({ex.GetType().Name}) — applying without removals.");
        }

        var written = new List<string>();
        foreach (var source in Directory.EnumerateFiles(StagedDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(StagedDirectory, source);
            // The baseline is written EXPLICITLY below, never carried by the overlay. It used to ride along
            // because the stage happens to contain it and the default destination happens to be the same
            // place — which is why a configured BaselinePath would otherwise put a copy in BOTH: one where
            // it was asked for, and a stray one at {installRoot}/manifest.json. Excluding it
            // unconditionally keeps the two cases one code path instead of a containment test.
            if (ManifestDiff.Normalize(relative) == StagedManifestName) continue;
            var destination = Path.Combine(installRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            written.Add(relative.Replace('\\', '/'));
        }

        // The new baseline, at whatever location this stage was configured with. This is what makes the next
        // apply able to compute removals, so it is written even when it lands outside the tree.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            File.Copy(stagedManifestPath, baselinePath, overwrite: true);
            // Reported as written only when it really landed IN the tree — which is the default, so an
            // install-tree consumer sees exactly the outcome it always saw. Pointed elsewhere it is
            // deliberately absent from `Written`, because the whole reason to move it is that the tree's
            // contents are being measured.
            if (IsUnder(baselinePath, installRoot))
                written.Add(Path.GetRelativePath(installRoot, baselinePath).Replace('\\', '/'));
        }
        catch (Exception ex)
        {
            // The payload is already overlaid, so the install IS the new version — abandoning here would
            // report a failure that has already half-happened. A missing baseline degrades to "remove
            // nothing next time", the safe direction, and is worth a loud log rather than a throw.
            Log($"[Shenora.Modules.Update] The payload applied but the baseline could not be written to " +
                $"'{baselinePath}' ({ex.GetType().Name}) — the next apply will compute no removals.");
        }

        // Tracked paths only, never a directory sweep: user data lives in this tree and the manifest
        // is the only thing that knows which files the app owns.
        var removed = new List<string>();
        foreach (var path in ManifestDiff.Compute(installed, release).Removed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The BASELINE drives this loop, and step 6 wrote that baseline from a staged manifest — so a
            // manifest that escaped once would be deleting outside the install root on the NEXT update.
            // Skip and log rather than throw: the overlay has already landed, and abandoning here would
            // leave a half-applied install over one bad row.
            var target = ResolveTracked(installRoot, path);
            if (target is null)
            {
                Log($"[Shenora.Modules.Update] Removal refused: '{path}' is not a contained relative path.");
                continue;
            }

            try
            {
                if (File.Exists(target)) { File.Delete(target); removed.Add(path); }
            }
            catch (Exception ex)
            {
                // A file that will not delete is not a reason to abandon a completed overlay — the
                // install is already the new version. Report and continue.
                Log($"[Shenora.Modules.Update] Could not remove '{path}': {ex.GetType().Name}");
            }
        }

        Clear();
        Log($"[Shenora.Modules.Update] Applied version {release.Version}: {written.Count} written, {removed.Count} removed.");
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
    /// <see cref="Path.GetFullPath(string, string)"/> rather than <see cref="Path.Combine(string, string)"/>, and that is the
    /// point of a named method: Combine SILENTLY DISCARDS its first argument when the second is rooted, which
    /// happens to produce the right answer here and is the exact behaviour this repo already had to fix a
    /// security bug over. GetFullPath states both cases — a relative path resolves against the root, a rooted
    /// one is itself — so nobody has to know the quirk to read this.
    /// </para>
    /// </summary>
    internal string ResolveBaselinePath(string installRoot) =>
        _options.BaselinePath is { Length: > 0 } configured
            ? Path.GetFullPath(configured, Path.GetFullPath(installRoot))
            : Path.Combine(Path.GetFullPath(installRoot), StagedManifestName);

    /// <summary>
    /// Whether <paramref name="path"/> sits inside <paramref name="root"/>.
    /// <para>
    /// The separator is appended before comparing, for the same reason
    /// <see cref="WebViewFiles.ResolveContained"/> does it: without that, <c>/app-old</c> passes as a child of
    /// <c>/app</c>. Not a security boundary here — the path is the APP's own configuration, not a page's — so
    /// it only decides whether the baseline is reported in <see cref="UpdateOutcome.Written"/>.
    /// </para>
    /// </summary>
    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(root);
        var prefix = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Is the full release manifest present in the stage, readable, and non-empty? This mirrors exactly
    /// what <see cref="ApplyAsync"/> requires, so the marker cannot promise more than the applier can use.
    /// Empty counts as unusable for the same reason it does there: an empty release manifest means
    /// "everything was removed", which would delete every tracked path including what was just overlaid.
    /// </summary>
    private bool StagedManifestIsUsable()
    {
        var path = Path.Combine(StagedDirectory, StagedManifestName);
        if (!File.Exists(path))
        {
            Log($"[Shenora.Modules.Update] Stage incomplete: '{StagedManifestName}' is not in the stage. It is the full " +
                "release manifest, and ApplyAsync computes REMOVALS from it. FetchAsync writes it for you; " +
                "an app staging by other means must write it itself.");
            return false;
        }

        try
        {
            var release = UpdateManifest.Parse(File.ReadAllText(path));
            if (release.Files.Count != 0) return true;
            Log($"[Shenora.Modules.Update] Stage rejected: '{StagedManifestName}' lists no files, which an applier reads " +
                "as 'the release removed everything'.");
        }
        catch (Exception ex)
        {
            Log($"[Shenora.Modules.Update] Stage rejected: '{StagedManifestName}' could not be read ({ex.GetType().Name}).");
        }
        return false;
    }

    /// <summary>
    /// The staged tree must contain NOTHING the manifest does not list — the intrusion half of
    /// verification. Returns false (and logs the offending path) when it does.
    /// <para>
    /// <b>The kit's own <c>manifest.json</c> is always exempt, and that is not a convenience.</b>
    /// <see cref="FetchAsync"/> writes the release manifest into the stage on purpose — an applier needs
    /// it to compute removals, and the overlay makes it the newly-installed baseline — while
    /// deliberately keeping it out of the staged manifest, since it is the record of what the payload
    /// means rather than a payload file. So a literal "nothing is exempt" rule would reject every stage
    /// this class itself produces. That is the inverted failure mode
    /// <see cref="UpdateStageOptions.IsUnindexed"/> warns about, arriving from the kit's own design
    /// rather than from any consumer's packaging.
    /// </para>
    /// <para>
    /// Comparison uses <see cref="ManifestDiff"/>'s normalization, not a local copy: a disk path and a
    /// manifest path must agree on separators and case here for exactly the reasons that rule already
    /// exists, and two copies of it would be one rule that can drift.
    /// </para>
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
            if (relative == StagedManifestName) continue;         // see the remarks — the kit's own
            if (_options.IsUnindexed?.Invoke(relative) == true) continue;

            // Named, and phrased as what it IS: a file the release did not describe, about to be
            // copied into the install root by the overlay. The path is the only useful detail; there
            // is no hash to report because nothing claimed one.
            Log($"[Shenora.Modules.Update] Stage rejected: '{relative}' is present but the manifest does not " +
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
    /// 🔴 <b>The ONE place a manifest path becomes a filesystem path</b> — every write, every existence
    /// check and every delete in this type goes through here, because a manifest arrives from a remote
    /// server and <see cref="Path.Combine(string, string)"/> silently discards <paramref name="root"/>
    /// when the second argument is rooted (see <see cref="ResolveBaselinePath"/>, which names the same
    /// quirk). <see cref="ManifestDiff.IsSafeRelativePath"/> already refuses such a manifest at diff
    /// time; this is the second line, for the paths that reach a file operation WITHOUT passing a diff —
    /// <see cref="CommitAsync"/> verifies a manifest handed straight in by the app.
    /// </para>
    /// <para>
    /// <see cref="Path.GetFullPath(string, string)"/> rather than <see cref="Path.Combine(string, string)"/>
    /// for the reason stated there, then <see cref="PathClaims.IsContained"/> — the kit's own containment
    /// rule, which compares case-sensitively off Windows, so this cannot be widened by a filesystem where
    /// <c>Public</c> and <c>public</c> are different directories.
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
            return null;   // malformed (invalid characters, too long, a reserved name) — never resolve it
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
