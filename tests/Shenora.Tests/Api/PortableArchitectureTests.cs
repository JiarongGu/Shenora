namespace Shenora.Tests.Api;

/// <summary>
/// 🔴 <b>THE ARCHITECTURE CLAIM THIS KIT IS TESTED AGAINST — that platforms differ only in what they can
/// DECODE, not in what the pipeline DOES.</b>
///
/// <para>
/// Owner, 2026-08-12: <i>"each device should only have difference in conversion, so test on 1 platform
/// should be able to tell the [architecture] is correct or not."</i> That is a statement with teeth: if it
/// holds, a device run on ONE platform validates probe → plan → remux → serve → player for every platform,
/// and the other shells need only their codec answers checked. If it stops holding, one-platform testing
/// silently stops proving anything and nothing would say so.
/// </para>
/// <para>
/// So it is asserted rather than believed. The portable assembly must contain <b>no platform conditional at
/// all</b> — measured 0 on the day this was written, across the whole of <c>src/Shenora/</c>.
/// </para>
/// <para>
/// ⚠ <b>WHERE DIFFERENCE IS ALLOWED TO LIVE, and it is deliberately not "nowhere":</b>
/// </para>
/// <list type="bullet">
/// <item><b>The codec seam</b> — <c>Android/IosMediaAudioConversion</c>, <c>AndroidMediaVideoConversion</c>,
/// <c>*MediaCapability</c>. This is the sanctioned difference, and the whole point of the claim.</item>
/// <item><b>The shell's own player</b> — <c>AVPlayer</c> against <c>android.media.MediaPlayer</c>. Different
/// bodies behind ONE contract, which is architecture-neutral: the pipeline never sees it.</item>
/// <item>🔴 <b>The WEBVIEW seam, which is the real exception and worth stating plainly.</b>
/// <c>MobileWebViewInterceptor</c> carries <c>#if ANDROID</c> for RANGE DELIVERY: Android's webview applies
/// the Range start to whatever body it is handed and ignores the end, while iOS and WebView2 need the body
/// sliced (D44). That is a media-path difference OUTSIDE conversion — so "ranges work on Android" does not
/// prove "ranges work on iOS", and it is exactly the kind of thing that ships broken on the platform nobody
/// ran. It lives in <c>Shenora.Mobile</c>, never in the pipeline.</item>
/// </list>
/// </summary>
public class PortableArchitectureTests
{
    private static string RepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(PortableArchitectureTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly.");
    }

    /// <summary>
    /// The portable assembly compiles once for every platform, so a platform conditional in it would mean
    /// the SAME type behaving differently per target — which is precisely the thing that makes a one-platform
    /// device run stop proving anything.
    /// </summary>
    [Fact]
    public void The_portable_assembly_contains_no_platform_conditional()
    {
        var portable = Path.Combine(RepoRoot(), "src", "Shenora");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(portable, "*.cs", SearchOption.AllDirectories))
        {
            // `obj/` and `bin/` carry generated sources that legitimately mention target frameworks.
            var relative = Path.GetRelativePath(portable, file).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (!line.StartsWith("#if", StringComparison.Ordinal) && !line.StartsWith("#elif", StringComparison.Ordinal)) continue;
                if (!line.Contains("ANDROID", StringComparison.Ordinal) && !line.Contains("IOS", StringComparison.Ordinal)
                    && !line.Contains("WINDOWS", StringComparison.Ordinal) && !line.Contains("MACCATALYST", StringComparison.Ordinal))
                {
                    continue;
                }
                offenders.Add($"{relative}:{i + 1}: {line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "The portable assembly must behave identically on every platform — a device run on ONE shell is "
            + "what validates the whole pipeline, and that only follows while this is true. Move the "
            + "platform-specific half behind a seam the shell implements (that is what IMediaStreamConversion, "
            + "IMediaCapability and IMediaPlayer are for):\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The media PIPELINE — probe, plan, remux, serve — must have no per-platform type at all. Everything a
    /// platform contributes arrives through a seam, which is what lets one device answer for the design.
    /// </summary>
    [Fact]
    public void The_media_pipeline_names_no_platform()
    {
        var pipeline = Path.Combine(RepoRoot(), "src", "Shenora", "Modules", "Media");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(pipeline, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            // ⚠ FILE NAMES, not prose. The docs in here name platforms constantly and must — "an iPhone
            // decodes AC-3, an AOSP Android does not" is the measurement the tier exists for. What must not
            // exist is a TYPE that is one platform's.
            if (name.Contains("Android", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Ios", StringComparison.Ordinal)
                || name.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add(Path.GetRelativePath(pipeline, file));
            }
        }

        Assert.True(offenders.Count == 0,
            "A platform-named type inside the media pipeline means the pipeline has grown a platform half, "
            + "and a one-platform device run stops proving the design:\n  " + string.Join("\n  ", offenders));
    }
}
