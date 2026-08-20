namespace Shenora.Tests.Api;

/// <summary>
/// 🔴 <b>A doc comment separated from the next one by nothing but blank lines documents NOTHING.</b> C# and
/// TypeScript both bind a doc block to the declaration that immediately follows it, so when two blocks sit
/// back to back the first one reaches no reader — not an IDE, not IntelliSense, not the generated XML.
///
/// <para>
/// ⚠ <b>It is invisible to every other check.</b> The prose is well formed, the tags balance, the build is
/// clean, and the text is often true — it is simply attached to nothing. Three of these were found in one
/// review pass, by reading: two in the CLI (one block written for a function 60 lines below it, and a
/// "project directory on the build machine" doc sitting on a memo cache while asserting something the code
/// contradicted) and one in the React package (a <c>createShenoraStore</c> options doc on a private helper,
/// while the interface it described had none).
/// </para>
/// <para>
/// ⚠ <b>A block at line 1 is EXEMPT and that is not a loophole.</b> A file-header comment describing the
/// module is a deliberate TypeScript convention — <c>@packageDocumentation</c> is exactly this shape — and
/// it is meant to bind to the file rather than to a declaration.
/// </para>
/// </summary>
public class OrphanedDocBlockTests
{
    private static string RepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(OrphanedDocBlockTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly.");
    }

    /// <summary>
    /// Every shipping source file. ⚠ <c>node_modules</c> and generated output are excluded — third-party
    /// declaration files carry this shape routinely and are nobody here's to fix.
    /// </summary>
    private static IEnumerable<string> SourceFiles()
    {
        var src = Path.Combine(RepoRoot(), "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("node_modules/", StringComparison.Ordinal)) continue;

            var extension = Path.GetExtension(file);
            if (extension is ".cs" || (extension is ".ts" && !file.EndsWith(".test.ts", StringComparison.Ordinal)))
                yield return file;
        }
    }

    /// <summary>The half-open line ranges of each doc-comment run, in order.</summary>
    private static List<(int Start, int End)> DocRuns(string[] lines, bool csharp)
    {
        var runs = new List<(int, int)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (csharp)
            {
                if (!lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal)) continue;
                var start = i;
                while (i < lines.Length && lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal)) i++;
                runs.Add((start, i - 1));
                i--;
            }
            else
            {
                if (!lines[i].TrimStart().StartsWith("/**", StringComparison.Ordinal)) continue;
                var start = i;
                while (i < lines.Length && !lines[i].Contains("*/", StringComparison.Ordinal)) i++;
                runs.Add((start, Math.Min(i, lines.Length - 1)));
            }
        }
        return runs;
    }

    /// <summary>
    /// No doc block may be followed by another with only blank lines between them.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Sabotage-verified both ways</b> — inserting a stray doc block above an existing one names the
    /// file and both line numbers; removing it returns the suite to green. A gate over source text that has
    /// never been shown to fail is worth nothing.
    /// </remarks>
    [Fact]
    public void No_doc_comment_is_followed_by_another_with_only_blank_lines_between()
    {
        var orphans = new List<string>();

        foreach (var file in SourceFiles())
        {
            var lines = File.ReadAllLines(file);
            var runs = DocRuns(lines, Path.GetExtension(file) is ".cs");

            for (var i = 0; i + 1 < runs.Count; i++)
            {
                // A file HEADER binds to the file, not to a declaration — see the type's remarks.
                if (runs[i].Start == 0) continue;

                var gap = Enumerable.Range(runs[i].End + 1, runs[i + 1].Start - runs[i].End - 1);
                if (gap.Any() && gap.All(line => lines[line].Trim().Length == 0))
                {
                    orphans.Add($"{Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/')}"
                              + $": the doc block at line {runs[i].Start + 1} binds to nothing — the block at "
                              + $"line {runs[i + 1].Start + 1} is what the next declaration gets.");
                }
            }
        }

        Assert.True(orphans.Count == 0,
            "Doc comments that document nothing:\n  " + string.Join("\n  ", orphans)
            + "\n\nBoth languages bind a doc block to the declaration that IMMEDIATELY follows it, so the "
            + "first of two back-to-back blocks reaches no reader. Merge them, move the orphan onto what it "
            + "describes, or delete it.");
    }

    /// <summary>
    /// The scan must actually be looking at something — a filter that matches nothing is a gate that cannot
    /// fail, which is the failure this whole suite exists to avoid.
    /// </summary>
    [Fact]
    public void The_scan_covers_both_languages()
    {
        var files = SourceFiles().ToList();
        Assert.True(files.Count(f => Path.GetExtension(f) is ".cs") > 100, "the C# scan found almost nothing");
        Assert.True(files.Count(f => Path.GetExtension(f) is ".ts") > 20, "the TypeScript scan found almost nothing");
    }
}
