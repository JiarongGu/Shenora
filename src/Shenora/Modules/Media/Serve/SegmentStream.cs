using System.Globalization;
using System.Text;
using Shenora;
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
    /// The app's map from a request URI to a source file, and the reason this route carries no layout
    /// knowledge of its own. Return null for "not mine" and the request falls through the pipeline.
    /// <para>
    /// The URI handed here has the RESOURCE stripped — it is the stream's own URL, so an app can reuse the
    /// resolver its other routes already use rather than writing a second one. ⚠ Two functions that must
    /// agree about where a file lives will eventually disagree, and the symptom is a download that succeeds
    /// and a stream that 404s.
    /// </para>
    /// </summary>
    public Func<Uri, string?> Resolve { get; init; } = static _ => null;

    /// <summary>
    /// The directories a source may come from. <b>Empty means nothing is servable</b>, deliberately: the
    /// PAGE supplies the path, and this is the same fail-closed rule the file route follows.
    /// </summary>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];

    /// <summary>
    /// Where produced segments live. Required.
    /// <para>
    /// 🔴 <b>This must NOT be the directory a finished conversion goes to, and the reason is a measured data
    /// loss rather than tidiness.</b> Segments are rebuildable from the original at any moment; a completed
    /// conversion backing an offline download is not — deleting it costs a user a file they waited for. On
    /// iOS <c>Library/Caches</c> may be PURGED by the OS under storage pressure, which is exactly the right
    /// thing to happen to one and exactly the wrong thing to happen to the other. Cache tenancy is two
    /// things; give them two roots.
    /// </para>
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Give this its OWN directory, never the conversion cache's.</b> Segments are rebuildable and a
    /// conversion backing an offline download is not, so the two want opposite answers when the OS reclaims
    /// space — and on iOS <c>Library/Caches</c> is purged whenever the system likes. Sharing a root makes
    /// that purge either a pointless rebuild or real data loss, depending which file it took.
    /// ⚠ A purged directory also 503s forever if it is not re-created per restart, because "the file is
    /// missing" reads as "the engine failed". Both halves were hit by the first adopter.
    /// </remarks>
    public required string CacheRoot { get; init; }

    /// <summary>The URL prefix this route answers. Relative, so one string works on every shell.</summary>
    public string RoutePath { get; init; } = "/shenora-hls/";

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
    private const string SegmentContentType = "video/mp2t";

    /// <summary>Poll interval while waiting for a segment to close.</summary>
    private const int PollMilliseconds = 50;

    private readonly ISegmentEngine _engine;
    private readonly SegmentStreamOptions _options;
    private readonly WebViewRangeDelivery _delivery;
    private readonly Action<string>? _log;

    /// <summary>Live sources by cache key. Guarded by <see cref="_gate"/>.</summary>
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);

    /// <summary>Last time each key was ASKED for, which is what LRU should order by — see the sweep.</summary>
    private readonly Dictionary<string, DateTime> _touched = new(StringComparer.Ordinal);

    private readonly object _gate = new();
    private bool _disposed;

    private SegmentStream(ISegmentEngine engine, SegmentStreamOptions options,
                          WebViewRangeDelivery delivery, Action<string>? log)
    {
        _engine = engine;
        _options = options;
        _delivery = delivery;
        _log = log;
    }

    /// <summary>
    /// Register the route. Dispose the result to remove it AND kill every running production — a window that
    /// outlives the page it was feeding is a process nobody is waiting for.
    /// </summary>
    /// <param name="interceptor">The shell's interceptor. Supplies the platform's range-delivery rule.</param>
    /// <param name="engine">The app's production engine.</param>
    /// <param name="options">The app's route, resolver and roots.</param>
    /// <param name="log">Optional diagnostics. Guarded — a throwing sink must not break serving.</param>
    internal static IDisposable Use(IWebViewInterceptor interceptor, ISegmentEngine engine,
                                  SegmentStreamOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CacheRoot);

        var stream = new SegmentStream(engine, options, interceptor.RangeDelivery, log);
        var route = interceptor.Use((request, next, cancellationToken) =>
        {
            // "Not mine" must fall through the REST of the pipeline, not terminate it.
            if (stream.Parse(request.Uri) is not { } parsed) return next(request, cancellationToken);
            return Task.FromResult<WebViewResourceResponse?>(
                stream.Answer(request, parsed.Source, parsed.Resource));
        });

        return new Registration(route, stream);
    }

    /// <summary>
    /// Split <c>{RoutePath}{whatever the app's layout is}/{resource}</c> into a source path and the resource
    /// asked for, or null when it is not ours.
    /// <para>
    /// Everything between the route prefix and the LAST segment is the app's to interpret, which is what
    /// keeps this route free of any layout knowledge: the kit knows only that the final segment names a
    /// resource, and hands the rest to <see cref="SegmentStreamOptions.Resolve"/>.
    /// </para>
    /// </summary>
    private (string Source, string Resource)? Parse(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return null;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!path.StartsWith(_options.RoutePath, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = path[_options.RoutePath.Length..];
        var slash = rest.LastIndexOf('/');
        // At least one leading part AND a resource; anything else is not a shape this route serves.
        if (slash <= 0 || slash == rest.Length - 1) return null;

        var resource = rest[(slash + 1)..];
        var sourceUri = new UriBuilder(uri)
        {
            Path = _options.RoutePath + rest[..slash],
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;

        return _options.Resolve(sourceUri) is { } source ? (source, resource) : null;
    }

    /// <summary>Answer one request: the manifest, a segment, or a refusal.</summary>
    private WebViewResourceResponse Answer(WebViewResourceRequest request, string requested, string resource)
    {
        // Containment runs BEFORE the filesystem is touched, and a refusal is the same 404 as a missing file
        // so nothing can probe for existence by comparing responses.
        if (WebViewFiles.ResolveContained(requested, _options.AllowedRoots) is not { } contained)
        {
            Log(() => "segments: refused a source outside the allowed roots");
            return WebViewResourceResponse.NotFound();
        }

        FileInfo info;
        try
        {
            info = new FileInfo(contained);
            if (!info.Exists) return WebViewResourceResponse.NotFound();
        }
        catch (Exception ex)
        {
            // No exception text on the wire, ever — a path is the likeliest thing it would carry.
            Log(() => $"segments: could not stat a source ({ex.GetType().Name})");
            return WebViewResourceResponse.NotFound();
        }

        var source = OpenSource(contained, info);
        if (source is null) return WebViewResourceResponse.NotFound();

        if (resource.Equals(_options.ManifestName, StringComparison.OrdinalIgnoreCase))
            return WebViewResourceResponse.Bytes(Encoding.UTF8.GetBytes(Manifest(source)), ManifestContentType);

        if (!TryParseSegmentIndex(resource, out var index) || index < 0 || index >= source.SegmentCount)
            return WebViewResourceResponse.NotFound();

        return EnsureSegment(source, index)
            ? WebViewFiles.Serve(request, SegmentPath(source, index), SegmentContentType, _delivery)
            : NotReadyYet();
    }

    /// <summary>
    /// The source's cache entry, probing it the first time it is seen.
    /// <para>
    /// ⚠ <b>This probes ON THE REQUEST PATH</b>, which the kit's other routes refuse to do — and it is
    /// unavoidable here rather than sloppy: the manifest IS the answer to the first request, and it cannot be
    /// written without the duration. It costs a container-header read, once per source per process.
    /// </para>
    /// </summary>
    private Source? OpenSource(string path, FileInfo info)
    {
        // Identity+length+mtime: replacing the source invalidates its segments rather than serving
        // yesterday's.
        var key = DerivedCacheKey.For(path, info.Length, info.LastWriteTimeUtc, "hls");

        lock (_gate)
        {
            if (_disposed) return null;
            _touched[key] = DateTime.UtcNow;
            if (_sources.TryGetValue(key, out var existing)) return existing;

            if (_engine.DurationOf(path) is not { } duration || duration <= TimeSpan.Zero)
            {
                Log(() => $"segments: no duration for {Path.GetFileName(path)} — refusing");
                return null;
            }

            var directory = Path.Combine(_options.CacheRoot, key);
            System.IO.Directory.CreateDirectory(directory);

            var source = new Source
            {
                Path = path,
                Directory = directory,
                Duration = duration,
                SegmentCount = (int)Math.Ceiling(duration.TotalSeconds / _options.SegmentSeconds),
                HasPicture = _engine.HasPicture(path),
            };
            DropUnfinishedTail(source);
            _sources[key] = source;

            Log(() => $"segments: {Path.GetFileName(path)} {duration.TotalSeconds:0.00}s"
                    + $" -> {source.SegmentCount} segments (picture={source.HasPicture})");

            // Off the request path: the sweep stats every cached directory, and a request should never pay
            // for housekeeping that a previous request created the need for.
            _ = Task.Run(() => SweepCache(key));
            return source;
        }
    }

    /// <summary>
    /// The synthetic VOD playlist. Every segment named before any exists.
    /// <para>
    /// ⚠ The tail carries the REAL remainder rather than a flat segment length. The grid is fixed everywhere
    /// else, but a playlist's declared total is the sum of its <c>EXTINF</c>s — a flat last entry would
    /// overstate the source by up to one whole segment, and a scrub bar built on it seeks past the end.
    /// </para>
    /// <para>
    /// The engine may write ONE segment past the last one named here — an AAC encoder adds priming, so the
    /// encoded stream runs a frame or two longer than the source. It is never asked for, because a request
    /// outside <see cref="Source.SegmentCount"/> is refused.
    /// </para>
    /// </summary>
    private string Manifest(Source source)
    {
        var builder = new StringBuilder();
        builder.Append("#EXTM3U\n");
        builder.Append("#EXT-X-VERSION:3\n");
        builder.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
        builder.Append(CultureInfo.InvariantCulture,
            $"#EXT-X-TARGETDURATION:{(int)Math.Ceiling(_options.SegmentSeconds)}\n");
        builder.Append("#EXT-X-MEDIA-SEQUENCE:0\n");

        for (var i = 0; i < source.SegmentCount; i++)
        {
            var remaining = source.Duration.TotalSeconds - (i * _options.SegmentSeconds);
            var seconds = Math.Max(0.001, Math.Min(_options.SegmentSeconds, remaining));
            builder.Append(CultureInfo.InvariantCulture, $"#EXTINF:{seconds:0.000},\n");
            builder.Append(CultureInfo.InvariantCulture, $"seg{i}.ts\n");
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
    /// Is the segment on disk AND finished?
    /// <para>
    /// ⚠ <b>Existing is not finished.</b> A segment muxer writes each piece progressively, so the file a
    /// request finds may be half a segment. The next one opening is the signal that this one closed; for the
    /// final segment nothing follows, so the run exiting is. Serving on existence alone truncates the first
    /// segment of every stream — the kind of failure that plays for two seconds and then stops.
    /// </para>
    /// </summary>
    private static bool IsComplete(Source source, int index)
    {
        var path = SegmentPath(source, index);
        if (!NonEmpty(path)) return false;
        if (File.Exists(SegmentPath(source, index + 1))) return true;
        // Nothing is writing it: whatever is there is all there will ever be.
        return source.Run is null || source.Run.HasExited;
    }

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
    /// A window that has been running past its grace period, has written something, and still has no picture
    /// in it. True means the ladder was advanced and the caller should loop.
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
            Log(() => $"segments: could not re-create the cache directory ({ex.GetType().Name})");
        }

        var run = _engine.Start(new SegmentRunRequest(
            source.Path, source.Directory, source.HasPicture, index, _options.SegmentSeconds, source.Attempt));
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
    /// Delete the highest-numbered segment the first time a source is opened in this process.
    /// <para>
    /// ⚠ The app can be killed mid-write, and what that leaves behind is a segment file that EXISTS, is
    /// non-empty and is truncated — which <see cref="IsComplete"/> would then accept forever, because the
    /// process that could have said otherwise is gone. Dropping exactly one file per source costs one
    /// segment of re-encoding and closes the hole.
    /// </para>
    /// </summary>
    private void DropUnfinishedTail(Source source)
    {
        try
        {
            var highest = -1;
            foreach (var file in System.IO.Directory.EnumerateFiles(source.Directory, "seg*.ts"))
            {
                if (TryParseSegmentIndex(Path.GetFileName(file), out var index) && index > highest) highest = index;
            }

            if (highest < 0) return;
            File.Delete(SegmentPath(source, highest));
            Log(() => $"segments: dropped seg{highest} — it may have been truncated by a kill");
        }
        catch (Exception ex)
        {
            Log(() => $"segments: could not sweep an unfinished tail ({ex.GetType().Name})");
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
            var root = new DirectoryInfo(_options.CacheRoot);
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
                    Log(() => $"segments: could not evict {entry.Dir.Name} ({ex.GetType().Name})");
                }
            }
        }
        catch (Exception ex)
        {
            Log(() => $"segments: cache sweep failed ({ex.GetType().Name})");
        }
    }

    private static string SegmentPath(Source source, int index) =>
        Path.Combine(source.Directory, string.Create(CultureInfo.InvariantCulture, $"seg{index}.ts"));

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

    /// <summary><c>seg12.ts</c> → 12. False for anything else, including the manifest.</summary>
    internal static bool TryParseSegmentIndex(string resource, out int index)
    {
        index = -1;
        if (!resource.StartsWith("seg", StringComparison.Ordinal)) return false;
        if (!resource.EndsWith(".ts", StringComparison.Ordinal)) return false;
        var digits = resource[3..^3];
        return digits.Length > 0
            && digits.All(char.IsAsciiDigit)
            && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    /// <summary>
    /// The answer while a segment is still being produced: <c>503</c> with <c>Retry-After</c>. 503 rather
    /// than 404, because the distinction is real and a player can act on it — the segment is not missing, it
    /// is not ready, and a 404 tells a player to give up permanently.
    /// </summary>
    private static WebViewResourceResponse NotReadyYet() => new()
    {
        Content = new MemoryStream([], writable: false),
        StatusCode = 503,
        ReasonPhrase = "Service Unavailable",
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Retry-After"] = "1",
            ["Cache-Control"] = "no-store",
        },
    };

    private void Log(Func<string> message) => AppCallback.Log(_log, message);

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
        public required string Path { get; init; }
        public required string Directory { get; init; }
        public required TimeSpan Duration { get; init; }
        public required int SegmentCount { get; init; }
        public required bool HasPicture { get; init; }

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
    private sealed class Registration(IDisposable route, SegmentStream stream) : IDisposable
    {
        public void Dispose()
        {
            try { route.Dispose(); } catch (Exception) { /* the pipeline is going away anyway */ }
            stream.Dispose();
        }
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
    /// <returns>Dispose to remove the route and kill every running production.</returns>
    public static IDisposable UseSegmentStream(this IWebViewInterceptor interceptor, ISegmentEngine engine,
                                               SegmentStreamOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        ArgumentNullException.ThrowIfNull(engine);
        return engine.IsAvailable
            ? SegmentStream.Use(interceptor, engine, options, log)
            : new NoRoute();
    }

    private sealed class NoRoute : IDisposable
    {
        public void Dispose() { }
    }
}
