using System.IO.Compression;
using Shenora.Engine.Compression;
using Shenora.Engine.Update;
using Shenora.Core.WebView;
using Shenora.Engine.Files;

namespace Shenora.Tests.Compression;

/// <summary>
/// Safe ZIP extraction. The extraction itself is one framework call — what is worth testing is everything
/// it REFUSES, because an archive is a list of paths chosen by whoever built it.
///
/// <para>
/// ⚠ The donor this was harvested from has no containment check of its own; it relies on its native
/// extractor's behaviour. That is the gap `extraction-sources.md` says to FIX during a port rather than
/// carry, so these tests are the point of the port, not a formality.
/// </para>
/// </summary>
public class ZipExtractionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shenora-zx-" + Guid.NewGuid().ToString("N")[..8]);

    public ZipExtractionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Build a zip with entry names written VERBATIM — the whole point is hostile names.</summary>
    private string NewZip(string name, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write(content);
        }
        return path;
    }

    private string Destination(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void An_ordinary_archive_extracts_with_its_directory_structure()
    {
        var zip = NewZip("ok.zip", ("readme.txt", "hello"), ("bin/app.dll", "bytes"));
        var into = Destination("ok");

        var result = ZipExtraction.ExtractTo(zip, into);

        Assert.Equal(2, result.Files.Count);
        Assert.Empty(result.Refused);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(into, "readme.txt")));
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(into, "bin", "app.dll")));
    }

    [Fact]
    public void An_entry_climbing_out_with_dot_dot_is_REFUSED_and_named()
    {
        // Zip slip. The escaping entry must not be written ANYWHERE — and the caller has to be able to
        // find out it was attempted, which is why it is named rather than silently dropped.
        var zip = NewZip("evil.zip", ("../escaped.txt", "pwned"), ("safe.txt", "fine"));
        var into = Destination("evil");

        var result = ZipExtraction.ExtractTo(zip, into);

        Assert.Equal("../escaped.txt", Assert.Single(result.Refused));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")), "the escaping entry was written");
        // The rest of the archive still extracts: one hostile entry is usually still an archive you want.
        Assert.Equal("fine", File.ReadAllText(Path.Combine(into, "safe.txt")));
    }

    [Fact]
    public void A_BACKSLASH_traversal_is_refused_too()
    {
        // On Windows both separators are separators, so a check that only knew about '/' would treat
        // `..\..\x` as a FILE NAME and write it happily.
        var zip = NewZip("evil2.zip", (@"..\..\escaped-back.txt", "pwned"));
        var into = Destination("evil2");

        var result = ZipExtraction.ExtractTo(zip, into);

        Assert.Single(result.Refused);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void A_SIBLING_directory_sharing_the_destination_s_PREFIX_is_refused()
    {
        // The prefix bug WebViewFiles.ResolveContained already documents: without a separator appended to
        // the fence, `data-evil` passes as a child of `data`. Two features needing the identical rule.
        var into = Destination("data");
        Directory.CreateDirectory(Path.Combine(_root, "data-evil"));
        var zip = NewZip("prefix.zip", ("../data-evil/x.txt", "pwned"));

        var result = ZipExtraction.ExtractTo(zip, into);

        Assert.Single(result.Refused);
        Assert.False(File.Exists(Path.Combine(_root, "data-evil", "x.txt")));
    }

    [Fact]
    public void Exceeding_the_total_size_bound_THROWS_rather_than_stopping_quietly()
    {
        // The zip-bomb bound. It throws because a partial extraction that stopped silently would leave the
        // caller believing it had the whole archive.
        var zip = NewZip("big.zip", ("a.txt", new string('x', 500)), ("b.txt", new string('x', 500)));
        var into = Destination("big");

        var error = Assert.Throws<InvalidOperationException>(() =>
            ZipExtraction.ExtractTo(zip, into, new ExtractionLimits { MaxTotalBytes = 600 }));

        Assert.Contains("zip-bomb", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exceeding_the_entry_count_bound_THROWS()
    {
        var zip = NewZip("many.zip", ("a.txt", "1"), ("b.txt", "2"), ("c.txt", "3"));
        var into = Destination("many");

        Assert.Throws<InvalidOperationException>(() =>
            ZipExtraction.ExtractTo(zip, into, new ExtractionLimits { MaxEntries = 2 }));
    }

    [Fact]
    public void A_directory_entry_creates_no_file_and_costs_no_budget()
    {
        // A zip may or may not carry explicit directory entries. Counting one against MaxEntries, or
        // trying to write it as a file, would make the same archive behave differently by how it was built.
        var path = Path.Combine(_root, "dirs.zip");
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            archive.CreateEntry("nested/");
            using var writer = new StreamWriter(archive.CreateEntry("nested/f.txt").Open());
            writer.Write("v");
        }
        var into = Destination("dirs");

        var result = ZipExtraction.ExtractTo(path, into);

        Assert.Single(result.Files);
        Assert.Equal("v", File.ReadAllText(Path.Combine(into, "nested", "f.txt")));
    }
}
