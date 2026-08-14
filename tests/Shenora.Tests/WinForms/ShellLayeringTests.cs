using Xunit;

namespace Shenora.Tests.WinForms;

/// <summary>
/// D19/D20's layering law, pinned where it was actually broken.
///
/// <para>
/// 🔴 <b>THE INVARIANT WAS FALSE AND THREE DOCUMENTS ASSERTED IT.</b> <c>Shell/</c> carries the Windows
/// desktop primitives and must not know the IPC stack exists — the direction is
/// <c>WebView/</c> → <c>Shell/</c> and never back. On 2026-08-10
/// <c>WinFormsUiDispatcher.cs</c> was found importing <c>Shenora.Core.Ipc</c>, and had been since the
/// namespace moved. Nothing caught it: the compiler cannot, because since D37 both halves live in ONE
/// package and an unnecessary <c>using</c> is legal; no analyser was configured for it; and the claim
/// was stated as settled fact in <c>DECISIONS.md</c>, <c>ARCHITECTURE.md</c> and the type's own XML doc.
/// </para>
///
/// <para>
/// ⚠ <b>It was a DEAD using — which is exactly what makes it worth a test.</b> Nothing depended on it,
/// so removing it changed no behaviour and broke no build; the layering violation was pure documentation
/// drift in the source itself. That is the shape that survives review forever: no symptom, no failure,
/// and a doc insisting it cannot happen. The next one will not be dead.
/// </para>
///
/// <para>
/// ⚠ <b>The <c>Shell/</c> → <c>WebView/</c> direction is deliberately NOT pinned here.</b> Both folders
/// compile into the same package and the same namespace, so there is no import to scan for — a text
/// check would be reading comments and reporting them as structure. That half is held by review and by
/// <c>ARCHITECTURE.md</c>, and saying so is better than a test that looks like it covers it.
/// </para>
/// </summary>
public class ShellLayeringTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir is null, "repo root (Shenora.slnx) not found above the test output dir");
        return dir!;
    }

    [Fact]
    public void Shell_primitives_do_not_reference_the_IPC_stack()
    {
        var shell = Path.Combine(RepoRoot(), "src", "Shenora.Windows", "Shell");
        Assert.True(Directory.Exists(shell), $"{shell} not found — did Shell/ move?");

        var sources = Directory.GetFiles(shell, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(sources);   // a glob that matches nothing would pass vacuously forever

        var offenders = sources
            .Where(f => File.ReadLines(f).Any(l => l.TrimStart().StartsWith("using Shenora.Core.Ipc",
                StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "D19/D20: Shell/ holds the Windows desktop primitives and must not reference the IPC stack — "
            + "the direction is WebView/ -> Shell/, never back. These files import Shenora.Core.Ipc: "
            + string.Join(", ", offenders) + ". If one genuinely needs IPC, the type belongs in WebView/ "
            + "(or the dependency belongs in a seam), not in a new using here.");
    }
}
