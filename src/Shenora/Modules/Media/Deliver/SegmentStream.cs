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
    /// events publish on. <see cref="MediaAccessOptions.Resolve"/> is handed the stream's own URL, with the
    /// RESOURCE stripped.
    /// <para>
    /// 🔴 <b><see cref="MediaAccessOptions.CacheRoot"/> must NOT be the directory a finished conversion goes
    /// to</b> — measured data loss. Segments are rebuildable; a completed conversion backing an offline
    /// download is not, and on iOS <c>Library/Caches</c> may be PURGED by the OS under storage pressure.
    /// ⚠ A purged directory also 503s for ever unless it is re-created per restart.
    /// </para>
    /// </summary>
    public required MediaAccessOptions Access { get; init; }

    /// <summary>The URL prefix this route answers. Relative, so one string works on every shell.</summary>
    public string RoutePath { get; init; } = "/shenora-hls/";

    /// <summary>
    /// Remote sources this route may stream, each addressable only by a handle the APP issued. 🔴 Null —
    /// the default — serves local files only.
    /// </summary>
    /// <remarks>
    /// ⚠ A registered source bypasses <see cref="MediaAccessOptions.AllowedRoots"/> entirely: containment
    /// answers "may this PATH be read", and the boundary for a remote source is instead that the page cannot
    /// name one the app did not register (<see cref="MediaSourceRegistry"/>).
    /// </remarks>
    public MediaSourceRegistry? Sources { get; init; }

    /// <summary>
    /// The path segment that means "an issued handle follows": <c>{RoutePath}~remote/{handle}/{resource}</c>.
    /// <para>
    /// ⚠ RESERVED, and checked before <see cref="MediaAccessOptions.Resolve"/> is consulted, so a handle can
    /// never collide with a path an app's own resolver would have matched.
    /// </para>
    /// </summary>
    public const string RemotePrefix = "~remote/";

    /// <summary>The manifest's name under a source: <c>{RoutePath}{app-part}/index.m3u8</c>.</summary>
    public string ManifestName { get; init; } = "index.m3u8";

    /// <summary>The segment grid, in seconds — used by the manifest AND by the engine, so one number.</summary>
    public double SegmentSeconds { get; init; } = 6.0;

    /// <summary>
    /// Lengths for the FIRST segments, before <see cref="SegmentSeconds"/> takes over — a short head so
    /// playback starts sooner. Empty for a uniform stream; design in <c>docs/design/media.md</c>.
    /// <para>
    /// ⚠ <b>It is a REQUEST.</b> A copied picture is cut where the SOURCE has keyframes, so a ten-second GOP
    /// gives a ten-second first segment however short this asks for. Each entry must be a whole multiple of
    /// the encoders' one-second keyframe interval and no longer than <see cref="SegmentSeconds"/>; both are
    /// refused at composition time rather than discovered by a seek.
    /// </para>
    /// </summary>
    public IReadOnlyList<double> HeadSegmentSeconds { get; init; } = [1.0, 2.0, 4.0];

    /// <summary>The head and the steady length as the engine takes them.</summary>
    internal SegmentLengths Lengths => new(SegmentSeconds, HeadSegmentSeconds ?? []);

    /// <summary>
    /// The cache ceiling, swept oldest-USED first when a new source is opened — never on the request path.
    /// </summary>
    public long CacheCapBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// How long a segment request may wait before answering <c>503</c>.
    /// <para>
    /// ⚠ This blocks the platform's resource thread, which every shell resolves SYNCHRONOUSLY, so the budget
    /// is a real cost. Only a seek pays it: the window runs AHEAD of sequential playback.
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
    /// </summary>
    public int Lookahead { get; init; } = 2;
}

/// <summary>
/// Playing a source the webview cannot decode <b>without converting the whole thing first</b>: a segmented
/// stream, produced on the device, one piece at a time, served over the interceptor. Design:
/// <c>docs/design/media.md</c>.
/// <para>
/// ⚠ <b>The manifest is SYNTHETIC — computed from the source's DURATION alone, before any segment exists</b>,
/// which is what makes the scrub bar the right length and a seek to minute 40 expressible.
/// </para>
/// </summary>
internal sealed class SegmentStream : IDisposable
{
    private const string ManifestContentType = "application/vnd.apple.mpegurl";
    /// <summary>fMP4, so the segments and the init segment are the same container.</summary>
    private const string SegmentContentType = "video/mp4";

    /// <summary>Poll interval while waiting for a segment to close.</summary>
    private const int PollMilliseconds = 50;

    private readonly ISegmentEngine _engine;
    private readonly SegmentStreamOptions _options;
    private readonly WebViewRangeDelivery _delivery;
    private readonly ILogger? _log;

    /// <summary>Live sources by cache key. Guarded by <see cref="_gate"/>.</summary>
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);

    /// <summary>Last time each key was ASKED for — what the sweep's LRU orders by.</summary>
    private readonly Dictionary<string, DateTime> _touched = new(StringComparer.Ordinal);

    private readonly object _gate = new();
    private bool _disposed;

    private SegmentStream(ISegmentEngine engine, SegmentStreamOptions options,
                          WebViewRangeDelivery delivery, ILogger? log)
    {
        _engine = engine;
        _options = options;
        _delivery = delivery;
        // Falls back to the SHARED sink: an absent log is indistinguishable from a quiet one.
        _log = log ?? options.Access.Log;
    }

    /// <summary>
    /// Register the route. Dispose the result to remove it AND kill every running production.
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SegmentSeconds);
        // ⚠ Refused, never rounded: a head boundary the encoder has no keyframe at produces segments that
        // PLAY and misbehave only when somebody seeks.
        if (!options.Lengths.IsUsable(out var badLength)) throw new ArgumentException(badLength, nameof(options));

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
    /// Split <c>{RoutePath}{the app's own layout}/{resource}</c> into a source path and the resource asked
    /// for, or null when it is not ours. Everything before the LAST segment goes to
    /// <see cref="MediaAccessOptions.Resolve"/>, so this route carries no layout knowledge.
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
        // app would have matched. An unissued handle falls through as "not mine" rather than 404ing.
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

    /// <summary>What a request named — a resolved path or a registered remote source, exactly one.</summary>
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

        // 🔴 THE INIT SEGMENT IS PRODUCED, NOT STORED — the engine writes it beside its FIRST fragment, so a
        // page following `#EXT-X-MAP` asks for it before anything exists and must be told "not yet".
        if (resource.Equals(SegmentRunRequest.InitSegmentName, StringComparison.OrdinalIgnoreCase))
        {
            // Asking for it drives segment 0, exactly as a request for seg0 would.
            return EnsureSegment(source, 0) && File.Exists(InitPath(source))
                ? WebViewFiles.Serve(request, InitPath(source), SegmentContentType, _delivery)
                : MediaConversionExtensions.NotReadyYet();
        }

        if (!TryParseSegmentIndex(resource, out var index) || index < 0 || index >= source.SegmentCount)
            return WebViewResourceResponse.NotFound();

        return EnsureSegment(source, index)
            ? WebViewFiles.Serve(request, SegmentPath(source, index), SegmentContentType, _delivery)
            : MediaConversionExtensions.NotReadyYet();
    }

    /// <summary>
    /// The source's cache entry, probed and PLANNED the first time it is seen.
    /// <para>
    /// ⚠ This probes ON THE REQUEST PATH, unlike the kit's other routes: the manifest IS the answer to the
    /// first request, and it cannot be written without the duration and the boundaries (D76).
    /// </para>
    /// </summary>
    private Source? OpenLocal(string requested, CancellationToken cancellationToken)
    {
        // Containment runs BEFORE the filesystem is touched; a refusal is the same 404 as a missing file.
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

        // Identity+length+mtime, so replacing the source invalidates its segments.
        var key = DerivedCacheKey.For(contained, info.Length, info.LastWriteTimeUtc, "hls");
        return OpenSource(key, MediaByteSource.ForFile(contained), null, null, cancellationToken);
    }

    /// <summary>
    /// A source the app registered. No containment — see <see cref="SegmentStreamOptions.Sources"/> — and
    /// no <c>stat</c>, because neither length nor mtime is knowable without fetching.
    /// </summary>
    private Source? OpenRemote(RemoteMediaSource remote, CancellationToken cancellationToken)
    {
        // ⚠ Keyed on IDENTITY, falling back to the url — see RemoteMediaSource.Identity.
        var key = DerivedCacheKey.For(
            remote.Identity ?? remote.Url.AbsoluteUri, 0, DateTime.UnixEpoch, "hls-remote");

        // 🔴 No opener, no bytes: this route can serve a manifest for such a source and never a segment.
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
            if (_sources.TryGetValue(key, out var existing))
            {
                // 🔴 ADOPT THE NEW OPENER. `RemoteMediaSource.Identity` exists precisely so the cache key
                // stays STABLE while the url rotates — and this used to return the cached source and drop
                // the freshly supplied `bytes`, so an app following that instruction kept the EXPIRED
                // presigned url for the life of the process and every later read 503'd with nothing in
                // the log naming a stale url. Only the opener changes; the plan, the directory and
                // everything already produced stay, which is the whole point of a stable identity.
                if (!ReferenceEquals(existing.Bytes, bytes))
                {
                    existing.Bytes = bytes;
                    Log(() => $"segments: refreshed the opener for {label} (same identity, new source)");
                }
                return existing;
            }
        }

        // ⚠ The caller's value wins: a supplied duration is the app's own claim, and it saves a probe launch.
        if ((knownDuration ?? _engine.DurationOf(bytes)) is not { } duration || duration <= TimeSpan.Zero)
        {
            Log(() => $"segments: no duration for {label} — refusing");
            return null;
        }

        // 🔴 ONE plan object, used by the manifest AND handed to every run: boundaries computed twice would
        // disagree silently — the bytes stay valid and a seek lands at the wrong moment.
        var lengths = _options.Lengths;
        var plan = _engine.PlanSegments(bytes, lengths, cancellationToken)
                   ?? (lengths.Head.Count > 0 ? SegmentPlan.EncoderCuts(lengths.StartsFor(duration), duration) : null)
                   ?? SegmentPlan.Grid(lengths.Seconds, duration);
        var hasPicture = knownPicture ?? _engine.HasPicture(bytes);

        lock (_gate)
        {
            if (_disposed) return null;
            // ⚠ Re-checked: the planning above runs OUTSIDE this lock, so two first-requests for one source
            // may both plan — a duplicated walk, against stalling every other stream for seconds.
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

            // Off the request path: the sweep stats every cached directory.
            _ = Task.Run(() => SweepCache(key));
            return source;
        }
    }

    /// <summary>
    /// The synthetic VOD playlist. Every segment named before any exists, each with the length the PLAN says
    /// it has — including the tail's REAL remainder, since a playlist's declared total is the sum of its
    /// <c>EXTINF</c>s and a scrub bar built on a flat last entry seeks past the end.
    /// </summary>
    private static string Manifest(Source source)
    {
        var builder = new StringBuilder();
        builder.Append("#EXTM3U\n");
        // 🔴 VERSION 7 because `#EXT-X-MAP` is illegal below 6: a reader honouring a lower declared version
        // skips the one line without which no segment can be decoded.
        builder.Append("#EXT-X-VERSION:7\n");
        builder.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
        // 🔴 The LONGEST segment, not the length that was asked for: a TARGETDURATION below any EXTINF
        // breaks a MUST of the playlist spec, and a strict reader may refuse a stream whose bytes are fine.
        builder.Append(CultureInfo.InvariantCulture,
            $"#EXT-X-TARGETDURATION:{(int)Math.Ceiling(source.Plan.LongestSeconds)}\n");
        builder.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
        // The initialisation segment: the tracks and their decoder configuration.
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
    /// Serialised per source, so two requests cannot each undo the other's restart.
    /// </summary>
    private bool EnsureSegment(Source source, int index)
    {
        var deadline = DateTime.UtcNow + _options.WaitBudget;

        lock (source.Gate)
        {
            while (true)
            {
                // The verification can REJECT what is on disk, so it sits between "the file is there" and
                // "serve it" rather than after the loop.
                if (IsComplete(source, index) && VerifyPicture(source, SegmentPath(source, index)))
                {
                    // Record that this window HAS produced — what Restart's ladder bump reads.
                    if (index >= source.WindowStart) source.Newest = Math.Max(source.Newest, index);
                    return true;
                }

                // ⚠ The same check on a segment that has NOT closed yet: an encoder writing no frames means
                // the muxer never rotates, so segment 0 grows to hold the WHOLE source and every request
                // stalls until the budget expires with the ladder never advancing.
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
    /// ⚠ Non-empty, not merely present: a rename cannot produce a zero-length file, but a foreign engine
    /// ignoring the contract can.
    /// </para>
    /// </summary>
    private static bool IsComplete(Source source, int index) => NonEmpty(SegmentPath(source, index));

    /// <summary>
    /// For a source WITH a picture: does the segment actually contain one? One probe per WINDOW.
    /// <para>
    /// 🔴 <b>Non-empty is not enough, and "exit 0" is worth even less.</b> A hardware encoder can accept
    /// every frame, write <c>video:0KiB</c> and exit 0, so a segmenter fed by it produces a perfectly
    /// playable AUDIO-ONLY stream for a video source and reports success. Only the output is evidence.
    /// </para>
    /// </summary>
    private bool VerifyPicture(Source source, string path)
    {
        if (!source.HasPicture || source.Verified) return true;

        // ⚠ HasRENDEREDPicture, not HasPicture: the segment declares a video stream either way, so asking
        // the source's question here passes picture-less output and ships an audio-only video stream.
        if (_engine.HasRenderedPicture(path))
        {
            source.Verified = true;
            return true;
        }

        // Drop it — left in place it is a cache hit every later request serves — and advance the ladder.
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
    /// ⚠ It cannot see a run that has published NOTHING at all — a <c>.part</c> is never judged, since
    /// mid-write "no picture yet" and "no picture ever" are the same bytes. That case falls to
    /// <see cref="SegmentStreamOptions.WaitBudget"/> and answers <c>503</c> without advancing the ladder.
    /// </para>
    /// </summary>
    private bool StalledWithoutPicture(Source source)
    {
        if (!source.HasPicture || source.Verified || source.Run is null) return false;
        if (DateTime.UtcNow - source.WindowStartedUtc < _options.PictureGrace) return false;

        var first = SegmentPath(source, source.WindowStart);
        if (!NonEmpty(first)) return false;   // nothing written yet is not evidence of anything

        return !VerifyPicture(source, first);
    }

    /// <summary>
    /// Why waiting for this segment would be waiting for something that is not coming — or null to keep
    /// waiting. A reason rather than a bool because "it restarted" with no cause makes a stall unreadable.
    /// </summary>
    private string? RestartReason(Source source, int index)
    {
        if (source.Run is null) return "nothing is producing";
        // Behind the window: this run numbers from WindowStart and will never go back.
        if (index < source.WindowStart) return $"seek back inside the window from seg{source.WindowStart}";
        // The run is over. Anything it did not write, it never will.
        if (source.Run.HasExited)
            // 🔴 `IsComplete`, not `File.Exists`. The reader requires a NON-EMPTY file, so a zero-byte
            // segment left behind by a run that died mid-write satisfied `Exists`, this answered "keep
            // waiting", and the stream wedged at 503 for ever — burning the full wait budget on a mobile
            // platform thread, inside the source gate, on every single request. The two tests have to be
            // the same test.
            return IsComplete(source, index) ? null : "the run ended without writing it";
        // Far enough ahead that waiting means encoding everything in between — seek instead.
        var newest = NewestProduced(source);
        return index > newest + _options.Lookahead ? $"seek forward past seg{newest}" : null;
    }

    /// <summary>
    /// The highest segment the current window has reached, or <c>WindowStart - 1</c> when it has written
    /// nothing yet. Contiguous from the window start, because the muxer writes in order; resumes from the
    /// last answer rather than re-walking, since this is called on every poll of a wait loop.
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
        // A run that died at this window HAVING PRODUCED NOTHING indicts the ENCODER, so advance the ladder.
        // ⚠ "Produced nothing" is `Newest < WindowStart`, NOT "the file is missing" — a measured bug: a run
        // that finished the whole source and had its cache purged afterwards also has no file at the index,
        // and reading that as an encoder failure walks a WORKING ladder off its end into a permanent 503.
        if (source.Run is not null && source.WindowStart == index && source.Newest < source.WindowStart)
            source.Attempt++;

        StopRun(source);

        // ⚠ Re-created every time, not once at OpenSource: this is a CACHE directory and iOS purges it under
        // storage pressure. Measured — with the directory gone the engine died on every restart, the ladder
        // ran out of candidates, and the route answered 503 for ever with nothing in the log to say why.
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
            // Start the ladder over, so a transient failure heals instead of leaving the source unservable.
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
    /// Delete anything a killed process left half-written, the first time a source is opened here. Under the
    /// atomic-publish contract that is exactly the <c>.part</c> files
    /// (<see cref="SegmentRunRequest.PartialExtension"/>) — a rename either happened or it did not, so no
    /// FINAL name can be truncated and none has to be guessed at.
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
    /// Keep the cache under <see cref="SegmentStreamOptions.CacheCapBytes"/>, ordered by when a key was last
    /// ASKED for rather than by file mtime — a fully-produced stream played daily never has a byte written
    /// to it. Runs on a background thread, never on the request path.
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
        /// ⚠ An address never reaches this type: a remote source's url stays inside the app's own opener
        /// closure, so the only printable member is <see cref="MediaByteSource.Label"/>.
        /// </summary>
        /// <remarks>⚠ SETTABLE so a re-registration under the same identity can swap in a fresh opener —
        /// a rotated presigned url. Only ever replaced wholesale, and only under the registry's gate.</remarks>
        public required MediaByteSource Bytes { get; set; }

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
        /// every restart: the next candidate has to prove itself too.
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
    /// The cache entry for a source that has ALREADY been opened, or null. ⚠ Never opens one — a question
    /// about the cache must not do a container read.
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
    /// ⚠ Registers NOTHING when <see cref="ISegmentEngine.IsAvailable"/> is false, so a shell with no engine
    /// answers nothing rather than 503 for ever.
    /// </summary>
    /// <returns>
    /// Dispose to remove the route and kill every running production. Also the handle for asking whether a
    /// stream has FINISHED and turning it into one file (D71).
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
    /// What a shell with no engine returns. ⚠ Answers NOT-complete rather than throwing, so an app needs no
    /// platform branch to ask the question.
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
