using System.Reflection;
using System.Text;

namespace Shenora.Tests.Api;

/// <summary>
/// API-surface approval tests — the SemVer gate. Each assembly's public surface is dumped and
/// compared to a tracked baseline (<c>Api/Baselines/&lt;Assembly&gt;.txt</c>). Any add/remove/
/// rename FAILS until the baseline is updated: after an INTENTIONAL change, review the emitted
/// <c>&lt;Assembly&gt;.txt.actual</c> (gitignored), copy it over the baseline, and record the
/// change in <c>CHANGELOG.md</c> (breaking → under <c>### Breaking</c>). Don't blindly overwrite.
/// </summary>
public class ApiSurfaceTests
{
    public static IEnumerable<object[]> ShenoraAssemblies() =>
    [
        [typeof(global::Shenora.Core.ShenoraEnvironment).Assembly],
        [typeof(global::Shenora.Ipc.IpcRequest).Assembly],
        [typeof(global::Shenora.WebView2.BrowserArguments).Assembly],
        [typeof(global::Shenora.WinForms.DpiHelper).Assembly],
    ];

    [Theory]
    [MemberData(nameof(ShenoraAssemblies))]
    public void Public_surface_matches_baseline(Assembly assembly)
    {
        var name = assembly.GetName().Name!;
        var actual = Dump(assembly);
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

    /// <summary>Deterministic textual dump of an assembly's public surface.</summary>
    private static string Dump(Assembly assembly)
    {
        var sb = new StringBuilder();
        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            sb.AppendLine(type.FullName);
            var members = type
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.MemberType is not MemberTypes.NestedType)
                .Select(m => "  " + m)
                .OrderBy(s => s, StringComparer.Ordinal);
            foreach (var m in members) sb.AppendLine(m);
        }
        return sb.ToString();
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
