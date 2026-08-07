using System.Text;
using Shenora;

namespace Shenora.Tests.Io;

/// <summary>
/// The guarantee is narrow and worth stating exactly: after any failure the PREVIOUS file is intact.
/// Losing one edit is recoverable; silently reverting to defaults is not — and that is the shape of the
/// bug this replaces, because `File.WriteAllText` truncates before it writes and config stores load
/// best-effort, so a half-written file does not error, it resets the user's data.
/// <para>
/// The first six are ported with the implementation (D8), including the failure simulation, which is
/// the cleverest part: making the temp PATH a directory fails the write at exactly the point a crash
/// would, with no mocking and no injected filesystem.
/// </para>
/// </summary>
public sealed class FilesTests : IDisposable
{
    private readonly string _dir;

    public FilesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "shenora-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private string At(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Writes_and_reads_back()
    {
        var path = At("settings.json");

        Files.WriteAllText(path, """{"Language":"zh"}""");
        Assert.Equal("""{"Language":"zh"}""", File.ReadAllText(path));
    }

    [Fact]
    public void Replaces_existing_content_rather_than_leaving_a_tail()
    {
        // A shorter value must not leave the previous one's tail behind — the classic bug from reusing
        // a stream instead of recreating the file.
        var path = At("settings.json");
        Files.WriteAllText(path, "a-much-longer-previous-value");

        Files.WriteAllText(path, "short");

        Assert.Equal("short", File.ReadAllText(path));
    }

    [Fact]
    public void Leaves_no_temp_file_behind_on_success()
    {
        var path = At("settings.json");

        Files.WriteAllText(path, "value");

        Assert.False(File.Exists(path + Files.DefaultTempSuffix));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_failed_write_leaves_the_PREVIOUS_file_intact()
    {
        // THE guarantee. A DIRECTORY at the temp path makes the write fail where a crash would.
        var path = At("settings.json");
        Files.WriteAllText(path, """{"Language":"zh"}""");
        Directory.CreateDirectory(path + Files.DefaultTempSuffix);

        // THROWS rather than returning false: a caller that ignores a failure carries on with a stale
        // file, which is the same silent failure this type exists to prevent one level up. Best-effort
        // is a POLICY the caller writes with a catch, not one the kit imposes.
        Assert.ThrowsAny<Exception>(() => Files.WriteAllText(path, """{"Language":"en"}"""));

        Assert.Equal("""{"Language":"zh"}""", File.ReadAllText(path));
    }

    [Fact]
    public void Creates_the_directory_when_it_does_not_exist_yet()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "settings.json");

        Files.WriteAllText(path, "value");
        Assert.Equal("value", File.ReadAllText(path));
    }

    [Fact]
    public void Writes_utf8_without_a_bom()
    {
        // A BOM is a silent format change for a file other tools already parse.
        var path = At("settings.json");

        Files.WriteAllText(path, """{"Language":"中文"}""");

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("""{"Language":"中文"}""", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void The_encoding_is_the_CALLER_s_choice_not_a_rule()
    {
        // The no-BOM default is right for JSON and for anything a shell script or a native launcher
        // parses — but it was hard-coded in the first draft, which shipped one adopter's requirement as
        // the kit's law and would have locked out an app talking to a legacy tool that NEEDS the BOM.
        var path = At("legacy.txt");

        Files.WriteAllText(path, "x", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public void Atomic_is_the_DEFAULT_so_a_caller_cannot_forget_it()
    {
        // The reason there is a mode rather than two types: the safe behaviour is what you get for
        // free, and Direct is the thing you have to ask for. An opt-IN to safety is forgotten exactly
        // where it matters.
        var path = At("settings.json");

        Files.WriteAllText(path, "value");   // no mode argument

        // Atomic went through a temp beside the target, and cleaned it up.
        Assert.False(File.Exists(path + Files.DefaultTempSuffix));
        Assert.Equal("value", File.ReadAllText(path));
    }

    [Fact]
    public void Direct_writes_straight_at_the_target_with_no_temp()
    {
        var path = At("huge.bin");

        Files.WriteAllText(path, "value", mode: FileWriteMode.Direct);

        Assert.Equal("value", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Direct_does_NOT_protect_the_previous_file_and_that_is_the_trade()
    {
        // Pinned so the difference is honest rather than implied. Atomic keeps the old contents when a
        // write fails; Direct has already truncated the target by then. A caller choosing Direct for
        // peak-disk or a share that will not rename is accepting exactly this.
        var path = At("huge.bin");
        Files.WriteAllText(path, "ORIGINAL");

        Assert.ThrowsAny<Exception>(() =>
            Files.Write(path, _ => throw new InvalidOperationException("producer failed"),
                        FileWriteMode.Direct));

        Assert.NotEqual("ORIGINAL", File.ReadAllText(path));   // truncated — the documented cost
    }

    [Fact]
    public void Atomic_DOES_protect_the_previous_file_when_the_producer_throws()
    {
        // The same failure, the other mode. This pair is the whole argument for the default.
        var path = At("settings.json");
        Files.WriteAllText(path, "ORIGINAL");

        Assert.ThrowsAny<Exception>(() =>
            Files.Write(path, _ => throw new InvalidOperationException("producer failed")));

        Assert.Equal("ORIGINAL", File.ReadAllText(path));
        Assert.False(File.Exists(path + Files.DefaultTempSuffix));   // and no debris
    }

    [Fact]
    public void Writes_bytes_verbatim()
    {
        var path = At("blob.bin");
        byte[] payload = [0x00, 0xFF, 0x10, 0x00];

        Files.WriteAllBytes(path, payload);
        Assert.Equal(payload, File.ReadAllBytes(path));
    }

    // ---- the TRANSFORM half: produce over time, verify, then swap ----------------------------------

    [Fact]
    public void A_transform_does_not_touch_the_target_until_it_commits()
    {
        // The reason the primitive exists at all: a long encode/compile must be able to fail without
        // costing the original. Everything before Commit is invisible to a reader of the target.
        var path = At("video.mp4");
        Files.WriteAllText(path, "ORIGINAL");

        using var transform = Files.BeginReplace(path);
        File.WriteAllText(transform.TempPath, "TRANSCODED");

        Assert.Equal("ORIGINAL", File.ReadAllText(path));   // still, mid-transform

        transform.Commit();
        Assert.Equal("TRANSCODED", File.ReadAllText(path));
    }

    [Fact]
    public void An_abandoned_transform_discards_the_temp_and_keeps_the_original()
    {
        // The verify-said-no path: a fully written but INVALID output must not be swapped in.
        var path = At("video.mp4");
        Files.WriteAllText(path, "ORIGINAL");

        using (var transform = Files.BeginReplace(path))
        {
            File.WriteAllText(transform.TempPath, "CORRUPT-BUT-COMPLETE");
            // no Commit — as if a probe rejected it
        }

        Assert.Equal("ORIGINAL", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_transform_commits_over_a_target_that_does_not_exist_yet()
    {
        // First run. File.Replace would throw here, which is why Commit uses File.Move — the bug an
        // implementation only meets on a fresh install.
        var path = At("new.bin");

        using var transform = Files.BeginReplace(path);
        File.WriteAllText(transform.TempPath, "FIRST");

        transform.Commit();
        Assert.Equal("FIRST", File.ReadAllText(path));
    }

    [Fact]
    public void The_temp_is_a_sibling_of_the_target()
    {
        // Not the system temp folder: a rename is only atomic within a volume, and across volumes it
        // silently degrades to copy-then-delete.
        var path = At("video.mp4");

        using var transform = Files.BeginReplace(path);

        Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(transform.TempPath));
    }

    [Fact]
    public void Distinct_temp_suffixes_let_two_transforms_of_one_target_coexist()
    {
        // The concurrency escape hatch. The DEFAULT suffix is shared on purpose (one predictable
        // leftover beats accumulating debris), which is exactly why a long transform needs its own.
        var path = At("video.mp4");

        using var first = Files.BeginReplace(path, ".a.tmp");
        using var second = Files.BeginReplace(path, ".b.tmp");

        Assert.NotEqual(first.TempPath, second.TempPath);
    }

    [Fact]
    public void Committing_twice_is_idempotent_rather_than_a_second_move()
    {
        var path = At("settings.json");

        using var transform = Files.BeginReplace(path);
        File.WriteAllText(transform.TempPath, "value");

        transform.Commit();
        transform.Commit();   // the temp is gone; a second call must be a no-op, not a failure
        Assert.Equal("value", File.ReadAllText(path));
    }
}
