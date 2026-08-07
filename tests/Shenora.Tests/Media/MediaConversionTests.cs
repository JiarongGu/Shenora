using System.Collections.Concurrent;
using Shenora.Core;
using Shenora.Media;
using Shenora.Missions;

namespace Shenora.Tests.Media;

/// <summary>
/// The conversion middleware — DM3. It builds nothing: it COMPOSES the mission scheduler, a path claim, an
/// atomic replace and the derived cache key. So what is worth testing is the composition's guarantees, not
/// that four calls happen — a version that called all four in the wrong order would pass any "did it run"
/// check while converting twice, or serving a half-written file.
/// </summary>
public class MediaConversionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shenora-conv-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _sources;
    private readonly string _cache;

    public MediaConversionTests()
    {
        _sources = Path.Combine(_root, "src");
        _cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_sources);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a cache is disposable */ }
        GC.SuppressFinalize(this);
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

    private string NewSource(string name, string content = "original-bytes")
    {
        var path = Path.Combine(_sources, name);
        File.WriteAllText(path, content);
        return path;
    }

    private MediaConversionOptions Options(Func<MediaConversionRequest, CancellationToken, Task> convert) => new()
    {
        Resolve = uri => uri.AbsolutePath.StartsWith("/media", StringComparison.Ordinal)
            ? Path.Combine(_sources, Uri.UnescapeDataString(uri.Query.TrimStart('?')))
            : null,
        Convert = convert,
        CacheRoot = _cache,
        AllowedRoots = [_sources],
        CacheExtension = ".mp4",
    };

    private static async Task<string> BodyOf(WebViewResourceResponse response)
    {
        using var reader = new StreamReader(response.Content);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task A_miss_answers_503_and_does_not_block_on_the_conversion()
    {
        // The load-bearing property: the mobile seam resolves SYNCHRONOUSLY, so a request must never wait
        // for an encoder. The convert delegate here never completes; the request still returns.
        var started = new TaskCompletionSource();
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            Scopes = [PathClaims.Scope],
        });
        var interceptor = new FakeInterceptor();
        interceptor.UseMediaConversion(scheduler, new EventBus(), Options(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);   // never finishes — bounded by the test's own wait
        }));
        NewSource("clip.mkv");

        var response = await interceptor.AskAsync("https://0.0.0.1/media?clip.mkv").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(response);
        Assert.Equal(503, response!.StatusCode);
        Assert.Equal("1", response.Headers["Retry-After"]);
        // …and the conversion really did start, so the 503 is "not yet" rather than "nothing happened".
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_converted_source_is_served_from_cache_on_the_next_request()
    {
        // ⚠ Wait on READY, not on the convert delegate returning. The delegate finishes BEFORE the atomic
        // Commit that publishes the file, so signalling from inside it races the very thing under test —
        // the first version of this test did exactly that and failed with a 503 that was CORRECT.
        // READY is also what a real page waits for, so this exercises the shipped contract.
        var ready = new TaskCompletionSource();
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            Scopes = [PathClaims.Scope],
        });
        var bus = new EventBus();
        bus.Subscribe("SHENORA.MEDIA", MediaConversionEvents.Ready, _ => { ready.TrySetResult(); return Task.CompletedTask; });
        var interceptor = new FakeInterceptor();
        interceptor.UseMediaConversion(scheduler, bus, Options(async (conversion, ct) =>
            await File.WriteAllTextAsync(conversion.DestinationPath, "converted-bytes", ct)));
        NewSource("clip.mkv");

        Assert.Equal(503, (await interceptor.AskAsync("https://0.0.0.1/media?clip.mkv"))!.StatusCode);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await interceptor.AskAsync("https://0.0.0.1/media?clip.mkv");
        Assert.Equal(200, second!.StatusCode);
        Assert.Equal("converted-bytes", await BodyOf(second));
        // Served as MEDIA, from the cache extension — a body no <video> can identify is the same as no body.
        Assert.Equal("video/mp4", second.Headers["Content-Type"]);
    }

    [Fact]
    public async Task A_FAILED_conversion_leaves_NO_cache_entry_to_serve()
    {
        // The atomic-output guarantee, and the reason Files.BeginReplace is in this composition. A converter
        // that writes half a file and throws must not leave something a later request treats as a HIT —
        // that is a permanently broken video with no way to notice.
        var attempts = 0;
        var failed = new TaskCompletionSource();
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            Scopes = [PathClaims.Scope],
        });
        var bus = new EventBus();
        bus.Subscribe("SHENORA.MEDIA", MediaConversionEvents.Failed, _ => { failed.TrySetResult(); return Task.CompletedTask; });
        var interceptor = new FakeInterceptor();
        interceptor.UseMediaConversion(scheduler, bus, Options(async (conversion, ct) =>
        {
            Interlocked.Increment(ref attempts);
            await File.WriteAllTextAsync(conversion.DestinationPath, "half-writ", ct);
            throw new InvalidOperationException("encoder died");
        }));
        NewSource("clip.mkv");

        await interceptor.AskAsync("https://0.0.0.1/media?clip.mkv");
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(Directory.Exists(_cache) && Directory.EnumerateFiles(_cache, "*.mp4").Any(),
            "a failed conversion committed a cache entry — a later request would serve the half-written file");
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Replacing_the_SOURCE_invalidates_its_conversion()
    {
        // The cache key is identity+length+mtime, not the path — so yesterday's conversion is never served
        // for a file the user has since replaced. All three surveyed implementations reached this
        // independently, which is why DerivedCacheKey exists at all.
        var conversions = new ConcurrentBag<string>();
        var round = new TaskCompletionSource();
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            Scopes = [PathClaims.Scope],
        });
        var bus = new EventBus();
        // READY, not the delegate — see the cache-hit test for why signalling from inside Convert races
        // the atomic commit it precedes.
        bus.Subscribe("SHENORA.MEDIA", MediaConversionEvents.Ready, _ => { round.TrySetResult(); return Task.CompletedTask; });
        var interceptor = new FakeInterceptor();
        interceptor.UseMediaConversion(scheduler, bus, Options(async (conversion, ct) =>
        {
            var body = await File.ReadAllTextAsync(conversion.SourcePath, ct);
            conversions.Add(body);
            await File.WriteAllTextAsync(conversion.DestinationPath, "converted:" + body, ct);
        }));

        var source = NewSource("clip.mkv", "first");
        await interceptor.AskAsync("https://0.0.0.1/media?clip.mkv");
        await round.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(200, (await interceptor.AskAsync("https://0.0.0.1/media?clip.mkv"))!.StatusCode);

        // Same PATH, different content — a path-only key would serve the stale conversion here.
        var second = new TaskCompletionSource();
        bus.Subscribe("SHENORA.MEDIA", MediaConversionEvents.Ready, _ => { second.TrySetResult(); return Task.CompletedTask; });
        await File.WriteAllTextAsync(source, "second-and-longer");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(503, (await interceptor.AskAsync("https://0.0.0.1/media?clip.mkv"))!.StatusCode);
        await second.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var served = await interceptor.AskAsync("https://0.0.0.1/media?clip.mkv");
        Assert.Equal("converted:second-and-longer", await BodyOf(served!));
        Assert.Equal(2, conversions.Count);
    }

    [Fact]
    public async Task A_source_outside_the_allowed_roots_is_refused_as_a_plain_404()
    {
        // Containment, on a path the PAGE supplies. Refused with the SAME 404 as a missing file, so nothing
        // can probe for existence by comparing responses.
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            Scopes = [PathClaims.Scope],
        });
        var converted = false;
        var interceptor = new FakeInterceptor();
        interceptor.UseMediaConversion(scheduler, new EventBus(),
            Options((_, _) => { converted = true; return Task.CompletedTask; }));

        var escaped = await interceptor.AskAsync("https://0.0.0.1/media?" + Uri.EscapeDataString("../escaped.mkv"));

        Assert.Equal(404, escaped!.StatusCode);
        Assert.False(converted, "a source outside the allowed roots reached the converter");
    }

    private MediaConversionOptions RemoteOptions(string url, Func<Uri, bool>? allow,
        Func<MediaConversionRequest, CancellationToken, Task> convert) => new()
    {
        Resolve = uri => uri.AbsolutePath.StartsWith("/media", StringComparison.Ordinal) ? url : null,
        Convert = convert,
        CacheRoot = _cache,
        AllowedRoots = [_sources],
        AllowRemoteSource = allow,
    };

    [Fact]
    public async Task A_remote_source_is_refused_when_there_is_NO_policy()
    {
        // Fail-CLOSED by default (DM4). An app that never thought about SSRF is safe rather than exposed:
        // the page picks the url, and the HOST can reach addresses the page cannot.
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            Scopes = [PathClaims.Scope],
        });
        var converted = false;
        var interceptor = new FakeInterceptor();
        interceptor.UseMediaConversion(scheduler, new EventBus(), RemoteOptions(
            "http://169.254.169.254/latest/meta-data/", allow: null,
            (_, _) => { converted = true; return Task.CompletedTask; }));

        var response = await interceptor.AskAsync("https://0.0.0.1/media?whatever");

        Assert.Equal(404, response!.StatusCode);
        Assert.False(converted, "a remote source reached the engine with no policy allowing it");
    }

    [Fact]
    public async Task A_remote_source_is_refused_when_the_policy_THROWS()
    {
        // The second fail-closed direction, and the one that is easy to get wrong: a check that could not
        // be COMPLETED is not a check that passed.
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            Scopes = [PathClaims.Scope],
        });
        var converted = false;
        var interceptor = new FakeInterceptor();
        interceptor.UseMediaConversion(scheduler, new EventBus(), RemoteOptions(
            "https://cdn.example/clip.mkv", allow: _ => throw new InvalidOperationException("policy broke"),
            (_, _) => { converted = true; return Task.CompletedTask; }));

        var response = await interceptor.AskAsync("https://0.0.0.1/media?whatever");

        Assert.Equal(404, response!.StatusCode);
        Assert.False(converted, "a throwing policy was treated as permission");
    }

    [Fact]
    public async Task An_ALLOWED_remote_source_reaches_the_engine_as_its_url()
    {
        // The kit authorises; it never fetches. The engine gets the url and reads it itself, which is what
        // keeps an HTTP client (and its credential and proxy questions) out of this package.
        const string url = "https://cdn.example/clip.mkv";
        var seen = new TaskCompletionSource<string>();
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            Scopes = [PathClaims.Scope],
        });
        var interceptor = new FakeInterceptor();
        interceptor.UseMediaConversion(scheduler, new EventBus(), RemoteOptions(
            url, allow: u => u.Host == "cdn.example",
            async (conversion, ct) =>
            {
                seen.TrySetResult(conversion.SourcePath);
                await File.WriteAllTextAsync(conversion.DestinationPath, "converted", ct);
            }));

        Assert.Equal(503, (await interceptor.AskAsync("https://0.0.0.1/media?whatever"))!.StatusCode);

        Assert.Equal(url, await seen.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_non_web_scheme_falls_to_the_LOCAL_branch_and_is_contained()
    {
        // Only http/https count as remote. Anything else must NOT skip containment by being called
        // "remote" and then meeting a policy written to think about web addresses — file:// most of all.
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            Scopes = [PathClaims.Scope],
        });
        var converted = false;
        var interceptor = new FakeInterceptor();
        interceptor.UseMediaConversion(scheduler, new EventBus(), RemoteOptions(
            "file:///C:/Windows/System32/config/SAM", allow: _ => true,
            (_, _) => { converted = true; return Task.CompletedTask; }));

        var response = await interceptor.AskAsync("https://0.0.0.1/media?whatever");

        Assert.Equal(404, response!.StatusCode);
        Assert.False(converted, "a file:// source was treated as remote and skipped containment");
    }

    [Fact]
    public async Task A_request_this_route_does_not_own_falls_through_to_the_rest_of_the_pipeline()
    {
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            Scopes = [PathClaims.Scope],
        });
        var interceptor = new FakeInterceptor();
        interceptor.UseMediaConversion(scheduler, new EventBus(), Options((_, _) => Task.CompletedTask));

        Assert.Null(await interceptor.AskAsync("https://0.0.0.1/index.html"));
    }
}
