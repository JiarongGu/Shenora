using System.Security.Cryptography;
using Shenora;
using Shenora.Tests.TestSupport;
using Shenora.Engine.Update;
using Shenora.Engine.Files;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Io;

/// <summary>
/// The staging half of a two-phase update. The property under test throughout is the ORDERING:
/// <c>ready.json</c> exists only when every file verified, so an applier can trust the marker
/// without re-hashing anything. Every failure case therefore asserts the marker's ABSENCE, not just
/// a false return — a stage that reports failure while leaving a usable marker is the bug.
/// </summary>
public class UpdateStageTests
{
    private static string Sha256Of(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    private static UpdateManifest Manifest(params (string Path, string Content)[] files) => new()
    {
        Version = "2.0",
        Files = [.. files.Select(f => new ManifestFile
        {
            Path = f.Path,
            Size = System.Text.Encoding.UTF8.GetByteCount(f.Content),
            Sha256 = Sha256Of(f.Content),
        })],
    };

    private static UpdateStage StageIn(TempDir dir) =>
        new(new UpdateStageOptions { Root = dir.Combine(".update") });

    /// <summary>
    /// The FULL release manifest, written into the stage the way <c>FetchAsync</c> writes it. Removals
    /// are computed from this file, so <c>CommitAsync</c> refuses to publish a marker without it.
    /// </summary>
    private static void PublishReleaseManifest(UpdateStage stage, params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(stage.StagedDirectory);
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "manifest.json"), Manifest(files).ToJson());
    }

    // ── The marker must not promise more than the applier can use ─────────────────────────────────

    [Fact]
    public async Task A_stage_with_NO_release_manifest_is_refused_and_leaves_no_marker()
    {
        // THE GAP A REAL TREE FOUND (2026-08-05, `devtools/update-probe` on a `dotnet publish` output).
        // Every file verified, nothing unlisted — and `ApplyAsync` would still refuse, because removals
        // come from `staged/manifest.json` and only `FetchAsync` writes it. An app that stages by its own
        // means got a marker whose whole documented meaning is "an applier can act without re-checking".
        //
        // ⚠ Where that failed is why it is a guard: the applier is typically a LAUNCHER running after the
        // app exited, so the refusal surfaced on next start with nothing left to report it.
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "app.exe"), "binary");

        var status = await stage.CommitAsync(Manifest(("app.exe", "binary")));

        Assert.False(status.Pending);
        Assert.False(StageIn(dir).GetStatus().Pending);
    }

    [Fact]
    public async Task A_release_manifest_that_lists_NOTHING_is_refused_too()
    {
        // Present but empty is the dangerous variant, not the harmless one: an applier reads an empty
        // release manifest as "every tracked path was removed" and deletes the files it just overlaid.
        // `ApplyAsync` already refuses it; this stops the marker from being written in the first place.
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "app.exe"), "binary");
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "manifest.json"),
            new UpdateManifest { Version = "2.0", Files = [] }.ToJson());

        Assert.False((await stage.CommitAsync(Manifest(("app.exe", "binary")))).Pending);
        Assert.False(StageIn(dir).GetStatus().Pending);
    }

    [Fact]
    public async Task An_UNREADABLE_release_manifest_is_refused()
    {
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "app.exe"), "binary");
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "manifest.json"), "{ not json");

        Assert.False((await stage.CommitAsync(Manifest(("app.exe", "binary")))).Pending);
    }

    [Fact]
    public async Task A_fully_verified_stage_publishes_the_marker()
    {
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "app.exe"), "binary");
        Directory.CreateDirectory(Path.Combine(stage.StagedDirectory, "libs"));
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "libs", "x.dll"), "lib");
        PublishReleaseManifest(stage, ("app.exe", "binary"), ("libs/x.dll", "lib"));

        var status = await stage.CommitAsync(Manifest(("app.exe", "binary"), ("libs/x.dll", "lib")));

        Assert.True(status.Pending);
        Assert.Equal("2.0", status.Version);
        // …and it survives a fresh reader, which is what an applier actually is.
        var reread = StageIn(dir).GetStatus();
        Assert.True(reread.Pending);
        Assert.Equal("2.0", reread.Version);
    }

    [Fact]
    public async Task A_tampered_file_leaves_NO_marker()
    {
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        // The manifest says "good"; the disk says otherwise — a truncated or swapped download.
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "app.exe"), "tampered");

        var status = await stage.CommitAsync(Manifest(("app.exe", "good")));

        Assert.False(status.Pending);
        // The load-bearing half: an applier only ever looks at the marker, so "returned false" is
        // not enough — there must be nothing for it to find.
        Assert.False(StageIn(dir).GetStatus().Pending);
    }

    [Fact]
    public async Task A_missing_file_leaves_NO_marker()
    {
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "app.exe"), "binary");
        // libs/x.dll never arrived — the shape a download interrupted partway leaves behind.

        var status = await stage.CommitAsync(Manifest(("app.exe", "binary"), ("libs/x.dll", "lib")));

        Assert.False(status.Pending);
        Assert.False(StageIn(dir).GetStatus().Pending);
    }

    [Fact]
    public async Task An_empty_RELEASE_manifest_is_refused_rather_than_staged()
    {
        // An empty manifest tells an applier to remove every tracked path, so one that "loaded" to
        // nothing would destroy the install as the SUCCESSFUL outcome of an update.
        //
        // 🔴 THE OBJECT MATTERS. This guard belongs to `staged/manifest.json` — the full RELEASE manifest
        // ApplyAsync computes removals from — and it used to be enforced against the CHANGESET instead,
        // which is a different thing entirely. That defended the wrong object and made a removals-only
        // release impossible to stage; see the round trip below.
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        PublishReleaseManifest(stage);   // present, readable, and listing nothing

        var status = await stage.CommitAsync(new UpdateManifest { Version = "2.0", Files = [] });

        Assert.False(status.Pending);
        Assert.False(StageIn(dir).GetStatus().Pending);
    }

    [Fact]
    public async Task An_empty_changeset_with_NO_release_manifest_still_publishes_no_marker()
    {
        // The other half: dropping the changeset guard must not let a caller stage nothing at all. The
        // release manifest is what an applier needs, and its absence is still refusal.
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();

        var status = await stage.CommitAsync(new UpdateManifest { Version = "2.0", Files = [] });

        Assert.False(status.Pending);
        Assert.False(StageIn(dir).GetStatus().Pending);
    }

    [Fact]
    public async Task Begin_clears_a_previous_attempt_so_its_leftovers_cannot_be_verified_as_this_one()
    {
        using var dir = TempDir.Create();
        var stage = StageIn(dir);

        // A first attempt that got one of two files down, then died.
        stage.Begin();
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "app.exe"), "v1");
        Assert.False((await stage.CommitAsync(Manifest(("app.exe", "v1"), ("late.dll", "x")))).Pending);

        // The second attempt must not inherit v1's app.exe: if Begin did not clear, staging ONLY
        // late.dll would verify against a manifest whose app.exe is stale — a half-old install that
        // reports success.
        stage.Begin();
        Assert.False(File.Exists(Path.Combine(stage.StagedDirectory, "app.exe")));
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "late.dll"), "x");

        var status = await stage.CommitAsync(Manifest(("app.exe", "v2"), ("late.dll", "x")));
        Assert.False(status.Pending);
    }

    [Fact]
    public async Task Clear_removes_a_published_stage()
    {
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "app.exe"), "binary");
        PublishReleaseManifest(stage, ("app.exe", "binary"));
        Assert.True((await stage.CommitAsync(Manifest(("app.exe", "binary")))).Pending);

        stage.Clear();

        Assert.False(stage.GetStatus().Pending);
        Assert.False(Directory.Exists(stage.StagedDirectory));
    }

    [Fact]
    public void Status_is_not_pending_when_nothing_was_ever_staged()
    {
        using var dir = TempDir.Create();
        var status = StageIn(dir).GetStatus();

        Assert.False(status.Pending);
        Assert.Null(status.Version);
    }

    [Fact]
    public void An_unreadable_marker_reports_NOT_pending_rather_than_throwing()
    {
        // A stage nobody can describe is not one an applier should act on — and GetStatus is called
        // from UI code on a settings screen, where throwing would be its own bug.
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        File.WriteAllText(Path.Combine(dir.Combine(".update"), "ready.json"), "{ not json");

        Assert.False(stage.GetStatus().Pending);
    }

    /// <summary>A source over an in-memory release — the seam is the point, so the fake is trivial.</summary>
    private sealed class FakeSource(UpdateManifest release, Dictionary<string, string> content) : IUpdateSource
    {
        public List<string> Opened { get; } = [];

        public Task<UpdateManifest> GetManifestAsync(CancellationToken ct = default) => Task.FromResult(release);

        public Task<Stream> OpenAsync(ManifestFile file, CancellationToken ct = default)
        {
            Opened.Add(file.Path);
            return Task.FromResult<Stream>(
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content[file.Path])));
        }
    }

    [Fact]
    public async Task FetchAsync_downloads_only_the_CHANGED_files_and_commits()
    {
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        var installed = Manifest(("app.exe", "v1"), ("libs/keep.dll", "same"));
        var release = Manifest(("app.exe", "v2"), ("libs/keep.dll", "same"), ("new.dll", "brand new"));
        var source = new FakeSource(release, new()
        {
            ["app.exe"] = "v2", ["libs/keep.dll"] = "same", ["new.dll"] = "brand new",
        });

        var status = await stage.FetchAsync(source, installed);

        Assert.True(status.Pending);
        // The whole point of a differential update: keep.dll is unchanged and must NOT be fetched.
        Assert.Equal(["new.dll", "app.exe"], source.Opened);
        Assert.False(File.Exists(Path.Combine(stage.StagedDirectory, "libs", "keep.dll")));
        // …and the full release manifest rides along, because the applier needs it for REMOVALS —
        // the staged changeset alone cannot say what went away.
        var carried = UpdateManifest.Parse(File.ReadAllText(Path.Combine(stage.StagedDirectory, "manifest.json")));
        Assert.Equal(3, carried.Files.Count);
    }

    [Fact]
    public async Task FetchAsync_stages_nothing_when_already_up_to_date()
    {
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        var same = Manifest(("app.exe", "v1"));

        var status = await stage.FetchAsync(new FakeSource(same, new() { ["app.exe"] = "v1" }), same);

        // Not "an empty stage" — no stage at all. ⚠ This is the case that LOOKED like coverage for the
        // one below and is not: same manifest both sides means nothing to download AND nothing to
        // remove, so not-pending is correct here and wrong there.
        Assert.False(status.Pending);
        Assert.False(StageIn(dir).GetStatus().Pending);
    }

    [Fact]
    public async Task A_release_whose_only_change_is_a_DELETION_still_stages_and_applies()
    {
        // 🔴 THE DEFECT THIS EXISTS FOR: FetchAsync returned not-pending whenever there was nothing to
        // DOWNLOAD, so a release that only drops files never staged and never applied. The stale files
        // stayed on disk forever with no error anywhere — and a dropped-but-still-present assembly is
        // still loadable, which is the whole reason a release drops one.
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        var installed = Manifest(("app.exe", "v1"), ("libs/gone.dll", "obsolete"));
        var release = Manifest(("app.exe", "v1"));

        var status = await stage.FetchAsync(new FakeSource(release, new() { ["app.exe"] = "v1" }), installed);

        Assert.True(status.Pending);

        // Nothing was downloaded — the payload is genuinely empty — and the apply pass is driven by the
        // release manifest that rode along instead.
        Assert.False(File.Exists(Path.Combine(stage.StagedDirectory, "app.exe")));

        // End to end: the dropped file is gone from a real install root and the kept one survives.
        var install = dir.Combine("install");
        Directory.CreateDirectory(Path.Combine(install, "libs"));
        File.WriteAllText(Path.Combine(install, "app.exe"), "v1");
        File.WriteAllText(Path.Combine(install, "libs", "gone.dll"), "obsolete");
        // The BASELINE the applier diffs against — removals are "installed minus release", so without it
        // an apply legitimately removes nothing (a first install must not delete anything).
        File.WriteAllText(Path.Combine(install, "manifest.json"), installed.ToJson());

        var outcome = await stage.ApplyAsync(install);

        Assert.True(outcome.Applied, outcome.Failure);
        Assert.Equal(["libs/gone.dll"], outcome.Removed);
        // Only the new baseline — no payload was written, because there was none to write. That pair is
        // the whole shape of a removals-only release.
        Assert.Equal(["manifest.json"], outcome.Written);
        Assert.True(File.Exists(Path.Combine(install, "app.exe")));
        Assert.False(File.Exists(Path.Combine(install, "libs", "gone.dll")));
        Assert.False(StageIn(dir).GetStatus().Pending);   // the stage is cleared after applying
    }

    [Fact]
    public async Task FetchAsync_refuses_an_empty_release_manifest()
    {
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        var empty = new UpdateManifest { Version = "9.0", Files = [] };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => stage.FetchAsync(new FakeSource(empty, []), Manifest(("app.exe", "v1"))));

        // Earliest possible refusal: an empty release diffs to "remove everything installed".
        Assert.Contains("removing every installed file", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_that_throws_mid_download_leaves_no_usable_stage()
    {
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        var release = Manifest(("a.dll", "one"), ("b.dll", "two"));
        // b.dll is missing from the fake's content, so OpenAsync throws partway through.
        var source = new FakeSource(release, new() { ["a.dll"] = "one" });

        await Assert.ThrowsAnyAsync<Exception>(() => stage.FetchAsync(source, Manifest()));

        // A partial download must never look like a complete stage.
        Assert.False(StageIn(dir).GetStatus().Pending);
    }

    [Fact]
    public async Task Verification_observes_cancellation()
    {
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "app.exe"), "binary");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stage.CommitAsync(Manifest(("app.exe", "binary")), cancelled.Token));

        Assert.False(StageIn(dir).GetStatus().Pending);
    }
}
