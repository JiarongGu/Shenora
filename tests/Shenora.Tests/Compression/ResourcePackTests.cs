using System.IO.Compression;
using Shenora.IO.Compression;

namespace Shenora.Tests.Compression;

/// <summary>
/// The pack mechanism the kit ships so an app can supply its own native payload without re-solving where
/// it goes, whether it is complete, and what happens to the old one.
///
/// <para>
/// The cases below are the four failures this type exists to prevent, and every one of them is a bug some
/// app has shipped: running a half-extracted binary, letting an archive write outside its own directory,
/// treating a partially-refused archive as usable, and deleting a version that is still loaded.
/// </para>
/// </summary>
public class ResourcePackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shenora-packs-" + Guid.NewGuid().ToString("N"));

    private ResourcePack Pack(string version = "1.0.0") =>
        new("engine", version, new ResourcePackOptions { Root = _root });

    /// <summary>A zip built in memory, so a test never depends on a fixture file on disk.</summary>
    private static MemoryStream Zip(params (string Path, string Content)[] entries)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(content);
            }
        }
        buffer.Position = 0;
        return buffer;
    }

    [Fact]
    public async Task A_staged_pack_is_ready_and_its_files_resolve()
    {
        var pack = Pack();
        Assert.False(pack.IsReady);

        using var zip = Zip(("arm64-v8a/libengine.so", "ELF"), ("LICENSE", "LGPL"));
        Assert.True(await pack.StageAsync(zip));

        Assert.True(pack.IsReady);
        var resolved = pack.PathOf("arm64-v8a/libengine.so");
        Assert.NotNull(resolved);
        Assert.Equal("ELF", File.ReadAllText(resolved!));
    }

    [Fact]
    public async Task A_second_stage_is_a_no_op_so_a_caller_need_not_track_whether_it_already_ran()
    {
        var pack = Pack();
        using var zip = Zip(("f.txt", "first"));
        await pack.StageAsync(zip);

        // A DIFFERENT archive under the same version must not silently replace the ready one: the version
        // is the identity, so re-staging it would make "which bytes am I running" unanswerable.
        using var other = Zip(("f.txt", "second"));
        Assert.True(await pack.StageAsync(other));
        Assert.Equal("first", File.ReadAllText(pack.PathOf("f.txt")!));
    }

    /// <summary>
    /// The marker is written LAST, so a pack whose extraction was interrupted reads as NOT ready rather
    /// than as a smaller pack. Simulated by deleting the marker, which is what a killed process leaves.
    /// </summary>
    [Fact]
    public async Task A_pack_without_its_marker_is_not_ready_and_resolves_nothing()
    {
        var pack = Pack();
        using var zip = Zip(("bin/tool", "x"));
        await pack.StageAsync(zip);
        Assert.NotNull(pack.PathOf("bin/tool"));

        File.Delete(Path.Combine(pack.Directory, ".ready"));

        Assert.False(pack.IsReady);
        // ⚠ The FILE IS STILL THERE. Resolving it anyway is the bug — an app would execute a binary from a
        // run that never finished.
        Assert.True(File.Exists(Path.Combine(pack.Directory, "bin", "tool")));
        Assert.Null(pack.PathOf("bin/tool"));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("bin/../../escape.txt")]
    public async Task A_path_that_escapes_the_pack_is_refused(string relative)
    {
        var pack = Pack();
        using var zip = Zip(("bin/tool", "x"));
        await pack.StageAsync(zip);

        Assert.Null(pack.PathOf(relative));
    }

    [Fact]
    public async Task An_absent_file_and_an_escaping_one_are_refused_IDENTICALLY()
    {
        // Same answer for both, deliberately: a distinguishable refusal turns this into a probe for what
        // exists on the device. Pinned as a test because "helpfully" reporting why is the obvious change.
        var pack = Pack();
        using var zip = Zip(("bin/tool", "x"));
        await pack.StageAsync(zip);

        Assert.Null(pack.PathOf("bin/does-not-exist"));
        Assert.Null(pack.PathOf("../escape.txt"));
    }

    /// <summary>
    /// An archive carrying an entry that would land outside the pack is not a smaller pack — it is a broken
    /// one — so nothing is staged and nothing is marked ready.
    /// </summary>
    [Fact]
    public async Task A_refused_entry_fails_the_whole_stage_and_leaves_nothing_ready()
    {
        var pack = Pack();
        using var zip = Zip(("good.txt", "ok"), ("../evil.txt", "no"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => pack.StageAsync(zip));

        Assert.False(pack.IsReady);
        Assert.False(File.Exists(Path.Combine(_root, "engine", "evil.txt")));
    }

    [Fact]
    public async Task Two_versions_coexist_until_the_app_decides_to_prune()
    {
        var v1 = Pack("1.0.0");
        var v2 = Pack("2.0.0");
        using var a = Zip(("f.txt", "one"));
        using var b = Zip(("f.txt", "two"));
        await v1.StageAsync(a);
        await v2.StageAsync(b);

        // Both readable: the OLD one is usually still loaded when the new one is staged, which is exactly
        // why staging must not collect.
        Assert.True(v1.IsReady);
        Assert.True(v2.IsReady);

        Assert.Equal(1, v2.PruneOthers());
        Assert.True(v2.IsReady);
        Assert.False(v1.IsReady);
    }

    [Fact]
    public void PruneOthers_on_a_pack_that_was_never_staged_is_zero_rather_than_a_throw()
        => Assert.Equal(0, Pack().PruneOthers());

    [Theory]
    [InlineData("../engine")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void A_name_or_version_carrying_a_separator_is_REJECTED_not_sanitised(string bad)
    {
        var options = new ResourcePackOptions { Root = _root };
        // Rejected, because silently rewriting it would let two different names collide on one directory.
        Assert.Throws<ArgumentException>(() => new ResourcePack(bad, "1.0.0", options));
        Assert.Throws<ArgumentException>(() => new ResourcePack("engine", bad, options));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir that will not go is not a test failure */ }
    }
}
