using System.Text;
using Shenora;
using Shenora.Media;

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

    /// <summary>A fake interceptor: no webview, just the pipeline the shells share.</summary>
    private sealed class FakeInterceptor : IWebViewInterceptor
    {
        private readonly WebViewResourcePipeline _pipeline = new();
        public WebViewRangeDelivery RangeDelivery => WebViewRangeDelivery.Sliced;
        public IDisposable Use(WebViewResourceMiddleware middleware) => _pipeline.Use(middleware);

        public Task<WebViewResourceResponse?> AskAsync(string url) =>
            _pipeline.Build() is { } handler
                ? handler(new WebViewResourceRequest
                {
                    Uri = new Uri(url),
                    Method = "GET",
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                }, CancellationToken.None)
                : Task.FromResult<WebViewResourceResponse?>(null);
    }

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
            // Write the whole remainder immediately: this test is about POLICY, not about timing.
            for (var i = request.FirstSegment; i < 8; i++)
            {
                File.WriteAllText(Path.Combine(request.Directory, $"seg{i}.ts"),
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
        Resolve = uri => uri.AbsolutePath.StartsWith("/shenora-hls/", StringComparison.Ordinal)
            ? Path.Combine(_sources, Path.GetFileName(uri.AbsolutePath))
            : null,
        AllowedRoots = [_sources],
        CacheRoot = _cache,
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
        Assert.Contains("seg0.ts", manifest, StringComparison.Ordinal);
        Assert.Contains("seg3.ts", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("seg4.ts", manifest, StringComparison.Ordinal);

        // ⚠ The load-bearing assertion. A playlist's declared total is the SUM of its EXTINFs, so a flat
        // last entry would overstate the source by up to a whole segment and a scrub bar built on it seeks
        // past the end.
        Assert.Contains("#EXTINF:2.000,", manifest, StringComparison.Ordinal);
        Assert.Equal(3, manifest.Split("#EXTINF:6.000,").Length - 1);
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

        var response = await interceptor.AskAsync("https://x/shenora-hls/clip.mkv/seg0.ts");

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

        var response = await interceptor.AskAsync("https://x/shenora-hls/track.flac/seg0.ts");

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
        var response = await interceptor.AskAsync("https://x/shenora-hls/track.flac/seg4.ts");
        Assert.Equal(404, response!.StatusCode);
    }

    [Fact]
    public async Task A_source_outside_the_allowed_roots_is_refused_like_a_missing_one()
    {
        NewSource();
        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(new FakeEngine(), new SegmentStreamOptions
        {
            Resolve = _ => Path.Combine(_root, "outside.flac"),   // resolves, but not under AllowedRoots
            AllowedRoots = [_sources],
            CacheRoot = _cache,
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
    [InlineData("seg0.ts", true, 0)]
    [InlineData("seg12.ts", true, 12)]
    [InlineData("index.m3u8", false, -1)]
    [InlineData("seg.ts", false, -1)]
    [InlineData("segx.ts", false, -1)]
    [InlineData("seg-1.ts", false, -1)]
    [InlineData("seg1.tsx", false, -1)]
    [InlineData("seg 1.ts", false, -1)]
    public void Segment_names_parse_only_in_the_exact_shape(string resource, bool expected, int index)
    {
        Assert.Equal(expected, SegmentStream.TryParseSegmentIndex(resource, out var parsed));
        Assert.Equal(index, parsed);
    }
}
