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
    public void The_stamp_name_is_NOT_HIDDEN_because_Android_discards_a_dot_prefixed_asset()
    {
        // 🔴 Measured, not assumed: two probe assets written side by side into a MAUI head, and
        // `AndroidComputeResPaths` passed the plain name through to the staged assets directory and
        // dropped the dot-prefixed one — with no message, a green build, and the item still listed by
        // `-getItem:MauiAsset`. So the stamp would be readable on iOS and desktop and absent on the
        // platform that has app stores, which is worse than being absent everywhere: the comparison
        // `Open` forces would simply stop happening there.
        Assert.False(ResourcePackJournal.StampFileName.StartsWith('.'),
            "the stamp must not be a hidden file — a dot-prefixed MauiAsset never reaches an Android app.");
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

    [Fact]
    public void A_stamp_written_with_a_UTF8_BOM_still_reads()
    {
        // ⚠ Not hypothetical on Windows: PowerShell's `-Encoding utf8` writes a BOM, so a stamp an adopter
        // hand-writes or repairs there carries one. The stamp is parsed from BYTES now rather than from a
        // decoded string, and the two treat a leading BOM differently — a version that silently read as
        // absent would send the app back to the packaged pack while looking like an unstamped build.
        File.WriteAllText(Path.Combine(_dir, ResourcePackJournal.StampFileName),
            """{"version":"2.4.1"}""", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal("2.4.1", ResourcePackJournal.PackagedVersionIn(_dir));
    }

    /// <summary>
    /// The STREAM overload — the one an Android app can actually call, because its packaged bundle is a
    /// set of app-package assets rather than a directory.
    /// </summary>
    public class FromAStream
    {
        private static Stream Bytes(string content) =>
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

        [Fact]
        public void Reads_the_version_the_path_overload_would_have_read()
        {
            using var stamp = Bytes("""{"version":"2.4.1"}""");

            Assert.Equal("2.4.1", ResourcePackJournal.PackagedVersionIn(stamp));
        }

        [Theory]
        [InlineData("""{"version":""}""")]
        [InlineData("""{"version":"   "}""")]
        [InlineData("""{"version":123}""")]
        [InlineData("""{"other":"2.4.1"}""")]
        [InlineData("{ not json")]
        [InlineData("")]
        [InlineData("\"just a string\"")]
        public void Answers_ABSENT_for_the_same_content_the_path_overload_rejects(string content)
        {
            // 🔴 The two overloads must not disagree: an app that switches from one to the other because
            // its bundle stopped being a directory would otherwise see its version appear or vanish.
            using var stamp = Bytes(content);

            Assert.Null(ResourcePackJournal.PackagedVersionIn(stamp));
        }

        [Fact]
        public void Leaves_the_stream_OPEN_because_the_caller_opened_it()
        {
            // The caller got it from a platform asset manager and may still be holding it; disposing
            // someone else's stream is the kind of theft that only shows up in their next read.
            using var stamp = Bytes("""{"version":"2.4.1"}""");

            ResourcePackJournal.PackagedVersionIn(stamp);

            Assert.True(stamp.CanRead);
        }

        [Fact]
        public void A_stream_that_THROWS_reads_as_absent_rather_than_taking_the_app_down()
        {
            // Same failure direction as an unreadable file: the app still starts, on its packaged bundle.
            using var stamp = new ThrowingStream();

            Assert.Null(ResourcePackJournal.PackagedVersionIn(stamp));
        }

        private sealed class ThrowingStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new IOException("gone");
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
