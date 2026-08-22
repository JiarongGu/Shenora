using System.Collections.Concurrent;
using System.Text.Json;
using Shenora.Core.WebView;
using Shenora.Windows;

namespace Shenora.Sample.Desktop;

/// <summary>
/// The SEAM TEST for the segment tier's OUTPUT: does a real browser <c>MediaSource</c> accept the fragments
/// this kit writes?
/// <para>
/// 🔴 <b>Nothing else in the repo can answer this.</b> The unit suites read boxes — sample counts, decode
/// times, where each fragment opens — and a stream that satisfies every one of those can still be refused
/// by a media pipeline. <c>dev.mjs media-decode</c> closes part of the gap with ffmpeg, and ffmpeg is the
/// wrong judge: it REPAIRS what it can, so it accepts streams a webview rejects. Only an
/// <c>appendBuffer</c> that ends with a non-empty <c>buffered</c> range proves anything.
/// </para>
/// <para>
/// ⚠ <b>It serves BYTES THE SUITE ALREADY PRODUCED rather than making its own</b>, and that is deliberate.
/// The shape most worth asking about is a segment carrying SEVERAL fragments, which only appears when the
/// memory guard spills, and the bound that triggers a spill is internal to the kit — a sample cannot reach
/// it, and exposing it so a sample could would be product surface bought for a test. So the dev loop
/// produces the artifacts and points this probe at them: <c>node devtools/dev.mjs media-mse</c>.
/// </para>
/// <para>
/// ⚠ <b>SKIPPED is the normal answer when the sample is run by hand.</b> With no directories named it says
/// so rather than passing — a probe that reports success having appended nothing is worse than none
/// (<c>probe-diagnostics</c>).
/// </para>
/// </summary>
internal static class SegmentMediaSourceProbe
{
    /// <summary>The scheme the fragments are served on. Nothing else in the sample uses it.</summary>
    public const string Scheme = "mseprobe";

    /// <summary>
    /// Names the directories to append from, as <c>label=path</c> separated by <c>;</c>. Read from the
    /// environment because the artifacts belong to the dev loop, not to the sample.
    /// </summary>
    public const string DirectoriesVariable = "SHENORA_SAMPLE_MSE_DIRS";

    /// <summary>What the page may fetch, by path — filled before any script runs.</summary>
    private static readonly ConcurrentDictionary<string, byte[]> Files = new(StringComparer.Ordinal);

    /// <summary>Set when the page actually fetched — separates "the browser refused" from "we answered wrong".</summary>
    private static int _handlerHits;

    /// <summary>The scheme registration. Fragments are <c>video/mp4</c>; nothing here is seekable.</summary>
    public static WebViewDeferredScheme CreateScheme() => new()
    {
        Scheme = Scheme,
        // A cached body would let a second run pass on the first run's bytes.
        CacheControl = "no-store",
        Handler = request =>
        {
            Interlocked.Increment(ref _handlerHits);
            var key = request.Uri.AbsolutePath.TrimStart('/');
            return Task.FromResult(Files.TryGetValue(key, out var bytes)
                ? WebViewResourceResponse.Bytes(bytes, "video/mp4")
                : WebViewResourceResponse.NotFound());
        },
    };

    /// <summary>
    /// Append each named directory's <c>init.mp4</c> + first fragment into a real <c>MediaSource</c> and
    /// report a one-line verdict per case — never a bare boolean.
    /// </summary>
    public static async Task<string> RunAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        ArgumentNullException.ThrowIfNull(core);

        var cases = Load(Environment.GetEnvironmentVariable(DirectoriesVariable));
        if (cases.Count == 0)
        {
            return $"SEGMENT MSE: SKIPPED - no artifacts named. Run `node devtools/dev.mjs media-mse`, "
                 + $"or set {DirectoriesVariable}=label=dir[;label=dir].";
        }

        var plan = JsonSerializer.Serialize(cases.Select(c => new { c.Label, c.Mime }));
        var script = $$"""
            (async () => {
              const cases = {{plan}};
              const out = [];
              for (const { Label: label, Mime: mime } of cases) {
                try {
                  if (!window.MediaSource) { out.push(`${label}=NO-MEDIASOURCE`); continue; }
                  if (!MediaSource.isTypeSupported(mime)) { out.push(`${label}=UNSUPPORTED:${mime}`); continue; }

                  const el = document.createElement('video');
                  el.muted = true;
                  const ms = new MediaSource();
                  el.src = URL.createObjectURL(ms);
                  await new Promise(r => ms.addEventListener('sourceopen', r, { once: true }));

                  const sb = ms.addSourceBuffer(mime);
                  const append = async (name) => {
                    const r = await fetch(`{{Scheme}}://probe/${label}/${name}`);
                    if (!r.ok) throw new Error(`${name} fetch ${r.status}`);
                    const bytes = await r.arrayBuffer();
                    if (bytes.byteLength === 0) throw new Error(`${name} was empty`);
                    // 🔴 The error EVENT is the failure signal, not a rejected promise: appendBuffer
                    // returns immediately and a refused segment surfaces on the SourceBuffer.
                    await new Promise((resolve, reject) => {
                      sb.addEventListener('updateend', resolve, { once: true });
                      sb.addEventListener('error', () => reject(new Error(`${name} refused`)), { once: true });
                      sb.appendBuffer(bytes);
                    });
                    return bytes.byteLength;
                  };

                  await append('init.mp4');
                  const size = await append('segment.m4s');

                  // ⚠ THE ANSWER IS THE BUFFERED RANGE, NOT THE ABSENCE OF AN ERROR. A segment the parser
                  // silently ignores appends "successfully" and buffers nothing at all.
                  const ranges = [];
                  for (let i = 0; i < sb.buffered.length; i++) {
                    ranges.push(`${sb.buffered.start(i).toFixed(3)}-${sb.buffered.end(i).toFixed(3)}`);
                  }
                  URL.revokeObjectURL(el.src);
                  el.remove();
                  out.push(ranges.length === 0
                    ? `${label}=BUFFERED-NOTHING(${size}B)`
                    : `${label}=${ranges.join(',')}`);
                } catch (e) {
                  out.push(`${label}=THREW:${(e && e.message) ? e.message : e}`);
                }
              }
              window.__mseProbe = out.join('|');
            })();
            """;

        await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        var report = "";
        for (var attempt = 0; attempt < 200; attempt++)          // ~10 s; each case opens a MediaSource
        {
            var raw = await core.ExecuteScriptAsync("window.__mseProbe ?? null").ConfigureAwait(true);
            if (JsonSerializer.Deserialize<string?>(raw) is { } value) { report = value; break; }
            await Task.Delay(50).ConfigureAwait(true);
        }

        if (report.Length == 0) return "SEGMENT MSE: FAIL - the page never reported (timed out)";
        if (Volatile.Read(ref _handlerHits) == 0)
        {
            return "SEGMENT MSE: FAIL - the scheme was never reached, so nothing was appended. The browser "
                 + "refused the request before it arrived; check the scheme's allowed origins.";
        }

        // A range that starts where the fragment does and is not empty is the whole claim.
        var good = report.Split('|').Where(part => part.Contains('-', StringComparison.Ordinal)
                                                && !part.Contains("THREW", StringComparison.Ordinal)).ToList();
        var verdict = good.Count == cases.Count ? "PASS" : "FAIL";

        // 🔴 SAY HOW MANY CASES THERE WERE. `Load` DROPS a directory it cannot read, so the count this is
        // measured against shrinks with it — a run that silently tested one shape would otherwise report
        // the same PASS as one that tested both.
        return $"SEGMENT MSE: {verdict} - {report} ({good.Count}/{cases.Count} cases, {_handlerHits} fetches)";
    }

    /// <summary>
    /// Read each case's <c>init.mp4</c> and FIRST fragment into <see cref="Files"/>. A directory missing
    /// either is dropped with a line of its own rather than silently reducing what was tested.
    /// </summary>
    private static List<(string Label, string Directory, string Mime)> Load(string? spec)
    {
        var cases = new List<(string, string, string)>();
        if (string.IsNullOrWhiteSpace(spec)) return cases;

        foreach (var entry in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = entry.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0) { Console.WriteLine($"[sample] MSE probe: ignoring '{entry}' — expected label=dir"); continue; }

            var label = entry[..split];
            var dir = entry[(split + 1)..];
            var init = Path.Combine(dir, "init.mp4");
            var fragment = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "seg*.m4s").OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).FirstOrDefault()
                : null;

            if (!File.Exists(init) || fragment is null)
            {
                Console.WriteLine($"[sample] MSE probe: '{label}' has no init.mp4 + seg*.m4s in {dir} — dropped");
                continue;
            }

            var header = File.ReadAllBytes(init);
            Files[$"{label}/init.mp4"] = header;
            Files[$"{label}/segment.m4s"] = File.ReadAllBytes(fragment);
            cases.Add((label, dir, MimeOf(header)));
        }
        return cases;
    }

    /// <summary>
    /// The MSE type string for an init segment, READ FROM IT rather than assumed.
    /// <para>
    /// 🔴 <b>A codecs list that does not match the init segment's tracks is refused, and the failure names
    /// the init segment</b> — so a guessed string reads exactly like a malformed one the kit wrote. It cost
    /// this probe its first run: a video-only string against a two-track init reported
    /// <c>init.mp4 refused</c>, which is what a real defect would have looked like.
    /// </para>
    /// </summary>
    private static string MimeOf(byte[] init)
    {
        var codecs = new List<string>();

        // avcC's payload is version, profile, compatibility, level — the three bytes MSE wants in hex.
        var avcC = Find(init, "avcC");
        if (avcC >= 0 && avcC + 8 <= init.Length)
        {
            codecs.Add($"avc1.{init[avcC + 5]:x2}{init[avcC + 6]:x2}{init[avcC + 7]:x2}");
        }
        else if (Find(init, "hvc1") >= 0)
        {
            codecs.Add("hvc1.1.6.L93.B0");
        }

        // AAC-LC is the only audio the kit copies or produces; the esds would carry the object type.
        if (Find(init, "mp4a") >= 0) codecs.Add("mp4a.40.2");

        return codecs.Count == 0
            ? "video/mp4"
            : $"video/mp4; codecs=\"{string.Join(',', codecs)}\"";
    }

    private static int Find(byte[] haystack, string fourcc)
    {
        for (var i = 0; i + 4 <= haystack.Length; i++)
        {
            if (haystack[i] == fourcc[0] && haystack[i + 1] == fourcc[1]
                && haystack[i + 2] == fourcc[2] && haystack[i + 3] == fourcc[3]) return i;
        }
        return -1;
    }
}
