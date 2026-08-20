using System.Text;
using Shenora;
using Shenora.Modules.Media;
using Shenora.Core.WebView;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Media;

/// <summary>
/// The segment-stream route, driven with a FAKE engine — which is the point of the
/// <see cref="ISegmentEngine"/> seam existing at all. The route, the synthetic manifest, the rolling window
/// and the verification contract are portable and provable here; only the process launch needs a device.
///
/// <para>
/// Harvested from a consuming app that proved this on a device. The cases below are its measured bugs, and
/// the one that matters most is <see cref="A_segment_with_no_rendered_picture_is_rejected_and_the_ladder_advances"/>:
/// an encoder can accept every frame, write nothing and exit 0.
/// </para>
/// </summary>
public class SegmentStreamTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shenora-hls-" + Guid.NewGuid().ToString("N"));
    private readonly string _sources;
    private readonly string _cache;

    public SegmentStreamTests()
    {
        _sources = Path.Combine(_root, "src");
        _cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_sources);
        Directory.CreateDirectory(_cache);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a cache is disposable */ }
    }

    // The interceptor harness is `TestSupport.FakeInterceptor` — shared, because this file and
    // `MediaConversionTests` each carried a private copy of it until 2026-08-12.

    /// <summary>
    /// An engine that writes segments the instant it is started, so the route's policy can be exercised with
    /// no process, no encoder and no waiting.
    /// </summary>
    private sealed class FakeEngine : ISegmentEngine
    {
        public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(20);
        public bool SourceHasPicture { get; init; }
        /// <summary>Attempts BELOW this write a picture-less segment — the encoder ladder under test.</summary>
        public int FirstAttemptThatRenders { get; init; }

        /// <summary>
        /// Whether this run writes the init segment. A real engine always does; false models the window
        /// between a run starting and its first fragment landing, which is when a page asks for it.
        /// </summary>
        public bool WritesInit { get; init; } = true;

        /// <summary>
        /// Segment starts this engine will not negotiate — what a COPYING run answers, because its cuts land
        /// on the source's own keyframes. Null models the ordinary re-encoding engine, which hits the grid.
        /// </summary>
        public IReadOnlyList<double>? Cuts { get; init; }

        public List<SegmentRunRequest> Starts { get; } = [];

        public bool IsAvailable => true;
        public string Describe() => "fake";
        public TimeSpan? DurationOf(MediaByteSource source) => Duration;
        public bool HasPicture(MediaByteSource source) => SourceHasPicture;

        public SegmentPlan? PlanSegments(MediaByteSource source, SegmentLengths lengths, CancellationToken cancellationToken = default)
            => Cuts is null ? null : SegmentPlan.Cuts(Cuts, Duration);

        /// <summary>A segment "has a rendered picture" when the run that wrote it said so in its name.</summary>
        public bool HasRenderedPicture(string segment) =>
            File.Exists(segment) && File.ReadAllText(segment).Contains("picture", StringComparison.Ordinal);

        public ISegmentRun? Start(SegmentRunRequest request)
        {
            Starts.Add(request);
            var rendered = request.Attempt >= FirstAttemptThatRenders;
            if (WritesInit)
            {
                File.WriteAllText(Path.Combine(request.Directory, SegmentRunRequest.InitSegmentName), "init");
            }
            // Write the whole remainder immediately: this test is about POLICY, not about timing.
            for (var i = request.FirstSegment; i < 8; i++)
            {
                File.WriteAllText(Path.Combine(request.Directory, $"seg{i}.m4s"),
                    rendered ? "audio+picture" : "audio-only");
            }
            return new Run();
        }

        private sealed class Run : ISegmentRun
        {
            public bool HasExited => true;
            public void Dispose() { }
        }
    }

    /// <summary>
    /// A run that publishes <b>segment 0 and nothing else</b>, and STAYS ALIVE — the shape
    /// <see cref="FakeEngine"/> cannot model, because it writes every segment and exits at once, which
    /// satisfies "the next one exists" and "the run is over" simultaneously.
    /// <para>
    /// 🔴 That is why the readiness rule was never covered: both the old rule and the new one pass against
    /// a fake that finishes instantly. Real production has exactly one segment on disk and a live producer.
    /// </para>
    /// </summary>
    private sealed class OneSegmentEngine : ISegmentEngine
    {
        /// <summary>Write the part under its FINAL name (published) or its <c>.part</c> name (mid-write).</summary>
        public bool Publish { get; init; } = true;

        public bool IsAvailable => true;
        public string Describe() => "one-segment";
        public TimeSpan? DurationOf(MediaByteSource source) => TimeSpan.FromSeconds(20);
        public bool HasPicture(MediaByteSource source) => false;
        public SegmentPlan? PlanSegments(MediaByteSource source, SegmentLengths lengths, CancellationToken ct = default) => null;
        public bool HasRenderedPicture(string segment) => true;

        public ISegmentRun? Start(SegmentRunRequest request)
        {
            var suffix = Publish ? string.Empty : SegmentRunRequest.PartialExtension;
            File.WriteAllText(Path.Combine(request.Directory, SegmentRunRequest.InitSegmentName), "init");
            File.WriteAllText(Path.Combine(request.Directory, $"seg{request.FirstSegment}.m4s{suffix}"), "seg-body");
            return new Run();
        }

        /// <summary>Still producing — nothing here may be read as "whatever is there is all there is".</summary>
        private sealed class Run : ISegmentRun
        {
            public bool HasExited => false;
            public void Dispose() { }
        }
    }

    private string NewSource(string name = "track.flac")
    {
        var path = Path.Combine(_sources, name);
        File.WriteAllText(path, "original-bytes");
        return path;
    }

    /// <param name="head">
    /// ⚠ <b>Empty by default, which is NOT the shipped default.</b> The route ramps its first segments so
    /// playback starts sooner, and every test below that counts segments or reads an <c>EXTINF</c> is about
    /// something else — so they opt into a uniform stream and say so, rather than being quietly rewritten
    /// each time the ramp changes. <see cref="The_shipped_default_starts_with_a_SHORT_segment"/> covers the
    /// default itself.
    /// </param>
    private SegmentStreamOptions Options(TimeSpan? waitBudget = null, IReadOnlyList<double>? head = null) => new()
    {
        Access = new MediaAccessOptions
        {
            Resolve = uri => uri.AbsolutePath.StartsWith("/shenora-hls/", StringComparison.Ordinal)
                ? Path.Combine(_sources, Path.GetFileName(uri.AbsolutePath))
                : null,
            AllowedRoots = [_sources],
            CacheRoot = _cache,
        },
        // Default 20 s. A test that EXPECTS a refusal must not spend it.
        WaitBudget = waitBudget ?? TimeSpan.FromSeconds(20),
        HeadSegmentSeconds = head ?? [],
    };

    private static async Task<string> BodyOf(WebViewResourceResponse response)
    {
        using var reader = new StreamReader(response.Content);
        return await reader.ReadToEndAsync();
    }

    // ── the head ramp: what the first segment costs ───────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>The shipped default opens with a SHORT segment</b>, because segment 0 is the entire startup
    /// budget: a page cannot play until the init segment arrives, that request drives segment 0, and a VOD
    /// playlist starts there. Six seconds of production before the first frame is what the uniform default
    /// used to cost.
    /// <para>
    /// ⚠ <c>EXT-X-TARGETDURATION</c> is an UPPER bound, so it still states the STEADY length — a reader that
    /// saw the short lead-in there would size its buffers for the wrong stream.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_shipped_default_starts_with_a_SHORT_segment()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        // No `head:` argument — the SHIPPED default, which is the whole subject here.
        using var _ = interceptor.UseSegmentStream(
            new FakeEngine { Duration = TimeSpan.FromSeconds(20) },
            new SegmentStreamOptions
            {
                Access = new MediaAccessOptions
                {
                    Resolve = uri => Path.Combine(_sources, Path.GetFileName(uri.AbsolutePath)),
                    AllowedRoots = [_sources],
                    CacheRoot = _cache,
                },
            });

        var manifest = await BodyOf((await interceptor.AskAsync("https://x/shenora-hls/track.flac/index.m3u8"))!);

        // 1 s, then 2 s, then 4 s, then the steady 6 s — the ramp in the playlist itself.
        Assert.Contains("#EXTINF:1.000,", manifest, StringComparison.Ordinal);
        Assert.Contains("#EXTINF:2.000,", manifest, StringComparison.Ordinal);
        Assert.Contains("#EXTINF:4.000,", manifest, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-TARGETDURATION:6", manifest, StringComparison.Ordinal);
        // The sum still has to be the source: 1+2+4 = 7, leaving 13 s over 6 s segments.
        Assert.Contains("#EXTINF:6.000,", manifest, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔴 <b>A ramp is EncoderCuts, never SourceKeyFrames — so the run re-encodes rather than copying.</b>
    /// Copied frames keep the original encoder's keyframes, which are nowhere near a synthetic 1/2/4 ramp;
    /// let a run copy onto one and every cut slips to the next source keyframe, the segments still play, and
    /// only a seek shows it. The origin is what the run reads to decide, so it is asserted here.
    /// </summary>
    [Fact]
    public async Task A_head_ramp_is_declared_as_encoder_boundaries_so_a_run_will_not_copy_onto_it()
    {
        NewSource();
        var engine = new FakeEngine { Duration = TimeSpan.FromSeconds(20) };
        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(engine, Options(head: [1.0, 2.0]));

        await interceptor.AskAsync("https://x/shenora-hls/track.flac/seg0.m4s");

        var plan = Assert.Single(engine.Starts).Plan;
        Assert.Equal(SegmentBoundaries.EncoderCuts, plan.Origin);
        Assert.Null(plan.GridSeconds);
        Assert.Equal(1.0, plan.LengthOf(0), 6);
        Assert.Equal(2.0, plan.LengthOf(1), 6);
        Assert.Equal(6.0, plan.LengthOf(2), 6);
    }

    /// <summary>
    /// A head length the kit's encoders cannot land on is refused at COMPOSITION time. The same policy a
    /// fractional grid gets, for the same reason: those segments play, and only a seek misbehaves — so the
    /// failure has to be a sentence at startup rather than a bug report about scrubbing.
    /// </summary>
    [Fact]
    public void A_head_length_that_cannot_land_on_a_keyframe_is_refused_when_the_route_is_built()
    {
        var interceptor = new FakeInterceptor();

        // 1.5 s: no encoder keyframe sits there.
        var fractional = Assert.Throws<ArgumentException>(() =>
            interceptor.UseSegmentStream(new FakeEngine(), Options(head: [1.5])));
        Assert.Contains("keyframe", fractional.Message, StringComparison.OrdinalIgnoreCase);

        // And a head LONGER than the steady length, which would delay playback rather than start it sooner.
        var backwards = Assert.Throws<ArgumentException>(() =>
            interceptor.UseSegmentStream(new FakeEngine(), Options(head: [8.0])));
        Assert.Contains("longer than the steady", backwards.Message, StringComparison.Ordinal);
    }

    // ── readiness: publishing is what makes a part servable ───────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>A published segment is served immediately — the route does NOT wait for the next one.</b> This
    /// is a whole segment of startup latency: a page cannot play until <c>init.mp4</c> arrives, that request
    /// drives segment 0, and requiring segment 1 to exist meant waiting for a second segment's worth of
    /// production before the first frame.
    /// <para>
    /// The old rule inferred completeness from "the next file appeared, or the run is over", because a
    /// progressive muxer creates a file when it STARTS writing. Atomic publish
    /// (<see cref="SegmentRunRequest.PartialExtension"/>) makes the producer answer instead.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_published_segment_is_served_without_waiting_for_the_next_one()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        // A short budget so that if this ever regresses it FAILS in a second rather than hanging for 20.
        using var _ = interceptor.UseSegmentStream(new OneSegmentEngine(), Options(TimeSpan.FromSeconds(1)));

        var response = await interceptor.AskAsync("https://x/shenora-hls/track.flac/seg0.m4s");

        Assert.NotNull(response);
        Assert.Equal(200, response!.StatusCode);
        Assert.Equal("seg-body", await BodyOf(response));
        // The producer is still running and seg1 does not exist — which under the old rule was the whole
        // reason this request could not be answered.
        Assert.False(File.Exists(Path.Combine(_cache, "seg1.m4s")));
    }

    /// <summary>
    /// The other direction, without which the test above would pass just as well if the route served
    /// ANYTHING it found: a part still being written is not servable, and must not be mistaken for one.
    /// </summary>
    [Fact]
    public async Task A_part_still_being_written_is_not_served()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(new OneSegmentEngine { Publish = false },
                                                   Options(TimeSpan.FromMilliseconds(300)));

        var response = await interceptor.AskAsync("https://x/shenora-hls/track.flac/seg0.m4s");

        Assert.NotNull(response);
        Assert.Equal(503, response!.StatusCode);
    }

    /// <summary>
    /// The whole playlist is declared from the DURATION alone, before a segment exists — that is what makes
    /// the scrub bar the right length and a seek anywhere expressible.
    /// </summary>
    [Fact]
    public async Task The_manifest_is_synthetic_and_its_TAIL_carries_the_real_remainder()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(new FakeEngine { Duration = TimeSpan.FromSeconds(20) }, Options());

        var response = await interceptor.AskAsync("https://x/shenora-hls/track.flac/index.m3u8");
        Assert.NotNull(response);
        var manifest = await BodyOf(response!);

        // 20s over a 6s grid = 4 segments (6+6+6+2).
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD", manifest, StringComparison.Ordinal);
        Assert.Contains("seg0.m4s", manifest, StringComparison.Ordinal);
        Assert.Contains("seg3.m4s", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("seg4.m4s", manifest, StringComparison.Ordinal);

        // ⚠ The load-bearing assertion. A playlist's declared total is the SUM of its EXTINFs, so a flat
        // last entry would overstate the source by up to a whole segment and a scrub bar built on it seeks
        // past the end.
        Assert.Contains("#EXTINF:2.000,", manifest, StringComparison.Ordinal);
        Assert.Equal(3, manifest.Split("#EXTINF:6.000,").Length - 1);
    }

    /// <summary>
    /// 🔴 <b>The fMP4 pair, and it is a pair: <c>#EXT-X-MAP</c> and <c>#EXT-X-VERSION:7</c> must move
    /// together.</b> An <c>#EXT-X-MAP</c> is illegal below version 6, so a playlist that declares 3 while
    /// carrying one invites a reader honouring the version to skip the single line without which no segment
    /// can be decoded — and the failure is a silent append rejection rather than an error.
    /// </summary>
    [Fact]
    public async Task The_manifest_declares_the_init_segment_and_a_version_that_permits_it()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(new FakeEngine { Duration = TimeSpan.FromSeconds(20) }, Options());

        var manifest = await BodyOf((await interceptor.AskAsync("https://x/shenora-hls/track.flac/index.m3u8"))!);

        Assert.Contains("#EXT-X-VERSION:7", manifest, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MAP:URI=\"init.mp4\"", manifest, StringComparison.Ordinal);
        // Before the first segment, or a reader meets media it has no configuration for.
        Assert.True(manifest.IndexOf("#EXT-X-MAP", StringComparison.Ordinal)
                    < manifest.IndexOf("seg0.m4s", StringComparison.Ordinal));
    }

    /// <summary>
    /// 🔴 <b>When the engine cannot hit the grid, the manifest states ITS boundaries — and the run is handed
    /// the SAME plan.</b> This is the invariant the copy path rests on (D76): a copied track lands on the
    /// source's own keyframes, so the playlist and the producer must be built from one object. Two derivations
    /// of "where the cuts are" fail silently — every segment is valid, and a seek arrives somewhere else.
    /// </summary>
    [Fact]
    public async Task A_derived_plan_drives_the_manifest_AND_the_run_that_produces_it()
    {
        NewSource("clip.mkv");
        var interceptor = new FakeInterceptor();
        // Keyframes at 0, 7.5 and 13.2 s of a 20 s source: nothing a 6 s grid would ever produce.
        var engine = new FakeEngine { Duration = TimeSpan.FromSeconds(20), Cuts = [0, 7.5, 13.2] };
        using var _ = interceptor.UseSegmentStream(engine, Options());

        var manifest = await BodyOf((await interceptor.AskAsync("https://x/shenora-hls/clip.mkv/index.m3u8"))!);

        // Three segments, each stating its REAL length: 7.5 + 5.7 + 6.8 = the 20 s source.
        Assert.Contains("#EXTINF:7.500,", manifest, StringComparison.Ordinal);
        Assert.Contains("#EXTINF:5.700,", manifest, StringComparison.Ordinal);
        Assert.Contains("#EXTINF:6.800,", manifest, StringComparison.Ordinal);
        Assert.Contains("seg2.m4s", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("seg3.m4s", manifest, StringComparison.Ordinal);
        // ⚠ The longest EXTINF, never the length that was asked for: a TARGETDURATION below any EXTINF is a
        // MUST the playlist spec states, so a strict reader may refuse a stream whose bytes are perfectly good.
        Assert.Contains("#EXT-X-TARGETDURATION:8", manifest, StringComparison.Ordinal);

        // And the producer is asked for exactly that plan — not a grid it would then cut somewhere else.
        var response = await interceptor.AskAsync("https://x/shenora-hls/clip.mkv/seg1.m4s");
        Assert.Equal(200, response!.StatusCode);
        var plan = Assert.Single(engine.Starts).Plan;
        Assert.Null(plan.GridSeconds);
        Assert.Equal(3, plan.Count);
        Assert.Equal(7.5, plan.StartOf(1), 6);
    }

    /// <summary>A grid engine — one that answers no plan — is still handed one, built from the option it declared.</summary>
    [Fact]
    public async Task An_engine_with_no_plan_of_its_own_is_handed_the_GRID_it_will_hit()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        var engine = new FakeEngine { Duration = TimeSpan.FromSeconds(20) };
        using var _ = interceptor.UseSegmentStream(engine, Options());

        await interceptor.AskAsync("https://x/shenora-hls/track.flac/seg0.m4s");

        var plan = Assert.Single(engine.Starts).Plan;
        Assert.Equal(6.0, plan.GridSeconds);
        Assert.Equal(4, plan.Count);
    }

    /// <summary>
    /// The init segment is PRODUCED, not stored: its decoder configuration is knowable only once an encoder
    /// has emitted output, so the engine writes it beside the first fragment. A page following
    /// <c>#EXT-X-MAP</c> therefore asks for it before anything exists, and must be told "not yet" rather than
    /// "no such thing" — a 404 ends playback before it begins.
    /// </summary>
    [Fact]
    public async Task The_init_segment_is_served_once_production_has_written_it()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        var engine = new FakeEngine { Duration = TimeSpan.FromSeconds(20), WritesInit = true };
        using var _ = interceptor.UseSegmentStream(engine, Options());

        var response = await interceptor.AskAsync("https://x/shenora-hls/track.flac/init.mp4");

        Assert.NotNull(response);
        Assert.Equal(200, response!.StatusCode);
        Assert.Equal("video/mp4", response.Headers["Content-Type"]);
    }

    /// <summary>An engine that has not written it yet answers 503, not 404 — the shared not-ready reply.</summary>
    [Fact]
    public async Task An_init_segment_that_production_has_not_written_is_NOT_READY_rather_than_missing()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        var engine = new FakeEngine { Duration = TimeSpan.FromSeconds(20), WritesInit = false };
        using var _ = interceptor.UseSegmentStream(engine, Options());

        var response = await interceptor.AskAsync("https://x/shenora-hls/track.flac/init.mp4");

        Assert.NotNull(response);
        Assert.Equal(503, response!.StatusCode);
    }

    /// <summary>
    /// 🔴 The contract this whole feature is built around: an encoder can accept every frame, write nothing,
    /// and exit 0. Only the OUTPUT is evidence.
    /// </summary>
    [Fact]
    public async Task A_segment_with_no_rendered_picture_is_rejected_and_the_ladder_advances()
    {
        NewSource("clip.mkv");
        var engine = new FakeEngine
        {
            SourceHasPicture = true,
            // Attempt 0 writes an audio-only segment and reports success, exactly as the measured encoder did.
            FirstAttemptThatRenders = 1,
        };
        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(engine, Options());

        var response = await interceptor.AskAsync("https://x/shenora-hls/clip.mkv/seg0.m4s");

        Assert.NotNull(response);
        Assert.Equal(200, response!.StatusCode);
        Assert.Contains("picture", await BodyOf(response), StringComparison.Ordinal);

        // The ladder advanced rather than serving the picture-less output: attempt 0 then attempt 1.
        Assert.True(engine.Starts.Count >= 2, $"expected a restart on the next candidate, saw {engine.Starts.Count}");
        Assert.Equal(0, engine.Starts[0].Attempt);
        Assert.Equal(1, engine.Starts[^1].Attempt);
    }

    /// <summary>
    /// The same output is fine when the SOURCE has no picture — the check must not reject audio for being
    /// audio, which is the obvious over-correction.
    /// </summary>
    [Fact]
    public async Task An_audio_only_source_is_served_without_any_picture_check()
    {
        NewSource();
        var engine = new FakeEngine { SourceHasPicture = false, FirstAttemptThatRenders = int.MaxValue };
        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(engine, Options());

        var response = await interceptor.AskAsync("https://x/shenora-hls/track.flac/seg0.m4s");

        Assert.Equal(200, response!.StatusCode);
        Assert.Single(engine.Starts);
    }

    [Fact]
    public async Task A_segment_past_the_end_of_the_manifest_is_refused()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(new FakeEngine { Duration = TimeSpan.FromSeconds(20) }, Options());

        // 20 s over a 6 s grid names seg0..seg3. The engine may write ONE past that (encoder priming); it is
        // never asked for, and asking is refused rather than served.
        var response = await interceptor.AskAsync("https://x/shenora-hls/track.flac/seg4.m4s");
        Assert.Equal(404, response!.StatusCode);
    }

    [Fact]
    public async Task A_source_outside_the_allowed_roots_is_refused_like_a_missing_one()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(new FakeEngine(), new SegmentStreamOptions
        {
            Access = new MediaAccessOptions
            {
                Resolve = _ => Path.Combine(_root, "outside.flac"),   // resolves, but not under AllowedRoots
                AllowedRoots = [_sources],
                CacheRoot = _cache,
            },
        });
        File.WriteAllText(Path.Combine(_root, "outside.flac"), "x");

        var response = await interceptor.AskAsync("https://x/shenora-hls/anything/index.m3u8");

        // 404, identical to a missing file, so nothing can probe for existence by comparing responses.
        Assert.Equal(404, response!.StatusCode);
    }

    [Fact]
    public async Task A_url_this_route_does_not_own_falls_THROUGH_the_pipeline()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(new FakeEngine(), Options());

        // Null, not 404: declining must let the rest of the pipeline and then the platform answer.
        Assert.Null(await interceptor.AskAsync("https://x/something-else/track.flac"));
    }

    /// <summary>
    /// A shell with no engine registers NOTHING, so the call site is identical everywhere and a platform
    /// without an engine answers nothing rather than 503 forever.
    /// </summary>
    [Fact]
    public async Task An_unavailable_engine_registers_no_route_at_all()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(new UnavailableEngine(), Options());

        Assert.Null(await interceptor.AskAsync("https://x/shenora-hls/track.flac/index.m3u8"));
    }

    private sealed class UnavailableEngine : ISegmentEngine
    {
        public bool IsAvailable => false;
        public string Describe() => "none";
        public TimeSpan? DurationOf(MediaByteSource source) => null;
        public bool HasPicture(MediaByteSource source) => false;
        public bool HasRenderedPicture(string segment) => false;
        public SegmentPlan? PlanSegments(MediaByteSource source, SegmentLengths lengths, CancellationToken ct = default) => null;
        public SegmentPlan? PlanSegments(MediaByteSource source, double seconds, CancellationToken cancellationToken = default) => null;
        public ISegmentRun? Start(SegmentRunRequest request) => null;
    }

    // ── piece 5: the collapse from a stream to a finished artifact (D71) ──────────────────────────────

    /// <summary>
    /// 🔴 <b>"We have every segment" and "we have the finished file" are ONE state.</b> The initialisation
    /// segment followed by every fragment in plan order IS a valid fMP4, so merging is a byte copy
    /// rather than a second production — which is the whole reason streaming is the PRIMARY path and the
    /// whole file is what it leaves behind.
    /// </summary>
    [Fact]
    public async Task A_finished_stream_becomes_ONE_file_of_its_parts_in_order()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseSegmentStream(new FakeEngine { Duration = TimeSpan.FromSeconds(20) }, Options());

        // Asking for a segment is what opens the source and drives production of the whole remainder.
        await interceptor.AskAsync("https://x/shenora-hls/track.flac/seg0.m4s");
        var source = Path.Combine(_sources, "track.flac");
        Assert.True(route.IsComplete(source), "the fake writes every segment, so the stream is complete");

        var artifact = Path.Combine(_root, "offline", "track.mp4");
        var result = await route.MergeAsync(source, artifact);

        Assert.True(result.Ok, result.Detail);
        // init, then seg0..seg3 — the plan's order, which is the only order that decodes.
        Assert.Equal("init" + string.Concat(Enumerable.Repeat("audio+picture", 4)), File.ReadAllText(artifact));
    }

    /// <summary>
    /// 🔴 <b>THE CACHE AND THE ARTIFACT HAVE OPPOSITE POLICIES, so this is refused rather than documented.</b>
    /// The cache is swept oldest-used-first under a byte cap; a persisted artifact must be evicted by
    /// nothing. Writing one into the other means ordinary playback silently deletes a file somebody waited
    /// for, and it surfaces much later as a download that used to work.
    /// </summary>
    [Fact]
    public async Task An_artifact_may_NOT_be_written_inside_the_evictable_segment_cache()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseSegmentStream(new FakeEngine { Duration = TimeSpan.FromSeconds(20) }, Options());
        await interceptor.AskAsync("https://x/shenora-hls/track.flac/seg0.m4s");
        var source = Path.Combine(_sources, "track.flac");

        var refused = await route.MergeAsync(source, Path.Combine(_cache, "keep", "track.mp4"));

        Assert.Equal(SegmentMergeOutcome.DestinationRefused, refused.Outcome);
        Assert.Contains("evict", refused.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_cache, "keep")), "nothing may be created at a refused destination");
    }

    /// <summary>
    /// An incomplete stream is not an artifact, and a source nobody has streamed is not one either — both
    /// answer without writing anything. ⚠ "Complete" is a predicate over PRODUCED OUTPUT, so a source this
    /// route has never served can only honestly answer no.
    /// </summary>
    [Fact]
    public async Task An_unstreamed_or_unfinished_source_is_reported_rather_than_written()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        // WritesInit false leaves the init segment missing, so the parts are not all there.
        var engine = new FakeEngine { Duration = TimeSpan.FromSeconds(20), WritesInit = false };
        using var route = interceptor.UseSegmentStream(engine, Options());
        var source = Path.Combine(_sources, "track.flac");
        var artifact = Path.Combine(_root, "offline", "track.mp4");

        Assert.False(route.IsComplete(source), "nothing has been asked for yet");
        var unknown = await route.MergeAsync(source, artifact);
        Assert.Equal(SegmentMergeOutcome.UnknownSource, unknown.Outcome);

        await interceptor.AskAsync("https://x/shenora-hls/track.flac/seg0.m4s");

        Assert.False(route.IsComplete(source), "the init segment was never written");
        var incomplete = await route.MergeAsync(source, artifact);
        Assert.Equal(SegmentMergeOutcome.Incomplete, incomplete.Outcome);
        Assert.False(File.Exists(artifact));
    }

    /// <summary>
    /// A shell with no engine registers no route, and must still ANSWER the question rather than throw —
    /// an app should not need a platform branch to ask whether something was produced.
    /// </summary>
    [Fact]
    public async Task A_shell_with_no_engine_answers_the_artifact_questions_without_throwing()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseSegmentStream(new UnavailableEngine(), Options());

        Assert.False(route.IsComplete(Path.Combine(_sources, "track.flac")));
        var result = await route.MergeAsync(Path.Combine(_sources, "track.flac"),
                                                  Path.Combine(_root, "offline", "x.mp4"));
        Assert.Equal(SegmentMergeOutcome.UnknownSource, result.Outcome);
    }

    /// <summary>
    /// ⚠ The cache check compares full paths with a trailing separator, or a sibling directory whose name
    /// merely STARTS with the cache's would be refused — the prefix bug every containment check answers.
    /// </summary>
    [Fact]
    public void A_sibling_directory_sharing_the_cache_s_prefix_is_not_inside_it()
    {
        Assert.True(SegmentMerge.IsInside(Path.Combine(_cache, "a", "f.mp4"), _cache));
        Assert.False(SegmentMerge.IsInside(_cache + "-offline/f.mp4", _cache));
        Assert.False(SegmentMerge.IsInside(Path.Combine(_root, "elsewhere", "f.mp4"), _cache));
    }

    [Theory]
    [InlineData("seg0.m4s", true, 0)]
    [InlineData("seg12.m4s", true, 12)]
    [InlineData("index.m3u8", false, -1)]
    [InlineData("seg.m4s", false, -1)]
    [InlineData("segx.m4s", false, -1)]
    [InlineData("seg-1.m4s", false, -1)]
    [InlineData("seg1.m4sx", false, -1)]
    [InlineData("seg 1.m4s", false, -1)]
    public void Segment_names_parse_only_in_the_exact_shape(string resource, bool expected, int index)
    {
        Assert.Equal(expected, SegmentStream.TryParseSegmentIndex(resource, out var parsed));
        Assert.Equal(index, parsed);
    }
}
