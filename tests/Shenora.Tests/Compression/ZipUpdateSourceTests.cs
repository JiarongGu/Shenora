using System.IO.Compression;
using System.Security.Cryptography;
using Shenora.IO.Compression;
using Shenora.Core;

namespace Shenora.Tests.Compression;

/// <summary>
/// The ZIP-backed <see cref="IUpdateSource"/>. The interesting cases are the ones a single-archive or
/// naive implementation gets wrong — a release spanning several archives, a manifest and a zip that spell
/// paths differently, and a file the manifest lists that nothing carries.
///
/// <para>
/// The last one matters most: the whole point of <see cref="UpdateStage"/> is that a truncated release is
/// never staged as if it were whole, and returning an empty stream for a missing entry would defeat that
/// with a SHA mismatch instead of a name.
/// </para>
/// </summary>
public class ZipUpdateSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shenora-zip-" + Guid.NewGuid().ToString("N")[..8]);

    public ZipUpdateSourceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string NewArchive(string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryPath).Open());
            writer.Write(content);
        }
        return path;
    }

    private static ManifestFile Entry(string path, string content) => new()
    {
        Path = path,
        Size = content.Length,
        Sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))),
    };

    private static UpdateManifest Manifest(params ManifestFile[] files) =>
        new() { Version = "1.0.0", Files = files };

    private static async Task<string> ReadAsync(IUpdateSource source, ManifestFile file)
    {
        await using var stream = await source.OpenAsync(file);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task A_release_SPANNING_several_archives_serves_every_file()
    {
        // One zip per PART is the shape that motivated this — a single-archive implementation would serve
        // half a release and throw on the rest.
        var backend = NewArchive("backend.zip", ("bin/api.dll", "api-bytes"));
        var frontend = NewArchive("frontend.zip", ("www/index.html", "page-bytes"));
        var manifest = Manifest(Entry("bin/api.dll", "api-bytes"), Entry("www/index.html", "page-bytes"));

        using var source = ZipUpdateSource.Open(manifest, backend, frontend);

        Assert.Equal("api-bytes", await ReadAsync(source, manifest.Files[0]));
        Assert.Equal("page-bytes", await ReadAsync(source, manifest.Files[1]));
    }

    [Fact]
    public async Task A_manifest_written_with_BACKSLASHES_matches_a_zip_written_with_forward_ones()
    {
        // Zip entries always use '/', a Windows-built manifest often uses '\'. Without normalising, every
        // file looks missing FOREVER — the same rule ManifestDiff already learned.
        var archive = NewArchive("app.zip", ("bin/tools/helper.exe", "helper"));
        var manifest = Manifest(Entry(@"bin\tools\helper.exe", "helper"));

        using var source = ZipUpdateSource.Open(manifest, archive);

        Assert.Equal("helper", await ReadAsync(source, manifest.Files[0]));
    }

    [Fact]
    public async Task Case_differences_between_the_manifest_and_the_archive_still_match()
    {
        var archive = NewArchive("app.zip", ("Bin/App.dll", "bytes"));
        var manifest = Manifest(Entry("bin/app.dll", "bytes"));

        using var source = ZipUpdateSource.Open(manifest, archive);

        Assert.Equal("bytes", await ReadAsync(source, manifest.Files[0]));
    }

    [Fact]
    public async Task A_file_NO_archive_carries_throws_and_NAMES_it()
    {
        // Not an empty stream. FetchAsync lets this escape on purpose so a truncated release is never
        // staged as whole — and the name is what makes the cause obvious instead of a SHA mismatch.
        var archive = NewArchive("app.zip", ("bin/app.dll", "bytes"));
        var manifest = Manifest(Entry("bin/app.dll", "bytes"), Entry("bin/missing.dll", "nope"));

        using var source = ZipUpdateSource.Open(manifest, archive);

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => source.OpenAsync(manifest.Files[1]));
        Assert.Contains("bin/missing.dll", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_archives_carrying_the_SAME_path_are_refused_rather_than_last_wins()
    {
        // Last-wins would make which bytes get installed depend on the ORDER the archives were passed —
        // reproducing on some inputs and not others, which is the worst shape a release bug can have.
        var first = NewArchive("a.zip", ("bin/app.dll", "from-a"));
        var second = NewArchive("b.zip", ("bin/app.dll", "from-b"));
        var manifest = Manifest(Entry("bin/app.dll", "from-a"));

        var error = Assert.Throws<InvalidOperationException>(() => ZipUpdateSource.Open(manifest, first, second));
        Assert.Contains("bin/app.dll", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_NON_SEEKABLE_stream_is_refused_up_front_with_the_reason()
    {
        // The trap the port note called out: ZipArchive reads the central directory from the END, so a live
        // HTTP response fails with an unhelpful format error deep inside. Rejected here, naming the fix.
        using var forwardOnly = new ForwardOnlyStream();

        var error = Assert.Throws<ArgumentException>(() =>
            new ZipUpdateSource(Manifest(Entry("a", "b")), [forwardOnly]));

        Assert.Contains("seekable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task It_composes_with_UpdateStage_end_to_end()
    {
        // The point of the whole exercise: a real FetchAsync against a real archive, so the bridge is
        // proven against the machinery it exists to feed rather than in isolation.
        var archive = NewArchive("app.zip", ("bin/app.dll", "v2-bytes"), ("www/index.html", "v2-page"));
        var release = Manifest(Entry("bin/app.dll", "v2-bytes"), Entry("www/index.html", "v2-page"));
        using var source = ZipUpdateSource.Open(release, archive);

        var stage = new UpdateStage(new UpdateStageOptions { Root = Path.Combine(_dir, "stage") });
        var status = await stage.FetchAsync(source, new UpdateManifest { Version = "1.0.0", Files = [] });

        Assert.True(status.Pending);
        Assert.Equal("1.0.0", status.Version);
        Assert.Equal("v2-bytes", await File.ReadAllTextAsync(Path.Combine(stage.StagedDirectory, "bin", "app.dll")));
    }

    /// <summary>A stream that cannot seek — what a live HTTP response body behaves like.</summary>
    private sealed class ForwardOnlyStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
