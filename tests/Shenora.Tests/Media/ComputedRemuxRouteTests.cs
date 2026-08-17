using Shenora.Core.Events;
using Shenora.Core.WebView;
using Shenora.Engine.Missions;
using Shenora.Modules.Media;
using Shenora.Tests.TestSupport;

// ⚠ THE FIXTURE BUILDER IS `Mp4RemuxerTests`', NOT A COPY OF IT — the same rule `Mp4LayoutTests` follows.
// Every assertion below compares what the ROUTE serves against what the REMUXER really writes, so the two
// halves have to be the same file; a second builder could make them two files that merely look alike.
using static Shenora.Tests.Media.Mp4RemuxerTests;

using Shenora;
namespace Shenora.Tests.Media;

/// <summary>
/// The computed-remux route — <b>an MP4 served over HTTP ranges that has never been produced</b>, and the
/// payoff of D71's whole design: <c>Plan</c> states the total, <c>CopyRange</c> answers any window of it, and
/// this route is the join between an HTTP <c>Range</c> header and those two.
///
/// <para>
/// 🔴 <b>What makes these assertions the spec rather than a formality.</b> Every case below compares the
/// route's bytes and its <c>Content-Range</c> against the file <see cref="Mp4Remuxer"/> really writes. A route
/// that advertised a total the bytes do not honour, or served the right NUMBER of bytes from the wrong offset,
/// fails a media element SILENTLY — a blank picture, no error, nothing logged (D44's measured failure, and the
/// reason the layout exists at all). "Status code was 206" would catch neither.
/// </para>
///
/// <para>
/// ⚠ Three of the cases here are about ORDER rather than about bytes, and each is a defect that would look
/// fine in isolation: the route must run BEFORE the conversion route (or every plannable film waits for a
/// whole transcode), it must FALL THROUGH what it cannot plan (or every re-encode source becomes unplayable),
/// and it must key its cached layout on the source's IDENTITY (or file A's byte map serves file B's bytes with
/// no error at all).
/// </para>
///
/// <para>
/// 🔴 <b>AND ALMOST EVERY CASE HERE NOW GOES THROUGH <see cref="AskPlannedAsync"/> RATHER THAN
/// <c>AskAsync</c>, because the first request for a source is a <c>503</c>.</b> The metadata walk moved into an
/// <c>IMissionScheduler</c> mission on 2026-08-13 — it may not run on a webview's resource thread at any size
/// — so the route answers <c>503 Retry-After: 1</c> until the plan lands. A test that asked once and asserted
/// <c>206</c> would now be asserting the retry protocol away.
/// </para>
/// </summary>
public class ComputedRemuxRouteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shenora-remux-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _sources;
    private readonly string _elsewhere;
    private readonly List<string> _log = [];

    /// <summary>
    /// A REAL scheduler, the way <c>MediaConversionTests</c> drives the conversion route — not a fake, because
    /// what these cases are about is work that genuinely happens on another thread.
    /// <para>
    /// ⚠ <b>No <c>Scopes</c>, deliberately: it is the assertion that the route needs no claim scope
    /// registered.</b> A planning mission that declared a <c>PathClaims</c> claim would make
    /// <c>SubmitAsync</c> THROW against this scheduler, and every test below would fail on a source it could
    /// never plan. (The composition case builds its own scheduler with the scope, because the conversion route
    /// behind it does claim a path.)
    /// </para>
    /// <para>
    /// Capacity 2 so a second film can be planned while one is parked — the case
    /// <see cref="A_film_already_planned_keeps_being_served_while_another_is_being_planned"/> needs.
    /// </para>
    /// </summary>
    private readonly MissionScheduler _scheduler = new(new MissionSchedulerOptions { GlobalLaneCapacity = 2 });

    public ComputedRemuxRouteTests()
    {
        _sources = Path.Combine(_root, "src");
        _elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(_sources);
        Directory.CreateDirectory(_elsewhere);
    }

    public void Dispose()
    {
        // The synchronous Dispose: it drops what is queued without awaiting a running walk, which is all a
        // test needs — every case that parks a planner releases it in a `finally`.
        _scheduler.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a temp tree is disposable */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Ask until the route stops answering <c>503</c> — <b>the page's own retry loop, compressed into three
    /// lines</b>, and the honest shape of every request for a source nobody has planned yet.
    ///
    /// <para>
    /// 🔴 It is NOT a convenience. The route answers <c>503 Retry-After: 1</c> while its planning mission
    /// walks the source, so "ask once and assert 206" is a test of a route that plans on the caller's thread —
    /// exactly the thing this design removed. Anything that is NOT a 503 is the answer and comes straight back,
    /// including a fall-through (null, or the tail middleware's body) and a 404: those are terminal too.
    /// </para>
    /// <para>
    /// ⚠ The deadline FAILS rather than returning a 503, because a 503 that never resolves is the specific
    /// defect the state machine can produce (a walk that recorded nothing), and a caller asserting on a status
    /// code would report it as the wrong thing entirely.
    /// </para>
    /// </summary>
    private static async Task<WebViewResourceResponse?> AskPlannedAsync(
        FakeInterceptor interceptor, string url, string? range = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            var response = await interceptor.AskAsync(url, range);
            if (response is null || response.StatusCode != 503) return response;

            Assert.True(DateTime.UtcNow < deadline,
                $"the route was still answering 503 after 20s for {url} — a plan that records no answer is a "
                + "permanent 503, which is the failure this route's fall-through rule exists to prevent");
            await Task.Delay(10);
        }
    }

    // ── the app's half ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The containment boundary and the resolver, as an adopting app would state them once for every delivery
    /// path (D71).
    /// </summary>
    /// <param name="roots">
    /// Overridden only by the fail-closed case: an EMPTY list must serve nothing at all.
    /// </param>
    private MediaAccessOptions Access(IReadOnlyList<string>? roots = null) => new()
    {
        Resolve = uri =>
        {
            if (!uri.AbsolutePath.StartsWith("/media", StringComparison.Ordinal)) return null;
            var named = Uri.UnescapeDataString(uri.Query.TrimStart('?'));
            // ⚠ A ROOTED query is returned as-is, which is how the outside-the-roots case is expressed here.
            // Written out rather than leaning on `Path.Combine` discarding its first argument for a rooted
            // second — that behaviour is one of the traps this kit's containment check exists for, and a test
            // that depends on it silently is a test nobody can read.
            return Path.IsPathRooted(named) ? named : Path.Combine(_sources, named);
        },
        AllowedRoots = roots ?? [_sources],
        // Nothing is written by this path — a computed remux has no artifact — but the object is shared with
        // the routes that DO write, so it still carries one.
        CacheRoot = Path.Combine(_root, "cache"),
        Log = AppCallback.Logger(line => { lock (_log) _log.Add(line); }),
    };

    private string[] Log()
    {
        lock (_log) return [.. _log];
    }

    /// <summary>
    /// How many times the route has PLANNED <paramref name="name"/> — the host log is the only observable for
    /// it, and the route emits exactly one line per source per plan. A second line therefore means an eviction
    /// (or a cache that was never consulted) rather than anything about the response.
    /// </summary>
    private int Planned(string name) =>
        Log().Count(line => line.Contains($"planned {name}", StringComparison.Ordinal));

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An ordinary plannable film: an H.264 picture and an AAC soundtrack interleaved across
    /// <paramref name="videoFrames"/> clusters, every second frame a keyframe (so <c>stss</c> is written).
    /// <para>
    /// The two parameters exist so two films can differ in OUTPUT LENGTH, which is what the cache-invalidation
    /// case needs: a stale layout is only detectable when the right answer is a different number.
    /// </para>
    /// </summary>
    private static byte[] Film(int videoFrames = 6, int frameSize = 300)
    {
        var clusters = new List<byte[]>();
        for (var i = 0; i < videoFrames; i++)
        {
            clusters.Add(Cluster((ulong)(i * 40),
                SimpleBlock(1, 0, i % 2 == 0, Frame(i, frameSize)),
                SimpleBlock(2, 0, true, Frame(100 + i, 80))));
        }

        using var mkv = Mkv(Info(videoFrames * 40),
            [VideoTrack(config: AvcConfig), AudioTrack(config: AacConfig)], [.. clusters]);
        return mkv.ToArray();
    }

    /// <summary>
    /// A film the plan must REFUSE: the picture is carriable and the soundtrack is AC-3, which MP4 cannot hold
    /// without re-encoding. The writer takes this file happily and reports what it dropped; a layout has no
    /// channel for that, so <c>Plan</c> answers null and this source belongs on whichever path is registered
    /// behind this one — the conversion route in the composition case below, or a segment route (D71).
    /// </summary>
    private static byte[] FilmNeedingReEncode()
    {
        using var mkv = Mkv(Info(1000),
            [VideoTrack(config: AvcConfig), AudioTrack(codec: "A_AC3")],
            Cluster(0, SimpleBlock(1, 0, true, Frame(0, 128)), SimpleBlock(2, 0, true, Frame(9, 64))));
        return mkv.ToArray();
    }

    private string Stage(string name, byte[] bytes)
    {
        var path = Path.Combine(_sources, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// What the remuxer really produces for <paramref name="sourceBytes"/> — the yardstick every assertion
    /// here measures the route against.
    /// <para>
    /// ⚠ It re-asserts that the plan's total equals the written length. That is <c>Mp4LayoutTests</c>' claim,
    /// not this file's, but every expectation below rests on it — so if it ever breaks, these tests should say
    /// so here rather than reporting a route bug.
    /// </para>
    /// </summary>
    private static (long Total, byte[] Bytes) Expected(byte[] sourceBytes)
    {
        using var source = new MemoryStream(sourceBytes);
        var layout = Mp4Remuxer.Plan(source, CancellationToken.None);
        Assert.NotNull(layout);

        source.Position = 0;
        using var produced = new MemoryStream();
        Assert.True(new Mp4Remuxer().Write(source, produced, conversion: null).Succeeded);
        var bytes = produced.ToArray();

        Assert.Equal(bytes.LongLength, layout!.TotalLength);
        return (layout.TotalLength, bytes);
    }

    private static byte[] Body(WebViewResourceResponse response)
    {
        using var buffer = new MemoryStream();
        response.Content.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// A terminal middleware registered AFTER the route, so "the route declined and <c>next</c> ran" is
    /// observable as a body rather than inferred from a null.
    /// </summary>
    private static readonly byte[] FellThrough = "the-next-middleware-ran"u8.ToArray();

    private static void UseTail(FakeInterceptor interceptor, Action onReached) =>
        interceptor.Use((_, _, _) =>
        {
            onReached();
            return Task.FromResult<WebViewResourceResponse?>(
                WebViewResourceResponse.Bytes(FellThrough, "text/plain"));
        });

    // ── the answer ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>THE CLAIM: a byte range of a file that has never been written, answered with the total that file
    /// WOULD have.</b> The <c>Content-Range</c> total is what a media element learns the duration and the
    /// seekable window from — on Android it is the ONLY place it can learn it, because MAUI's intercept path
    /// always emits <c>Content-Length: 0</c> (D71's measurement) — so the total being real, and the bytes
    /// being the real output's bytes, is the whole feature.
    /// </summary>
    [Fact]
    public async Task A_ranged_request_answers_206_with_the_PLANNED_total()
    {
        var film = Film();
        var expected = Expected(film);
        Stage("film.mkv", film);

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        var response = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv", "bytes=0-99");

        Assert.NotNull(response);
        Assert.Equal(206, response!.StatusCode);
        Assert.Equal($"bytes 0-99/{expected.Total}", response.Headers["Content-Range"]);
        Assert.Equal("100", response.Headers["Content-Length"]);
        // Without this a player will not even ATTEMPT a seek, which is indistinguishable from broken seeking.
        Assert.Equal("bytes", response.Headers["Accept-Ranges"]);
        // The BYTES, not just their count: a length check passes for a body read from the wrong offset.
        Assert.Equal(expected.Bytes[..100], Body(response));
    }

    /// <summary>
    /// The end of the file, which is the range shape that breaks independently: an <c>endInclusive</c> the
    /// route translated wrongly by one would be rejected by <see cref="Mp4LayoutReader.CopyRange"/>'s own
    /// bounds check and come back as a 404 rather than as slightly wrong bytes.
    /// </summary>
    [Fact]
    public async Task A_range_at_the_very_END_of_the_planned_output_is_exact()
    {
        var film = Film();
        var expected = Expected(film);
        Stage("film.mkv", film);

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        var from = expected.Total - 100;
        var response = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv", $"bytes={from}-{expected.Total - 1}");

        Assert.Equal(206, response!.StatusCode);
        Assert.Equal($"bytes {from}-{expected.Total - 1}/{expected.Total}", response.Headers["Content-Range"]);
        Assert.Equal(expected.Bytes[^100..], Body(response));
    }

    /// <summary>
    /// No <c>Range</c> at all: the whole computed file, with the planned total as its <c>Content-Length</c> —
    /// byte-identical to what the remuxer writes, which is the strongest form of "this file exists without
    /// having been produced".
    /// </summary>
    [Fact]
    public async Task A_request_with_no_range_answers_200_with_the_WHOLE_computed_file()
    {
        var film = Film();
        var expected = Expected(film);
        Stage("film.mkv", film);

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        var response = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv");

        Assert.Equal(200, response!.StatusCode);
        Assert.Equal(expected.Total.ToString(System.Globalization.CultureInfo.InvariantCulture),
            response.Headers["Content-Length"]);
        Assert.Equal("bytes", response.Headers["Accept-Ranges"]);
        Assert.Equal(expected.Bytes, Body(response));
    }

    /// <summary>
    /// 🔴 <b>THE ANDROID RULE, ON A COMPUTED BODY (D44).</b> Android's webview applies the requested range
    /// START to whatever body it is handed and ignores the end, so a handler must produce the resource from
    /// offset 0 and let the platform skip; the headers then describe <c>from</c>→EOF, which is what the client
    /// really receives. Slicing anyway applies the offset TWICE — <c>bytes=100-199</c> would return bytes
    /// 200-299 — and a player asking for a file's tail gets an empty body and retries forever.
    /// <para>
    /// ⚠ This is the case a desktop-only or simulator-only run cannot see: on the sliced platforms the
    /// wrong choice is invisible, and on Android the right choice looks wrong in a unit test that asserts the
    /// requested window came back.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Unsliced_delivery_hands_the_WHOLE_planned_output_with_headers_describing_from_to_EOF()
    {
        var film = Film();
        var expected = Expected(film);
        Stage("film.mkv", film);

        var interceptor = new FakeInterceptor(WebViewRangeDelivery.Unsliced);
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        var response = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv", "bytes=100-199");

        Assert.Equal(206, response!.StatusCode);
        Assert.Equal($"bytes 100-{expected.Total - 1}/{expected.Total}", response.Headers["Content-Range"]);
        // What ARRIVES is total-100 bytes, because the platform drops the first 100 of the body below.
        Assert.Equal((expected.Total - 100).ToString(System.Globalization.CultureInfo.InvariantCulture),
            response.Headers["Content-Length"]);
        // …and the body itself starts at offset ZERO and runs to the end.
        Assert.Equal(expected.Bytes, Body(response));
    }

    /// <summary>
    /// The two deliveries must genuinely DIFFER for a computed body exactly as they do for a file. This earns
    /// its own case because unifying them reads as a tidy-up and the field symptom is subtle: every faststart
    /// file keeps playing.
    /// </summary>
    [Fact]
    public async Task The_two_deliveries_are_not_the_same_response()
    {
        var film = Film();
        Stage("film.mkv", film);

        var sliced = new FakeInterceptor(WebViewRangeDelivery.Sliced);
        using var slicedRoute = sliced.UseComputedRemux(_scheduler, Access());
        var unsliced = new FakeInterceptor(WebViewRangeDelivery.Unsliced);
        using var unslicedRoute = unsliced.UseComputedRemux(_scheduler, Access());

        var one = await AskPlannedAsync(sliced, "https://0.0.0.1/media?film.mkv", "bytes=100-199");
        var two = await AskPlannedAsync(unsliced, "https://0.0.0.1/media?film.mkv", "bytes=100-199");

        Assert.NotEqual(Body(one!).Length, Body(two!).Length);
        Assert.NotEqual(one!.Headers["Content-Range"], two!.Headers["Content-Range"]);
    }

    /// <summary>
    /// A range starting past the planned end is unsatisfiable, and the 416 must carry <c>bytes */total</c> or
    /// a player retries the same bad range forever.
    /// </summary>
    [Fact]
    public async Task A_range_past_the_planned_end_is_416_and_reports_the_planned_total()
    {
        var film = Film();
        var expected = Expected(film);
        Stage("film.mkv", film);

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        // ⚠ Through the retry helper, because a 416 needs a PLAN first: the total it reports is the planned
        // one, so an unsatisfiable range asked before the walk lands is a 503 like any other first request.
        var response = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv", $"bytes={expected.Total + 10}-");

        Assert.Equal(416, response!.StatusCode);
        Assert.Equal($"bytes */{expected.Total}", response.Headers["Content-Range"]);
    }

    /// <summary>
    /// ⚠ The content type is the OUTPUT's container, never the source file's — and the trap is live: the
    /// source is <c>.mkv</c>, so deriving the type from the path would answer <c>video/x-matroska</c> for a
    /// body that is MP4. A media element told the wrong container refuses before it has tried a byte.
    /// </summary>
    [Fact]
    public async Task The_content_type_describes_the_OUTPUT_container_not_the_source_file()
    {
        Stage("film.mkv", Film());

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        var response = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv", "bytes=0-99");

        Assert.Equal("video/mp4", response!.Headers["Content-Type"]);
        // The hazard, asserted rather than described: the path-derived answer really is something else.
        Assert.NotEqual("video/mp4", WebViewContentTypes.FromPath(Path.Combine(_sources, "film.mkv")));
    }

    // ── the D71 split: what this route does NOT serve ────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>THE D71 SPLIT, EXPRESSED AS CODE.</b> A source needing a re-encode cannot be planned, and the
    /// route must DECLINE it — return null so the pipeline carries on — rather than answer 404. A 404 here
    /// makes every <c>Transcode</c> source permanently unplayable, and it looks like a working route right up
    /// until someone opens a film with AC-3 sound.
    /// </summary>
    [Fact]
    public async Task A_source_that_cannot_be_planned_falls_through_rather_than_failing()
    {
        Stage("ac3.mkv", FilmNeedingReEncode());

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());
        var nextRan = false;
        UseTail(interceptor, () => nextRan = true);

        var response = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?ac3.mkv", "bytes=0-99");

        Assert.True(nextRan, "an unplannable source never reached the rest of the pipeline");
        Assert.Equal(FellThrough, Body(response!));
    }

    /// <summary>
    /// 🔴 <b>THE REGISTRATION ORDER, PROVEN AS A COMPOSITION rather than described in a doc.</b> The computed
    /// route goes FIRST and the conversion route second: a plannable film is then served over 206s as soon as
    /// its plan lands, and only what cannot be planned reaches the converter.
    /// <para>
    /// ⚠ Reverse the two and this test fails on its first half — the conversion route answers every request
    /// its own <c>Resolve</c> matches, so a plannable film would 503 through a whole transcode and the computed
    /// path would be dead code that still passed every test above.
    /// </para>
    /// <para>
    /// ⚠ <b>BOTH routes now 503 while they work, which is why the AC-3 arm cannot use the retry helper.</b> For
    /// that film the computed route declines and the conversion route answers 503 for the whole conversion, so
    /// "ask until it stops 503ing" would never terminate. The observable that the request reached the route
    /// behind is the CONVERTER being asked, which is what it always was.
    /// </para>
    /// <para>
    /// ⚠ ONE scheduler for both routes, as an adopter would wire it — and it registers <c>PathClaims.Scope</c>
    /// because the CONVERSION route claims a path. The computed route needs no scope (see
    /// <see cref="_scheduler"/>), which is the arrangement every other case here proves.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Registered_BEFORE_the_conversion_route_it_leaves_that_route_only_what_it_cannot_plan()
    {
        var film = Film();
        var expected = Expected(film);
        Stage("film.mkv", film);
        Stage("ac3.mkv", FilmNeedingReEncode());

        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            Scopes = [Shenora.Engine.Files.PathClaims.Scope],
        });
        var converting = new TaskCompletionSource<string>();
        var access = Access();
        var interceptor = new FakeInterceptor();

        // The order under test. Both routes read the SAME MediaAccessOptions — one containment boundary,
        // stated once (D71) — which is exactly the composition an adopting app writes.
        using var computed = interceptor.UseComputedRemux(scheduler, access);
        using var conversion = interceptor.UseMediaConversion(scheduler, new EventBus(), new MediaConversionOptions
        {
            Access = access,
            Convert = async (request, ct) =>
            {
                converting.TrySetResult(request.SourcePath);
                await File.WriteAllTextAsync(request.DestinationPath, "converted", ct);
            },
        });

        // The plannable film: answered by the computed route, and the converter is never asked.
        var served = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv", "bytes=0-99");
        Assert.Equal(206, served!.StatusCode);
        Assert.Equal($"bytes 0-99/{expected.Total}", served.Headers["Content-Range"]);
        Assert.False(converting.Task.IsCompleted, "a plannable film was sent to the converter");

        // The AC-3 film: declined by the computed route, picked up by the conversion route behind it. Retried
        // by hand — the computed route 503s while it walks, then declines, and then the CONVERSION route 503s
        // for the whole conversion, so 503 is the answer from beginning to end and only the converter can say
        // which route produced it.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!converting.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            var deferred = await interceptor.AskAsync("https://0.0.0.1/media?ac3.mkv", "bytes=0-99");
            Assert.Equal(503, deferred!.StatusCode);
            await Task.Delay(10);
        }
        Assert.EndsWith("ac3.mkv", await converting.Task.WaitAsync(TimeSpan.FromSeconds(10)), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ A REMOTE source is left alone, and this is the case the "authorise then refuse" shape gets wrong. The
    /// conversion route accepts an <c>http</c>/<c>https</c> source (its engine reads the url itself, behind an
    /// SSRF policy); a plan needs a seekable local file, so this route cannot serve one. Answering 404 would
    /// make every remote conversion unreachable the moment this route is registered in front of it — the
    /// registration order that is otherwise correct.
    /// </summary>
    [Fact]
    public async Task A_remote_source_is_DECLINED_so_the_conversion_route_can_still_have_it()
    {
        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, new MediaAccessOptions
        {
            Resolve = _ => "https://cdn.example/clip.mkv",
            AllowedRoots = [_sources],
            CacheRoot = Path.Combine(_root, "cache"),
            Log = AppCallback.Logger(line => { lock (_log) _log.Add(line); }),
        });
        var nextRan = false;
        UseTail(interceptor, () => nextRan = true);

        var response = await interceptor.AskAsync("https://0.0.0.1/media?whatever");

        Assert.True(nextRan, "a remote source was answered here instead of being left to the conversion route");
        Assert.Equal(FellThrough, Body(response!));
    }

    [Fact]
    public async Task A_request_this_route_does_not_own_falls_through_to_the_rest_of_the_pipeline()
    {
        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        Assert.Null(await interceptor.AskAsync("https://0.0.0.1/index.html"));
    }

    // ── containment ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 Containment, on a path the PAGE supplies, and it runs BEFORE any work: an unauthorised path must
    /// never cost a metadata walk of the file it names.
    /// <para>
    /// The control is the load-bearing half — <b>the same bytes</b> are staged inside the roots and served as
    /// a 206, so the refusal is provably about the LOCATION rather than about the file being unplannable.
    /// Without it this test passes for a fixture that could never have been planned in the first place.
    /// </para>
    /// <para>
    /// ⚠ Refused with the SAME 404 as a missing file, so nothing can probe for existence by comparing
    /// responses. The host log is where the two are distinguishable, which is why the assertion on it is here:
    /// it is the only observable that says the walk never happened.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_path_outside_the_allowed_roots_is_refused_as_a_plain_404()
    {
        var film = Film();
        var outside = Path.Combine(_elsewhere, "unauthorised.mkv");
        File.WriteAllBytes(outside, film);
        Stage("film.mkv", film);

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        var refused = await interceptor.AskAsync(
            "https://0.0.0.1/media?" + Uri.EscapeDataString(outside.Replace('\\', '/')));

        Assert.Equal(404, refused!.StatusCode);
        // "Indistinguishable from missing" is the actual claim, so the BODY is asserted too — a refusal with a
        // different body is a probe: ask for a path and learn from the reply whether it exists.
        Assert.Equal("Not Found", System.Text.Encoding.UTF8.GetString(Body(refused)));
        Assert.DoesNotContain(_elsewhere, System.Text.Encoding.UTF8.GetString(Body(
            (await interceptor.AskAsync("https://0.0.0.1/media?" + Uri.EscapeDataString(outside.Replace('\\', '/'))))!)),
            StringComparison.OrdinalIgnoreCase);

        // The control: the identical bytes, inside the roots, really are servable — so the refusal is about
        // the LOCATION and not about a fixture that could never have been planned anyway.
        var served = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv");
        Assert.Equal(200, served!.StatusCode);

        // ⚠ What the log proves, precisely: NO PLAN ran for the unauthorised file. The only lines naming a file
        // come from the planning step, so a "planned unauthorised.mkv" line would appear if containment ran
        // after the walk — which is what the reordering sabotage confirms. It does NOT prove the file was never
        // STAT-ed; there is no observable for that, and the cost the ordering exists to avoid is the walk.
        Assert.Contains(Log(), line => line.Contains("outside the allowed roots", StringComparison.Ordinal));
        Assert.DoesNotContain(Log(), line => line.Contains("unauthorised.mkv", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, Planned("film.mkv"));
    }

    /// <summary>Traversal is refused before the filesystem is consulted, the same as for any other route.</summary>
    [Fact]
    public async Task A_traversal_out_of_the_roots_is_refused()
    {
        File.WriteAllBytes(Path.Combine(_elsewhere, "film.mkv"), Film());

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        var response = await interceptor.AskAsync(
            "https://0.0.0.1/media?" + Uri.EscapeDataString("../elsewhere/film.mkv"));

        Assert.Equal(404, response!.StatusCode);
    }

    /// <summary>
    /// Fail CLOSED: with no allowed roots configured, nothing is servable — because the alternative default is
    /// the whole filesystem, and a route wired up before it is configured must refuse rather than expose one.
    /// </summary>
    [Fact]
    public async Task With_no_allowed_roots_a_real_film_is_still_refused()
    {
        Stage("film.mkv", Film());

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access(roots: []));

        Assert.Equal(404, (await interceptor.AskAsync("https://0.0.0.1/media?film.mkv"))!.StatusCode);
    }

    /// <summary>A source that is not there is a plain 404 rather than an exception out of a webview callback.</summary>
    [Fact]
    public async Task A_missing_source_is_a_plain_404()
    {
        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        var response = await interceptor.AskAsync("https://0.0.0.1/media?absent.mkv");

        Assert.Equal(404, response!.StatusCode);
        Assert.Equal("Not Found", System.Text.Encoding.UTF8.GetString(Body(response)));
    }

    // ── the layout cache, keyed on source IDENTITY ───────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>REPLACING THE SOURCE INVALIDATES ITS CACHED LAYOUT — the one defect this route is uniquely able
    /// to cause.</b> A layout carries no identity of its own (<c>Plan</c> takes a <c>Stream</c>), so applying
    /// film A's byte map to film B's bytes resolves every span to a valid offset in the wrong file and serves
    /// a garbage picture with NO error anywhere. The key is identity+length+mtime, exactly as
    /// <c>SegmentStream</c> and <c>MediaConversion</c> key theirs.
    /// <para>
    /// The two films are deliberately DIFFERENT LENGTHS: a stale layout is only detectable when the right
    /// answer is a different number.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Replacing_the_SOURCE_invalidates_the_cached_layout()
    {
        var first = Film(videoFrames: 6, frameSize: 300);
        var second = Film(videoFrames: 10, frameSize: 220);
        var expectedFirst = Expected(first);
        var expectedSecond = Expected(second);
        Assert.NotEqual(expectedFirst.Total, expectedSecond.Total);   // or this fixture proves nothing

        var path = Stage("film.mkv", first);
        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        var before = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv");
        // Drained here rather than later: the body reads LAZILY off an open handle on `path`, and the rewrite
        // below needs that handle closed — which happens when something reads it to EOF.
        Assert.Equal(expectedFirst.Bytes, Body(before!));

        // Same PATH, different film. A path-only key would serve the first film's byte map over these bytes.
        File.WriteAllBytes(path, second);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var after = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv");
        Assert.Equal(expectedSecond.Total.ToString(System.Globalization.CultureInfo.InvariantCulture),
            after!.Headers["Content-Length"]);
        Assert.Equal(expectedSecond.Bytes, Body(after));
    }

    /// <summary>
    /// 🔴 <b>AND THE OTHER DIRECTION: the layout is planned ONCE per identity, not once per request.</b> That
    /// is not an optimisation — planning peaks at 110–150 MB for a two-hour film and walks its whole metadata,
    /// so a plan per range request is unusable on a phone, and iOS reads a container in HUNDREDS of tiny
    /// ranges (D71).
    /// <para>
    /// ⚠ The observation is indirect on purpose, because there is no seam to count calls through: the file's
    /// CONTENT is replaced with bytes no plan could ever accept, while its length and mtime are restored so
    /// the cache key is unchanged. A route that re-planned would find garbage, answer null and fall through;
    /// one that reuses its cached layout still answers 206 with the same total.
    /// </para>
    /// <para>
    /// ⚠ The bytes served in that state are meaningless, and that is the whole hazard the identity key exists
    /// for — the case above is what makes it detectable in the real world, where a replacement changes the
    /// length or the mtime.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_layout_is_planned_ONCE_per_source_identity()
    {
        var film = Film();
        var expected = Expected(film);
        var path = Stage("film.mkv", film);
        var stamp = File.GetLastWriteTimeUtc(path);

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());
        var nextRan = false;
        UseTail(interceptor, () => nextRan = true);

        var first = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv", "bytes=0-99");
        Assert.Equal(206, first!.StatusCode);
        // 🔴 The body is now read LAZILY straight off an open `FileStream` on `path` (see `Produce`), and that
        // stream closes itself only once something reads it to EOF — so it must be drained here, BEFORE the
        // rewrite below, or the rewrite fails with a sharing violation against this test's own still-open
        // handle rather than proving anything about the cache.
        Body(first);

        // Same length, same mtime, unplannable content — so the KEY is identical and only a cache hit can
        // still answer 206.
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0xEE, film.Length).ToArray());
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Null(Mp4Remuxer.Plan(new MemoryStream(File.ReadAllBytes(path)), CancellationToken.None));

        // ⚠ Asked ONCE, not through the retry helper, and that is the sharper assertion: a cached layout is
        // answered from the dictionary, so a 503 here would ALSO be a re-plan.
        var again = await interceptor.AskAsync("https://0.0.0.1/media?film.mkv", "bytes=0-99");

        Assert.Equal(206, again!.StatusCode);
        Assert.Equal($"bytes 0-99/{expected.Total}", again.Headers["Content-Range"]);
        Assert.False(nextRan, "the source was re-planned on the second request instead of being served from the cached layout");
    }

    /// <summary>
    /// 🔴 <b>A REFUSAL IS CACHED TOO, and it matters more than it looks.</b> The route behind this one answers
    /// <c>503 Retry-After: 1</c> while a conversion runs, so a page RETRIES about once a second — and every one
    /// of those retries reaches here first. Re-planning an unplannable multi-gigabyte film on each of them is a
    /// full metadata walk per second, which is worse than the cost the cache was added to avoid.
    /// <para>
    /// ⚠ The instrument is a LOCK rather than a swapped file: the source is held open with
    /// <see cref="FileShare.None"/>, so any attempt to plan it again cannot even open it. A route that
    /// re-planned would answer 404; one that remembers its refusal declines without touching the file at all.
    /// The lock is proven to bite before it is relied on.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_source_that_cannot_be_planned_is_only_planned_ONCE()
    {
        var path = Stage("ac3.mkv", FilmNeedingReEncode());

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());
        var reached = 0;
        UseTail(interceptor, () => reached++);

        Assert.Equal(FellThrough, Body((await AskPlannedAsync(interceptor, "https://0.0.0.1/media?ac3.mkv"))!));

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // The instrument, verified: while this handle is held, nothing else can read the file.
            Assert.ThrowsAny<IOException>(() => File.OpenRead(path).Dispose());

            // ⚠ Asked ONCE. A remembered refusal is answered from the dictionary, so a 503 here would mean a
            // second walk was submitted — which is precisely what this test forbids.
            var again = await interceptor.AskAsync("https://0.0.0.1/media?ac3.mkv");

            Assert.Equal(FellThrough, Body(again!));
        }

        Assert.Equal(2, reached);
    }

    /// <summary>
    /// 🔴 <b>THE INTENTIONAL 404 → DECLINE CHANGE, GIVEN ITS OWN TEST.</b> A source that is contained, present,
    /// and NOT YET PLANNED, but cannot be OPENED, used to be caught synchronously on the request path and
    /// answered as a plain 404 — back when planning opened the file inline, before the walk moved into a
    /// mission. Now <see cref="ComputedRemuxRoute"/> never touches the file for a source it has not yet
    /// claimed (see <see cref="Claim"/>): the open happens inside <see cref="PlanSource"/>, so an unopenable
    /// file is indistinguishable from any other planning failure — recorded as <see cref="PlanState.Failed"/>,
    /// and this request DECLINES (falls through) rather than 404ing. Every other decline door on this route has
    /// a test (unplannable content, a remote source, a walk that throws mid-plan); this was the one that did
    /// not, though the behaviour has been live and correct since the walk moved off-thread.
    /// <para>
    /// ⚠ The instrument is the SAME lock <see cref="A_source_that_cannot_be_planned_is_only_planned_ONCE"/>
    /// uses to prove a remembered refusal never reopens the file — held here BEFORE the first request instead
    /// of after one, so this is the FIRST walk failing to even OPEN the source, not a second walk against an
    /// already-declined one.
    /// </para>
    /// <para>
    /// The other half, same as any planning failure: NOT remembered. Once the lock clears, the very next
    /// request plans the source normally — an unopenable file says nothing about whether it is PLANNABLE.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_source_that_cannot_be_OPENED_and_was_never_planned_declines_rather_than_404ing()
    {
        var path = Stage("film.mkv", Film());

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());
        var reached = 0;
        UseTail(interceptor, () => reached++);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // The instrument, verified: while this handle is held, nothing else can even OPEN the file — not
            // even for reading, which is what distinguishes this from the plain sharing violation `Answer`'s
            // own post-plan open already tolerates (`FileShare.Read | FileShare.Delete` on both sides).
            Assert.ThrowsAny<IOException>(() => File.OpenRead(path).Dispose());

            // Through the retry helper: the first request claims the walk (503), the mission's own open then
            // fails against this lock, and the retry that follows is the DECLINE — never a 404.
            var response = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv", "bytes=0-99");

            Assert.Equal(FellThrough, Body(response!));
            Assert.Equal(1, reached);
            Assert.Contains(Log(), line => line.Contains("could not plan film.mkv", StringComparison.Ordinal));
            Assert.Contains(Log(), line => line.Contains("plan FAILED", StringComparison.Ordinal));
        }

        // The lock is gone now, and the failure was never remembered — the source plans normally next time.
        var recovered = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv", "bytes=0-99");
        Assert.Equal(206, recovered!.StatusCode);
    }

    /// <summary>
    /// 🔴 <b>THE CACHE IS BOUNDED, and this is the test that can fail if the bound is removed.</b> An
    /// unbounded dictionary keyed on identity keeps every distinct version of every file a page ever names —
    /// ~13 MB of spans per two-hour film, and a REPLACED file adds a key rather than reusing one, so a single
    /// path can contribute several. On a phone that is the process being killed later, for a reason nothing
    /// points at.
    /// <para>
    /// The observable is the host log: a plan names the file it walked, exactly once per identity, so a second
    /// line for the same film is an eviction followed by a re-plan.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_layout_cache_is_BOUNDED_and_evicts_rather_than_growing()
    {
        // Five distinct sources against a cap of four, each a different length so nothing is shared.
        for (var i = 0; i < 5; i++) Stage($"film{i}.mkv", Film(videoFrames: 4 + i));

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(206, (await AskPlannedAsync(interceptor, $"https://0.0.0.1/media?film{i}.mkv", "bytes=0-49"))!.StatusCode);
        }

        // The four most recent are still cached — asking again plans nothing new.
        for (var i = 1; i < 5; i++)
        {
            Assert.Equal(206, (await AskPlannedAsync(interceptor, $"https://0.0.0.1/media?film{i}.mkv", "bytes=0-49"))!.StatusCode);
            Assert.Equal(1, Planned($"film{i}.mkv"));
        }

        // The coldest was evicted, so IT plans a second time. Which is the assertion that fails if the cache
        // is unbounded (nothing is ever evicted, so this stays at 1).
        Assert.Equal(206, (await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film0.mkv", "bytes=0-49"))!.StatusCode);
        Assert.Equal(2, Planned("film0.mkv"));
    }

    /// <summary>
    /// 🔴 <b>A WALK THAT FAILS AFTER ITS IN-FLIGHT MARKER WAS EVICTED MUST NOT POISON THE KEY — the whole-branch
    /// review's first residual.</b> <see cref="Fail"/> used to store <see cref="PlanState.Failed"/> whenever the
    /// cache did not hold an entry it recognised as still <see cref="PlanState.Planning"/>, which is also true
    /// when the entry is simply GONE. Four films planned at once fill the four-deep cache with nothing but
    /// in-flight markers (<see cref="Store"/>'s own remarks), so a fifth evicts one of them while its walk is
    /// still running; when that walk then fails, the old code invented a <c>Failed</c> answer for a key nobody
    /// was waiting on any more. The next, unrelated request for the SAME source would consume that phantom entry
    /// and decline — starting a whole transcode on the route behind, for a source the request after it would
    /// have planned in seconds (<see cref="SubmitGuardedAsync"/>'s own remarks state that cost).
    /// <para>
    /// ⚠ <b>The synchronisation is the scheduler's own admission order, not a sleep.</b> A dedicated
    /// one-permit scheduler makes film0's walk the only one RUNNING while four more queue behind it; releasing
    /// film0's gate lets it fail and free the permit, and film1's walk can only START once that whole
    /// <c>Run</c> — throw, log, <see cref="Fail"/>, return — has finished, because nothing here awaits between
    /// them. So waiting for film1 to start is a deterministic proxy for "film0's <c>Fail</c> call has already
    /// returned", with no race against this test's own next request for film0.
    /// </para>
    /// <para>
    /// ⚠ <b>Sabotage-verified.</b> Reverting the fix (back to <c>TryGetValue(...) &amp;&amp; entry.State !=
    /// Planning</c>) makes this test fail on both assertions: the request for film0 comes back <c>200</c> (the
    /// <c>FellThrough</c> tail body) instead of <c>503</c>, and <c>reached</c> is <c>1</c> instead of <c>0</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_walk_that_fails_after_its_inflight_marker_was_evicted_does_not_poison_the_key()
    {
        for (var i = 0; i < 5; i++) Stage($"film{i}.mkv", Film(videoFrames: 4 + i));

        // ONE permit, so admission is strictly FIFO and observable — the same instrument
        // `A_walk_outranks_a_QUEUED_conversion_on_a_shared_scheduler` uses for the same reason.
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions { GlobalLaneCapacity = 1 });

        using var film0Gate = new ManualResetEventSlim(initialState: false);
        using var film1Started = new ManualResetEventSlim(initialState: false);

        var interceptor = new FakeInterceptor();
        using var route = ComputedRemuxRoute.Use(interceptor, scheduler, Access(), (source, token) =>
        {
            if (source is FileStream file)
            {
                if (file.Name.EndsWith("film0.mkv", StringComparison.OrdinalIgnoreCase))
                {
                    // Bounded, so a broken run ends rather than hanging the whole suite.
                    film0Gate.Wait(TimeSpan.FromSeconds(20));
                    // What a real failure here IS (see `A_planning_failure_falls_through_rather_than_503ing_forever`):
                    // `Plan` catches everything else itself, so an OOM or a source that could not be opened are
                    // the realistic causes. This one throws AFTER the marker below has already been evicted.
                    throw new IOException("simulated: the source could not be read for this walk");
                }
                if (file.Name.EndsWith("film1.mkv", StringComparison.OrdinalIgnoreCase)) film1Started.Set();
            }
            return Mp4Remuxer.Plan(source, token);
        });
        var reached = 0;
        UseTail(interceptor, () => reached++);

        try
        {
            // Five claims against a cap of four: the fifth evicts film0's in-flight marker while film0's walk
            // is still parked mid-plan — exactly the "four films planned at once" shape the residual names.
            for (var i = 0; i < 5; i++)
            {
                Assert.Equal(503,
                    (await interceptor.AskAsync($"https://0.0.0.1/media?film{i}.mkv", "bytes=0-49"))!.StatusCode);
            }

            Assert.Equal(1, scheduler.RunningCount);   // film0's walk, still parked on the one permit
            Assert.Equal(4, scheduler.PendingCount);   // film1..film4, queued behind it

            // Release the stale walk: it fails NOW, once its cache entry is already gone.
            film0Gate.Set();

            Assert.True(film1Started.Wait(TimeSpan.FromSeconds(20)),
                "film0's walk never freed the one permit — film1 never started");

            // THE CLAIM: film0 gets a FRESH claim (a new walk, 503) rather than a decline through a Failed
            // entry the evicted walk had no business leaving behind.
            var again = await interceptor.AskAsync("https://0.0.0.1/media?film0.mkv", "bytes=0-49");
            Assert.Equal(503, again!.StatusCode);
            Assert.Equal(0, reached);
        }
        finally
        {
            // Always release, even when an assertion above failed — a parked walk must not outlive the test.
            film0Gate.Set();
        }
    }

    /// <summary>
    /// 🔴 <b>Eviction is least-recently-USED, not least-recently-ADDED — and the difference is the film the
    /// user is watching.</b> A playing film is touched by every one of its range requests but written to the
    /// cache only once, so an insertion-ordered eviction drops exactly the entry earning its keep and pays a
    /// full re-plan, on the request path, mid-playback. <c>SegmentStream</c>'s sweep records the same lesson
    /// for its own cache.
    /// </summary>
    [Fact]
    public async Task Eviction_is_least_recently_USED_not_least_recently_added()
    {
        for (var i = 0; i < 5; i++) Stage($"film{i}.mkv", Film(videoFrames: 4 + i));

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        // ⚠ Every ask goes through the retry loop, and it has to: a walk still in flight occupies an entry, so
        // asking for the next film before the previous one's plan has landed would measure a cache of
        // PLACEHOLDERS rather than of layouts.
        for (var i = 0; i < 4; i++) await AskPlannedAsync(interceptor, $"https://0.0.0.1/media?film{i}.mkv", "bytes=0-49");

        // Touch the OLDEST entry — a cache hit, which writes nothing — then overflow the cap.
        await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film0.mkv", "bytes=0-49");
        Assert.Equal(1, Planned("film0.mkv"));
        await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film4.mkv", "bytes=0-49");

        // film0 was used most recently of the four, so film1 is the victim.
        await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film0.mkv", "bytes=0-49");
        Assert.Equal(1, Planned("film0.mkv"));
        await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film1.mkv", "bytes=0-49");
        Assert.Equal(2, Planned("film1.mkv"));
    }

    /// <summary>
    /// 🔴 <b>A CACHE STAMPEDE IS PREVENTED: many requests arriving for one source at once plan it ONCE.</b>
    /// Planning peaks on the order of 110–150 MB for a two-hour film, so two concurrent walks of one film are
    /// not "slower" on a phone, they are the process being killed — and iOS opens a container with hundreds of
    /// range requests, which is exactly the arrival pattern that would race.
    /// <para>
    /// ⚠ The assertion holds whether or not the threads actually overlap (without overlap the first claims the
    /// walk and the rest see it in flight), so it cannot fail spuriously; it discriminates when they DO
    /// overlap, which is also when the bug it guards would appear.
    /// </para>
    /// <para>
    /// 🔴 <b>And this is now the ONLY coverage of the mechanism that prevents it</b>, which changed on
    /// 2026-08-13: not a planning lock, and deliberately not the mission scheduler's own
    /// <c>MissionKey</c> dedup (which would report <c>Deduplicated</c> for a body that never ran and could
    /// strand a source at 503 — see <c>StartPlanning</c>), but the in-flight entry the route writes ATOMICALLY
    /// while deciding. Drop that write and every one of the 16 submits its own walk.
    /// </para>
    /// <para>
    /// ⚠ <b>What the burst is allowed to answer changed with the walk moving off-thread</b>: any of the 16 may
    /// be a <c>503</c>, because a plan takes as long as it takes and 503 IS the answer while it does. What may
    /// NOT happen is a second walk — which is what <c>Planned</c> counts, and it is counted after the retry
    /// loop has drained the burst so a still-running walk cannot make the count look right by being
    /// unfinished.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Concurrent_requests_for_one_source_plan_it_ONCE()
    {
        var film = Film();
        var expected = Expected(film);
        Stage("film.mkv", film);

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        // Task.Run, not a bare loop: the middleware answers synchronously, so a sequential `Select` over
        // AskAsync would never have two requests in flight and the race could not occur at all.
        var responses = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => interceptor.AskAsync("https://0.0.0.1/media?film.mkv", "bytes=0-99"))));

        Assert.All(responses, r =>
        {
            Assert.NotNull(r);
            // Either the plan had landed for this one, or it had not — and nothing in between. A fall-through
            // (null) or a 404 would mean the burst broke the state machine.
            if (r!.StatusCode == 503) return;
            Assert.Equal(206, r.StatusCode);
            Assert.Equal($"bytes 0-99/{expected.Total}", r.Headers["Content-Range"]);
        });

        var served = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv", "bytes=0-99");
        Assert.Equal(206, served!.StatusCode);
        Assert.Equal(1, Planned("film.mkv"));
    }

    /// <summary>
    /// One decline — UNPLANNABLE — and the door it must leave by: the route registered behind this one, plus a
    /// host log line naming the reason. The remote decline and a FAILED walk have tests of their own.
    /// </summary>
    [Fact]
    public async Task A_declined_source_reaches_the_route_behind_this_one_whatever_the_reason()
    {
        Stage("ac3.mkv", FilmNeedingReEncode());

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());
        var reached = 0;
        UseTail(interceptor, () => reached++);

        Assert.Equal(FellThrough, Body((await AskPlannedAsync(interceptor, "https://0.0.0.1/media?ac3.mkv", "bytes=0-99"))!));
        Assert.Equal(1, reached);
        Assert.Contains(Log(), line => line.Contains("cannot plan ac3.mkv", StringComparison.Ordinal));
    }

    // ── the WALK is off the request path, and the states it can leave behind ──────────────────────────
    //
    // ⚠ The five tests below drive the route through its INTERNAL `Use` overload with a stub planner
    // (`InternalsVisibleTo("Shenora.Tests")`, the same arrangement `ShenoraEnvironment` and `ShenoraPaths`
    // already use for their test seams). Not for convenience: none of the five is reachable through a fixture
    // — three need a walk held STILL at a moment the test chooses, one needs a walk that THROWS, and one needs
    // a layout that does not describe its source. They were "documented invariants" until 2026-08-12, which
    // this repo's own scoring says is the arrangement that fails.
    //
    // 🔴 The ceiling this section used to open with — `MaxBufferedOutputBytes`, and its "a source planning past
    // 64 MiB is declined" test — went on 2026-08-13, together with the constant. ⚠ NOT because the walk moved:
    // that number was checked against the layout's TotalLength, which the walk produces, so it never bounded a
    // walk at all (a big film paid the whole thing and was declined after). It was a BUFFERED body's memory
    // budget, and the lazy body had already left it justifying nothing. The three cases at the top of this
    // section cover the walk's move, which is a separate change with a separate reason — never block the
    // platform's resource thread, at any size.

    /// <summary>
    /// 🔴 <b>THE FIRST REQUEST FOR AN UNPLANNED SOURCE MUST NOT WALK THE FILE ON THE CALLER'S THREAD.</b> Both
    /// mobile shells resolve a webview resource SYNCHRONOUSLY, so the caller here is the platform's own
    /// resource thread with the webview waiting on it — and a walk is 110–150 MB of peak and seconds of IO for
    /// a two-hour film. So the answer is <c>503 Retry-After: 1</c> and the walk happens in a mission.
    /// <para>
    /// ⚠ <b>Two assertions, because either alone is weak.</b> The thread ids differing says the walk went
    /// somewhere else; the 503 arriving <i>while the stub is still parked</i> says the request did not WAIT for
    /// it, which is the property that actually matters and the one an implementation could break while keeping
    /// the mission. A route that submitted a mission and then blocked on its task would pass the first and fail
    /// the second.
    /// </para>
    /// <para>
    /// ⚠ The thread comparison is deterministic rather than lucky: nothing is awaited between capturing the
    /// requesting id and the request answering (the middleware is synchronous and <c>NotReadyYet</c> completes
    /// synchronously), and this thread is then blocked in <c>started.Wait</c> — so the pool cannot have run the
    /// walk on it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_first_request_answers_503_rather_than_planning_on_the_callers_thread()
    {
        Stage("film.mkv", Film());

        using var started = new ManualResetEventSlim(initialState: false);
        using var release = new ManualResetEventSlim(initialState: false);
        var plannerThread = 0;

        var interceptor = new FakeInterceptor();
        using var route = ComputedRemuxRoute.Use(interceptor, _scheduler, Access(), (source, token) =>
        {
            Volatile.Write(ref plannerThread, Environment.CurrentManagedThreadId);
            started.Set();
            // Bounded, so a broken run ends rather than hanging the whole suite.
            release.Wait(TimeSpan.FromSeconds(20));
            return Mp4Remuxer.Plan(source, token);
        });

        try
        {
            var requesting = Environment.CurrentManagedThreadId;
            var response = await interceptor.AskAsync("https://0.0.0.1/media?film.mkv", "bytes=0-99");

            Assert.Equal(503, response!.StatusCode);
            // The retry INTERVAL is the contract with the page's loop — see MediaConversion.NotReadyYet, which
            // is the one implementation both routes answer from.
            Assert.Equal("1", response.Headers["Retry-After"]);
            Assert.Equal("no-store", response.Headers["Cache-Control"]);
            Assert.Empty(Body(response));

            Assert.True(started.Wait(TimeSpan.FromSeconds(20)), "the walk never started");
            Assert.NotEqual(requesting, Volatile.Read(ref plannerThread));

            // Still parked — so this is not a race that happened to answer 503 before a fast walk finished.
            Assert.Equal(503, (await interceptor.AskAsync("https://0.0.0.1/media?film.mkv", "bytes=0-99"))!.StatusCode);
            Assert.Equal(0, Planned("film.mkv"));
        }
        finally
        {
            // Always release, even when an assertion above failed — a parked walk must not outlive the test.
            release.Set();
        }
    }

    /// <summary>
    /// 🔴 <b>A WALK OUTRANKS A QUEUED CONVERSION — and this is a functional claim, not tuning.</b> The route's
    /// own docs tell an app to share ONE scheduler with <c>UseMediaConversion</c>, whose missions are
    /// minutes-long transcodes at the default priority 0, and the global lane is as narrow as ONE permit on a
    /// small device (<c>clamp(cores-1, 1, 4)</c>). Queued FIFO behind a transcode, a plan that takes seconds
    /// would answer <c>503</c> for minutes — for a film that played on the first request before the walk moved
    /// off-thread — with no timeout and no escape for the page. So the plan mission declares
    /// <c>Priority = 1</c>.
    /// <para>
    /// ⚠ <b>The instrument is the ONE permit</b>: a blocker mission holds it, a competing priority-0 mission is
    /// queued BEHIND it, and only then is the route asked. Releasing the blocker makes both eligible at once,
    /// so admission order is observable as the order the two bodies run — and submission order (which favours
    /// the conversion) is the tie-break that priority has to beat. Without the priority this test sees
    /// <c>[conversion, plan]</c>.
    /// </para>
    /// <para>
    /// ⚠ What it does NOT prove, because the scheduler does not do it: priority never PREEMPTS. A transcode
    /// already running still holds its permit to the end.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_walk_outranks_a_QUEUED_conversion_on_a_shared_scheduler()
    {
        Stage("film.mkv", Film());

        // ONE permit, which is what a small device gets — and what makes admission order observable at all.
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions { GlobalLaneCapacity = 1 });
        var order = new List<string>();
        void Ran(string what) { lock (order) order.Add(what); }

        using var blocking = new ManualResetEventSlim(initialState: false);
        using var holding = new ManualResetEventSlim(initialState: false);

        var interceptor = new FakeInterceptor();
        using var route = ComputedRemuxRoute.Use(interceptor, scheduler, Access(), (source, token) =>
        {
            Ran("plan");
            return Mp4Remuxer.Plan(source, token);
        });

        // 1. The blocker takes the only permit and holds it.
        var blocker = scheduler.SubmitAsync(new MissionDefinition
        {
            Kind = "test-blocker",
            Run = (_, _) =>
            {
                holding.Set();
                blocking.Wait(TimeSpan.FromSeconds(20));
                return Task.CompletedTask;
            },
        });
        Assert.True(holding.Wait(TimeSpan.FromSeconds(20)), "the blocker never took the permit");

        // 2. A conversion-shaped mission queues at the DEFAULT priority, BEFORE the route is asked — so
        //    submission order favours it and only priority can put the walk first.
        var conversion = scheduler.SubmitAsync(new MissionDefinition
        {
            Kind = "test-conversion",
            Run = (_, _) => { Ran("conversion"); return Task.CompletedTask; },
        });

        // 3. Now the page asks. The walk is submitted and queued behind that conversion.
        Assert.Equal(503, (await interceptor.AskAsync("https://0.0.0.1/media?film.mkv", "bytes=0-99"))!.StatusCode);
        Assert.Equal(1, scheduler.RunningCount);
        Assert.Equal(2, scheduler.PendingCount);

        // 4. Release the permit: both are eligible, and the scheduler picks by priority.
        blocking.Set();
        await blocker;
        await conversion;

        var served = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv", "bytes=0-99");
        Assert.Equal(206, served!.StatusCode);

        lock (order) Assert.Equal(["plan", "conversion"], order);
    }

    /// <summary>
    /// …and once the plan is ready, the next request serves it — with the PLANNED total, out of the real
    /// output's bytes. Without this the case above would be satisfied by a route that answered 503 for ever.
    /// </summary>
    [Fact]
    public async Task Once_the_plan_is_ready_the_next_request_serves_a_206()
    {
        var film = Film();
        var expected = Expected(film);
        Stage("film.mkv", film);

        using var release = new ManualResetEventSlim(initialState: false);

        var interceptor = new FakeInterceptor();
        using var route = ComputedRemuxRoute.Use(interceptor, _scheduler, Access(), (source, token) =>
        {
            release.Wait(TimeSpan.FromSeconds(20));
            return Mp4Remuxer.Plan(source, token);
        });

        // The walk is parked, so the first answer can only be a 503 — the state this case starts from.
        Assert.Equal(503, (await interceptor.AskAsync("https://0.0.0.1/media?film.mkv", "bytes=0-99"))!.StatusCode);

        release.Set();

        var served = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv", "bytes=0-99");

        Assert.Equal(206, served!.StatusCode);
        Assert.Equal($"bytes 0-99/{expected.Total}", served.Headers["Content-Range"]);
        // The BYTES, not just the status: the plan has to be the one that walk produced.
        Assert.Equal(expected.Bytes[..100], Body(served));
        Assert.Equal(1, Planned("film.mkv"));
    }

    /// <summary>
    /// 🔴 <b>A WALK THAT FAILS FALLS THROUGH — a source must never 503 for ever.</b> A permanent 503 is the
    /// same "unplayable by every route" outcome the null-plan fall-through exists to prevent, wearing a
    /// different status code: the conversion or segment route behind would never see the film, and one host-log
    /// line would be the only tell.
    /// <para>
    /// ⚠ <b>And the failure is NOT remembered</b>, which is the second half and the difference from a refusal.
    /// `Plan` swallows everything but cancellation, so what reaches the route's catch is an OOM or a file it
    /// could not open — neither says anything about whether the source is plannable, so the NEXT request walks
    /// it again. Remembering it would strand a film that plans perfectly well a second later; never declining
    /// would strand it behind a 503 for ever. The state machine has to do both, which is why the failure is
    /// recorded and then CONSUMED.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_planning_failure_falls_through_rather_than_503ing_forever()
    {
        Stage("film.mkv", Film());
        var attempts = 0;

        var interceptor = new FakeInterceptor();
        using var route = ComputedRemuxRoute.Use(interceptor, _scheduler, Access(), (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            // What a real failure here IS: `Plan` catches everything else itself, so the realistic cause is an
            // OOM allocating the span array for a very large film.
            throw new OutOfMemoryException("the span array did not fit");
        });
        var reached = 0;
        UseTail(interceptor, () => reached++);

        Assert.Equal(FellThrough,
            Body((await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv", "bytes=0-99"))!));
        Assert.Equal(1, reached);
        Assert.Contains(Log(), line => line.Contains("could not plan film.mkv", StringComparison.Ordinal));
        Assert.Contains(Log(), line => line.Contains("plan FAILED", StringComparison.Ordinal));

        // The other half: it is not remembered, so the source is walked again rather than being declined for
        // the life of the route.
        Assert.Equal(FellThrough,
            Body((await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv", "bytes=0-99"))!));
        Assert.Equal(2, reached);
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    /// <summary>
    /// 🔴 <b>A film already planned keeps being SERVED while another film is being planned.</b> A plan is a full
    /// metadata walk, and a page with two elements — or a library opening a second film while the first plays —
    /// must not stall the one that is already working.
    /// <para>
    /// ⚠ <b>This was a comment until 2026-08-12, and what it guards against changed shape on 2026-08-13
    /// without going away.</b> It used to catch hoisting the planning lock up to wrap the whole answer; there is
    /// no planning lock any more, because the walk runs in a mission. What it catches now is the one remaining
    /// way to reintroduce the same stall: holding <c>_gate</c> — the cache lock, which every answer takes —
    /// across the walk. That reads as a simplification (one lock, fewer states) and makes this case HANG, which
    /// the timeout turns into a failure rather than a stuck run.
    /// </para>
    /// <para>
    /// The stub planner is what makes it deterministic: it parks film B's walk on a gate, so "while another is
    /// being planned" is a state the test controls rather than races.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_film_already_planned_keeps_being_served_while_another_is_being_planned()
    {
        Stage("a.mkv", Film(videoFrames: 5));
        Stage("b.mkv", Film(videoFrames: 7));

        using var parked = new ManualResetEventSlim(initialState: false);
        using var planningB = new ManualResetEventSlim(initialState: false);

        var interceptor = new FakeInterceptor();
        using var route = ComputedRemuxRoute.Use(interceptor, _scheduler, Access(), (source, token) =>
        {
            // The stream is the file, so B is identifiable without the route having to say so.
            if (source is FileStream file && file.Name.EndsWith("b.mkv", StringComparison.OrdinalIgnoreCase))
            {
                planningB.Set();
                // Bounded, so a broken run ends rather than hanging the whole suite.
                parked.Wait(TimeSpan.FromSeconds(20));
            }
            return Mp4Remuxer.Plan(source, token);
        });

        // A is planned and cached first.
        Assert.Equal(206, (await AskPlannedAsync(interceptor, "https://0.0.0.1/media?a.mkv", "bytes=0-49"))!.StatusCode);

        // B's first request starts its walk and answers 503 — it does not wait, which the case above pins.
        Assert.Equal(503, (await interceptor.AskAsync("https://0.0.0.1/media?b.mkv", "bytes=0-49"))!.StatusCode);
        try
        {
            Assert.True(planningB.Wait(TimeSpan.FromSeconds(20)), "B's walk never started");

            // THE CLAIM: B is inside the planner — and A is answered anyway, out of the cache, with no wait.
            var served = await Task.Run(() => interceptor.AskAsync("https://0.0.0.1/media?a.mkv", "bytes=0-49"))
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(206, served!.StatusCode);
        }
        finally
        {
            // Always release B, even when the assertion above failed — a parked thread must not outlive the test.
            parked.Set();
        }

        Assert.Equal(206, (await AskPlannedAsync(interceptor, "https://0.0.0.1/media?b.mkv", "bytes=0-49"))!.StatusCode);
    }

    /// <summary>
    /// 🔴 <b>A body-production failure must not leave a film's cached plan POISONED FOREVER — the recovery
    /// half of an invariant that changed shape, not disappeared, when the body became lazy.</b> A buffered
    /// body used to catch this synchronously and answer a clean 404; a lazy one commits its status line and
    /// <c>Content-Length</c> BEFORE the bytes are read, so a source that moved under a plan can no longer be
    /// caught before the headers go out — <c>WebViewFiles.Read</c>'s own doc states the identical tradeoff for
    /// the plain file path, and there is no way around it for a genuinely lazy body. What still MUST hold is
    /// that the cached layout gets dropped the moment the read fails, so the NEXT request re-plans instead of
    /// repeating the same broken read forever — which is the failure this test actually pins now.
    /// <para>
    /// The stub returns a layout that does not describe its source (a span pointing past the end of the file),
    /// which is what a genuinely transient IO failure looks like from the reader's side — then the truth on
    /// the next call, so RECOVERY is what is asserted rather than merely "the first read failed".
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_body_production_failure_does_not_leave_the_film_404ing_forever()
    {
        var film = Film();
        var expected = Expected(film);
        Stage("film.mkv", film);

        var planned = 0;
        var interceptor = new FakeInterceptor();
        using var route = ComputedRemuxRoute.Use(interceptor, _scheduler, Access(), (source, token) =>
            Interlocked.Increment(ref planned) == 1
                // A span whose source bytes are nowhere near the file: reading it throws, which is the shape
                // of a source that moved under a plan.
                ? new Mp4Layout(new byte[8], [new Mp4SampleSpan(long.MaxValue - 1024, 64, 8)], 72)
                : Mp4Remuxer.Plan(source, token));

        var broken = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv");
        // The headers are already committed by the time the read fails — a lazy body's bytes are pulled by
        // the platform AFTER `Answer` has already returned this response, so the failure surfaces here, on
        // the BODY read, rather than as a status code.
        Assert.Equal(200, broken!.StatusCode);
        // ⚠ NOT a fixed exception type: whether an absurd seek offset comes back as a clean "read returned
        // zero" (which `Mp4LayoutRangeStream` turns into `EndOfStreamException`) or as a raw `IOException`
        // straight from the OS is a platform detail, not a contract this route makes — `EndOfStreamException`
        // itself derives from `IOException`, so this covers both without over-specifying either.
        Assert.ThrowsAny<IOException>(() => Body(broken));
        Assert.Contains(Log(), line => line.Contains("could not read a planned range", StringComparison.Ordinal));

        // The next request re-plans instead of repeating the same broken read — through the retry loop, because
        // "re-plans" now means a fresh mission and therefore a fresh 503 first.
        var recovered = await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv");

        Assert.Equal(200, recovered!.StatusCode);
        Assert.Equal(expected.Bytes, Body(recovered));
        Assert.Equal(2, Volatile.Read(ref planned));
    }

    // ── registration ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Disposing the registration removes the route AND drops the layouts it was holding — a route that
    /// outlived the page it served would answer for the next one, and a layout that outlived the route is tens
    /// of megabytes nobody can reach.
    /// </summary>
    [Fact]
    public async Task Disposing_the_registration_removes_the_route()
    {
        Stage("film.mkv", Film());

        var interceptor = new FakeInterceptor();
        var route = interceptor.UseComputedRemux(_scheduler, Access());
        Assert.Equal(206, (await AskPlannedAsync(interceptor, "https://0.0.0.1/media?film.mkv", "bytes=0-99"))!.StatusCode);

        route.Dispose();

        Assert.Null(await interceptor.AskAsync("https://0.0.0.1/media?film.mkv", "bytes=0-99"));
    }

    /// <summary>
    /// A composition mistake must be loud at REGISTRATION, while the app is still being built, rather than as
    /// a route that quietly serves nothing. ⚠ Including the SCHEDULER, which is the argument an adopter is most
    /// likely to have nowhere to hand from — and a route with no scheduler could never plan anything, which
    /// looks exactly like a route that declines every source.
    /// </summary>
    [Fact]
    public void Registration_refuses_missing_configuration_rather_than_serving_nothing()
    {
        var interceptor = new FakeInterceptor();

        Assert.Throws<ArgumentNullException>(() => interceptor.UseComputedRemux(_scheduler, null!));
        Assert.Throws<ArgumentNullException>(() => interceptor.UseComputedRemux(null!, Access()));
        Assert.Throws<ArgumentNullException>(() =>
            ((IWebViewInterceptor)null!).UseComputedRemux(_scheduler, Access()));
    }

    // ── warming a plan before the page asks (D72) ─────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>THE CLAIM D72 IS MADE OF, and the reason there is no readiness event: after
    /// <c>PlanAsync</c>, an element's FIRST request is a 206.</b>
    ///
    /// <para>
    /// This is deliberately NOT written with <see cref="AskPlannedAsync"/> — that helper exists to retry past
    /// the 503, which is exactly the behaviour this test must prove is ABSENT. It asks ONCE. If warming did
    /// nothing, the single ask returns 503 and this fails, which is the only way the test can mean anything.
    /// </para>
    /// </summary>
    [Fact]
    public async Task After_PlanAsync_the_FIRST_request_is_206_with_no_503_at_all()
    {
        var film = Film();
        var expected = Expected(film);
        var path = Stage("film.mkv", film);

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        Assert.Equal(MediaPlanOutcome.Ready, await route.PlanAsync(path, CancellationToken.None));

        // ONE ask. No retry loop anywhere in this test.
        var response = await interceptor.AskAsync("https://0.0.0.1/media?film.mkv", "bytes=0-99");

        Assert.NotNull(response);
        Assert.Equal(206, response!.StatusCode);
        Assert.Equal($"bytes 0-99/{expected.Total}", response.Headers["Content-Range"]);
        Assert.Equal(expected.Bytes[..100], Body(response));
    }

    /// <summary>
    /// ⚠ <b>The warm path authorises exactly like the request path.</b> A source outside
    /// <see cref="MediaAccessOptions.AllowedRoots"/> is REFUSED rather than walked — otherwise
    /// <c>PlanAsync</c> would be a way to make the kit open any file the process can read, called from app
    /// code that believed it was only hinting.
    /// </summary>
    [Fact]
    public async Task PlanAsync_REFUSES_a_source_outside_the_allowed_roots()
    {
        // Staged somewhere real, then named through a route whose roots do not contain it — so a failure
        // here is containment, never a missing file.
        var outside = Path.Combine(_root, "outside.mkv");
        File.WriteAllBytes(outside, Film());

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        Assert.Equal(MediaPlanOutcome.Refused,
            await route.PlanAsync(outside, CancellationToken.None));
    }

    /// <summary>
    /// A remote source belongs to the conversion route behind this one, and warming must say so with the same
    /// answer the request path gives — a routing outcome, not a refusal and not an error.
    /// </summary>
    [Fact]
    public async Task PlanAsync_reports_a_remote_source_as_UNPLANNABLE_rather_than_refusing_it()
    {
        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        Assert.Equal(MediaPlanOutcome.Unplannable,
            await route.PlanAsync("https://example.invalid/film.mkv", CancellationToken.None));
    }

    /// <summary>
    /// 🔴 <b>The routing answer an app acts on.</b> A source whose output would LOSE a stream is the one case
    /// where warming changes what the app does next: it must send this film to the conversion or segment
    /// path instead of pointing an element at a URL that will never be served.
    /// </summary>
    [Fact]
    public async Task PlanAsync_reports_a_source_this_route_cannot_serve_as_UNPLANNABLE()
    {
        var path = Stage("needs-reencode.mkv", FilmNeedingReEncode());

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        Assert.Equal(MediaPlanOutcome.Unplannable,
            await route.PlanAsync(path, CancellationToken.None));
    }

    /// <summary>
    /// ⚠ <b>Warming twice WALKS ONCE</b> — the second call answers from the same cache the request path
    /// reads, which is what makes it safe to call on every navigation rather than tracking which sources have
    /// already been warmed.
    /// <para>
    /// Counted at the PLAN delegate rather than inferred from timing: "the second call was fast" would pass
    /// for a second walk of a small fixture.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Warming_the_same_source_twice_walks_it_ONCE()
    {
        var film = Film();
        var path = Stage("film.mkv", film);

        var walks = 0;
        var interceptor = new FakeInterceptor();
        using var route = ComputedRemuxRoute.Use(interceptor, _scheduler, Access(), (source, token) =>
        {
            Interlocked.Increment(ref walks);
            return Mp4Remuxer.Plan(source, token);
        });

        Assert.Equal(MediaPlanOutcome.Ready, await route.PlanAsync(path, CancellationToken.None));
        Assert.Equal(MediaPlanOutcome.Ready, await route.PlanAsync(path, CancellationToken.None));
        Assert.Equal(1, walks);

        // And the page's request that follows reuses it too — the whole point of warming.
        Assert.Equal(206, (await interceptor.AskAsync("https://0.0.0.1/media?film.mkv", "bytes=0-99"))!.StatusCode);
        Assert.Equal(1, walks);
    }

    /// <summary>
    /// 🔴 <b>A warmed plan does NOT outlive its source, and this is the "identity, not the path" rule
    /// (<c>Claim</c>'s own) reaching the warm path.</b> The key is derived from length and mtime, so a source
    /// that is gone cannot be keyed at all — and answering <c>Ready</c> off a stale entry would let an app
    /// point an element at a URL whose bytes no longer exist.
    /// <para>
    /// ⚠ It is <see cref="MediaPlanOutcome.Refused"/> rather than <see cref="MediaPlanOutcome.Failed"/>
    /// because "no such file" and "outside the roots" are deliberately ONE answer — see the enum.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Warming_a_source_that_has_since_been_DELETED_is_refused_rather_than_answered_from_cache()
    {
        var path = Stage("film.mkv", Film());

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        Assert.Equal(MediaPlanOutcome.Ready, await route.PlanAsync(path, CancellationToken.None));

        File.Delete(path);
        Assert.Equal(MediaPlanOutcome.Refused, await route.PlanAsync(path, CancellationToken.None));
    }

    /// <summary>
    /// ⚠ Concurrent warms of ONE source share ONE walk — the same <c>Claim</c> that stops an iOS burst of
    /// hundreds of range requests submitting hundreds of walks. Both callers get the same answer.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_PlanAsync_calls_for_one_source_both_answer_Ready()
    {
        var path = Stage("film.mkv", Film());

        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        var both = await Task.WhenAll(
            route.PlanAsync(path, CancellationToken.None),
            route.PlanAsync(path, CancellationToken.None));

        Assert.Equal([MediaPlanOutcome.Ready, MediaPlanOutcome.Ready], both);
    }

    /// <summary>
    /// A composition mistake is loud here too, and for the same reason it is at registration.
    /// </summary>
    [Fact]
    public async Task PlanAsync_refuses_an_empty_source_rather_than_planning_nothing()
    {
        var interceptor = new FakeInterceptor();
        using var route = interceptor.UseComputedRemux(_scheduler, Access());

        await Assert.ThrowsAsync<ArgumentException>(() => route.PlanAsync(" ", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => route.PlanAsync(null!, CancellationToken.None));
    }
}
