using System.Security.Cryptography;
using Shenora.Core;
using Shenora.IO;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Io;

/// <summary>
/// The THIRD failure mode of stage verification: INTRUSION — a file present in the stage that the
/// manifest does not list. Tamper (wrong hash) and truncation (listed but missing) were covered from
/// the start; this was not, and the gap was end-to-end rather than theoretical, because
/// <c>ApplyAsync</c> overlays the staged TREE rather than the manifest. A file nothing verified was
/// therefore copied into the install root, while the marker's own documentation promised "complete and
/// verified — an applier never has to re-check".
/// <para>
/// Every rejection case asserts the marker is ABSENT, following this area's existing rule: a stage that
/// reports failure while leaving a usable marker is the bug.
/// </para>
/// </summary>
public class UpdateStageIntrusionTests
{
    private static string Sha256Of(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    private static ManifestFile Entry(string path, string content) => new()
    {
        Path = path,
        Size = System.Text.Encoding.UTF8.GetByteCount(content),
        Sha256 = Sha256Of(content),
    };

    private static UpdateManifest Manifest(params (string Path, string Content)[] files) => new()
    {
        Version = "2.0",
        Files = [.. files.Select(f => Entry(f.Path, f.Content))],
    };

    private static UpdateStage StageIn(TempDir dir, Func<string, bool>? isUnindexed = null) =>
        new(new UpdateStageOptions { Root = dir.Combine(".update"), IsUnindexed = isUnindexed });

    private static void Stage(UpdateStage stage, string relativePath, string content)
    {
        var full = Path.Combine(stage.StagedDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // ── The hole ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_UNLISTED_staged_file_is_rejected_and_leaves_NO_marker()
    {
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        Stage(stage, "app.exe", "binary");
        Stage(stage, "evil.dll", "injected"); // hashes fine — nothing ever claimed a hash for it

        var status = await stage.CommitAsync(Manifest(("app.exe", "binary")));

        Assert.False(status.Pending);
        Assert.False(StageIn(dir).GetStatus().Pending);
    }

    [Fact]
    public async Task An_unlisted_file_in_a_SUBDIRECTORY_is_caught_too()
    {
        // The enumeration has to be recursive. An overlay copies the whole tree, so hiding one level
        // down would otherwise be the trivial bypass.
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        Stage(stage, "app.exe", "binary");
        Stage(stage, "libs/plugins/evil.dll", "injected");

        Assert.False((await stage.CommitAsync(Manifest(("app.exe", "binary")))).Pending);
    }

    [Fact]
    public async Task A_stage_containing_exactly_what_the_manifest_lists_still_commits()
    {
        // The quiet direction: the check must not fire on an honest stage. Without this the two tests
        // above pass just as happily against a verifier that rejects everything.
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        Stage(stage, "app.exe", "binary");
        Stage(stage, "libs/x.dll", "lib");

        Assert.True((await stage.CommitAsync(Manifest(("app.exe", "binary"), ("libs/x.dll", "lib")))).Pending);
    }

    [Fact]
    public async Task A_listed_file_is_recognised_whatever_SEPARATOR_and_CASE_the_manifest_used()
    {
        // Disk paths and manifest paths must agree through ONE normalization rule (ManifestDiff's), or
        // an honest release with backslashed or differently-cased manifest paths reads as an intrusion —
        // the too-strict direction, which breaks for every user at once.
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        Stage(stage, "libs/x.dll", "lib");

        Assert.True((await stage.CommitAsync(Manifest((@"Libs\X.DLL", "lib")))).Pending);
    }

    // ── The exemption seam ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IsUnindexed_exempts_what_a_clean_release_legitimately_carries()
    {
        using var dir = TempDir.Create();
        var stage = StageIn(dir, path => path.StartsWith("data/", StringComparison.Ordinal)
                                         || path == "app-version.txt");
        stage.Begin();
        Stage(stage, "app.exe", "binary");
        Stage(stage, "data/seed.db", "rows");        // bundled, deliberately not indexed
        Stage(stage, "app-version.txt", "2.0");      // changes every release

        Assert.True((await stage.CommitAsync(Manifest(("app.exe", "binary")))).Pending);
    }

    [Fact]
    public async Task The_predicate_receives_a_manifest_relative_FORWARD_SLASHED_path()
    {
        // What the predicate is handed is a contract an adopter writes against, so it is pinned rather
        // than left to be discovered — a Windows-separator path would silently break every
        // `StartsWith("data/")` exemption anyone writes.
        using var dir = TempDir.Create();
        var seen = new List<string>();
        var stage = StageIn(dir, path => { seen.Add(path); return true; });
        stage.Begin();
        Stage(stage, "app.exe", "binary");
        Stage(stage, "libs/deep/extra.dll", "x");

        await stage.CommitAsync(Manifest(("app.exe", "binary")));

        Assert.Contains("libs/deep/extra.dll", seen);
        Assert.DoesNotContain(seen, p => p.Contains('\\'));
    }

    [Fact]
    public async Task With_NO_predicate_the_default_is_STRICT()
    {
        // Strict by default is the whole point: an exemption must be opted into deliberately, because
        // the loose direction is the one that lets an injected file through.
        using var dir = TempDir.Create();
        var stage = StageIn(dir, isUnindexed: null);
        stage.Begin();
        Stage(stage, "app.exe", "binary");
        Stage(stage, "data/seed.db", "rows");

        Assert.False((await stage.CommitAsync(Manifest(("app.exe", "binary")))).Pending);
    }

    // ── The trap: the kit stages an unindexed file ITSELF ─────────────────────────────────────────

    [Fact]
    public async Task The_kit_s_OWN_manifest_json_is_exempt_without_any_predicate()
    {
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        Stage(stage, "app.exe", "binary");
        Stage(stage, "manifest.json", "{}"); // what FetchAsync writes for the applier's removals

        Assert.True((await stage.CommitAsync(Manifest(("app.exe", "binary")))).Pending);
    }

    [Fact]
    public async Task THE_REAL_FetchAsync_FLOW_STILL_COMMITS()
    {
        // THE test that matters, and the reason it drives FetchAsync instead of building a stage by
        // hand: FetchAsync writes the release manifest INTO the stage on purpose and deliberately keeps
        // it out of the staged manifest, so a literal "nothing is exempt" rule rejects every stage the
        // kit itself produces. That is the inverted failure the option's own docs warn about, arriving
        // from the kit's design rather than a consumer's packaging — and a hand-built fixture cannot
        // catch it, because the test author writes both sides and they agree by construction.
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        var release = Manifest(("app.exe", "v2 binary"), ("libs/x.dll", "v2 lib"));

        var status = await stage.FetchAsync(new FakeSource(release, new()
        {
            ["app.exe"] = "v2 binary",
            ["libs/x.dll"] = "v2 lib",
        }), installed: new UpdateManifest { Version = "1.0", Files = [] });

        Assert.True(status.Pending);
        Assert.Equal("2.0", status.Version);
        // And the manifest really is sitting in the stage — otherwise this test would pass for the
        // wrong reason (nothing unindexed present at all).
        Assert.True(File.Exists(Path.Combine(stage.StagedDirectory, "manifest.json")));
    }

    [Fact]
    public async Task An_ARCHIVE_extracted_into_the_stage_is_rejected_when_it_carries_more_than_the_manifest()
    {
        // The case the check exists for, in the flow it actually happens in. `UpdateStage` documents
        // that an app may fill StagedDirectory "however it likes — HTTP, a share, a USB stick", and one
        // ZIP per part extracted whole is the shape at least as common as loose files. An extraction
        // brings whatever the archive holds, so entries the manifest never described land in the tree —
        // and before this check they were overlaid into the install root unverified.
        using var dir = TempDir.Create();
        var stage = StageIn(dir);
        stage.Begin();
        foreach (var (path, content) in ExtractArchive())
            Stage(stage, path, content);

        var status = await stage.CommitAsync(Manifest(("app.exe", "v2 binary"), ("libs/x.dll", "v2 lib")));

        Assert.False(status.Pending);
        Assert.False(StageIn(dir).GetStatus().Pending);
    }

    /// <summary>An archive whose payload is correct but which also carries one entry nobody indexed.</summary>
    private static (string Path, string Content)[] ExtractArchive() =>
    [
        ("app.exe", "v2 binary"),
        ("libs/x.dll", "v2 lib"),
        ("libs/smuggled.dll", "payload"),
    ];

    /// <summary>A source serving the loose files its manifest lists — the shape `FetchAsync` expects.</summary>
    private sealed class FakeSource(UpdateManifest manifest, Dictionary<string, string> content) : IUpdateSource
    {
        public Task<UpdateManifest> GetManifestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(manifest);

        public Task<Stream> OpenAsync(ManifestFile file, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content[file.Path])));
    }
}
