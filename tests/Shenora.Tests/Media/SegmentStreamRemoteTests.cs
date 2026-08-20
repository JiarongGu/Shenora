using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;
using Shenora.Core.WebView;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Media;

/// <summary>
/// The remote-source door: a page cannot name a url, it names a HANDLE the app issued.
///
/// <para>
/// The load-bearing test here is <see cref="A_registered_url_never_reaches_a_log_line"/>. A remote media
/// url routinely carries the caller's credentials, this route logs the source it is working on, and
/// <c>Path.GetFileName</c> — which sanitises a local path — leaves a query string completely intact. So the
/// leak is one careless interpolation away at all times, and nothing else in the suite would notice it.
/// </para>
/// </summary>
public class SegmentStreamRemoteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shenora-rhls-" + Guid.NewGuid().ToString("N"));
    private readonly string _sources;
    private readonly string _cache;

    public SegmentStreamRemoteTests()
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

    /// <summary>Everything the route wrote, so a test can read the diagnostics back.</summary>
    private sealed class Recorder : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
                                Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, error));
        public string All => string.Join("\n", Lines);
    }

    /// <summary>A fake that COUNTS its probes — the supplied-vs-probed claim is about calls not happening.</summary>
    private sealed class CountingEngine : ISegmentEngine
    {
        public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(20);
        public bool SourceHasPicture { get; init; }
        public int DurationProbes { get; private set; }
        public int PictureProbes { get; private set; }
        public List<SegmentRunRequest> Starts { get; } = [];

        public bool IsAvailable => true;
        public string Describe() => "counting";
        public TimeSpan? DurationOf(MediaByteSource source) { DurationProbes++; return Duration; }
        public bool HasPicture(MediaByteSource source) { PictureProbes++; return SourceHasPicture; }
        public SegmentPlan? PlanSegments(MediaByteSource source, double seconds, CancellationToken ct = default) => null;
        public bool HasRenderedPicture(string segment) => true;

        public ISegmentRun? Start(SegmentRunRequest request)
        {
            Starts.Add(request);
            File.WriteAllText(Path.Combine(request.Directory, SegmentRunRequest.InitSegmentName), "init");
            for (var i = request.FirstSegment; i < 8; i++)
                File.WriteAllText(Path.Combine(request.Directory, $"seg{i}.m4s"), "audio+picture");
            return new Run();
        }

        private sealed class Run : ISegmentRun
        {
            public bool HasExited => true;
            public void Dispose() { }
        }
    }

    private SegmentStreamOptions Options(MediaSourceRegistry? sources = null, ILogger? log = null) => new()
    {
        Access = new MediaAccessOptions
        {
            // Deliberately narrow: a remote source must work while the LOCAL resolver matches nothing,
            // which is what proves the two paths are independent.
            Resolve = _ => null,
            AllowedRoots = [_sources],
            CacheRoot = _cache,
            Log = log,
        },
        Sources = sources,
    };

    private static string Url(string handle, string resource) => $"https://x/shenora-hls/~remote/{handle}/{resource}";

    /// <summary>
    /// A stand-in transport. ⚠ Every test that drives the ROUTE needs one: without an opener the route
    /// refuses the source outright, which is deliberate — a manifest served over bytes nobody can read
    /// gives a page a playlist whose every segment 503s for ever.
    /// </summary>
    private static Func<CancellationToken, Stream> Bytes => _ => new MemoryStream();

    private static async Task<string> BodyOf(WebViewResourceResponse response)
    {
        using var reader = new StreamReader(response.Content);
        return await reader.ReadToEndAsync();
    }

    // ── The reason the type exists ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 A url is a credential carrier. The route may say what it is doing; it may not say it with the url.
    /// </summary>
    [Fact]
    public async Task A_registered_url_never_reaches_a_log_line()
    {
        const string secret = "AKIAI0SFODNN7EXAMPLE-signature-that-must-not-be-logged";
        var log = new Recorder();
        var registry = new MediaSourceRegistry();
        var handle = registry.Register(new RemoteMediaSource
        {
            // Every shape that has ever leaked: a token in the query, and one in the userinfo.
            Url = new Uri($"https://cdn.example.com/films/reel.mkv?X-Amz-Signature={secret}"),
            Label = "Reel (2026)",
            Open = Bytes,
        });

        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(new CountingEngine(), Options(registry, log));

        // Drive enough of the route to exercise the diagnostics: the manifest, then a segment.
        await interceptor.AskAsync(Url(handle, "index.m3u8"));
        await interceptor.AskAsync(Url(handle, "seg0.m4s"));

        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(secret, log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Amz-Signature", log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.example.com", log.All, StringComparison.Ordinal);
        // ⚠ And the positive half, or the test would pass just as well if the route logged NOTHING —
        // which would be a different defect wearing this one's green tick.
        Assert.Contains("Reel (2026)", log.All, StringComparison.Ordinal);
    }

    /// <summary>The redaction that stops a debugger watch or a formatted exception doing the same thing.</summary>
    [Fact]
    public void ToString_does_not_print_the_url()
    {
        var source = new RemoteMediaSource
        {
            Url = new Uri("https://cdn.example.com/x.mkv?token=SECRET"),
            Label = "Track",
        };
        Assert.DoesNotContain("SECRET", source.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.example.com", source.ToString(), StringComparison.Ordinal);
        Assert.Contains("Track", source.ToString(), StringComparison.Ordinal);
    }

    // ── The inversion ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_handle_that_was_never_issued_is_not_served()
    {
        var registry = new MediaSourceRegistry();
        registry.Register(new RemoteMediaSource { Url = new Uri("https://cdn/x.mkv"), Label = "x" });

        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(new CountingEngine(), Options(registry));

        // A guess. 128 bits means this is the only way in, and it is not a way in.
        Assert.Null(await interceptor.AskAsync(Url("0123456789abcdef0123456789abcdef", "index.m3u8")));
    }

    [Fact]
    public async Task Without_a_registry_the_remote_shape_is_refused_entirely()
    {
        var interceptor = new FakeInterceptor();
        // No `Sources` — the default. A remote source must be impossible until an app makes one possible.
        using var _ = interceptor.UseSegmentStream(new CountingEngine(), Options());

        Assert.Null(await interceptor.AskAsync(Url("0123456789abcdef0123456789abcdef", "index.m3u8")));
    }

    /// <summary>
    /// 🔴 <b>A source with no opener is refused AT THE MANIFEST, not at the first segment.</b> The kit ships
    /// no transport, so bytes are the app's to fetch — and a manifest is derived from the DURATION, which is
    /// suppliable. Serve one anyway and the page gets a complete playlist whose every entry <c>503</c>s for
    /// ever, which is the failure this route spends most of its diagnostics trying not to produce.
    /// </summary>
    [Fact]
    public async Task A_source_registered_without_an_opener_is_refused_and_says_why()
    {
        var log = new Recorder();
        var registry = new MediaSourceRegistry();
        // Everything else supplied, so nothing but the missing opener can be the reason.
        var handle = registry.Register(new RemoteMediaSource
        {
            Url = new Uri("https://cdn/film.mkv"),
            Label = "openerless",
            Duration = TimeSpan.FromSeconds(20),
            HasPicture = true,
        });

        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(new CountingEngine(), Options(registry, log));

        var manifest = await interceptor.AskAsync(Url(handle, "index.m3u8"));

        Assert.Equal(404, manifest!.StatusCode);
        // ⚠ And it must NAME the cause. A 404 alone reads as "no such source", which is the one thing this
        // is not — the source was authorised and the route simply cannot read it.
        Assert.Contains("openerless", log.All, StringComparison.Ordinal);
        Assert.Contains("opener", log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Releasing_a_handle_stops_new_streams_through_it()
    {
        var registry = new MediaSourceRegistry();
        var handle = registry.Register(new RemoteMediaSource
        {
            Url = new Uri("https://cdn/x.mkv"), Label = "x", Open = Bytes,
        });

        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(new CountingEngine(), Options(registry));

        Assert.NotNull(await interceptor.AskAsync(Url(handle, "index.m3u8")));
        Assert.True(registry.Release(handle));
        Assert.Null(await interceptor.AskAsync(Url(handle, "index.m3u8")));
        Assert.False(registry.Release(handle));   // idempotent, and honest about it
    }

    /// <summary>
    /// Containment answers "may this PATH be read", which is not a question about a url — so a registered
    /// source is served while the allowed roots match nothing about it.
    /// </summary>
    [Fact]
    public async Task A_registered_source_is_served_without_meeting_the_allowed_roots()
    {
        var registry = new MediaSourceRegistry();
        var handle = registry.Register(new RemoteMediaSource
        {
            Url = new Uri("https://cdn.example.com/somewhere/else/entirely.mkv"),
            Label = "elsewhere",
            Open = Bytes,
        });

        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(new CountingEngine(), Options(registry));

        var manifest = await BodyOf((await interceptor.AskAsync(Url(handle, "index.m3u8")))!);
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD", manifest, StringComparison.Ordinal);
    }

    // ── Suppliable duration and picture ───────────────────────────────────────────────────────────

    /// <summary>
    /// ⚠ Two engine launches, each reading a network header, stand between a request and the FIRST
    /// manifest. The caller usually knows both already.
    /// </summary>
    [Fact]
    public async Task A_supplied_duration_and_picture_are_not_probed()
    {
        var registry = new MediaSourceRegistry();
        var handle = registry.Register(new RemoteMediaSource
        {
            Url = new Uri("https://cdn/film.mkv"),
            Label = "film",
            Duration = TimeSpan.FromSeconds(20),
            HasPicture = true,
            Open = Bytes,
        });

        var engine = new CountingEngine();
        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(engine, Options(registry));

        var manifest = await BodyOf((await interceptor.AskAsync(Url(handle, "index.m3u8")))!);

        Assert.Equal(0, engine.DurationProbes);
        Assert.Equal(0, engine.PictureProbes);
        // The supplied duration is the one the manifest was built from: 20s over a 6s grid is 4 segments.
        Assert.Contains("seg3.m4s", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("seg4.m4s", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unsupplied_duration_still_falls_back_to_the_engine()
    {
        // The other direction, or "not probed" could be true because nothing works.
        var registry = new MediaSourceRegistry();
        var handle = registry.Register(new RemoteMediaSource
        {
            Url = new Uri("https://cdn/f.mkv"), Label = "f", Open = Bytes,
        });

        var engine = new CountingEngine { Duration = TimeSpan.FromSeconds(20) };
        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(engine, Options(registry));

        var manifest = await BodyOf((await interceptor.AskAsync(Url(handle, "index.m3u8")))!);
        Assert.Equal(1, engine.DurationProbes);
        Assert.Contains("seg3.m4s", manifest, StringComparison.Ordinal);
    }

    // ── Caching a url that rotates ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 A presigned url is a different string every hour for the same film. Keyed on the url, the cache
    /// misses every time — re-segmenting from scratch while the previous copies wait for the sweep.
    /// </summary>
    [Fact]
    public async Task A_rotating_url_with_a_stable_identity_keeps_its_cache()
    {
        var registry = new MediaSourceRegistry();
        var engine = new CountingEngine();
        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(engine, Options(registry));

        var first = registry.Register(new RemoteMediaSource
        {
            Url = new Uri("https://cdn/film.mkv?sig=AAAA"), Label = "film", Identity = "catalogue:film-42",
            Open = Bytes,
        });
        await interceptor.AskAsync(Url(first, "seg0.m4s"));
        var firstDirectory = engine.Starts.Single().Directory;

        // The signature rotates; the film does not.
        var second = registry.Register(new RemoteMediaSource
        {
            Url = new Uri("https://cdn/film.mkv?sig=BBBB"), Label = "film", Identity = "catalogue:film-42",
            Open = Bytes,
        });
        await interceptor.AskAsync(Url(second, "seg0.m4s"));

        // Same cache directory, and no second production run — the segments were already there.
        Assert.Single(engine.Starts);
        Assert.Equal(firstDirectory, engine.Starts[0].Directory);
    }

    [Fact]
    public async Task Two_different_sources_do_not_share_a_cache()
    {
        // The direction that would make the test above pass with a constant key.
        var registry = new MediaSourceRegistry();
        var engine = new CountingEngine();
        var interceptor = new FakeInterceptor();
        using var _ = interceptor.UseSegmentStream(engine, Options(registry));

        var a = registry.Register(new RemoteMediaSource { Url = new Uri("https://cdn/a.mkv"), Label = "a", Open = Bytes });
        var b = registry.Register(new RemoteMediaSource { Url = new Uri("https://cdn/b.mkv"), Label = "b", Open = Bytes });
        await interceptor.AskAsync(Url(a, "seg0.m4s"));
        await interceptor.AskAsync(Url(b, "seg0.m4s"));

        Assert.Equal(2, engine.Starts.Count);
        Assert.NotEqual(engine.Starts[0].Directory, engine.Starts[1].Directory);
    }

    // ── Registration ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_handle_is_unguessable_and_unique()
    {
        var registry = new MediaSourceRegistry();
        var source = new RemoteMediaSource { Url = new Uri("https://cdn/x.mkv"), Label = "x" };
        var handles = Enumerable.Range(0, 200).Select(_ => registry.Register(source)).ToList();

        Assert.Equal(200, handles.Distinct(StringComparer.Ordinal).Count());
        // 128 bits as hex. Anything derived from the source would be guessable, which hands back exactly
        // the property the inversion bought.
        Assert.All(handles, h => Assert.Equal(32, h.Length));
        Assert.All(handles, h => Assert.DoesNotContain("cdn", h, StringComparison.Ordinal));
    }

    [Fact]
    public void Registration_refuses_what_it_cannot_serve()
    {
        var registry = new MediaSourceRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(new RemoteMediaSource
        {
            Url = new Uri("films/reel.mkv", UriKind.Relative), Label = "x",
        }));
        Assert.Throws<ArgumentException>(() => registry.Register(new RemoteMediaSource
        {
            Url = new Uri("https://cdn/x.mkv"), Label = "   ",
        }));
    }

    [Fact]
    public void ReleaseAll_clears_the_registry()
    {
        var registry = new MediaSourceRegistry();
        registry.Register(new RemoteMediaSource { Url = new Uri("https://cdn/a.mkv"), Label = "a" });
        registry.Register(new RemoteMediaSource { Url = new Uri("https://cdn/b.mkv"), Label = "b" });
        Assert.Equal(2, registry.Count);
        registry.ReleaseAll();
        Assert.Equal(0, registry.Count);
    }
}
