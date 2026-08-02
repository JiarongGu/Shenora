using System.Reflection;
using System.Text.RegularExpressions;

namespace Shenora.Tests.Api;

/// <summary>
/// The GENERICITY gate — the companion to <see cref="ApiSurfaceTests"/>, which is a SemVer gate and
/// nothing more. That one proves the public surface did not change by accident; this one asks whether
/// a change should have been made at all, against the owner's standing criterion for the repo: *"make
/// sure this is a library — we are not solving specific business logic; everything here has to be
/// generic enough that any of our applications can adopt it."*
/// <para>
/// Until this existed that criterion had no tripwire, which made it the only load-bearing invariant in
/// the repo enforced solely by a reviewer remembering to look — and `ApiSurfaceTests`' own documented
/// workflow (copy the emitted <c>.actual</c> over the baseline) walks domain vocabulary straight
/// through. D22 says every public type is named for its MECHANISM; this is D22 with teeth.
/// </para>
/// <para>
/// It works off an ALLOW-LIST (<c>Api/surface-lexicon.txt</c>) rather than a blocklist of business
/// words, because a blocklist only catches the domain nouns someone already thought of — and the leak
/// that matters comes from the app nobody anticipated. The lexicon file carries the full reasoning.
/// </para>
/// </summary>
public class SurfaceVocabularyTests
{
    /// <summary>PascalCase segmentation: <c>WebView2</c> → Web, View2; <c>DpiHelper</c> → Dpi, Helper.</summary>
    private static readonly Regex WordPattern = new(@"[A-Z][a-z0-9]*|[A-Z]+(?![a-z])", RegexOptions.Compiled);

    [Theory]
    [MemberData(nameof(ApiSurfaceTests.ShenoraAssemblies), MemberType = typeof(ApiSurfaceTests))]
    public void Public_type_names_use_only_mechanism_vocabulary(Assembly assembly)
    {
        var lexicon = Lexicon();
        var offenders = new List<string>();

        foreach (var type in assembly.GetExportedTypes())
        {
            foreach (var word in WordsOf(type))
            {
                if (lexicon.Contains(word)) continue;
                offenders.Add($"  {type.FullName} — unknown word \"{word}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            $"""
             {assembly.GetName().Name} has public type names built from words that are not in the
             shell/platform lexicon:

             {string.Join(Environment.NewLine, offenders.Distinct().Order(StringComparer.Ordinal))}

             This is the genericity gate, not a spelling check. Two ways forward, and the choice is
             the whole point of the gate:
               1. The word is DOMAIN vocabulary — an application's concept leaking into the kit.
                  Rename the type after the MECHANISM it provides (D22), so every sibling app can
                  adopt it. This is the common case and the reason the gate exists.
               2. The word is genuinely generic shell/platform vocabulary the kit had not needed yet.
                  Add it to tests/Shenora.Tests/Api/surface-lexicon.txt, in the right section.
             """);
    }

    /// <summary>
    /// The lexicon must not rot into a list of words nothing uses — an allow-list that only ever grows
    /// stops being a review of anything. A word retired from the surface has to leave the file with the
    /// type that justified it.
    /// </summary>
    [Fact]
    public void Lexicon_has_no_unused_words()
    {
        var used = ApiSurfaceTests.ShenoraAssemblies()
            .Select(row => (Assembly)row[0])
            .SelectMany(assembly => assembly.GetExportedTypes())
            .SelectMany(WordsOf)
            .ToHashSet(StringComparer.Ordinal);

        // The metadata-gated assemblies count as USED too, or a word that only their type names need
        // (Maui) would look unused and this test would demand its removal — which would then fail the
        // vocabulary gate that requires it. Two gates over one lexicon must read the same surface.
        foreach (var word in MetadataSurfaceTests.AllExportedTypeWords()) used.Add(word);

        var unused = Lexicon().Except(used).Order(StringComparer.Ordinal).ToArray();

        Assert.True(unused.Length == 0,
            $"surface-lexicon.txt allows words no public type uses any more: {string.Join(", ", unused)}. " +
            "Remove them — an allow-list that only grows reviews nothing.");
    }

    /// <summary>
    /// Words of a type name, with the <c>I</c>-prefix convention stripped so <c>IClipboardService</c>
    /// costs Clipboard + Service rather than a bogus <c>I</c>. Generic arity (<c>`1</c>) is dropped.
    /// </summary>
    private static IEnumerable<string> WordsOf(Type type) => WordsOfName(type.Name);

    /// <summary>
    /// <see cref="WordsOf"/> over a bare NAME — shared with <see cref="MetadataSurfaceTests"/>, which
    /// gates assemblies this project cannot reference and so has no <see cref="Type"/> to pass.
    /// One splitter, so the two gates cannot disagree about what a word is.
    /// </summary>
    internal static IEnumerable<string> WordsOfName(string typeName)
    {
        var name = typeName;
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        if (name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1])) name = name[1..];
        return WordPattern.Matches(name).Select(m => m.Value);
    }

    internal static HashSet<string> Lexicon()
    {
        var path = Path.Combine(BaselinesDir(), "..", "surface-lexicon.txt");
        Assert.True(File.Exists(path), $"missing lexicon at {Path.GetFullPath(path)}");
        return File.ReadAllLines(path)
            .Select(line => line.Split('#')[0].Trim())
            .Where(line => line.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Mirrors <see cref="ApiSurfaceTests"/>: walk up to the repo root marker.</summary>
    private static string BaselinesDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir is null, "repo root (Shenora.slnx) not found above the test output dir");
        return Path.Combine(dir!, "tests", "Shenora.Tests", "Api", "Baselines");
    }
}
