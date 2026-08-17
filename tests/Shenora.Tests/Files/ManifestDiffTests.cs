using System.Text.Json;
using Shenora;
using Shenora.Engine.Update;
using Shenora.Engine.Files;

namespace Shenora.Tests.Io;

/// <summary>
/// The changeset both sibling apps hand-rolled twice. Pure inputs and outputs, so the cases that
/// matter are the ones where a plausible-but-wrong implementation would still look right.
/// </summary>
public class ManifestDiffTests
{
    private static ManifestFile File(string path, string sha, long size = 10) =>
        new() { Path = path, Size = size, Sha256 = sha };

    private static UpdateManifest Manifest(params ManifestFile[] files) =>
        new() { Version = "1.0", Files = files };

    /// <summary>
    /// 🔴 The manifest is the only input in this kit that comes from a REMOTE server, and it drives both
    /// <c>File.Create</c> and <c>File.Delete</c>. A path that can resolve outside the install root is
    /// refused for the WHOLE manifest, in either position — nothing is written and nothing is deleted.
    /// <para>
    /// Both escaping shapes are covered because they fail differently: a <c>..</c> segment walks out the
    /// ordinary way, while a ROOTED path makes <c>Path.Combine</c> discard the root entirely — the quirk
    /// <c>UpdateStage.ResolveBaselinePath</c> names as one this repo has already had to fix a security
    /// bug over. Hash verification cannot catch either: it checks a file's CONTENT, never its PATH.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("libs/../../escape.txt")]
    [InlineData("/etc/passwd")]
    public void A_manifest_path_that_can_escape_the_install_root_is_refused(string escaping)
    {
        var clean = Manifest(File("app.exe", "aaa"));
        var hostile = Manifest(File("app.exe", "aaa"), File(escaping, "bbb"));

        // In the RELEASE position it would be written; in the INSTALLED position it would be deleted.
        Assert.Throws<ArgumentException>(() => ManifestDiff.Compute(clean, hostile));
        Assert.Throws<ArgumentException>(() => ManifestDiff.Compute(hostile, clean));
    }

    /// <summary>
    /// A Windows-rooted path, kept separate because it only IS rooted on Windows —
    /// <c>Path.IsPathRooted</c> is platform-correct by design, and asserting the refusal unconditionally
    /// would fail the suite on a POSIX runner for the right reason.
    /// </summary>
    [Fact]
    public void A_windows_rooted_manifest_path_is_refused_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var hostile = Manifest(File(@"C:\Windows\System32\evil.dll", "bbb"));
        Assert.Throws<ArgumentException>(() => ManifestDiff.Compute(Manifest(), hostile));
    }

    /// <summary>
    /// The other direction, and the one that makes the refusal above worth anything: ordinary nested
    /// paths, and a file whose NAME merely begins with dots, still diff normally. A guard that also
    /// refuses <c>..foo</c> would be matching a substring rather than a segment.
    /// </summary>
    [Fact]
    public void Ordinary_nested_paths_and_dotted_names_still_diff()
    {
        var installed = Manifest(File("libs/deep/nested/app.dll", "aaa"), File("..config", "bbb"));
        var release = Manifest(File("libs/deep/nested/app.dll", "CHANGED"), File("..config", "bbb"));

        var diff = ManifestDiff.Compute(installed, release);

        Assert.Equal(["libs/deep/nested/app.dll"], diff.Updated.Select(f => f.Path));
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void Splits_added_updated_and_removed()
    {
        var installed = Manifest(File("app.exe", "aaa"), File("libs/keep.dll", "bbb"), File("gone.txt", "ccc"));
        var release = Manifest(File("app.exe", "AAA-CHANGED"), File("libs/keep.dll", "bbb"), File("new.txt", "ddd"));

        var diff = ManifestDiff.Compute(installed, release);

        Assert.Equal(["new.txt"], diff.Added.Select(f => f.Path));
        Assert.Equal(["app.exe"], diff.Updated.Select(f => f.Path));
        Assert.Equal(["gone.txt"], diff.Removed);
        // keep.dll is in neither list — the whole point of a differential update.
        Assert.False(diff.IsEmpty);
    }

    [Fact]
    public void An_identical_manifest_produces_nothing_to_do()
    {
        var files = new[] { File("app.exe", "aaa"), File("libs/x.dll", "bbb") };
        var diff = ManifestDiff.Compute(Manifest(files), Manifest(files));

        Assert.True(diff.IsEmpty);
        Assert.Equal(0, diff.DownloadBytes);
    }

    [Fact]
    public void Hash_comparison_ignores_case()
    {
        // Generators disagree about hex casing. Treating "ABC" and "abc" as different would report
        // EVERY file changed on the first update produced by a different tool — a full redownload
        // that looks exactly like a legitimate one.
        var diff = ManifestDiff.Compute(Manifest(File("app.exe", "abc123")), Manifest(File("app.exe", "ABC123")));

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void Path_comparison_normalizes_separators_and_case()
    {
        // A manifest written with backslashes must diff cleanly against one written with forward
        // slashes; otherwise the same file is "added" on every check and the update never converges.
        var diff = ManifestDiff.Compute(
            Manifest(File(@"libs\App.dll", "aaa")),
            Manifest(File("libs/app.dll", "aaa")));

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void DownloadBytes_counts_added_and_updated_only()
    {
        var installed = Manifest(File("same.dll", "aaa", 1000), File("old.dll", "bbb", 9999));
        var release = Manifest(File("same.dll", "aaa", 1000), File("new.dll", "ccc", 30), File("changed.dll", "ddd", 12));

        var diff = ManifestDiff.Compute(installed, release);

        // 30 + 12. NOT the unchanged file (that is the saving), and NOT the removed one (nothing is
        // fetched to delete something).
        Assert.Equal(42, diff.DownloadBytes);
    }

    [Fact]
    public void An_empty_release_removes_everything_which_is_why_callers_must_validate_first()
    {
        // Pinned deliberately rather than defended against here: Compute is a pure function and an
        // empty release legitimately means "everything went away". The DANGER is handing it a
        // manifest that failed to load — the changeset then deletes the whole install as the
        // successful outcome of a copy. One sibling's applier carries exactly this guard and the
        // other does not; this test exists so the behaviour is a decision rather than a surprise.
        var diff = ManifestDiff.Compute(Manifest(File("app.exe", "aaa"), File("x.dll", "bbb")), Manifest());

        Assert.Equal(["app.exe", "x.dll"], diff.Removed);
        Assert.Empty(diff.Added);
    }

    [Fact]
    public void A_duplicate_path_is_loud_rather_than_last_wins()
    {
        // Last-wins would make the changeset depend on list order — a bug that reproduces only on
        // some inputs. The message names the offending path and which manifest carried it.
        var duplicated = Manifest(File("app.exe", "aaa"), File(@"App.EXE", "bbb"));

        var error = Assert.Throws<ArgumentException>(() => ManifestDiff.Compute(duplicated, Manifest()));

        Assert.Contains("more than once", error.Message, StringComparison.Ordinal);
        Assert.Contains("installed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Results_are_ordered_so_a_changeset_is_reviewable_and_repeatable()
    {
        var release = Manifest(File("z.dll", "1"), File("a.dll", "2"), File("m.dll", "3"));

        var diff = ManifestDiff.Compute(Manifest(), release);

        // Dictionary enumeration order is not a contract, and this list is shown to users.
        Assert.Equal(["a.dll", "m.dll", "z.dll"], diff.Added.Select(f => f.Path));
    }

    [Fact]
    public void Round_trips_through_json_in_the_shape_the_siblings_write()
    {
        var manifest = new UpdateManifest
        {
            Version = "2.5",
            GeneratedAt = DateTimeOffset.Parse("2026-08-02T05:41:14+00:00"),
            Files = [File("libs/7z.dll", "bbd705e3", 1908736)],
        };

        var json = manifest.ToJson();

        // camelCase keys, matching what both siblings already emit — a manifest written by the kit
        // must be readable by an applier written against theirs.
        using (var doc = JsonDocument.Parse(json))
        {
            var file = doc.RootElement.GetProperty("files")[0];
            Assert.Equal("libs/7z.dll", file.GetProperty("path").GetString());
            Assert.Equal(1908736, file.GetProperty("size").GetInt64());
            Assert.Equal("bbd705e3", file.GetProperty("sha256").GetString());
            Assert.Equal("2.5", doc.RootElement.GetProperty("version").GetString());
        }

        var parsed = UpdateManifest.Parse(json);
        Assert.Equal("2.5", parsed.Version);
        Assert.Single(parsed.Files);
        Assert.True(ManifestDiff.Compute(manifest, parsed).IsEmpty);
    }

    [Fact]
    public void Malformed_json_throws_rather_than_yielding_an_empty_manifest()
    {
        // The dangerous shape: a manifest that "loads" to nothing drives a diff that removes
        // everything. Parse must refuse instead.
        Assert.ThrowsAny<JsonException>(() => UpdateManifest.Parse("{ not json"));
        Assert.ThrowsAny<JsonException>(() => UpdateManifest.Parse("null"));
    }
}
