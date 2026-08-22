using System.Text.RegularExpressions;
using Shenora.Engine.Compression;

namespace Shenora.Tests.Compression;

/// <summary>
/// <see cref="ResourcePackJournal.StampFileName"/> — and the agreement it keeps with <c>@shenora/cli</c>.
/// <para>
/// 🔴 <b>The stamp is written in TypeScript and read in C#, and nothing else can notice a drift.</b> The
/// two halves live in different languages, run at different times — one at build, one at boot — and a
/// mismatch produces no error on either side: the reader simply finds no stamp, the shell falls back to a
/// hand-maintained constant, and the version comparison that <see cref="ResourcePackJournal"/> exists to
/// force becomes wrong while looking right. Same reasoning as the IPC wire mirror.
/// </para>
/// </summary>
public class ResourcePackStampTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shenora-stamp-" + Guid.NewGuid().ToString("N")[..8]);

    public ResourcePackStampTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>The CLI source that owns the other half of this contract.</summary>
    private static string CliSource()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir is null, "repo root (Shenora.slnx) not found above the test output dir");
        var path = Path.Combine(dir!, "src", "Shenora.Cli", "src", "copy.ts");
        Assert.True(File.Exists(path), $"CLI source not found: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_stamp_FILE_NAME_matches_the_one_the_CLI_writes()
    {
        var match = Regex.Match(CliSource(), @"const\s+STAMP\s*=\s*'([^']+)'");
        Assert.True(match.Success, "copy.ts no longer declares `const STAMP = '…'` — the mirror cannot check "
                                 + "a name it cannot find, so this test would otherwise pass by going blind.");
        Assert.Equal(match.Groups[1].Value, ResourcePackJournal.StampFileName);
    }

    [Fact]
    public void The_stamp_KEY_matches_the_one_the_CLI_writes()
    {
        // ⚠ The NAME agreeing is not enough: a stamp written as {"packVersion":…} and read as "version"
        // is a file both sides find and neither understands.
        Assert.Matches(@"JSON\.stringify\(\s*\{\s*version\s*\}", CliSource());
    }

    [Fact]
    public void A_stamped_bundle_reports_its_version()
    {
        File.WriteAllText(Path.Combine(_dir, ResourcePackJournal.StampFileName), """{"version":"2.4.1"}""");

        Assert.Equal("2.4.1", ResourcePackJournal.PackagedVersionIn(_dir));
    }

    [Theory]
    [InlineData("""{"version":""}""")]
    [InlineData("""{"version":"   "}""")]
    [InlineData("""{"version":123}""")]
    [InlineData("""{"other":"2.4.1"}""")]
    [InlineData("{ not json")]
    public void A_stamp_that_names_no_usable_version_reads_as_ABSENT(string content)
    {
        // 🔴 Null rather than a throw or an empty string, and the difference matters: the caller's next move
        // is `Open(packagedVersion)`, which REFUSES a blank. A stamp that produced "" would turn a missing
        // version into an argument exception at boot instead of a decision the app can make.
        File.WriteAllText(Path.Combine(_dir, ResourcePackJournal.StampFileName), content);

        Assert.Null(ResourcePackJournal.PackagedVersionIn(_dir));
    }

    [Fact]
    public void A_bundle_with_no_stamp_at_all_reads_as_absent()
    {
        // The ordinary case for an older CLI, a hand-assembled bundle, or a web app that declares no
        // version — `shenora copy` writes no stamp rather than inventing one.
        Assert.Null(ResourcePackJournal.PackagedVersionIn(_dir));
    }

    [Fact]
    public void A_directory_that_does_not_exist_reads_as_absent_rather_than_throwing()
    {
        Assert.Null(ResourcePackJournal.PackagedVersionIn(Path.Combine(_dir, "no-such-bundle")));
    }
}
