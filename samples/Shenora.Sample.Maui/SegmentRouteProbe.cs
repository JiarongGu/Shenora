using Shenora.Core.WebView;
using Shenora.Modules.Media;

using Shenora;
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
/// <item><description>✅ <b>Does the platform encoder REORDER its output? NO</b> — answered on an iPhone
/// 2026-08-15 by <see cref="CheckReencodedPictureAsync"/>: 60 frames read, 60 emitted, no warning.
/// <c>SegmentRunWriter</c> fail-closes on a backwards presentation time and drops the frame, so a
/// reordering encoder would read as <c>emitted</c> below <c>read</c>.</description></item>
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
/// 🔴 <b>THE PICTURE IS ANSWERABLE ON A SIMULATOR NOW, and D76 is why.</b> While the engine re-encoded
/// every track, a video fixture needed an encoder this simulator does not have — it converts neither h263
/// nor mpeg4, so the engine correctly reported <c>no Video converter</c>, produced nothing, and the route
/// answered 503 for ever. The fixture had to be sound-only and the append proved nothing about a picture.
/// **Copying removed the encoder from the path**: an H.264 track is carried into the fragments verbatim, so
/// <c>clip-video-ac3.mkv</c> now exercises BOTH halves in one run — picture copied, soundtrack converted.
/// </para>
/// <para>
/// ⚠ <b>Reaching an encoder at all needed D76 first, and then a DEVICE.</b> A copied track's reordering is
/// expressed exactly (whole-track decode times plus signed composition offsets), so the question was only
/// ever about a track the kit RE-ENCODES — a picture MP4 cannot carry. The simulator converts no video at
/// all, so it could not ask; the phone converts h263, which is what closed it.
/// </para>
/// </summary>
internal static class SegmentRouteProbe
{
    /// <summary>The URL prefix, matching <see cref="SegmentStreamOptions.RoutePath"/>'s default shape.</summary>
    public const string RoutePath = "/shenora-hls/";

    /// <summary>
    /// 🔴 <b>H.264 + AC-3 — the case D76 exists for, and the one a simulator can now answer.</b> The picture
    /// is COPIED (Matroska already stores H.264 in the length-prefixed form MP4 uses, so no encoder is
    /// involved) and only the soundtrack is converted, which this simulator can do.
    /// <para>
    /// ⚠ That is what makes the VIDEO question answerable here at all. While the engine re-encoded
    /// everything, a picture needed an encoder the simulator does not have — h263 and mpeg4 both refused —
    /// so the fixture had to be sound-only and the append proved nothing about a picture. Copying removed
    /// the encoder from the path, not the simulator's limitation.
    /// </para>
    /// </summary>
    public const string Fixture = "clip-video-ac3.mkv";

    /// <summary>
    /// A SECOND fixture, 60 s long, and it is what answered <c>endstreaming</c>.
    /// <para>
    /// 🔴 <b>iOS DOES stop asking, and the buffer is what decides it — so <c>nextSegment</c>'s streaming
    /// gate is load-bearing rather than defensive.</b> Measured 2026-08-15, iPhone 16 Pro simulator, iOS
    /// 26.3, same engine and same page path for both:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="Fixture"/> — 6.06 s buffered → <c>endstreaming=0</c>,
    /// <c>streaming=TRUE</c>. The source had not declined to stop; it was never given enough to want
    /// to.</description></item>
    /// <item><description>this one — 60.02 s buffered → <c>endstreaming=1</c>,
    /// <c>streaming=FALSE</c>.</description></item>
    /// </list>
    /// <para>
    /// ⚠ <b>So a binder that ignores the stop half fetches against a source that has said stop</b> — the
    /// exact misuse <c>ManagedMediaSource</c> exists to detect, and on iOS the penalty is a torn-down
    /// source. The threshold is somewhere between those two numbers and this probe does not pin it; what
    /// it establishes is that the gate is real, which a 6 s clip alone reads as "never fires".
    /// </para>
    /// <para>
    /// ⚠ Both tracks of this one are COPIED (H.264 + AAC), so it exercises no encoder — which is why
    /// <see cref="Fixture"/> stays the primary: only that one covers picture-copied-plus-sound-converted.
    /// </para>
    /// </summary>
    public const string LongFixture = "clip-h264-aac.mkv";

    /// <summary>
    /// A source with ONE keyframe and more bytes than the writer may hold — so the run SPILLS, and the
    /// segment it produces carries several fragments instead of one.
    ///
    /// <para>
    /// 🔴 <b>The device produces that shape ITSELF; nothing is shipped to it.</b> The memory bound is
    /// internal and must stay so, and a pre-produced artifact copied onto the phone would go stale the
    /// moment the writer changed. A source that simply cannot be cut makes the real engine spill through
    /// the real code path — which is the only version of this worth running on hardware.
    /// </para>
    /// <para>
    /// ⚠ <b>Both bounds matter and the window between them is narrow.</b> Over ~30 s the plan is refused
    /// as uncopyable and the run re-encodes instead (a different path); under 64 MB nothing spills at all.
    /// 25 s at ~26 Mbps sits inside both. Regenerate with:
    /// <code>
    /// ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=30:duration=25 -c:v libx264 -preset ultrafast \
    ///        -b:v 26M -minrate 26M -maxrate 26M -bufsize 52M -nal-hrd cbr \
    ///        -g 100000 -keyint_min 100000 -sc_threshold 0 -pix_fmt yuv420p clip-spill.mkv
    /// </code>
    /// ⚠ <c>-nal-hrd cbr</c> is what makes the size land: x264 CAPS a bitrate and will happily undershoot
    /// a compressible source, so asking for one without padding to fill it gives a file well under the
    /// bound and a run that proves nothing.
    /// </para>
    /// ⚠ Staged by hand like <see cref="RemuxRouteProbe.BigFixture"/>, so its absence SKIPS loudly.
    /// </summary>
    public const string SpillFixture = "clip-spill.mkv";

    /// <summary>
    /// A picture the shell must RE-ENCODE, sized so the encoder's bitrate arithmetic is observable.
    /// <para>
    /// 🔴 <b>The committed mpeg4 fixture cannot answer that question and this one can.</b> The rate is
    /// <c>w × h × fps × 0.15</c> bits clamped to [400 kbps, 12 Mbps], so 480×270@10 asks for 194 kbps and
    /// lands on the FLOOR — the same place the arithmetic lands when the frame rate is missing entirely,
    /// which is the defect the factor was added for. 1280×720@30 asks for ~4.1 Mbps, ten times the floor.
    /// </para>
    /// <para>
    /// ⚠ Staged by hand like <see cref="SpillFixture"/>, so its absence falls through to the smaller
    /// fixture rather than failing. Five seconds is enough and keeps it under a megabyte:
    /// <c>ffmpeg -i clip-h264-aac.mkv -t 5 -vf "scale=1280:720,fps=30" -c:v mpeg4 -q:v 12 -c:a aac
    /// -b:a 96k clip-mpeg4-720p.mkv</c>
    /// </para>
    /// </summary>
    public const string BitrateFixture = "clip-mpeg4-720p.mkv";

    /// <summary>
    /// What the page opens its <c>SourceBuffer</c> with: a copied H.264 picture plus a converted AAC
    /// soundtrack.
    /// <para>
    /// ⚠ <b>The PROFILE is a guess and the FAMILY is not.</b> A copied picture keeps whatever profile the
    /// source was encoded with, which this string cannot know — implementations check the family (`avc1`),
    /// which is why one string serves any H.264 source. An HEVC source would need `hvc1` and is a different
    /// measurement.
    /// </para>
    /// </summary>
    public const string Mime = "video/mp4; codecs=\\\"avc1.640028,mp4a.40.2\\\"";

    /// <summary>
    /// Mount the segment route with the kit's DEFAULT engine, through the same public factory an adopter
    /// uses — <see cref="SegmentEngine.Default"/>.
    /// <para>
    /// ⚠ This probe still borrows two internals (<c>Mp4FragmentReader</c>, <c>SegmentRunWriter</c>) to read
    /// back what it produced; the ENGINE is no longer one of them. See <c>src/Directory.Build.props</c>.
    /// </para>
    /// </summary>
    public static ISegmentStreamRoute Register(IWebViewInterceptor interceptor, IMediaStreamConversion? conversion,
                                       string sourceRoot, string cache, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        ArgumentNullException.ThrowIfNull(log);

        var engine = SegmentEngine.Default(conversion, AppCallback.Logger(log));
        log($"[SEG] engine: {engine.Describe()} · available={engine.IsAvailable}");

        return interceptor.UseSegmentStream(engine, new SegmentStreamOptions
        {
            RoutePath = RoutePath,
            // A 6 s grid over a ~30 s clip — five segments, and a whole multiple of the encoders' own
            // one-second keyframe interval, which `SegmentGrid.IsUsable` requires.
            SegmentSeconds = 6.0,
            Access = new MediaAccessOptions
            {
                Log = AppCallback.Logger(log),
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
                    return name == Fixture || name == LongFixture || name == RemuxRouteProbe.BigFixture
                        || name == SpillFixture
                        ? Path.Combine(sourceRoot, name)
                        : null;
                },
                AllowedRoots = [sourceRoot],
                CacheRoot = cache,
            },
        }, AppCallback.Logger(log));
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
                                                string sourceRoot, Action<string> log,
                                                string? fixture = null)
    {
        ArgumentNullException.ThrowIfNull(webView);
        ArgumentNullException.ThrowIfNull(log);

        var name = fixture ?? Fixture;
        await MediaRangeProbe.EnsureStagedAsync(sourceRoot, name, log).ConfigureAwait(false);

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
            webView, $"window.__shenoraSegCheck('{RoutePath}{name}/', '{Mime}')").ConfigureAwait(false);

        if (started is null) return "SEGMENTS: FAIL — could not evaluate in the page";

        // Minutes, not the page-probe default: a segment run TRANSCODES before it can answer, and the
        // page's own fetch loops already retry a 503 sixty times at one second each.
        var report = await PageProbe.PollSlotAsync(webView, slot, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        return report is null
            ? $"SEGMENTS[{name}]: FAIL — the page never answered"
            : $"SEGMENTS[{name}]: {report}";
    }

    /// <summary>
    /// 🔴 <b>The same stream through the SHIPPED binder</b> — <c>bindSegmentStream</c> from
    /// <c>@shenora/react</c>, rather than the page's own copy of that logic.
    ///
    /// <para>
    /// Every device run before this one drove MediaSource by hand, so the module an adopter actually
    /// receives had never executed on a device at all. D63: a seam nothing consults is indistinguishable
    /// from a broken one — and this tier has three of its four pieces proven only through a hand-written
    /// stand-in.
    /// </para>
    /// <para>
    /// ⚠ <b>The hand-written probe stays as the CONTROL</b>, deliberately. It is what established the
    /// bytes independently of the kit's own module, so a disagreement between the two identifies which
    /// side is wrong; a probe that only exercises the thing under test cannot.
    /// </para>
    /// </summary>
    public static async Task<string> CheckKitBinderAsync(Microsoft.Maui.Controls.HybridWebView webView,
                                                         string sourceRoot, Action<string> log,
                                                         string? fixture = null)
    {
        ArgumentNullException.ThrowIfNull(webView);
        ArgumentNullException.ThrowIfNull(log);

        var name = fixture ?? Fixture;
        await MediaRangeProbe.EnsureStagedAsync(sourceRoot, name, log).ConfigureAwait(false);

        const string slot = "__shenoraSegKitProbe";
        var started = await PageProbe.EvaluateAsync(
            webView, $"window.__shenoraSegKit('{RoutePath}{name}/')").ConfigureAwait(false);
        if (started is null) return "SEGMENTS-KIT: FAIL — could not evaluate in the page";

        var report = await PageProbe.PollSlotAsync(webView, slot, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        return report is null
            ? $"SEGMENTS-KIT[{name}]: FAIL — the page never answered"
            : $"SEGMENTS-KIT[{name}]: {report}";
    }

    /// <summary>
    /// 🔴 <b>A run that STARTS past segment zero — the shape a seek produces, and the one that hid a
    /// converted soundtrack timed from 0.0 s.</b>
    ///
    /// <para>
    /// The page cannot force this reliably: whether a request lands on a producing run or starts a new one
    /// is a race against the cache, and the run that first exposed the bug arose by accident. So this asks
    /// the engine DIRECTLY for a run beginning at segment 1 and reads what the fragment declares, which is
    /// deterministic and needs no browser at all.
    /// </para>
    /// <para>
    /// ⚠ <b>It asserts the fragment's DECODE TIME, never its size.</b> The bytes were always right — a
    /// mistimed fragment is the correct length, carries the correct samples, and appends without error.
    /// That is exactly why a suite measuring only <c>SampleBytes</c> passed over this for weeks.
    /// </para>
    /// </summary>
    public static string CheckSeekRun(IMediaStreamConversion? conversion, string sourceRoot, string cache,
                                      Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var source = Path.Combine(sourceRoot, Fixture);
        if (!File.Exists(source)) return $"SEEK-RUN: SKIPPED — {Fixture} is not staged";

        var engine = SegmentEngine.Default(conversion, AppCallback.Logger(log));
        if (!engine.IsAvailable) return "SEEK-RUN: SKIPPED — this shell registered no conversion";

        // 🔴 CAN THIS SHELL PUT SOUND IN THE SEGMENT AT ALL? Ask BEFORE running, because the check below
        // reads the absence of an audio `traf` and cannot tell two very different causes apart: the writer
        // DROPPING the soundtrack (the defect this probe exists for) and the engine never opening an audio
        // channel because the shell can neither carry nor convert that codec (correct behaviour).
        // ⚠ Measured 2026-08-23 on an Android emulator that reports `convert ac3: accepted=False`: the run
        // produced picture-only segments exactly as it should, and this probe called it FAIL. It sat in
        // TASKS.md as a suspected kit defect until someone asked why playback sounded fine.
        var audio = MatroskaProbe.Read(source)?.Streams.FirstOrDefault(s => s.Kind is MediaStreamKind.Audio);
        if (audio is null) return $"SEEK-RUN: SKIPPED — {Fixture} carries no sound track";
        // AAC is the one audio codec an MP4 fragment can carry verbatim; anything else needs the device.
        var carried = audio.Codec.Equals("aac", StringComparison.OrdinalIgnoreCase);
        if (!carried && conversion?.CanConvert(MediaStreamKind.Audio, audio.Codec) is not true)
        {
            return $"SEEK-RUN: SKIPPED — this shell can neither carry nor convert {audio.Codec}, so a run "
                 + "over this fixture has no sound to place and the check below would report its absence "
                 + "as a fault";
        }

        var plan = engine.PlanSegments(MediaByteSource.ForFile(source), SegmentLengths.Of(6.0));
        if (plan is null || plan.Count < 2) return $"SEEK-RUN: SKIPPED — {Fixture} plans {plan?.Count ?? 0} segment(s)";

        // A directory of its own, so nothing already produced can answer this.
        var dir = Path.Combine(cache, "seek-run");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        Directory.CreateDirectory(dir);

        const int first = 1;
        using (var run = engine.Start(new SegmentRunRequest(MediaByteSource.ForFile(source), dir, HasPicture: true, first, plan, Attempt: 0)))
        {
            if (run is null) return "SEEK-RUN: FAIL — the engine would not start a run at segment 1";
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!run.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(50);
            if (!run.HasExited) return "SEEK-RUN: FAIL — the run did not finish within 60s";
        }

        var segment = Path.Combine(dir, $"seg{first}{SegmentRunRequest.SegmentExtension}");
        if (!File.Exists(segment)) return $"SEEK-RUN: FAIL — no seg{first} was written";

        var sound = Mp4FragmentReader.BaseDecodeTime(segment, DefaultSegmentEngine.AudioTrackId);
        var picture = Mp4FragmentReader.BaseDecodeTime(segment, DefaultSegmentEngine.VideoTrackId);
        if (sound is null) return $"SEEK-RUN: FAIL — seg{first} declares no sound (picture={picture?.ToString() ?? "none"})";

        // The audio timescale IS its sample rate, so segment 1 begins at StartOf(1) × rate. Read from the
        // probe rather than assumed: a fixture's rate is a property of the file.
        var rate = MatroskaProbe.Read(source)?.Streams
            .FirstOrDefault(s => s.Kind is MediaStreamKind.Audio)?.SampleRate ?? 0;
        var expected = plan.StartOf(first) * rate;
        var offBy = rate > 0 ? Math.Abs(sound.Value - expected) / rate : double.NaN;

        var verdict = rate <= 0 ? "INCONCLUSIVE — the source declares no audio rate"
                    : offBy <= 0.25 ? "PASS — sound starts where the segment does"
                    : $"FAIL — sound starts {offBy:0.###}s away from segment {first}";
        return $"SEEK-RUN: {verdict} (soundTicks={sound} expected≈{expected:0} rate={rate} "
             + $"pictureTicks={picture?.ToString() ?? "none"})";
    }

    /// <summary>
    /// 🔴 <b>Does the platform's video encoder REORDER its output?</b> The last question the segment tier
    /// could not answer, and only a real device can: a picture MP4 can carry is COPIED, so the only way to
    /// reach the encoder at all is a source whose picture it cannot — and this simulator converts no video
    /// whatsoever, while the phone converts h263.
    ///
    /// <para>
    /// A GRID plan is what forces the re-encode: <c>Pick</c> refuses to copy when the run must hit uniform
    /// boundaries, because a copied track can only be cut where the SOURCE has a keyframe. So this asks for
    /// a one-second grid over the h263 fixture and reads what came out.
    /// </para>
    /// <para>
    /// ⚠ <b>The measurement is FRAMES IN against FRAMES OUT, not bytes.</b> The output is h264 where the
    /// input was h263, so byte counts are not comparable at all — but the writer fail-closes on a backwards
    /// presentation time and DROPS the frame, so a reordering encoder shows up as <c>emitted</c> below
    /// <c>read</c> on the end-of-run line, with its own warning beside it.
    /// </para>
    /// </summary>
    public static async Task<string> CheckReencodedPictureAsync(IMediaStreamConversion? conversion,
                                                                string sourceRoot, string cache, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        // 🔴 ASK THE SHELL WHICH PICTURE IT CANNOT CARRY, rather than naming one. Every device answers
        // differently — the phone converts h263, an API 36 emulator converts ONLY mpeg4, the iOS simulator
        // converts nothing — so a hardcoded codec made this probe SKIP on the very hosts that could run it.
        // Same selection the conversion route's own probe makes, for the same reason.
        //
        // ⚠ The 720p entry is FIRST and is the only one that can confirm the ENCODER'S BITRATE. The rate is
        // `w × h × fps × 0.15` bits clamped to [400 kbps, 12 Mbps], so a 480×270@10 source lands on the
        // floor whether or not the frame rate is in the arithmetic at all — it cannot tell a correct
        // encoder from the bug the factor was added to fix. 1280×720@30 asks for ~4.1 Mbps and the floor is
        // ~10× below it. Staged by hand (see BitrateFixture), so its absence falls through to the next.
        if (conversion is null) return "REORDER: SKIPPED — this shell converts nothing";
        var candidates = new[]
        {
            (Codec: "mpeg4", Clip: BitrateFixture),
            (Codec: "mpeg4", Clip: "clip-mpeg4-aac.mkv"),
            (Codec: "h263", Clip: "clip-h263-aac.mkv"),
        };
        var chosen = candidates.FirstOrDefault(c =>
            conversion.CanConvert(MediaStreamKind.Video, c.Codec)
            && (c.Clip != BitrateFixture || File.Exists(Path.Combine(sourceRoot, c.Clip))));
        if (chosen.Clip is null)
            return "REORDER: SKIPPED — this shell converts no picture codec we have a fixture for";
        var fixture = chosen.Clip;

        // ⚠ Staged HERE rather than trusted: this probe runs before the conversion route's, and an absent
        // fixture answers as a missing file rather than as the engine refusing — the trap that has already
        // cost this sample three debugging rounds.
        await MediaRangeProbe.EnsureStagedAsync(sourceRoot, fixture, log).ConfigureAwait(false);
        var source = Path.Combine(sourceRoot, fixture);
        if (!File.Exists(source)) return $"REORDER: SKIPPED — {fixture} could not be staged";

        var engine = SegmentEngine.Default(conversion, AppCallback.Logger(log));
        if (engine.DurationOf(MediaByteSource.ForFile(source)) is not { } duration || duration <= TimeSpan.Zero)
            return $"REORDER: SKIPPED — {fixture} declares no duration";

        var dir = Path.Combine(cache, "reorder-run");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        Directory.CreateDirectory(dir);

        // A GRID, deliberately: it is what makes the engine refuse to copy and spend the encoder.
        var plan = SegmentPlan.Grid(1.0, duration);
        using (var run = engine.Start(new SegmentRunRequest(MediaByteSource.ForFile(source), dir, HasPicture: true, 0, plan, Attempt: 0)))
        {
            if (run is null) return "REORDER: FAIL — the engine would not start a grid run";
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!run.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(50);
            if (!run.HasExited) return "REORDER: FAIL — the run did not finish within 120s";
        }

        var segments = Directory.GetFiles(dir, $"seg*{SegmentRunRequest.SegmentExtension}");
        var picture = segments.Sum(s => Mp4FragmentReader.SampleBytes(s, DefaultSegmentEngine.VideoTrackId));
        // The rate the ENCODER actually achieved, which is the only way to see the shell's bitrate
        // arithmetic from outside — the request itself never leaves the shell. Judge it against
        // `w × h × fps × 0.15` clamped to [400 kbps, 12 Mbps]: at the FLOOR the picture is telling you the
        // frame rate did not reach the encoder.
        var kbps = duration > TimeSpan.Zero ? picture * 8 / duration.TotalSeconds / 1000 : 0;
        return $"REORDER: ran a re-encode over {fixture} — {segments.Length} segment(s), "
             + $"{picture} picture bytes over {duration.TotalSeconds:F2}s = {kbps:F0} kbps. "
             + "Read the 'picture (converted) … read=/emitted=' line above: equal "
             + "means the encoder does NOT reorder; emitted below read means it does, and the writer said so.";
    }

    /// <summary>
    /// D71 piece 5 on a device: once the whole stream has been produced, collapse it to ONE file and check
    /// that a plain element plays it.
    ///
    /// <para>
    /// 🔴 <b>The merge is a byte copy of bytes a MediaSource has just accepted, so what this measures is
    /// the CONTAINER rather than the codecs</b> — whether init + fragments concatenated in plan order is a
    /// file the platform's ordinary demuxer opens. That is a different question from the append, and the
    /// answer is not implied by it: the fragments could be individually valid and the concatenation still
    /// wrong.
    /// </para>
    /// <para>
    /// ⚠ Written into <paramref name="sourceRoot"/> deliberately — that is what the sample's media route
    /// already serves, so the page can reach it with a plain <c>&lt;video src&gt;</c> and no MediaSource at
    /// all. It is also outside the segment cache, which the route requires.
    /// </para>
    /// </summary>
    /// <returns>A one-line report, whether or not the stream was complete.</returns>
    public static async Task<string> MergeAsync(ISegmentStreamRoute route, string sourceRoot, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(log);

        var source = Path.Combine(sourceRoot, Fixture);
        if (!route.IsComplete(source))
        {
            // Not a failure: the page fetches only the first segment, so the run has usually been killed
            // long before the tail. Say which it is rather than reporting a defect.
            return "MERGE: SKIPPED — the stream is not complete (the page fetched one segment, not all)";
        }

        var merged = Path.Combine(sourceRoot, "merged-from-segments.mp4");
        var result = await route.MergeAsync(source, merged).ConfigureAwait(false);
        if (!result.Ok) return $"MERGE: FAIL — {result.Detail}";

        var bytes = new FileInfo(merged).Length;
        log($"[SEG] merged {bytes} bytes -> {Path.GetFileName(merged)}");
        return $"MERGE: PASS — {bytes} bytes as one file; play it with a plain <video> to finish the check";
    }
}
