using Shenora.Core.WebView;
using Shenora.Modules.Media;

namespace Shenora.Sample.Maui;

/// <summary>
/// The SEGMENT route on a device (D71 piece 3) — the first time the kit's default segment engine meets a
/// real encoder and a real <c>ManagedMediaSource</c>.
///
/// <para>
/// 🔴 <b>What this exists to measure, because all of it is currently unverified.</b> Every part of the
/// segment tier is unit-tested against a FAKE <c>IMediaStreamConversion</c>, which by construction cannot
/// answer three questions:
/// </para>
/// <list type="number">
/// <item><description><b>Does the platform encoder REORDER its output?</b> <c>SegmentRunWriter</c>
/// fail-closes on a backwards presentation time and drops the frame, so a reordering encoder produces short
/// segments rather than wrong ones — visible here as a picture-byte count far below the source's.</description></item>
/// <item><description><b>Is <c>OutputConfig</c> populated early enough to write the init segment?</b> The
/// contract says it is knowable only after the encoder has produced output; the engine writes the init
/// segment beside the FIRST fragment on that basis. If it is still empty then, the init segment carries no
/// decoder configuration and every append fails.</description></item>
/// <item><description><b>Does a real <c>ManagedMediaSource</c> ACCEPT these fragments?</b>
/// <c>isTypeSupported</c> already answered `true`, and this repo has a measured case of that exact query
/// lying (`video/mp2t` on both shells). Only an append proves it.</description></item>
/// </list>
///
/// <para>
/// 🔴 <b>THE FIXTURE IS SOUND-ONLY, AND THAT IS A MEASURED PLATFORM LIMIT rather than a shortcut.</b> The
/// first attempt used <c>clip-h263-aac.mkv</c>, because the recorded device table says the kit converts
/// h263 on an iPhone 17 Pro. **The iPhone 16 Pro SIMULATOR converts neither h263 nor mpeg4** — its own
/// `CONVERT-PICTURE` probe says so in the same run — so the engine correctly reported
/// <c>no Video converter for 'h263'</c>, produced nothing, and the route answered 503 for ever.
/// </para>
/// <para>
/// <c>clip-ac3.mkv</c> is what this simulator CAN transcode (`TRANSCODE: PASS — decoded ac3, encoded AAC`
/// in the same run), so the segments are AAC and the append is an audio one. That answers two of the three
/// questions above; **the reordering question is a VIDEO question and stays open until this runs on a
/// device whose encoder the kit can drive.**
/// </para>
/// </summary>
internal static class SegmentRouteProbe
{
    /// <summary>The URL prefix, matching <see cref="SegmentStreamOptions.RoutePath"/>'s default shape.</summary>
    public const string RoutePath = "/shenora-hls/";

    /// <summary>The one fixture this SIMULATOR can transcode — see the type remarks for why it is sound-only.</summary>
    public const string Fixture = "clip-ac3.mkv";

    /// <summary>
    /// What the page opens its <c>SourceBuffer</c> with. Audio-only, matching what the engine produces from
    /// <see cref="Fixture"/> — a video mime would make <c>addSourceBuffer</c> throw and report a codec
    /// problem where there is only a sound-only source.
    /// </summary>
    public const string Mime = "audio/mp4; codecs=\\\"mp4a.40.2\\\"";

    /// <summary>
    /// Mount the segment route with the kit's DEFAULT engine.
    /// <para>
    /// ⚠ <b>The engine is reached through <c>InternalsVisibleTo</c>, deliberately and temporarily.</b>
    /// Making it public to run this measurement would commit SemVer surface to a shape the measurement
    /// might change — backwards. The public entry point gets designed once these numbers exist; see the
    /// note in <c>src/Directory.Build.props</c>.
    /// </para>
    /// </summary>
    public static IDisposable Register(IWebViewInterceptor interceptor, IMediaStreamConversion? conversion,
                                       string sourceRoot, string cache, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        ArgumentNullException.ThrowIfNull(log);

        var engine = new DefaultSegmentEngine(conversion, log);
        log($"[SEG] engine: {engine.Describe()} · available={engine.IsAvailable}");

        return interceptor.UseSegmentStream(engine, new SegmentStreamOptions
        {
            RoutePath = RoutePath,
            // A 6 s grid over a ~30 s clip — five segments, and a whole multiple of the encoders' own
            // one-second keyframe interval, which `SegmentGrid.IsUsable` requires.
            SegmentSeconds = 6.0,
            Access = new MediaAccessOptions
            {
                Log = log,
                Resolve = uri =>
                {
                    if (!uri.AbsolutePath.StartsWith(RoutePath, StringComparison.OrdinalIgnoreCase)) return null;
                    // `/shenora-hls/<name>/<resource>` — the route reads the resource itself, so this only
                    // has to answer with the SOURCE.
                    var rest = uri.AbsolutePath[RoutePath.Length..];
                    var name = rest.Split('/')[0];
                    // ⚠ An app-level allow-list on top of the kit's containment. Forgetting a name here
                    // produces a 404 that reads as an engine fault — which has cost this sample three
                    // debugging rounds in one day, per ConversionRouteProbe's own warning.
                    return name == Fixture ? Path.Combine(sourceRoot, name) : null;
                },
                AllowedRoots = [sourceRoot],
                CacheRoot = cache,
            },
        }, log);
    }

    /// <summary>
    /// Drive the whole page-side flow and report it as TEXT: fetch the manifest, fetch the init segment,
    /// fetch segment 0, and APPEND both to a real <c>ManagedMediaSource</c>.
    ///
    /// <para>
    /// 🔴 <b>The append is the point.</b> Everything before it — the manifest parsing, the 503 retry, the
    /// byte counts — this repo can already verify without a device. What it cannot verify anywhere else is
    /// whether the platform's MediaSource accepts what the kit's fragment writer produced, and an
    /// <c>appendBuffer</c> failure is SILENT: the buffer fires `error` and the element simply never plays.
    /// </para>
    /// <para>
    /// ⚠ Retries on <c>503</c>, because the route answers that while the engine produces — the same contract
    /// the conversion and computed-remux probes already honour, at the same interval
    /// (<see cref="PageProbe.RetryAfter"/>).
    /// </para>
    /// </summary>
    /// <param name="webView">The sample's webview.</param>
    /// <param name="sourceRoot">
    /// Where the fixture is staged. ⚠ <b>Staging is NOT optional and its absence answers 404</b>, which
    /// reads as a route that refused rather than a file that was never copied — the first run of this probe
    /// lost a round trip to exactly that. `EnsureStagedAsync` records the same trap.
    /// </param>
    /// <param name="log">Diagnostics sink.</param>
    public static async Task<string> CheckAsync(Microsoft.Maui.Controls.HybridWebView webView,
                                                string sourceRoot, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(webView);
        ArgumentNullException.ThrowIfNull(log);

        await MediaRangeProbe.EnsureStagedAsync(sourceRoot, Fixture, log).ConfigureAwait(false);

        // ⚠ ONE SHORT CALL, and the heavy lifting lives in the PAGE (`window.__shenoraSegCheck`).
        // A 4 KB script through `EvaluateJavaScriptAsync` failed here with a bare null — `Safe()`
        // flattens it (so a `//` comment swallows the rest), MAUI re-escapes the result, and the catch
        // in `EvaluateAsync` turns every cause into the same "could not evaluate". Measured 2026-08-14:
        // the identical script parsed fine offline, which is what ruled the syntax out and the transport
        // in. `mobile-shells.md` already recommends this shape — heavy logic in the page, answers over
        // the log mirror.
        //
        // ⚠ PARK-AND-POLL: `EvaluateJavaScriptAsync` does not await a promise, so the page writes its
        // answer into a slot and the host polls for it.
        const string slot = "__shenoraSegProbe";
        var started = await PageProbe.EvaluateAsync(
            webView, $"window.__shenoraSegCheck('{RoutePath}{Fixture}/', '{Mime}')").ConfigureAwait(false);

        if (started is null) return "SEGMENTS: FAIL — could not evaluate in the page";

        // Minutes, not the page-probe default: a segment run TRANSCODES before it can answer, and the
        // page's own fetch loops already retry a 503 sixty times at one second each.
        var report = await PageProbe.PollSlotAsync(webView, slot, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        return report is null ? "SEGMENTS: FAIL — the page never answered" : $"SEGMENTS: {report}";
    }
}
