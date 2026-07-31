using System.Reflection;

namespace Shenora.Tests.Api;

/// <summary>
/// API-surface approval tests — the SemVer gate. Each assembly's consumer-visible surface is rendered
/// (see <see cref="ApiSurfaceDump"/> for WHAT is rendered and why each part earns its place) and compared
/// to a tracked baseline (<c>Api/Baselines/&lt;Assembly&gt;.txt</c>). Any add/remove/rename/signature
/// change FAILS until the baseline is updated: after an INTENTIONAL change, review the emitted
/// <c>&lt;Assembly&gt;.txt.actual</c> (gitignored), copy it over the baseline, and record the change in
/// <c>CHANGELOG.md</c> (breaking → under <c>### Breaking</c>). Don't blindly overwrite.
/// <para>
/// REVIEWING A DIFF: compare by TYPE SECTION, not with a flat line diff. A member moving from one type to
/// another renders the same text under a different header, so a flat diff pairs the two and shows an
/// addition with no matching removal — which is how a public static field becoming an option looked like
/// a pure add (P5.5 H3, found while reviewing exactly that change).
/// </para>
/// </summary>
public class ApiSurfaceTests
{
    /// <summary>
    /// Derived from the BASELINE FILES, not a hardcoded list (P5.5 H6). It used to be five
    /// <c>typeof(...).Assembly</c> literals — a second hand-maintained copy of
    /// <c>devtools/project.config.mjs</c>'s packable projects, where deleting a line silently reduced the
    /// theory to four cases and left an orphaned baseline that nothing compared against.
    /// <see cref="Every_shipped_assembly_has_a_baseline"/> closes the other direction.
    /// </summary>
    public static IEnumerable<object[]> ShenoraAssemblies() =>
        Directory.EnumerateFiles(BaselinesDir(), "*.txt")
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new object[] { Assembly.Load(name) });

    /// <summary>
    /// The gate is only as good as its coverage: deriving the cases from the baseline directory means a
    /// NEW package with no baseline would simply not be checked. This walks the test assembly's
    /// transitive references (so a package reached only indirectly — as <c>Shenora.Core</c> is — still
    /// counts) and fails if any shipped assembly has no baseline file.
    /// </summary>
    [Fact]
    public void Every_shipped_assembly_has_a_baseline()
    {
        var baselined = Directory.EnumerateFiles(BaselinesDir(), "*.txt")
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .ToHashSet(StringComparer.Ordinal);

        var shipped = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>([typeof(ApiSurfaceTests).Assembly]);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                var name = reference.Name!;
                if (!name.StartsWith("Shenora.", StringComparison.Ordinal) || !visited.Add(name)) continue;
                // Samples are never packable, so they are not "shipped" and have no API surface to
                // gate. The test project references one deliberately (the cookie-login driver moved
                // there in P7 and kept its tests), which otherwise reads as a new ungated package.
                // Narrow by PREFIX rather than by name: this must not become a hand-maintained list
                // of exceptions, which is the exact failure the case source above was rewritten to
                // avoid — and no real package can ever be called Shenora.Sample.*.
                if (name.StartsWith("Shenora.Sample.", StringComparison.Ordinal)) continue;
                shipped.Add(name);
                try { queue.Enqueue(Assembly.Load(reference)); }
                catch (Exception) { /* unresolvable reference — the baseline check below still reports it */ }
            }
        }

        var missing = shipped.Except(baselined).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0,
            $"These shipped assemblies have no API baseline, so their surface is ungated: {string.Join(", ", missing)}. " +
            $"Run the surface test to emit a .actual file and commit it as the baseline.");
    }

    [Theory]
    [MemberData(nameof(ShenoraAssemblies))]
    public void Public_surface_matches_baseline(Assembly assembly)
    {
        var name = assembly.GetName().Name!;
        var actual = ApiSurfaceDump.Render(assembly);
        var baselinePath = Path.Combine(BaselinesDir(), name + ".txt");

        if (!File.Exists(baselinePath))
        {
            File.WriteAllText(baselinePath + ".actual", actual);
            Assert.Fail($"No baseline for {name}. Review {baselinePath}.actual and copy it to {baselinePath}.");
        }

        var expected = File.ReadAllText(baselinePath).ReplaceLineEndings();
        if (expected != actual.ReplaceLineEndings())
        {
            File.WriteAllText(baselinePath + ".actual", actual);
            Assert.Fail($"{name} public surface drifted from its baseline. " +
                        $"Review {baselinePath}.actual, copy it over the baseline if intentional, and note the change in CHANGELOG.md.");
        }
    }

    /// <summary>The SOURCE-tree baselines dir (not the copied output) so .actual lands where git sees it.</summary>
    private static string BaselinesDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir is null, "repo root (Shenora.slnx) not found above the test output dir");
        return Path.Combine(dir!, "tests", "Shenora.Tests", "Api", "Baselines");
    }
}
