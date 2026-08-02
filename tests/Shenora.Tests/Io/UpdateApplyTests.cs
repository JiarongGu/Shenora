using System.Security.Cryptography;
using Shenora.Core;
using Shenora.Tests.TestSupport;

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
}
