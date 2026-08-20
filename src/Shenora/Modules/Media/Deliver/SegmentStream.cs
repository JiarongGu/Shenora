using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Shenora.Core.WebView;
using Shenora.Engine;

namespace Shenora.Modules.Media;

/// <summary>
/// What an app supplies so a <see cref="SegmentStream"/> can answer: its route, where a URL's source lives,
/// and what may be reached.
/// </summary>
public sealed class SegmentStreamOptions
{
    /// <summary>
    /// Where a source may be read from, where produced segments are cached, and which module the route's
    /// events would publish on — stated ONCE for every media delivery path rather than declared here a
    /// third time. See <see cref="MediaAccessOptions"/> for the full reasoning.
    /// <para>
    /// <see cref="MediaAccessOptions.Resolve"/> is this route's map from a request URI to a source file, and
    /// the reason this route carries no layout knowledge of its own — return null for "not mine" and the
    /// request falls through the pipeline. The URI handed here has the RESOURCE stripped — it is the
    /// stream's own URL, so an app can reuse the resolver its other routes already use rather than writing a
    /// second one. ⚠ Two functions that must agree about where a file lives will eventually disagree, and
    /// the symptom is a download that succeeds and a stream that 404s.
    /// </para>
    /// <para>
    /// 🔴 <b><see cref="MediaAccessOptions.CacheRoot"/> must NOT be the directory a finished conversion goes
    /// to, and the reason is a measured data loss rather than tidiness.</b> Segments are rebuildable from
    /// the original at any moment; a completed conversion backing an offline download is not — deleting it
    /// costs a user a file they waited for. On iOS <c>Library/Caches</c> may be PURGED by the OS under
    /// storage pressure, which is exactly the right thing to happen to one and exactly the wrong thing to
    /// happen to the other. Cache tenancy is two things; give them two roots. ⚠ A purged directory also
    /// 503s forever if it is not re-created per restart, because "the file is missing" reads as "the engine
    /// failed". Both halves were hit by the first adopter.
    /// </para>
    /// </summary>
    public required MediaAccessOptions Access { get; init; }

    /// <summary>The URL prefix this route answers. Relative, so one string works on every shell.</summary>
    public string RoutePath { get; init; } = "/shenora-hls/";

    /// <summary>
    /// Remote sources this route may stream, each addressable only by a handle the APP issued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Null — the default — means this route serves local files only</b>, so a remote source is
    /// impossible until an app deliberately makes one possible. Fail-closed, like
    /// <see cref="MediaAccessOptions.AllowedRoots"/> beside it.
    /// </para>
    /// <para>
    /// ⚠ A registered source bypasses <see cref="MediaAccessOptions.AllowedRoots"/> entirely, and that is
    /// the design rather than a hole in it: containment answers "may this PATH be read", which is not a
    /// question about a url. The equivalent boundary for a remote source is that the page cannot name one
    /// at all — see <see cref="MediaSourceRegistry"/>.
    /// </para>
    /// </remarks>
    public MediaSourceRegistry? Sources { get; init; }

    /// <summary>
    /// The path segment that means "an issued handle follows": <c>{RoutePath}~remote/{handle}/{resource}</c>.
    /// <para>
    /// ⚠ A RESERVED segment, checked before <see cref="MediaAccessOptions.Resolve"/> is consulted, so a
    /// handle can never collide with something an app's own resolver would have matched — and so the app
    /// does not have to know this route has a second shape.
    /// </para>
    /// </summary>
    public const string RemotePrefix = "~remote/";

    /// <summary>The manifest's name under a source: <c>{RoutePath}{app-part}/index.m3u8</c>.</summary>
    public string ManifestName { get; init; } = "index.m3u8";

    /// <summary>
    /// The segment grid, in seconds. One number, used by the manifest AND handed to the engine — they must
    /// agree or the cuts are not where the playlist says they are.
    /// </summary>
    public double SegmentSeconds { get; init; } = 6.0;

    /// <summary>
    /// The cache ceiling, swept oldest-USED first when a new source is opened — never on the request path.
    /// Defaults to 2 GB, which is a budget for a rebuildable cache on a phone.
    /// </summary>
    public long CacheCapBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// How long a segment request may wait before answering <c>503</c>.
    /// <para>
    /// ⚠ This blocks the platform's resource thread, which every shell resolves SYNCHRONOUSLY — so the
    /// budget is a real cost, not a formality. It is affordable because the common case is not a wait at
    /// all: the window runs AHEAD of sequential playback, so only a seek pays, and only for the segment it
    /// lands on.
    /// </para>
    /// </summary>
    public TimeSpan WaitBudget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a window with a picture may run before its output is inspected mid-flight — the deadline on
    /// "the muxer has not rotated yet", which is the symptom of an encoder writing nothing.
    /// </summary>
    public TimeSpan PictureGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How far ahead of what has been produced a request may be before the window RESTARTS instead of
    /// waiting — the whole seek policy in one number.
    /// <para>
    /// Inside the window a seek is a file that already exists or is seconds away; outside it, waiting would
    /// mean encoding everything in between. Two segments of tolerance covers a player reading slightly ahead
    /// of production without treating ordinary lookahead as a seek.
    /// </para>
    /// </summary>
    public int Lookahead { get; init; } = 2;
}

/// <summary>
/// Playing a source the webview cannot decode <b>without converting the whole thing first</b>: a segmented
/// stream, produced on the device, one piece at a time, served over the interceptor.
///
/// <para>
/// <b>Why this exists beside a conversion route rather than instead of it.</b> Converting produces a finished
/// file before a single byte plays — correct, and an hour-long source is an hour-long wait. This route
/// answers a manifest immediately and produces only the segments actually asked for, so playback starts in
/// seconds and a seek costs one restart. They are complements: a finished conversion is a file you own, a
/// segment stream is a file you are watching.
/// </para>
///
/// <para>
/// ⚠ <b>The manifest is SYNTHETIC — computed from the source's DURATION alone, before any segment exists.</b>
/// That is the load-bearing idea, not a shortcut. Declaring the whole playlist up front is what makes the
/// scrub bar the right length and a seek to minute 40 expressible; a manifest that grew as segments appeared
/// would make the player believe the source ends wherever production had reached.
/// </para>
///
/// <para>
/// Harvested from a consuming app that built and proved it on a device (D15). Its engine, its codec policy
/// and its binaries stayed with it — see <see cref="ISegmentEngine"/> and D51.
/// </para>
/// </summary>
internal sealed class SegmentStream : IDisposable
{
    private const string ManifestContentType = "application/vnd.apple.mpegurl";
    /// <summary>
    /// fMP4, so the segments and the init segment are the same container — see
    /// <see cref="SegmentRunRequest.SegmentExtension"/> for why this is not MPEG-TS.
    /// </summary>
    private const string SegmentContentType = "video/mp4";

    /// <summary>Poll interval while waiting for a segment to close.</summary>
    private const int PollMilliseconds = 50;

    private readonly ISegmentEngine _engine;
    private readonly SegmentStreamOptions _options;
    private readonly WebViewRangeDelivery _delivery;
    private readonly ILogger? _log;

    /// <summary>Live sources by cache key. Guarded by <see cref="_gate"/>.</summary>
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);

    /// <summary>Last time each key was ASKED for, which is what LRU should order by — see the sweep.</summary>
    private readonly Dictionary<string, DateTime> _touched = new(StringComparer.Ordinal);

    private readonly object _gate = new();
    private bool _disposed;

    private SegmentStream(ISegmentEngine engine, SegmentStreamOptions options,
                          WebViewRangeDelivery delivery, ILogger? log)
    {
        _engine = engine;
        _options = options;
        _delivery = delivery;
        // 🔴 Falls back to the SHARED sink. `MediaAccessOptions.Log` says it is stated once for every
        // delivery path, and the conversion route beside this one reads it — this route read only its own
        // parameter, so an app that set the shared one got diagnostics from one route and silence from the
        // other, with nothing to indicate which. An absent log is indistinguishable from a quiet one.
        _log = log ?? options.Access.Log;
    }

    /// <summary>
    /// Register the route. Dispose the result to remove it AND kill every running production — a window that
    /// outlives the page it was feeding is a process nobody is waiting for.
    /// </summary>
    /// <param name="interceptor">The shell's interceptor. Supplies the platform's range-delivery rule.</param>
    /// <param name="engine">The app's production engine.</param>
    /// <param name="options">The app's route, resolver and roots.</param>
    /// <param name="log">Optional diagnostics. Guarded — a throwing sink must not break serving.</param>
    internal static ISegmentStreamRoute Use(IWebViewInterceptor interceptor, ISegmentEngine engine,
                                  SegmentStreamOptions options, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Access);
        ArgumentNullException.ThrowIfNull(options.Access.Resolve);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Access.CacheRoot);
        // At composition time: a non-positive grid is a plan nothing can be built from, and discovering that
        // on the first request would answer 404 for a source that is perfectly fine.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SegmentSeconds);

        var stream = new SegmentStream(engine, options, interceptor.RangeDelivery, log);
        var route = interceptor.Use((request, next, cancellationToken) =>
        {
            // "Not mine" must fall through the REST of the pipeline, not terminate it.
            if (stream.Parse(request.Uri) is not { } parsed) return next(request, cancellationToken);
            return Task.FromResult<WebViewResourceResponse?>(
                stream.Answer(request, parsed.Target, parsed.Resource, cancellationToken));
        });

        return new Registration(route, stream);
    }

    /// <summary>
    /// Split <c>{RoutePath}{whatever the app's layout is}/{resource}</c> into a source path and the resource
    /// asked for, or null when it is not ours.
    /// <para>
    /// Everything between the route prefix and the LAST segment is the app's to interpret, which is what
    /// keeps this route free of any layout knowledge: the kit knows only that the final segment names a
    /// resource, and hands the rest to <see cref="MediaAccessOptions.Resolve"/>.
    /// </para>
    /// </summary>
    private (Requested Target, string Resource)? Parse(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return null;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!path.StartsWith(_options.RoutePath, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = path[_options.RoutePath.Length..];
        var slash = rest.LastIndexOf('/');
        // At least one leading part AND a resource; anything else is not a shape this route serves.
        if (slash <= 0 || slash == rest.Length - 1) return null;

        var resource = rest[(slash + 1)..];

        // The reserved shape, checked BEFORE the app's resolver so a handle cannot collide with a path the
        // app would have matched. An unissued handle falls through as "not mine" rather than 404ing: the
        // rest of the pipeline may still have something to say about this url.
        if (rest.StartsWith(SegmentStreamOptions.RemotePrefix, StringComparison.Ordinal))
        {
            var handle = rest[SegmentStreamOptions.RemotePrefix.Length..slash];
            return _options.Sources?.Resolve(handle) is { } remote
                ? (new Requested(null, remote), resource)
                : null;
        }

        var sourceUri = new UriBuilder(uri)
        {
            Path = _options.RoutePath + rest[..slash],
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;

        return _options.Access.Resolve(sourceUri) is { } source ? (new Requested(source, null), resource) : null;
    }

    /// <summary>
    /// What a request named: a path the app resolved, or a remote source the app registered. Exactly one.
    /// </summary>
    private readonly record struct Requested(string? Path, RemoteMediaSource? Remote);

    /// <summary>Answer one request: the manifest, a segment, or a refusal.</summary>
    private WebViewResourceResponse Answer(WebViewResourceRequest request, Requested requested, string resource,
                                           CancellationToken cancellationToken)
    {
        var source = requested.Remote is { } remote
            ? OpenRemote(remote, cancellationToken)
            : OpenLocal(requested.Path!, cancellationToken);
        if (source is null) return WebViewResourceResponse.NotFound();

        if (resource.Equals(_options.ManifestName, StringComparison.OrdinalIgnoreCase))
            return WebViewResourceResponse.Bytes(Encoding.UTF8.GetBytes(Manifest(source)), ManifestContentType);

        // 🔴 THE INIT SEGMENT IS PRODUCED, NOT STORED, so it goes through the same wait/produce path as a
        // segment rather than being served as a static file. Its decoder configuration is knowable only once
        // an encoder has emitted output, so the engine writes it beside its FIRST fragment — which means a
        // page following `#EXT-X-MAP` asks for it before anything exists and must be able to be told "not
        // yet". Serving a 404 instead would end playback before it began, and serving an empty file would
        // produce a movie that opens and plays nothing.
        if (resource.Equals(SegmentRunRequest.InitSegmentName, StringComparison.OrdinalIgnoreCase))
        {
            // Asking for it IS asking for production to have started, so it drives segment 0 exactly as a
            // request for seg0 would. A run already past that point has written it too.
            return EnsureSegment(source, 0) && File.Exists(InitPath(source))
                ? WebViewFiles.Serve(request, InitPath(source), SegmentContentType, _delivery)
                : MediaConversionExtensions.NotReadyYet();
        }

        if (!TryParseSegmentIndex(resource, out var index) || index < 0 || index >= source.SegmentCount)
            return WebViewResourceResponse.NotFound();

        return EnsureSegment(source, index)
            ? WebViewFiles.Serve(request, SegmentPath(source, index), SegmentContentType, _delivery)
            // ⚠ The ONE not-ready answer, shared with the conversion and computed-remux routes rather than
            // copied per route. The `Retry-After` interval is a contract with the page's own retry loop, so
            // separate copies are separate numbers that can drift apart while every test still passes.
            : MediaConversionExtensions.NotReadyYet();
    }

    /// <summary>
    /// The source's cache entry, probed and PLANNED the first time it is seen.
    /// <para>
    /// ⚠ <b>This probes ON THE REQUEST PATH</b>, which the kit's other routes refuse to do — and it is
    /// unavoidable here rather than sloppy: the manifest IS the answer to the first request, and it cannot be
    /// written without the duration or the boundaries. A re-encoding engine answers both from the container
    /// header; a COPYING one has to find the source's keyframes, which costs a walk of its frame index — the
    /// same walk every production run already pays, in exchange for not encoding the picture at all (D76).
    /// </para>
    /// </summary>
    private Source? OpenLocal(string requested, CancellationToken cancellationToken)
    {
        // Containment runs BEFORE the filesystem is touched, and a refusal is the same 404 as a missing file
        // so nothing can probe for existence by comparing responses.
        if (WebViewFiles.ResolveContained(requested, _options.Access.AllowedRoots) is not { } contained)
        {
            Log(() => "segments: refused a source outside the allowed roots");
            return null;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(contained);
            if (!info.Exists) return null;
        }
        catch (Exception ex)
        {
            // No exception text on the wire, ever — a path is the likeliest thing it would carry.
            Log(() => "segments: could not stat a source", ex);
            return null;
        }

        // Identity+length+mtime: replacing the source invalidates its segments rather than serving
        // yesterday's.
        var key = DerivedCacheKey.For(contained, info.Length, info.LastWriteTimeUtc, "hls");
        return OpenSource(key, MediaByteSource.ForFile(contained), null, null, cancellationToken);
    }

    /// <summary>
    /// A source the app registered. No containment — see <see cref="SegmentStreamOptions.Sources"/> — and
    /// no <c>stat</c>, because neither length nor mtime is knowable without fetching.
    /// </summary>
    private Source? OpenRemote(RemoteMediaSource remote, CancellationToken cancellationToken)
    {
        // ⚠ Keyed on IDENTITY, falling back to the url. A presigned url rotates, and keying on it directly
        // re-segments the same film under a new key every time the signature changes while the previous
        // copies sit in the cache until the sweep reaches them.
        var key = DerivedCacheKey.For(
            remote.Identity ?? remote.Url.AbsoluteUri, 0, DateTime.UnixEpoch, "hls-remote");

        // 🔴 Producing needs BYTES, and only the app can fetch them: the kit ships no transport, so a source
        // registered without an opener is one this route can serve a manifest for and never a segment. Said
        // once, here, rather than discovered as a run that dies on every restart.
        if (remote.Open is not { } open)
        {
            Log(() => $"segments: {remote.Label} was registered without an opener, so its bytes cannot be read"
                    + " — set RemoteMediaSource.Open to a seekable stream over the transport");
            return null;
        }

        // The LABEL is what every log line prints; the url never leaves the app's own closure.
        var bytes = new MediaByteSource { Label = remote.Label, Open = open };
        return OpenSource(key, bytes, remote.Duration, remote.HasPicture, cancellationToken);
    }

    private Source? OpenSource(string key, MediaByteSource bytes, TimeSpan? knownDuration,
                               bool? knownPicture, CancellationToken cancellationToken)
    {
        var label = bytes.Label;

        lock (_gate)
        {
            if (_disposed) return null;
            _touched[key] = DateTime.UtcNow;
            if (_sources.TryGetValue(key, out var existing)) return existing;
        }

        // ⚠ The caller's value wins when it has one. Probing a REMOTE source costs an engine launch reading
        // a network header before the first manifest can be answered — twice, with the picture probe below
        // — and whoever registered the source usually knows both from the catalogue entry the url came
        // from. A supplied value is trusted: it is the app's own claim about its own media.
        if ((knownDuration ?? _engine.DurationOf(bytes)) is not { } duration || duration <= TimeSpan.Zero)
        {
            Log(() => $"segments: no duration for {label} — refusing");
            return null;
        }

        // 🔴 ONE plan object, used by the manifest AND handed to every run for this source. A playlist and a
        // producer that computed boundaries separately would disagree silently: the bytes stay valid, and a
        // seek lands at the wrong moment. Null means the engine will hit the grid, which is then the plan.
        var plan = _engine.PlanSegments(bytes, _options.SegmentSeconds, cancellationToken)
                   ?? SegmentPlan.Grid(_options.SegmentSeconds, duration);
        var hasPicture = knownPicture ?? _engine.HasPicture(bytes);

        lock (_gate)
        {
            if (_disposed) return null;
            // ⚠ Re-checked because the planning above runs OUTSIDE this lock — deliberately, since it can
            // take seconds on a large film and this lock guards every OTHER stream's entry too. Two
            // first-requests for one source may therefore both plan; that costs a duplicated walk, where
            // holding the lock would stall an unrelated stream for the whole of it.
            if (_sources.TryGetValue(key, out var raced)) return raced;

            var directory = Path.Combine(_options.Access.CacheRoot, key);
            System.IO.Directory.CreateDirectory(directory);

            var source = new Source
            {
                Bytes = bytes,
                Directory = directory,
                Duration = duration,
                Plan = plan,
                HasPicture = hasPicture,
            };
            DropPartials(source);
            _sources[key] = source;

            Log(() => $"segments: {label} {duration.TotalSeconds:0.00}s"
                    + $" -> {plan} (picture={source.HasPicture})");

            // Off the request path: the sweep stats every cached directory, and a request should never pay
            // for housekeeping that a previous request created the need for.
            _ = Task.Run(() => SweepCache(key));
            return source;
        }
    }

    /// <summary>
    /// The synthetic VOD playlist. Every segment named before any exists, each with the length the PLAN says
    /// it has.
    /// <para>
    /// ⚠ The tail carries the REAL remainder rather than a flat segment length, and a derived plan's entries
    /// differ from one another throughout. A playlist's declared total is the sum of its <c>EXTINF</c>s — a
    /// flat last entry would overstate the source by up to one whole segment, and a scrub bar built on it
    /// seeks past the end.
    /// </para>
    /// <para>
    /// Anything the engine produces past the last segment named here lands INSIDE it — an AAC encoder adds
    /// priming, so an encoded stream runs a frame or two longer than the source declared. The plan clamps a
    /// time past its end to its last index rather than naming a segment nobody can ask for, since a request
    /// outside <see cref="Source.SegmentCount"/> is refused.
    /// </para>
    /// </summary>
    private static string Manifest(Source source)
    {
        var builder = new StringBuilder();
        builder.Append("#EXTM3U\n");
        // 🔴 VERSION 7, not 3, and it is the fMP4 switch rather than a bump for its own sake: an
        // `#EXT-X-MAP` is illegal below 6, and a reader honouring the declared version would skip the one
        // line without which no segment can be decoded. The two must move together.
        builder.Append("#EXT-X-VERSION:7\n");
        builder.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
        // 🔴 The LONGEST segment, not the length that was asked for. A plan cut on the source's own keyframes
        // routinely holds a segment longer than the target, and a TARGETDURATION below any EXTINF is a
        // MUST the playlist spec states, so a strict reader may refuse the lot — for a stream whose bytes are fine.
        builder.Append(CultureInfo.InvariantCulture,
            $"#EXT-X-TARGETDURATION:{(int)Math.Ceiling(source.Plan.LongestSeconds)}\n");
        builder.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
        // The initialisation segment: the tracks and their decoder configuration, which the numbered
        // segments deliberately never repeat.
        builder.Append(CultureInfo.InvariantCulture,
            $"#EXT-X-MAP:URI=\"{SegmentRunRequest.InitSegmentName}\"\n");

        for (var i = 0; i < source.SegmentCount; i++)
        {
            var seconds = Math.Max(0.001, source.Plan.LengthOf(i));
            builder.Append(CultureInfo.InvariantCulture, $"#EXTINF:{seconds:0.000},\n");
            builder.Append(CultureInfo.InvariantCulture, $"seg{i}{SegmentRunRequest.SegmentExtension}\n");
        }

        builder.Append("#EXT-X-ENDLIST\n");
        return builder.ToString();
    }

    /// <summary>
    /// Have segment <paramref name="index"/> on disk and closed, restarting the window if it is not coming.
    /// <para>
    /// Serialised per source: the requests for one stream arrive in order, and letting two of them steer the
    /// same run would have each undo the other's restart.
    /// </para>
    /// </summary>
    private bool EnsureSegment(Source source, int index)
    {
        var deadline = DateTime.UtcNow + _options.WaitBudget;

        lock (source.Gate)
        {
            while (true)
            {
                // The verification can REJECT what is on disk (and restart on the next encoder), so it sits
                // between "the file is there" and "serve it" rather than after the loop.
                if (IsComplete(source, index) && VerifyPicture(source, SegmentPath(source, index)))
                {
                    // Record that this window HAS produced — it is what tells a later restart apart from an
                    // encoder that never wrote a frame. See the ladder bump in Restart.
                    if (index >= source.WindowStart) source.Newest = Math.Max(source.Newest, index);
                    return true;
                }

                // ⚠ And the same check has to run on a segment that has NOT closed yet, because the two
                // failures are the same failure. An encoder writing no frames means the segment muxer never
                // rotates — it only cuts on a video keyframe — so the first segment simply grows to hold the
                // WHOLE source and closes when the run ends. On a twenty-minute video that is a stall the
                // budget expires inside, with the ladder never advancing and every later request repeating
                // it. A partial MPEG-TS still declares its picture's size once one keyframe is muxed, so the
                // same question answers the same way ten seconds in.
                if (StalledWithoutPicture(source)) continue;

                if (RestartReason(source, index) is { } reason && !Restart(source, index, reason)) return false;

                if (DateTime.UtcNow >= deadline)
                {
                    Log(() => $"segments: seg{index} did not arrive within {_options.WaitBudget.TotalSeconds:0}s"
                            + $" (window from {source.WindowStart}, attempt {source.Attempt})");
                    return false;
                }

                Thread.Sleep(PollMilliseconds);
            }
        }
    }

    /// <summary>
    /// Is the segment on disk AND finished? Under the atomic-publish contract those are ONE question: a run
    /// writes <c>seg{k}.m4s.part</c> and renames it, so the final name appears only when the bytes are whole
    /// (<see cref="SegmentRunRequest.PartialExtension"/>).
    /// <para>
    /// 🔴 <b>It used to require the NEXT segment to exist</b>, because a progressive muxer creates a file
    /// when it STARTS writing it — which made first playback wait for segment 0 <i>and</i> the opening of
    /// segment 1, a whole segment of latency for a question the producer can answer for free. ⚠ Non-empty,
    /// not merely present: a rename cannot produce a zero-length file, but a foreign engine ignoring the
    /// contract can, and this is the cheaper half of noticing.
    /// </para>
    /// </summary>
    private static bool IsComplete(Source source, int index) => NonEmpty(SegmentPath(source, index));

    /// <summary>
    /// For a source WITH a picture: does the segment actually contain one?
    ///
    /// <para>
    /// 🔴 <b>Non-empty is not enough, and "exit 0" is worth even less.</b> A hardware encoder can open, map
    /// the stream, accept every frame, write <c>video:0KiB</c> and exit 0. A segmenter fed that encoder
    /// produces a perfectly valid, perfectly sized, perfectly playable AUDIO-ONLY stream for a video source
    /// and reports success the whole way. Only the output is evidence.
    /// </para>
    /// <para>
    /// One probe per WINDOW, not per segment: an encoder that wrote a picture into the first segment is
    /// writing one into the rest.
    /// </para>
    /// </summary>
    private bool VerifyPicture(Source source, string path)
    {
        if (!source.HasPicture || source.Verified) return true;

        // ⚠ HasRENDEREDPicture, not HasPicture. The segment declares a video stream either way — MPEG-TS
        // names its streams in the PMT — so the question has to be whether the frames have a SIZE. Asking
        // the source's question here passes the picture-less output and ships an audio-only video stream.
        if (_engine.HasRenderedPicture(path))
        {
            source.Verified = true;
            return true;
        }

        // Drop the evidence — leaving it would make it a cache hit that every later request happily serves —
        // and advance the ladder. The caller's loop sees no run and restarts on the next candidate.
        Log(() => $"segments: {Path.GetFileName(path)} came back with no picture"
                + " — advancing past the encoder that wrote it");
        try { File.Delete(path); } catch (Exception) { /* it will be overwritten anyway */ }
        StopRun(source);
        source.Attempt++;
        return false;
    }

    /// <summary>
    /// A window that has been running past its grace period, has PUBLISHED something, and still has no
    /// picture in it. True means the ladder was advanced and the caller should loop.
    /// <para>
    /// ⚠ <b>Atomic publish narrowed this, and the remaining gap is stated rather than papered over.</b> It
    /// used to inspect a still-open first segment — the tell for an encoder that writes no frames, whose
    /// muxer therefore never rotates. A part being written is now a <c>.part</c>, so what this sees is a
    /// FINISHED window start with no picture: still worth catching when the request is for a later index,
    /// and no longer able to see a run that has published nothing at all. That case falls to
    /// <see cref="SegmentStreamOptions.WaitBudget"/> and answers <c>503</c> without advancing the ladder.
    /// Reading a <c>.part</c> instead would judge a file mid-write, where "no picture yet" and "no picture
    /// ever" are the same bytes.
    /// </para>
    /// </summary>
    private bool StalledWithoutPicture(Source source)
    {
        if (!source.HasPicture || source.Verified || source.Run is null) return false;
        if (DateTime.UtcNow - source.WindowStartedUtc < _options.PictureGrace) return false;

        var first = SegmentPath(source, source.WindowStart);
        if (!NonEmpty(first)) return false;   // nothing written yet is not evidence of anything

        // Generous on purpose: a working encoder emits its first keyframe in well under this, and the cost
        // of being wrong is dropping to a slower encoder rather than failing. Being wrong the other way is a
        // stall with no end.
        return !VerifyPicture(source, first);
    }

    /// <summary>
    /// Why waiting for this segment would be waiting for something that is not coming — or null to keep
    /// waiting.
    /// <para>
    /// A reason rather than a bool because the four cases are the whole seek policy, and "it restarted" with
    /// no cause is the log line that makes a stall unreadable. It is what showed a restart-per-segment on a
    /// video path was the RUN DYING, not the lookahead being too tight.
    /// </para>
    /// </summary>
    private string? RestartReason(Source source, int index)
    {
        if (source.Run is null) return "nothing is producing";
        // Behind the window: this run numbers from WindowStart and will never go back.
        if (index < source.WindowStart) return $"seek back inside the window from seg{source.WindowStart}";
        // The run is over. Anything it did not write, it never will.
        if (source.Run.HasExited)
            return File.Exists(SegmentPath(source, index)) ? null : "the run ended without writing it";
        // Far enough ahead that waiting means encoding everything in between — seek instead.
        var newest = NewestProduced(source);
        return index > newest + _options.Lookahead ? $"seek forward past seg{newest}" : null;
    }

    /// <summary>
    /// The highest segment the current window has reached, or <c>WindowStart - 1</c> when it has written
    /// nothing yet. Contiguous from the window start, because the muxer writes in order.
    /// <para>
    /// Resumes from the last answer rather than re-walking. This is called on every poll of a wait loop, and
    /// a fresh walk over an hour-long stream would be six hundred <c>stat</c> calls every fifty milliseconds
    /// — CPU spent proving what the previous pass already knew.
    /// </para>
    /// </summary>
    private static int NewestProduced(Source source)
    {
        var index = Math.Max(source.Newest + 1, source.WindowStart);
        while (File.Exists(SegmentPath(source, index))) index++;
        source.Newest = index - 1;
        return source.Newest;
    }

    /// <summary>
    /// Kill whatever is running and start producing at <paramref name="index"/>. False when the engine has
    /// no candidate left — the caller then answers 503 and the next request starts the ladder over.
    /// </summary>
    private bool Restart(Source source, int index, string reason)
    {
        // A run that died at this very window HAVING PRODUCED NOTHING indicts the ENCODER, not the source,
        // so advance the ladder.
        //
        // ⚠ "Produced nothing" is `Newest < WindowStart`, NOT "the file is missing", and the difference is a
        // measured bug rather than a nicety. A run that finished the whole source and had its cache purged
        // afterwards also has no file at the index — read as an encoder failure, that walked a WORKING
        // ladder off its end and answered 503 for a source that had been playing perfectly a minute earlier.
        if (source.Run is not null && source.WindowStart == index && source.Newest < source.WindowStart)
            source.Attempt++;

        StopRun(source);

        // ⚠ Re-created every time, not once at OpenSource. This is a CACHE directory and something else may
        // have taken it away — iOS purges its caches under storage pressure, which is precisely why segments
        // live there. Measured the hard way: deleting the directory under a running app left the in-memory
        // source pointing at nothing, the engine died instantly on every restart, the ladder ran out of
        // candidates, and the route answered 503 forever with nothing in the log to say the directory was
        // the problem.
        try { System.IO.Directory.CreateDirectory(source.Directory); }
        catch (Exception ex)
        {
            Log(() => "segments: could not re-create the cache directory", ex);
        }

        var run = _engine.Start(new SegmentRunRequest(
            source.Bytes, source.Directory, source.HasPicture, index, source.Plan, source.Attempt));
        if (run is null)
        {
            Log(() => $"segments: no engine candidate left for seg{index} (attempt {source.Attempt}) — giving up");
            // Start the ladder over rather than leaving the source permanently unservable: the next request
            // gets the first candidate again, so a transient failure heals instead of sticking.
            source.Attempt = 0;
            return false;
        }

        source.Run = run;
        source.WindowStart = index;
        source.Newest = index - 1;
        source.Verified = false;
        source.WindowStartedUtc = DateTime.UtcNow;
        Log(() => $"segments: producing from seg{index} (attempt {source.Attempt}) — {reason}");
        return true;
    }

    private static void StopRun(Source source)
    {
        var run = source.Run;
        source.Run = null;
        source.WindowStart = -1;
        source.Newest = -1;
        try { run?.Dispose(); } catch (Exception) { /* already gone */ }
    }

    /// <summary>
    /// Delete anything a killed process left half-written, the first time a source is opened here.
    /// <para>
    /// Under the atomic-publish contract that is exactly the <c>.part</c> files
    /// (<see cref="SegmentRunRequest.PartialExtension"/>) — a rename either happened or it did not, so no
    /// FINAL name can be truncated and none has to be guessed at.
    /// </para>
    /// <para>
    /// 🔴 <b>It used to delete the highest-numbered SEGMENT instead</b>, on the reasoning that a kill leaves
    /// a file that exists, is non-empty and is short. That cost a segment of re-production on every open of
    /// every source — almost always a perfectly good one — and still could not see a truncated file anywhere
    /// but the tail.
    /// </para>
    /// </summary>
    private void DropPartials(Source source)
    {
        try
        {
            var dropped = 0;
            foreach (var file in System.IO.Directory.EnumerateFiles(
                         source.Directory, $"*{SegmentRunRequest.PartialExtension}"))
            {
                try { File.Delete(file); dropped++; }
                catch (Exception) { /* it will be overwritten by the next run anyway */ }
            }

            if (dropped > 0) Log(() => $"segments: dropped {dropped} part-file(s) left by an interrupted run");
        }
        catch (Exception ex)
        {
            Log(() => "segments: could not sweep interrupted writes", ex);
        }
    }

    /// <summary>
    /// Keep the cache under <see cref="SegmentStreamOptions.CacheCapBytes"/>, oldest-used first.
    /// <para>
    /// Ordered by when a key was last ASKED for rather than by file mtime: a fully-produced stream that is
    /// played every day never has a byte written to it, so an mtime ordering would evict exactly the entry
    /// that is earning its keep. Runs on a background thread, never on the request path.
    /// </para>
    /// </summary>
    private void SweepCache(string keep)
    {
        try
        {
            var root = new DirectoryInfo(_options.Access.CacheRoot);
            if (!root.Exists) return;

            var entries = new List<(DirectoryInfo Dir, long Size, DateTime Touched)>();
            foreach (var dir in root.GetDirectories())
            {
                long size = 0;
                foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories)) size += file.Length;
                DateTime touched;
                lock (_gate) touched = _touched.TryGetValue(dir.Name, out var t) ? t : dir.LastWriteTimeUtc;
                entries.Add((dir, size, touched));
            }

            var total = entries.Sum(e => e.Size);
            if (total <= _options.CacheCapBytes) return;

            foreach (var entry in entries.OrderBy(e => e.Touched))
            {
                if (total <= _options.CacheCapBytes) break;
                if (string.Equals(entry.Dir.Name, keep, StringComparison.Ordinal)) continue;
                // Never delete under a live window: the run would keep writing into a deleted directory and
                // every segment after it would 404 with nothing in the log to say why.
                lock (_gate)
                {
                    if (_sources.TryGetValue(entry.Dir.Name, out var live)
                        && live.Run is { HasExited: false }) continue;
                    _sources.Remove(entry.Dir.Name);
                }

                try
                {
                    entry.Dir.Delete(recursive: true);
                    total -= entry.Size;
                    Log(() => $"segments: evicted {entry.Dir.Name} ({entry.Size / (1024 * 1024)} MB)");
                }
                catch (Exception ex)
                {
                    Log(() => $"segments: could not evict {entry.Dir.Name}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log(() => "segments: cache sweep failed", ex);
        }
    }

    private static string SegmentPath(Source source, int index) =>
        Path.Combine(source.Directory,
            string.Create(CultureInfo.InvariantCulture, $"seg{index}{SegmentRunRequest.SegmentExtension}"));

    /// <summary>The run's initialisation segment, written beside its first fragment.</summary>
    private static string InitPath(Source source) =>
        Path.Combine(source.Directory, SegmentRunRequest.InitSegmentName);

    /// <summary>Non-zero length. Exit 0 is not evidence the output is correct — only bytes are.</summary>
    private static bool NonEmpty(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary><c>seg12.m4s</c> → 12. False for anything else, including the manifest and the init segment.</summary>
    internal static bool TryParseSegmentIndex(string resource, out int index)
    {
        index = -1;
        if (!resource.StartsWith("seg", StringComparison.Ordinal)) return false;
        if (!resource.EndsWith(SegmentRunRequest.SegmentExtension, StringComparison.Ordinal)) return false;
        var digits = resource[3..^SegmentRunRequest.SegmentExtension.Length];
        return digits.Length > 0
            && digits.All(char.IsAsciiDigit)
            && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_log, message, exception: failure);

    /// <inheritdoc />
    public void Dispose()
    {
        List<Source> sources;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            sources = [.. _sources.Values];
            _sources.Clear();
        }

        foreach (var source in sources)
        {
            lock (source.Gate) StopRun(source);
        }
    }

    /// <summary>One source's cache entry and its live window.</summary>
    private sealed class Source
    {
        /// <summary>
        /// What the engine reads, however it arrives — a local file, a LAN share, a ranged remote fetch.
        /// <para>
        /// ⚠ <b>An address never reaches this type.</b> A remote source's url stays inside the app's own
        /// opener closure, so the only printable member is <see cref="MediaByteSource.Label"/> — the leak
        /// this used to guard against with prose is now unrepresentable. A test still plants a token in a
        /// url and reads the log back.
        /// </para>
        /// </summary>
        public required MediaByteSource Bytes { get; init; }

        /// <summary>The safe name for diagnostics: a file name, or the label the app registered.</summary>
        public string Label => Bytes.Label;

        public required string Directory { get; init; }
        public required TimeSpan Duration { get; init; }
        public required bool HasPicture { get; init; }

        /// <summary>
        /// Where this source's cuts are — computed ONCE, stated by the manifest and handed to every run.
        /// </summary>
        public required SegmentPlan Plan { get; init; }

        /// <summary>How many segments the manifest names. The plan's, so the two can never differ.</summary>
        public int SegmentCount => Plan.Count;

        /// <summary>The live production, or null when nothing is running.</summary>
        public ISegmentRun? Run { get; set; }

        /// <summary>The segment index <see cref="Run"/> started at; -1 when there is no window.</summary>
        public int WindowStart { get; set; } = -1;

        /// <summary>Highest segment seen on disk in the current window — the resume point for the walk.</summary>
        public int Newest { get; set; } = -1;

        /// <summary>Which engine candidate the current window is using — see <see cref="SegmentRunRequest"/>.</summary>
        public int Attempt { get; set; }

        /// <summary>
        /// True once this window's output has been shown to contain the picture the source has. Reset on
        /// every restart, because the next candidate has to prove itself too.
        /// </summary>
        public bool Verified { get; set; }

        /// <summary>When the current window started — the clock the picture grace is measured against.</summary>
        public DateTime WindowStartedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Serialises the window: one run per source, never two.</summary>
        public object Gate { get; } = new();
    }

    /// <summary>Removing the route and killing the windows are one operation, so they are one disposable.</summary>
    private sealed class Registration(IDisposable route, SegmentStream stream) : ISegmentStreamRoute
    {
        /// <inheritdoc />
        public bool IsComplete(string source) => stream.IsComplete(source);

        /// <inheritdoc />
        public Task<SegmentMergeResult> MergeAsync(string source, string destination,
                                                            CancellationToken cancellationToken = default)
            => stream.MergeAsync(source, destination, cancellationToken);

        public void Dispose()
        {
            try { route.Dispose(); } catch (Exception) { /* the pipeline is going away anyway */ }
            stream.Dispose();
        }
    }

    /// <summary>
    /// The cache entry for a source that has ALREADY been opened, or null. ⚠ Deliberately does not open
    /// one: completeness is a fact about produced output, and probing a source nobody asked for would make
    /// a question about the cache do a container read.
    /// </summary>
    private Source? Known(string source)
    {
        string key;
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists) return null;
            key = DerivedCacheKey.For(Path.GetFullPath(source), info.Length, info.LastWriteTimeUtc, "hls");
        }
        catch (Exception)
        {
            return null;
        }

        lock (_gate) return _sources.TryGetValue(key, out var found) ? found : null;
    }

    private bool IsComplete(string source)
        => Known(source) is { } entry && SegmentMerge.IsComplete(entry.Directory, entry.Plan);

    private async Task<SegmentMergeResult> MergeAsync(string source, string destination,
                                                               CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (Known(source) is not { } entry)
        {
            return new SegmentMergeResult(SegmentMergeOutcome.UnknownSource,
                "this route has not served that source, so nothing has been produced for it");
        }

        // 🔴 The one refusal that is a POLICY rather than a failure: the cache may evict anything and an
        // artifact may be evicted by nothing, so they cannot share a directory (D71).
        if (SegmentMerge.IsInside(destination, _options.Access.CacheRoot))
        {
            return new SegmentMergeResult(SegmentMergeOutcome.DestinationRefused,
                "the destination is inside the segment cache, which is swept oldest-used-first under a byte "
                + "cap — a persisted artifact must live somewhere nothing evicts");
        }

        if (!SegmentMerge.IsComplete(entry.Directory, entry.Plan))
        {
            return new SegmentMergeResult(SegmentMergeOutcome.Incomplete,
                $"not every one of the {entry.SegmentCount} segments has been produced yet");
        }

        try
        {
            await SegmentMerge.WriteAsync(SegmentMerge.Parts(entry.Directory, entry.Plan), destination,
                                             cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(() => "segments: could not merge a stream", ex);
            return new SegmentMergeResult(SegmentMergeOutcome.Failed,
                $"the artifact could not be written ({ex.GetType().Name})");
        }

        Log(() => $"segments: merged {entry.SegmentCount} segments into one file");
        return new SegmentMergeResult(SegmentMergeOutcome.Written,
            $"{entry.SegmentCount} segments written as one fragmented MP4");
    }
}

/// <summary>Wiring a segment-stream route onto an interceptor.</summary>
public static class SegmentStreamExtensions
{
    /// <summary>
    /// Serve a segmented stream through <paramref name="interceptor"/>, produced by an app-supplied engine.
    /// <para>
    /// Registers nothing when the engine reports <see cref="ISegmentEngine.IsAvailable"/> false — a shell
    /// with no engine should answer nothing rather than answer 503 forever, and returning a disposable that
    /// removes nothing keeps the call site identical on every platform.
    /// </para>
    /// </summary>
    /// <returns>
    /// Dispose to remove the route and kill every running production. ⚠ Also the handle for asking whether
    /// a stream has FINISHED and turning it into one file (D71's piece 5) — the app asks in .NET, exactly
    /// as it warms a computed-remux plan, so the page contract does not change.
    /// </returns>
    public static ISegmentStreamRoute UseSegmentStream(this IWebViewInterceptor interceptor, ISegmentEngine engine,
                                                       SegmentStreamOptions options, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        ArgumentNullException.ThrowIfNull(engine);
        return engine.IsAvailable
            ? SegmentStream.Use(interceptor, engine, options, log)
            : new NoRoute();
    }

    /// <summary>
    /// What a shell with no engine returns. ⚠ It answers NOT-complete rather than throwing: a platform
    /// without an engine has produced nothing, which is the same true answer the route gives for a source
    /// it has never served, and an app should not need a platform branch to ask the question.
    /// </summary>
    private sealed class NoRoute : ISegmentStreamRoute
    {
        public bool IsComplete(string source) => false;

        public Task<SegmentMergeResult> MergeAsync(string source, string destination,
                                                            CancellationToken cancellationToken = default)
            => Task.FromResult(new SegmentMergeResult(SegmentMergeOutcome.UnknownSource,
                "this shell registered no segment engine, so nothing has been produced"));

        public void Dispose() { }
    }
}
