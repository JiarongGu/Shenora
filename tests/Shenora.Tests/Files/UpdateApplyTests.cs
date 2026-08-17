using System.Security.Cryptography;
using Shenora;
using Shenora.Tests.TestSupport;
using Shenora.Engine.Update;
using Shenora.Engine.Files;

namespace Shenora.Tests.Io;

/// <summary>
/// The apply pass — the half both donor apps wrote in C++ and this one does not have to.
/// <para>
/// The topology is what makes it tractable: the applier runs from OUTSIDE the tree it overlays, so
/// it can never overwrite or delete itself and four self-exclusion guards are unreachable rather
/// than merely handled. These tests model that layout — a stage under <c>root/.update</c> overlaying
/// <c>root/app</c>.
/// </para>
/// </summary>
public class UpdateApplyTests
{
    private static string Sha256Of(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    private static UpdateManifest Manifest(string version, params (string Path, string Content)[] files) => new()
    {
        Version = version,
        Files = [.. files.Select(f => new ManifestFile
        {
            Path = f.Path,
            Size = System.Text.Encoding.UTF8.GetByteCount(f.Content),
            Sha256 = Sha256Of(f.Content),
        })],
    };

    /// <summary>An install at root/app, with a manifest, exactly as a deployed app looks.</summary>
    private static void Install(TempDir dir, UpdateManifest manifest, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files) dir.WriteFile(Path.Combine("app", path), content);
        dir.WriteFile(Path.Combine("app", "manifest.json"), manifest.ToJson());
    }

    private static UpdateStage StageIn(TempDir dir) =>
        new(new UpdateStageOptions { Root = dir.Combine(".update") });

    /// <summary>Stage a changeset the way FetchAsync would: the files, plus the full release manifest.</summary>
    private static async Task StageAsync(UpdateStage stage, UpdateManifest release,
                                         params (string Path, string Content)[] staged)
    {
        stage.Begin();
        foreach (var (path, content) in staged)
        {
            var full = Path.Combine(stage.StagedDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "manifest.json"), release.ToJson());
        await stage.CommitAsync(Manifest(release.Version, staged));
    }

    [Fact]
    public async Task Overlays_the_changeset_removes_what_the_new_manifest_dropped_and_clears()
    {
        using var dir = TempDir.Create();
        var installed = Manifest("1.0", ("app.exe", "v1"), ("libs/keep.dll", "same"), ("old.dll", "gone soon"));
        Install(dir, installed, ("app.exe", "v1"), ("libs/keep.dll", "same"), ("old.dll", "gone soon"));

        var release = Manifest("2.0", ("app.exe", "v2"), ("libs/keep.dll", "same"), ("new.dll", "fresh"));
        var stage = StageIn(dir);
        await StageAsync(stage, release, ("app.exe", "v2"), ("new.dll", "fresh"));

        var outcome = await stage.ApplyAsync(dir.Combine("app"));

        Assert.True(outcome.Applied);
        Assert.Equal("2.0", outcome.Version);
        Assert.Equal("v2", File.ReadAllText(dir.Combine("app", "app.exe")));
        Assert.Equal("fresh", File.ReadAllText(dir.Combine("app", "new.dll")));
        // Unchanged and never staged — it must still be there. A "reinstall everything" applier
        // would pass every other assertion here and fail this one.
        Assert.Equal("same", File.ReadAllText(dir.Combine("app", "libs", "keep.dll")));
        Assert.False(File.Exists(dir.Combine("app", "old.dll")));
        Assert.Contains("old.dll", outcome.Removed);
        // The manifest rode along, so the install now describes itself as 2.0 for the NEXT diff.
        Assert.Equal("2.0", UpdateManifest.Parse(File.ReadAllText(dir.Combine("app", "manifest.json"))).Version);
        // Staging is gone: a stage that survives its own apply gets applied twice.
        Assert.False(stage.GetStatus().Pending);
        Assert.False(Directory.Exists(stage.StagedDirectory));
    }

    /// <summary>
    /// 🔴 <b>An INSTALLED baseline listing an escaping path must not delete outside the install root.</b>
    /// This is the reachable half of the manifest-path hole: the removal pass is driven by the baseline,
    /// and step 6 of a previous apply wrote that baseline from a manifest a remote server supplied — so
    /// one poisoned release arms the NEXT update's delete.
    /// <para>
    /// The baseline is written as raw JSON rather than through <c>Manifest(...)</c> on purpose: the diff
    /// now refuses such a manifest, so the only way to model an install that was poisoned BEFORE the
    /// guard existed — which is exactly the upgrade case — is to put the bytes on disk directly.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_baseline_path_that_escapes_the_install_root_deletes_NOTHING_outside_it()
    {
        using var dir = TempDir.Create();
        dir.WriteFile("bystander.txt", "must survive");
        var outside = dir.Combine("bystander.txt");

        Install(dir, Manifest("1.0", ("app.exe", "v1")), ("app.exe", "v1"));
        // Overwrite the baseline with one whose second entry climbs out of app/ into the bystander.
        File.WriteAllText(dir.Combine("app", "manifest.json"),
            """
            {"version":"1.0","files":[
              {"path":"app.exe","size":2,"sha256":"x"},
              {"path":"../bystander.txt","size":12,"sha256":"y"}
            ]}
            """);

        var release = Manifest("2.0", ("app.exe", "v2"));
        var stage = StageIn(dir);
        await StageAsync(stage, release, ("app.exe", "v2"));

        var outcome = await stage.ApplyAsync(dir.Combine("app"));

        // The update still lands — one refused row must not abandon a completed overlay.
        Assert.True(outcome.Applied);
        Assert.Equal("v2", File.ReadAllText(dir.Combine("app", "app.exe")));
        // The whole point.
        Assert.True(File.Exists(outside), "the escaping baseline path deleted a file outside the install root");
        Assert.Equal("must survive", File.ReadAllText(outside));
        Assert.DoesNotContain("../bystander.txt", outcome.Removed);
    }

    /// <summary>
    /// 🔴 <b>The second layer, and the only path that reaches it.</b> A manifest CONSTRUCTED IN CODE and
    /// handed to <see cref="UpdateStage.CommitAsync"/> passes neither <c>UpdateManifest.Parse</c> nor
    /// <c>ManifestDiff.Compute</c>, so <c>ResolveTracked</c>'s containment check is the only thing that
    /// looks at its paths — and <c>CommitAsync</c> is public API an app calls with its own manifest.
    /// <para>
    /// ⚠ The escaping file is made to EXIST with a matching hash on purpose. Without it the verify loop
    /// would answer "stage incomplete" for a missing file and the test would pass with the guard removed
    /// — discriminating nothing. Present and correct, an unguarded <c>CommitAsync</c> publishes the
    /// marker.
    /// </para>
    /// <para>
    /// 🔴 <b>WHAT THIS DOES AND DOES NOT DISCRIMINATE, measured by sabotage 2026-08-14 — read before
    /// "simplifying" either guard.</b> The two defences are REDUNDANT here, so this test fails only when
    /// BOTH are gone: neutering <c>ManifestDiff.IsSafeRelativePath</c> alone leaves
    /// <c>ResolveTracked</c>'s <c>PathClaims.IsContained</c> to refuse it, and removing that alone leaves
    /// the predicate. What pins the PREDICATE by itself is
    /// <c>ManifestDiffTests.A_manifest_path_that_can_escape_the_install_root_is_refused</c> (5 cases, all
    /// of which fail when it is neutered). So this test is the whole-defence backstop, not the
    /// predicate's tripwire — and the containment half has no test that isolates it, because the one
    /// escape the predicate cannot see (a Windows reserved device name: <c>GetFullPath("NUL", root)</c>
    /// is <c>\\.\NUL</c>, measured) cannot hold a file for the verify loop to accept.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CommitAsync_REFUSES_an_in_code_manifest_whose_path_escapes_the_stage()
    {
        using var dir = TempDir.Create();
        Install(dir, Manifest("1.0", ("app.exe", "v1")), ("app.exe", "v1"));

        var stage = StageIn(dir);
        stage.Begin();

        // `../escape.txt` from the staged directory lands beside it, inside `.update/`.
        const string content = "planted";
        var escaped = Path.Combine(stage.StagedDirectory, "..", "escape.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(escaped))!);
        File.WriteAllText(Path.GetFullPath(escaped), content);

        var hostile = new UpdateManifest
        {
            Version = "2.0",
            Files = [new ManifestFile
            {
                Path = "../escape.txt",
                Size = System.Text.Encoding.UTF8.GetByteCount(content),
                Sha256 = Sha256Of(content),
            }],
        };

        var status = await stage.CommitAsync(hostile);

        Assert.False(status.Pending, "an escaping manifest path was accepted and the stage was published");
    }

    [Fact]
    public async Task Does_nothing_when_no_stage_is_pending()
    {
        using var dir = TempDir.Create();
        Install(dir, Manifest("1.0", ("app.exe", "v1")), ("app.exe", "v1"));

        var outcome = await StageIn(dir).ApplyAsync(dir.Combine("app"));

        Assert.False(outcome.Applied);
        Assert.Contains("nothing is staged", outcome.Failure, StringComparison.Ordinal);
        Assert.Equal("v1", File.ReadAllText(dir.Combine("app", "app.exe")));
    }

    [Fact]
    public async Task An_unreadable_staged_manifest_blocks_the_apply_INSTEAD_of_removing_everything()
    {
        // The guard one donor has and the other does not. Removals are "installed minus release", so
        // a release manifest that fails to load would delete every tracked path — including files
        // just overlaid — turning a successful copy into a corrupt install.
        using var dir = TempDir.Create();
        var installed = Manifest("1.0", ("app.exe", "v1"), ("libs/x.dll", "keep me"));
        Install(dir, installed, ("app.exe", "v1"), ("libs/x.dll", "keep me"));

        var stage = StageIn(dir);
        await StageAsync(stage, Manifest("2.0", ("app.exe", "v2")), ("app.exe", "v2"));
        // Corrupt the baseline the applier would compute removals from.
        File.WriteAllText(Path.Combine(stage.StagedDirectory, "manifest.json"), "{ not json");

        var outcome = await stage.ApplyAsync(dir.Combine("app"));

        Assert.False(outcome.Applied);
        Assert.Contains("missing or empty", outcome.Failure, StringComparison.Ordinal);
        // NOTHING was touched — not the file it would have replaced, and above all not the one it
        // would have "removed".
        Assert.Equal("v1", File.ReadAllText(dir.Combine("app", "app.exe")));
        Assert.Equal("keep me", File.ReadAllText(dir.Combine("app", "libs", "x.dll")));
    }

    [Fact]
    public async Task A_first_install_with_no_baseline_applies_without_removing_anything()
    {
        using var dir = TempDir.Create();
        Directory.CreateDirectory(dir.Combine("app"));
        // Something the app owns but no manifest describes — a data file, or an install that predates
        // manifests. Guessing at removals without a trustworthy baseline is the destructive direction.
        dir.WriteFile(Path.Combine("app", "user-data.txt"), "precious");

        var stage = StageIn(dir);
        await StageAsync(stage, Manifest("2.0", ("app.exe", "v2")), ("app.exe", "v2"));

        var outcome = await stage.ApplyAsync(dir.Combine("app"));

        Assert.True(outcome.Applied);
        Assert.Empty(outcome.Removed);
        Assert.Equal("precious", File.ReadAllText(dir.Combine("app", "user-data.txt")));
    }

    [Fact]
    public async Task Untracked_files_are_never_swept()
    {
        // Removals come from the manifest DIFF, never from "what is in the folder". User data lives
        // in the same tree, and a directory sweep would take it.
        using var dir = TempDir.Create();
        Install(dir, Manifest("1.0", ("app.exe", "v1")), ("app.exe", "v1"));
        dir.WriteFile(Path.Combine("app", "settings.json"), "{}");
        dir.WriteFile(Path.Combine("app", "data", "profile.db"), "rows");

        var stage = StageIn(dir);
        await StageAsync(stage, Manifest("2.0", ("app.exe", "v2")), ("app.exe", "v2"));
        var outcome = await stage.ApplyAsync(dir.Combine("app"));

        Assert.True(outcome.Applied);
        Assert.True(File.Exists(dir.Combine("app", "settings.json")));
        Assert.True(File.Exists(dir.Combine("app", "data", "profile.db")));
    }

    // ── The baseline's LOCATION (2026-08-04) — filed by the first adopter, whose targets are deploy
    //    INPUTS rather than install trees: their aggregate content hash decides what gets re-uploaded, and a
    //    per-release manifest.json inside them changed that hash on every release even when the payload was
    //    byte-identical. The default must not move; everything else is new. ──────────────────────────────

    [Fact]
    public async Task By_DEFAULT_the_baseline_still_lands_in_the_install_root_and_is_reported_as_written()
    {
        using var dir = TempDir.Create();
        Install(dir, Manifest("1.0", ("app.exe", "v1")), ("app.exe", "v1"));

        var stage = StageIn(dir);
        await StageAsync(stage, Manifest("2.0", ("app.exe", "v2")), ("app.exe", "v2"));
        var outcome = await stage.ApplyAsync(dir.Combine("app"));

        Assert.True(outcome.Applied);
        // The behaviour every existing install depends on, now pinned rather than assumed — the baseline
        // used to ride along in the overlay and is now written explicitly, which must be indistinguishable.
        Assert.Equal("2.0", UpdateManifest.Parse(File.ReadAllText(dir.Combine("app", "manifest.json"))).Version);
        Assert.Contains("manifest.json", outcome.Written);
        Assert.Contains("app.exe", outcome.Written);
    }

    [Fact]
    public async Task A_baseline_OUTSIDE_the_root_leaves_the_tree_a_pure_function_of_the_payload()
    {
        using var dir = TempDir.Create();
        Install(dir, Manifest("1.0", ("app.exe", "v1")), ("app.exe", "v1"));
        // Remove the install-tree baseline the helper wrote, so this models the adopter's layout: a target
        // directory that contains payload and nothing else.
        File.Delete(dir.Combine("app", "manifest.json"));
        File.WriteAllText(dir.Combine("baseline.json"), Manifest("1.0", ("app.exe", "v1")).ToJson());

        var stage = new UpdateStage(new UpdateStageOptions
        {
            Root = dir.Combine(".update"),
            BaselinePath = dir.Combine("baseline.json"),
        });
        await StageAsync(stage, Manifest("2.0", ("app.exe", "v2")), ("app.exe", "v2"));
        var outcome = await stage.ApplyAsync(dir.Combine("app"));

        Assert.True(outcome.Applied);
        Assert.Equal("v2", File.ReadAllText(dir.Combine("app", "app.exe")));
        // THE POINT: no kit bookkeeping inside the measured tree. This is the assertion the adoption was
        // blocked on — a stray manifest.json here changes the aggregate hash on every release.
        Assert.False(File.Exists(dir.Combine("app", "manifest.json")));
        Assert.DoesNotContain("manifest.json", outcome.Written);
        Assert.Equal(["app.exe"], outcome.Written);
        // …and the baseline still moved forward, or the next apply could not compute removals.
        Assert.Equal("2.0", UpdateManifest.Parse(File.ReadAllText(dir.Combine("baseline.json"))).Version);
    }

    [Fact]
    public async Task A_baseline_outside_the_root_is_still_READ_for_removals()
    {
        // The half that would fail silently: writing the relocated baseline but reading the old default
        // leaves removals computed against an empty manifest, so nothing is ever removed and stale files
        // accumulate for the life of the install.
        using var dir = TempDir.Create();
        Install(dir, Manifest("1.0", ("app.exe", "v1"), ("old.dll", "gone soon")),
            ("app.exe", "v1"), ("old.dll", "gone soon"));
        File.Delete(dir.Combine("app", "manifest.json"));
        File.WriteAllText(dir.Combine("baseline.json"),
            Manifest("1.0", ("app.exe", "v1"), ("old.dll", "gone soon")).ToJson());

        var stage = new UpdateStage(new UpdateStageOptions
        {
            Root = dir.Combine(".update"),
            BaselinePath = dir.Combine("baseline.json"),
        });
        await StageAsync(stage, Manifest("2.0", ("app.exe", "v2")), ("app.exe", "v2"));
        var outcome = await stage.ApplyAsync(dir.Combine("app"));

        Assert.True(outcome.Applied);
        Assert.Contains("old.dll", outcome.Removed);
        Assert.False(File.Exists(dir.Combine("app", "old.dll")));
    }

    [Fact]
    public async Task A_RELATIVE_baseline_path_resolves_against_the_install_root()
    {
        using var dir = TempDir.Create();
        Install(dir, Manifest("1.0", ("app.exe", "v1")), ("app.exe", "v1"));
        File.Delete(dir.Combine("app", "manifest.json"));

        var stage = new UpdateStage(new UpdateStageOptions
        {
            Root = dir.Combine(".update"),
            BaselinePath = ".meta/baseline.json",
        });
        await StageAsync(stage, Manifest("2.0", ("app.exe", "v2")), ("app.exe", "v2"));
        var outcome = await stage.ApplyAsync(dir.Combine("app"));

        Assert.True(outcome.Applied);
        // Relative means relative to the INSTALL ROOT, and the directory is created for you.
        Assert.Equal("2.0",
            UpdateManifest.Parse(File.ReadAllText(dir.Combine("app", ".meta", "baseline.json"))).Version);
        // Inside the root, so it IS reported — and at the configured path, not the default one.
        Assert.Contains(".meta/baseline.json", outcome.Written);
        Assert.False(File.Exists(dir.Combine("app", "manifest.json")));
    }

    [Fact]
    public void ResolveBaselinePath_states_both_cases_rather_than_relying_on_Path_Combine()
    {
        using var dir = TempDir.Create();
        var root = dir.Combine("app");
        var rooted = dir.Combine("elsewhere", "baseline.json");

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "manifest.json"),
            new UpdateStage(new UpdateStageOptions { Root = dir.Combine(".update") }).ResolveBaselinePath(root));
        Assert.Equal(Path.GetFullPath(rooted),
            new UpdateStage(new UpdateStageOptions { Root = dir.Combine(".update"), BaselinePath = rooted })
                .ResolveBaselinePath(root));
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "sub", "b.json"),
            new UpdateStage(new UpdateStageOptions { Root = dir.Combine(".update"), BaselinePath = "sub/b.json" })
                .ResolveBaselinePath(root));
    }
}
