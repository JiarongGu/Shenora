namespace Shenora.Tests.Api;

/// <summary>
/// 🔴 <b>No <c>//</c> comment may appear inside a probe's JavaScript, because
/// <c>PageProbe.Safe</c> FLATTENS every script to one line before WKWebView will accept it — so a line
/// comment swallows the rest of the program.</b>
///
/// <para>
/// The failure is silent and expensive: the flattened script is a <c>SyntaxError</c>, the evaluation
/// returns null, and the probe reports <i>"the page did not answer"</i> — which reads as a broken page or a
/// broken navigation, not as a comment. Diagnosing it costs a build-and-deploy cycle on a simulator.
/// </para>
/// <para>
/// ⚠ <b>This exists because the prose did not work.</b> <c>Safe</c>'s own remarks already say
/// <i>"a <c>//</c> comment inside a script would swallow the rest of the program. Keep script commentary in
/// C#, outside the string"</i> — and a comment was added inside a script anyway, by someone who had read
/// that sentence. A documented invariant is not an enforced one; this is the enforcement.
/// </para>
/// <para>
/// Scoped to the files that actually call <c>EvaluateAsync</c>, since <c>Safe</c> is what creates the
/// hazard. A new probe file joins the list below.
/// </para>
/// </summary>
public class ProbeScriptTests
{
    /// <summary>The sample files whose raw strings reach <c>PageProbe.Safe</c>.</summary>
    private static readonly string[] ProbeFiles =
    [
        "PageProbe.cs", "PlayheadProbe.cs", "RemuxRouteProbe.cs", "SegmentRouteProbe.cs",
    ];

    private static string RepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(ProbeScriptTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly.");
    }

    private static string Source(string file) => File.ReadAllText(Path.Combine(
        RepoRoot(), "samples", "Shenora.Sample.Maui", file));

    /// <summary>
    /// Every file listed must still exist — otherwise a rename turns this gate into a no-op that passes.
    /// </summary>
    [Fact]
    public void The_scanned_probe_files_all_exist()
    {
        foreach (var file in ProbeFiles)
        {
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "samples", "Shenora.Sample.Maui", file)),
                        $"{file} is listed in {nameof(ProbeFiles)} but is not on disk — the gate would silently "
                        + "scan nothing. Update the list.");
        }
    }

    /// <summary>
    /// No <c>//</c> inside a raw-string block in any probe file.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>://</c> is allowed, so a URL inside a script does not read as a comment. That is the only
    /// exemption — a genuine line comment never follows a colon.
    /// </remarks>
    [Fact]
    public void No_probe_script_contains_a_line_comment()
    {
        var offenders = new List<string>();

        foreach (var file in ProbeFiles)
        {
            // Split on the raw-string delimiter: segments at ODD indices are inside a raw string, which for
            // these files is always a JavaScript body destined for Safe().
            var segments = Source(file).Split("\"\"\"");
            for (var i = 1; i < segments.Length; i += 2)
            {
                var script = segments[i];
                for (var at = script.IndexOf("//", StringComparison.Ordinal); at >= 0;
                     at = script.IndexOf("//", at + 2, StringComparison.Ordinal))
                {
                    if (at > 0 && script[at - 1] == ':') continue;   // https:// and friends

                    var line = script[..at].Count(c => c == '\n');
                    offenders.Add($"{file}: raw string #{(i + 1) / 2}, line {line + 1} of the script");
                }
            }
        }

        Assert.True(offenders.Count == 0,
                    "A `//` comment inside a probe script is flattened onto one line by PageProbe.Safe and "
                    + "swallows everything after it — the script becomes a SyntaxError and the probe reports "
                    + "'the page did not answer'. Move the commentary into C#, outside the string:\n  "
                    + string.Join("\n  ", offenders));
    }
}
