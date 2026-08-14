using Shenora.Core.Events;
using Shenora.Core.WebView;
using Shenora.Engine.Missions;
using Shenora.Modules.Media;

namespace Shenora.Sample.Maui;

/// <summary>
/// Does <c>UseMediaConversion</c> actually WORK, end to end, on a device?
///
/// <para>
/// 🔴 <b>Everything under this route was covered by unit tests against fakes and had never run whole on
/// hardware.</b> The plumbing is genuinely good — mission scheduling, `PathClaims` so a source converts
/// once, `BeginReplace` for atomic output, a derived cache key — but the one thing it exists to do,
/// turning a file the webview cannot play into one it can, was unproven on a real device until this probe.
/// </para>
///
/// <para>
/// ⚠ <b>The ENGINE here is the kit's own, and that is the point of the exercise.</b>
/// <c>MediaConversionOptions.Convert</c> is <c>required</c>, so today every adopter supplies one — which is
/// the reported gap ("no engine under it on mobile, and every adopter writes the same interop"). But the
/// kit already ships both halves: <c>Mp4Remuxer</c> for the container and the platform's
/// <see cref="IMediaStreamConversion"/> for the soundtrack. <c>ToConverter</c> joins them into exactly the
/// delegate this option wants, so the "default set" is one line — <b>if it holds up on a device.</b>
/// </para>
/// </summary>
internal static class ConversionRouteProbe
{
    /// <summary>The route the page asks for. Deliberately not `/media`, which the file route already owns.</summary>
    private const string RoutePath = "/converted";

    /// <summary>
    /// 🔴 <b>THE REFUSAL, WHICH IS AS WORTH TESTING AS THE SUCCESS.</b> A source whose VIDEO the kit cannot
    /// carry must fail loudly and name the codec — not quietly serve the audio-only file a remux happily
    /// produces. Until 2026-08-10 the route committed that file and reported <c>READY</c>, so a film became
    /// a soundtrack with nobody told.
    /// <para>
    /// The fixture is mpeg4 video + AAC audio: AAC is carriable by MP4 on every platform, so the ONLY
    /// unsupported stream is the video and the verdict cannot be blamed on the soundtrack. It also happens
    /// to be a real case — the device decodes mpeg4 and the webview will not (measured, `TASKS.md`).
    /// </para>
    /// <para>
    /// ⚠ <b>It waits for the EVENT, not for an HTTP status.</b> A failed conversion caches nothing, so the
    /// route keeps answering <c>503</c> ("not ready") to every later request — polling would spin forever
    /// and prove nothing. The reason and the codec live on
    /// <see cref="MediaConversionEvents.Failed"/>, which is where a page would read them too.
    /// </para>
    /// </summary>
    public static async Task<string> CheckRefusalAsync(HybridWebView webView, IEventBus events,
                                                      string sourceRoot, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(log);

        // 🔴 mpeg2video, and the codec CHANGED on 2026-08-12 for a reason worth keeping: this probe used
        // `clip-mpeg4-aac.mkv` until the Android picture converter made that file CONVERTIBLE, at which
        // point the probe asserted a refusal that correctly no longer happens. A refusal fixture has to be
        // a codec the device can neither CARRY nor CONVERT — AOSP ships no mpeg2 decoder, so the kit
        // declines it honestly. ⚠ If a device ever gains one, this probe goes green-by-accident and the
        // fixture must move again; the tell is a PASS with no FAILED event in the log.
        const string fixture = "clip-mpeg2-aac.mkv";
        try
        {
            await MediaRangeProbe.EnsureStagedAsync(sourceRoot, fixture, log).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"CONVERT-REFUSAL: FAIL — could not stage {fixture} ({ex.GetType().Name})";
        }

        var failed = new TaskCompletionSource<string>();
        using var subscription = events.Subscribe("SHENORA.MEDIA", MediaConversionEvents.Failed, payload =>
        {
            failed.TrySetResult(System.Text.Json.JsonSerializer.Serialize(payload));
            return Task.CompletedTask;
        });

        // One request to start the mission. A 503 is the expected answer and is NOT the assertion — but a
        // 404 means the route never accepted the request, so no conversion ran and the silence below would
        // be blamed on the engine. ⚠ The first version discarded this report and reported exactly that
        // false cause: `Resolve`'s allow-list was missing this fixture, and "no FAILED event" read as "the
        // conversion silently succeeded".
        var request = await PageProbe.FetchConvertedAsync(webView, $"{RoutePath}?{fixture}").ConfigureAwait(false);
        log($"[CONVERT-REFUSAL] request -> {request ?? "NO ANSWER"}");
        if (request is null || request.Contains("status=404", StringComparison.Ordinal))
            return $"CONVERT-REFUSAL: FAIL — the route DECLINED the request ({request}), so no conversion "
                + $"ran. Add {fixture} to Resolve's allow-list; this is not a conversion result.";

        var report = await Task.WhenAny(failed.Task, Task.Delay(TimeSpan.FromSeconds(25)))
            .ConfigureAwait(false) == failed.Task
            ? await failed.Task.ConfigureAwait(false)
            : null;

        if (report is null)
            return "CONVERT-REFUSAL: FAIL — no FAILED event within 25s. A conversion that drops the video "
                + "must refuse; silence here means it served an audio-only file as a success.";

        log($"[CONVERT-REFUSAL] {report}");
        var named = report.Contains(MediaConversionErrorCodes.UnsupportedCodec, StringComparison.Ordinal);
        // ⚠ The codec the FIXTURE carries, and it must track the fixture — this said "mpeg4" while the file
        // had become mpeg2video, so the probe reported "the refusal did not say which codec" about a refusal
        // that named it perfectly. A hardcoded expectation is an assertion about the fixture too.
        var codec = report.Contains("mpeg2video", StringComparison.OrdinalIgnoreCase);
        return named && codec
            ? $"CONVERT-REFUSAL: PASS — refused a video the kit cannot carry, and named it ({report})"
            : $"CONVERT-REFUSAL: FAIL — the refusal did not say {(named ? "which codec" : "UNSUPPORTED_CODEC")}: {report}";
    }

    /// <summary>
    /// Register the route. Returns null when the shell has no converter, which is not a failure — the
    /// route would then only repair containers, and this probe is about the transcode path.
    /// </summary>
    public static IDisposable? Register(IWebViewInterceptor interceptor, IMissionScheduler scheduler,
                                        IEventBus events, IMediaStreamConversion? conversion,
                                        string sourceRoot, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        ArgumentNullException.ThrowIfNull(log);

        if (conversion is null)
        {
            log("[CONVERT] SKIPPED — this shell registers no IMediaStreamConversion");
            return null;
        }

        var cache = Path.Combine(FileSystem.CacheDirectory, "converted");
        Directory.CreateDirectory(cache);

        return interceptor.UseMediaConversion(scheduler, events, new MediaConversionOptions
        {
            Access = new MediaAccessOptions
            {
                // 🔴 THE SINK THIS ROUTE HAD BEEN MISSING, and its absence cost two device round-trips on
                // 2026-08-13. `RemuxRouteProbe` sets `Log = log`; this one never did, so every diagnostic
                // the conversion route writes — including the writer's `Outcome: Reason`, which is the ONLY
                // place a refusal says WHY — went nowhere. What reached the page was the `FAILED` event,
                // whose `reason` is deliberately a TYPE NAME (`InvalidOperationException`) because exception
                // text must not travel to a page. So the one route whose failures are hardest to diagnose
                // was the one running blind.
                Log = log,

                // The app's own URL shape, exactly as an adopter would write it.
                Resolve = uri =>
                {
                    if (!uri.AbsolutePath.StartsWith(RoutePath, StringComparison.OrdinalIgnoreCase)) return null;
                    var name = uri.Query.TrimStart('?');
                    // An app-level allow-list on top of the kit's containment: this sample knows four names.
                    // ⚠ The `clip-video-*` pair carries a VIDEO track and the plain pair does not — see
                    // `CheckAsync`, where that difference is the whole point of the newer fixtures.
                    // ⚠ FORGETTING A NAME HERE PRODUCES A 404 THAT READS AS A CONVERSION FAULT, and it has
                    // now done so three times in one day — once as the CONVERT cold-install bug, once while
                    // testing mpeg4 in the webview, and once for `clip-mpeg4-aac.mkv` below. Add the fixture
                    // here in the same edit that adds it to the csproj.
                    return name is "clip-mp3.mkv" or "clip-ac3.mkv" or "clip-video-mp3.mkv" or "clip-video-ac3.mkv"
                        or "clip-mpeg4-aac.mkv" or "clip-mpeg2-aac.mkv" or "clip-h263-aac.mkv"
                        ? Path.Combine(sourceRoot, name)
                        : null;
                },
                AllowedRoots = [sourceRoot],
                CacheRoot = cache,
            },
            // 🔴 NO `Convert` AT ALL — this is the kit's DEFAULT engine, and handing it the platform's codec
            // seam is the whole configuration. It used to read `Convert = new Mp4Remuxer().ToConverter(
            // conversion)` with a comment calling itself "the candidate default … if this proves out, it is
            // what UseMediaConversion should offer rather than making every adopter rebuild it". It proved
            // out on an Android device and an iPhone, so the kit offers it and this line went away.
            //
            // ⚠ The sample deliberately exercises the DEFAULT rather than an override, because the default
            // is now the path almost every adopter takes and an untested default is worse than none.
            Conversion = conversion,
        });
    }

    /// <summary>
    /// Ask the PAGE to fetch the converted file, so the verdict is a real request through the real webview.
    /// <para>
    /// ⚠ <b>It stages its own source first</b>, and that is a fix rather than tidiness — see
    /// <see cref="MediaRangeProbe.EnsureStagedAsync"/>. This probe used to rely on
    /// <see cref="TranscodeProbe"/> having written the fixture, which happens LATER, so it reported a 404
    /// on every cold install and passed on every run after one.
    /// </para>
    /// </summary>
    public static async Task<string> CheckAsync(HybridWebView webView, string sourceRoot, string fixture,
                                                Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        // A failure to stage is reported AS ITSELF. Left to the route, a missing source is a plain 404 —
        // indistinguishable from "the route declined", which is what sent this to TASKS.md as a defect in
        // the kit's conversion path rather than a missing file in the sample.
        try
        {
            await MediaRangeProbe.EnsureStagedAsync(sourceRoot, fixture, log).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"CONVERT: FAIL — could not stage {fixture} out of the app package "
                + $"({ex.GetType().Name}: {ex.Message}). The route would answer 404 for this, which reads "
                + "as a conversion defect.";
        }

        // 🔴 RETRY ON 503, because that is the route working rather than failing. The conversion is a
        // scheduled MISSION, so the first request is answered `503` + `Retry-After` while it runs — which
        // is the correct shape for work that outlives a request, and exactly what a page's own fetch has
        // to handle. Measured 2026-08-09: the first version of this probe read that as a failure and
        // reported one, which would have blamed the kit for doing the right thing.
        string? report = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            report = await PageProbe.FetchConvertedAsync(webView, $"{RoutePath}?{fixture}").ConfigureAwait(false);
            if (report is null || !report.Contains("status=503", StringComparison.Ordinal)) break;
            if (attempt == 0) log("[CONVERT] 503 + Retry-After — the conversion is running as a mission; polling…");
            await Task.Delay(PageProbe.RetryAfter).ConfigureAwait(false);
        }
        if (report is null) return "CONVERT: FAIL — the page never answered";
        if (report.Contains("status=503", StringComparison.Ordinal))
            return $"CONVERT: FAIL — still converting after 20s ({report})";

        log($"[CONVERT] {report}");
        // ⚠ The BYTE COUNT is the assertion, not the status. A 200 proves the route answered; only a real
        // body proves something was converted, and this route's whole failure mode is answering with a
        // container that has no audio in it.
        if (!report.Contains("status=200", StringComparison.Ordinal) || report.Contains("bytes=0", StringComparison.Ordinal))
            return $"CONVERT: FAIL — {report}";

        // 🔴 AND THEN PLAY IT, because a fetch is not playback. Everything above proves bytes arrived; only
        // this proves the DEFAULT ENGINE produced something a webview can decode. Without it the route
        // could ship an audio-only container forever and every gate would agree — the same gap that let
        // `MEDIA: PASS` coexist with a video the owner could not play, one route over.
        var played = await PageProbe.PlayUrlAsync(webView, $"{RoutePath}?{fixture}").ConfigureAwait(false);
        log($"[CONVERT] playback -> {played ?? "NO ANSWER"}");
        if (played is null) return $"CONVERT: FAIL — served {report} but the page never reported playback";

        // ⚠ `size=0x0` is the ONLY signal for a video track that did not survive: the element raises no
        // error and still reaches readyState 4 (measured 2026-08-10). Assert the geometry, never the
        // absence of an error.
        // ⚠⚠ NO LEADING PIPE. The first version matched `"|size=0x0"` and the report STARTS with `size=`,
        // so the guard could never fire — it reported `PASS … DECODES AND PLAYS` over a literal
        // `size=0x0` on its own line. Caught on the first run only because the data was printed beside the
        // verdict; a check whose own format string is wrong is a gate that cannot fail.
        if (played.Contains("size=0x0", StringComparison.Ordinal))
            return $"CONVERT: FAIL — served a file with NO DECODABLE PICTURE ({played}). The conversion "
                + $"dropped or never carried the video track. Fetch said: {report}";
        if (!played.StartsWith("size=", StringComparison.Ordinal))
            return $"CONVERT: FAIL — the converted file would not play: {played}";

        return $"CONVERT: PASS — UseMediaConversion served a file that DECODES AND PLAYS ({played}) [{report}]";
    }
}
