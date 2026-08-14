namespace Shenora.Tests.Api;

/// <summary>
/// 🔴 <b>THE ONE STRUCTURAL INVARIANT THE ANDROID MID-RESPONSE-THROW FIX RESTS ON: the mobile interceptor
/// hands a body to the platform in exactly ONE place.</b>
///
/// <para>
/// <c>MobileWebViewInterceptor.PlatformBody</c> wraps every Android response body so a mid-read failure
/// arrives in Java as a <c>java.io.IOException</c> — the one throwable Chromium's <c>InputStreamUtil.read</c>
/// already catches — instead of killing the process. The wrapper is applied inside <c>Answer</c>, which is the
/// single <c>e.SetResponse(…)</c> call site. **A second call site is therefore not a style problem, it is the
/// crash coming back for whichever path takes it, silently.**
/// </para>
/// <para>
/// ⚠ <b>That is not hypothetical — it is the code the fix DELETED.</b> The middleware-failure <c>catch</c>
/// used to build its own reply and call <c>e.SetResponse(refusal.StatusCode, …, refusal.Content)</c> directly,
/// bypassing everything <c>Answer</c> does. It was harmless then (that body is a <c>MemoryStream</c> and cannot
/// throw) and would not be now, and nothing else in the repo could notice: the marshalling half needs a device,
/// and every other gate is green either way.
/// </para>
/// <para>
/// 🔴 <b>Why a TEXT matcher is the right instrument here rather than a weak substitute.</b> The suite cannot
/// reference the mobile shells at all — it is one <c>net10.0-windows</c> project, and
/// <c>Java.IO.IOException</c> exists only in the <c>net10.0-android</c> TFM (constructing one needs a live JVM
/// for the peer), so the CONVERSION can only be asserted on a device and is
/// (<c>.claude/knowledge/mobile-shells.md</c> carries the three-arm A/B). What is left is a claim about the
/// SOURCE — "one call site" — and a source claim is exactly what a source check can hold.
/// <see cref="PortableArchitectureTests"/> in this same folder gates a structurally identical thing the same
/// way, walking <c>src/Shenora/**/*.cs</c> line by line for a platform conditional.
/// </para>
/// </summary>
public class MobileHandoverTests
{
    private static string RepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(MobileHandoverTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly.");
    }

    private static string InterceptorSource() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "Shenora.Mobile", "WebView", "MobileWebViewInterceptor.cs"));

    /// <summary>
    /// Exactly one <c>e.SetResponse(</c> — the seam where a managed <see cref="Stream"/> becomes a platform
    /// response body, and therefore the only place the Android wrapper can be applied.
    /// </summary>
    /// <remarks>
    /// ⚠ Counts the CALL (<c>e.SetResponse(</c>), not the word: the file's prose mentions
    /// <c>SetResponse</c> several times, deliberately, and a matcher that counted those would fail on a comment
    /// and teach the next reader to delete it.
    /// </remarks>
    [Fact]
    public void The_mobile_interceptor_hands_a_body_to_the_platform_in_exactly_one_place()
    {
        var lines = InterceptorSource().Split('\n');
        var callSites = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            // Skip comment and XML-doc lines: this file explains the invariant in prose right beside it.
            var code = line.TrimStart();
            if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal))
                continue;
            if (line.Contains("e.SetResponse(", StringComparison.Ordinal)) callSites.Add($"{i + 1}: {code.Trim()}");
        }

        Assert.True(callSites.Count == 1,
            "MobileWebViewInterceptor must call e.SetResponse in exactly ONE place — the seam where a managed "
            + "Stream becomes a platform body, and so the only place PlatformBody can wrap it. A second call "
            + "site is a body that skipped the wrapper, and on Android that is the difference between a failed "
            + "load and a DEAD PROCESS (see this class's remarks and .claude/knowledge/mobile-shells.md). "
            + $"Route the new reply through Answer instead. Found {callSites.Count}:\n  "
            + string.Join("\n  ", callSites));
    }

    /// <summary>
    /// That single call site passes the body through <c>PlatformBody</c>. The count above is worth nothing if
    /// the one survivor hands <c>response.Content</c> over raw.
    /// </summary>
    [Fact]
    public void The_one_handover_wraps_the_body_for_the_platform()
    {
        var source = InterceptorSource();
        var callSite = source.Split('\n')
            .Select((line, index) => (line, index))
            .Where(x => x.line.Contains("e.SetResponse(", StringComparison.Ordinal))
            .Select(x => string.Join(' ', source.Split('\n').Skip(x.index).Take(3)))
            .FirstOrDefault();

        Assert.NotNull(callSite);
        Assert.Contains("PlatformBody(", callSite);
        Assert.Contains("PlatformHeaders(", callSite);
    }
}
