using System.Collections.Concurrent;
using Shenora;
using Shenora.Modules.Media;
using Shenora.Core.Events;
using Shenora.Core.WebView;
using Shenora.Engine;
using Shenora.Engine.Files;
using Shenora.Engine.Missions;
using Shenora.Tests.TestSupport;

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

    // The interceptor harness is `TestSupport.FakeInterceptor` — shared, because this file and
    // `SegmentStreamTests` each carried a private copy of it until 2026-08-12 and both had
    // `RangeDelivery` hard-coded, which made the Android range rule (D44) untestable from either.

    private string NewSource(string name, string content = "original-bytes")
    {
        var path = Path.Combine(_sources, name);
        File.WriteAllText(path, content);
        return path;
    }

    private MediaConversionOptions Options(Func<MediaConversionRequest, CancellationToken, Task> convert) => new()
    {
        Access = new MediaAccessOptions
        {
            Resolve = uri => uri.AbsolutePath.StartsWith("/media", StringComparison.Ordinal)
                ? Path.Combine(_sources, Uri.UnescapeDataString(uri.Query.TrimStart('?')))
                : null,
            CacheRoot = _cache,
            AllowedRoots = [_sources],
        },
        Convert = convert,
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
        Access = new MediaAccessOptions
        {
            Resolve = uri => uri.AbsolutePath.StartsWith("/media", StringComparison.Ordinal) ? url : null,
            CacheRoot = _cache,
            AllowedRoots = [_sources],
        },
        Convert = convert,
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

    // ── the DEFAULT engine ───────────────────────────────────────────────────────────────────────────
    //
    // 🔴 `Convert` STOPPED BEING REQUIRED on 2026-08-10: the kit now supplies a converter built from the
    // PLATFORM's own codecs, and an app writes one only for work past the platform's reach. The tests
    // below are about that default being REAL — D63's rule, because a default nothing consults is exactly
    // the shape this repo has shipped three times: declared, documented, and never reached.

    // ⚠ "the default really is Mp4Remuxer + the platform seam" is asserted in `Mp4RemuxerTests`
    // (`The_kit_default_converter_rescues_a_soundtrack_MP4_cannot_carry`), because proving it needs a real
    // AC-3 film and that file can only be built where the container helpers live. The first attempt at it
    // belonged here and could not work: fed bytes that are not a container, the muxer refuses before it
    // ever consults a codec, so the test asserted a call that could not happen.

    /// <summary>
    /// ⚠ Both set is a COMPOSITION mistake and must be loud at registration, not silent for the life of
    /// the app. `Conversion` configures the default engine, so a custom `Convert` makes it dead
    /// configuration — the reader would reasonably believe the codecs they passed were in use.
    /// <para>
    /// 🔴 <b>The assertion names the CURRENT property on purpose, and this test is why the rule is worth
    /// stating.</b> The stream-kind unification renamed <c>AudioConversion</c> to <c>Conversion</c> and
    /// updated the COMPILED half of this test — the initialiser below — while leaving the assertion
    /// matching the old name. It went on passing, so it PROTECTED an exception message that told adopters
    /// to set a property the surface no longer had, for as long as nobody read it. **A test pinning a
    /// message is only as good as the name inside the string**, which no compiler checks.
    /// </para>
    /// </summary>
    [Fact]
    public void Setting_both_Convert_and_Conversion_THROWS_rather_than_ignoring_one()
    {
        var options = new MediaConversionOptions
        {
            Access = new MediaAccessOptions { Resolve = _ => null, CacheRoot = _cache, AllowedRoots = [_sources] },
            Convert = (_, _) => Task.CompletedTask,
            Conversion = new StubAudioConversion(),
        };

        var error = Assert.Throws<InvalidOperationException>(() => options.Converter());
        // The whole phrase, not two loose substrings: it pins the message to the property names that
        // actually exist, so the next rename fails HERE instead of shipping guidance nobody can follow.
        Assert.Contains("sets both Convert and Conversion", error.Message, StringComparison.Ordinal);
    }

    /// <summary>With neither, the default still exists — container repair, which needs no codecs at all.</summary>
    [Fact]
    public void With_neither_the_default_is_container_repair_rather_than_nothing()
    {
        var options = new MediaConversionOptions
        {
            Access = new MediaAccessOptions { Resolve = _ => null, CacheRoot = _cache, AllowedRoots = [_sources] },
        };

        Assert.NotNull(options.Converter());
    }

    /// <summary>
    /// 🔴 <b>A DROPPED SOUNDTRACK IS A FAILURE, AND NOTHING IS CACHED.</b> It used to Commit first and
    /// report <c>READY</c> with the codecs beside it, so the route served — and cached forever — a SILENT
    /// FILM as a 200. Owner, 2026-08-10: <i>"i dont think fail silently is good; if codec not support just
    /// not support"</i>.
    /// <para>
    /// The cache assertion is the load-bearing half: a failure that still committed would be served as a
    /// HIT by every later request, so the silence would outlive the diagnosis.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_conversion_that_drops_a_stream_FAILS_and_caches_nothing()
    {
        var failed = new TaskCompletionSource<object?>();
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            Scopes = [PathClaims.Scope],
        });
        var bus = new EventBus();
        bus.Subscribe("SHENORA.MEDIA", MediaConversionEvents.Failed, payload =>
        {
            failed.TrySetResult(payload);
            return Task.CompletedTask;
        });
        var interceptor = new FakeInterceptor();
        interceptor.UseMediaConversion(scheduler, bus, Options(async (request, ct) =>
        {
            // A converter that produced a perfectly good FILE and lost the audio — the dangerous shape,
            // because on its own it looks like success.
            await File.WriteAllTextAsync(request.DestinationPath, "video-only-bytes", ct);
            request.Dropped.Add("ac3");
        }));
        NewSource("clip.mkv");

        await interceptor.AskAsync("https://0.0.0.1/media?clip.mkv");
        var payload = await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        Assert.Contains(MediaConversionErrorCodes.UnsupportedCodec, json, StringComparison.Ordinal);
        Assert.Contains("ac3", json, StringComparison.Ordinal);

        Assert.False(Directory.Exists(_cache) && Directory.EnumerateFiles(_cache, "*.mp4").Any(),
            "a conversion that dropped a stream committed a cache entry — every later request would serve "
            + "the silent film as a hit");

        // And the route does not serve the file it refused. ⚠ 404 rather than the 503 this asserted until
        // 2026-08-13: a permanent "not ready" INVITES the retry loop that re-transcodes for ever (see
        // `A_source_that_cannot_be_carried_is_converted_ONCE…`). The page has already been told why, by
        // codec name, on the FAILED event awaited above.
        Assert.Equal(404, (await interceptor.AskAsync("https://0.0.0.1/media?clip.mkv"))!.StatusCode);
    }

    /// <summary>
    /// 🔴 <b>A source that can never be carried is converted ONCE, however many times it is asked for.</b>
    ///
    /// <para>
    /// Found on the iOS simulator 2026-08-13: the sample's picture-conversion fixture failed six times in
    /// six seconds (missions m19…m24) because the page's own 503 retry restarted the whole conversion each
    /// time. <c>request.Dropped</c> is only populated AFTER the writer finishes, so discovering "this codec
    /// cannot be carried" COSTS A FULL TRANSCODE — a second on a fixture, minutes on a film, once per second
    /// for as long as the page is open.
    /// </para>
    /// <para>
    /// ⚠ <b>The two assertions catch DIFFERENT failures, and neither is redundant.</b> The status catches a
    /// route that does not decline at all — that is the one a "never remembers" sabotage trips first
    /// (<c>Expected 404, Actual 503</c>). The COUNT catches the subtler one this test exists for: a route
    /// that declines correctly and still submits the conversion, which no status code can see and which
    /// would leave the defect fully intact behind a green test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_source_that_cannot_be_carried_is_converted_ONCE_however_often_it_is_asked_for()
    {
        var failed = new TaskCompletionSource<object?>();
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            Scopes = [PathClaims.Scope],
        });
        var bus = new EventBus();
        bus.Subscribe("SHENORA.MEDIA", MediaConversionEvents.Failed, payload =>
        {
            failed.TrySetResult(payload);
            return Task.CompletedTask;
        });

        var conversions = 0;
        var interceptor = new FakeInterceptor();
        interceptor.UseMediaConversion(scheduler, bus, Options(async (request, ct) =>
        {
            Interlocked.Increment(ref conversions);
            await File.WriteAllTextAsync(request.DestinationPath, "video-only-bytes", ct);
            request.Dropped.Add("ac3");
        }));
        NewSource("clip.mkv");

        await interceptor.AskAsync("https://0.0.0.1/media?clip.mkv");
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The page's retry loop, which is what the device actually does.
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(404, (await interceptor.AskAsync("https://0.0.0.1/media?clip.mkv"))!.StatusCode);
        }

        Assert.Equal(1, conversions);
    }

    /// <summary>A stand-in platform seam — enough to be SET, which is all these two tests need.</summary>
    private sealed class StubAudioConversion : IMediaStreamConversion
    {
        public bool CanConvert(MediaStreamKind kind, string codec) => true;

        public IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate) => null;
    }
}
