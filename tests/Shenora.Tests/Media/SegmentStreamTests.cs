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

        public List<SegmentRunRequest> Starts { get; } = [];

        public bool IsAvailable => true;
        public string Describe() => "fake";
        public TimeSpan? DurationOf(string source) => Duration;
        public bool HasPicture(string source) => SourceHasPicture;

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

    private string NewSource(string name = "track.flac")
    {
        var path = Path.Combine(_sources, name);
        File.WriteAllText(path, "original-bytes");
        return path;
    }

    private SegmentStreamOptions Options() => new()
    {
        Access = new MediaAccessOptions
        {
            Resolve = uri => uri.AbsolutePath.StartsWith("/shenora-hls/", StringComparison.Ordinal)
                ? Path.Combine(_sources, Path.GetFileName(uri.AbsolutePath))
                : null,
            AllowedRoots = [_sources],
            CacheRoot = _cache,
        },
    };

    private static async Task<string> BodyOf(WebViewResourceResponse response)
    {
        using var reader = new StreamReader(response.Content);
        return await reader.ReadToEndAsync();
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
        public TimeSpan? DurationOf(string source) => null;
        public bool HasPicture(string source) => false;
        public bool HasRenderedPicture(string segment) => false;
        public ISegmentRun? Start(SegmentRunRequest request) => null;
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
