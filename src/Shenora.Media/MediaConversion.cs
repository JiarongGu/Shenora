using Shenora.Core;

namespace Shenora.Media;

/// <summary>
/// What the app's engine is being asked to produce: read <see cref="SourcePath"/>, write a playable file at
/// <see cref="DestinationPath"/>, report progress.
/// </summary>
/// <param name="SourcePath">The original file. Already authorised against the allowed roots.</param>
/// <param name="DestinationPath">
/// Where to write. ⚠ A TEMPORARY path — the kit swaps it into place only once the delegate returns without
/// throwing (<see cref="Files.BeginReplace"/>), so a cancelled or failed conversion can never leave a
/// half-written file where a later request would serve it as a cache hit.
/// </param>
/// <param name="Progress">
/// Fraction complete, 0…1, forwarded to the page as <see cref="MediaConversionEvents.SourceProgress"/>.
/// Optional to call: a converter with no usable progress simply never reports, and the page sees
/// <see cref="MediaConversionEvents.Ready"/> when it finishes.
/// </param>
public sealed record MediaConversionRequest(string SourcePath, string DestinationPath, IProgress<double> Progress);

/// <summary>Event types this middleware publishes, on <see cref="MediaConversionOptions.Module"/>.</summary>
public static class MediaConversionEvents
{
    /// <summary>Fraction complete: <c>{ source, progress }</c>. Throttle in the app if the engine is chatty.</summary>
    public const string SourceProgress = "SOURCE_PROGRESS";

    /// <summary>The converted file is servable: <c>{ source }</c>. The page may set its element's src now.</summary>
    public const string Ready = "READY";

    /// <summary>Conversion failed: <c>{ source, reason }</c>. <c>reason</c> is a TYPE name, never exception text.</summary>
    public const string Failed = "FAILED";
}

/// <summary>
/// Inputs for <see cref="MediaConversionExtensions.UseMediaConversion"/>.
/// </summary>
/// <remarks>
/// ⚠ Note what is NOT here: no probe, no codec policy, and no engine. **Whether a source needs converting
/// is the APP's decision**, made before it builds the URL — that is what <c>MediaPlaybackPlanner</c> is for,
/// and a source that plays directly should be pointed at <c>UseFiles</c> instead. Putting the decision here
/// would mean probing on the request path, which is a process launch inside a webview callback (see the
/// extension's remarks), and it would move the codec policy into the kit, which D42 keeps with the app.
/// </remarks>
public sealed class MediaConversionOptions
{
    /// <summary>
    /// Map a request to the SOURCE file it names. Return null for "not a conversion request" and the
    /// pipeline falls through. Whatever this returns is still authorised against
    /// <see cref="AllowedRoots"/>, so being generous here cannot widen what is reachable.
    /// </summary>
    public required Func<Uri, string?> Resolve { get; init; }

    /// <summary>
    /// Produce a playable file. <b>This is the app's engine</b> — the kit ships none and never vendors one
    /// (D42), because the right encoder differs per app and a bundled one is tens of megabytes every
    /// consumer pays for.
    /// <para>
    /// Runs inside a mission, so it may take minutes; it is never on the request path. It receives its OWN
    /// cancellation token, and honouring it is what makes shutdown prompt.
    /// </para>
    /// </summary>
    public required Func<MediaConversionRequest, CancellationToken, Task> Convert { get; init; }

    /// <summary>Directory for converted output. Created on demand; safe to delete wholesale, it is a cache.</summary>
    public required string CacheRoot { get; init; }

    /// <summary>
    /// Roots a source may come from. <b>Empty means NOTHING is servable</b> — fail-closed, the same rule
    /// <c>WebViewFileOptions</c> follows, because the page supplies the path.
    /// </summary>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];

    /// <summary>
    /// Extension for converted output, WITH the dot. It decides the served <c>Content-Type</c>, so it must
    /// match what <see cref="Convert"/> actually writes — a `.mp4` name on a WebM body is a file no
    /// <c>&lt;video&gt;</c> will play, and the failure looks like a broken converter rather than a wrong
    /// name here.
    /// </summary>
    public string CacheExtension { get; init; } = ".mp4";

    /// <summary>Override the content type derived from <see cref="CacheExtension"/>. Rarely needed.</summary>
    public Func<string, string>? ContentType { get; init; }

    /// <summary>
    /// May the app's engine read this REMOTE url on the page's behalf? <b>Fail-CLOSED: null refuses every
    /// remote source</b>, and so does a policy that throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>This is an SSRF boundary, and the asymmetry is the whole reason it exists: the HOST can reach
    /// addresses the PAGE cannot.</b> The page supplies the source, so without a policy it could name
    /// <c>http://169.254.169.254/</c>, a container-internal service, or anything else behind the machine —
    /// and the engine would fetch it with the host's own network position. Refusing by default means an app
    /// that never thought about this is safe rather than exposed; a throwing policy is treated as a refusal
    /// for the same reason a failed check is not a pass.
    /// </para>
    /// <para>
    /// <b>The kit never fetches.</b> It decides, and the app's <see cref="Convert"/> engine does the reading
    /// (ffmpeg and friends open URLs natively). That keeps an HTTP client out of this package and leaves the
    /// credentials, proxy and retry questions where they belong.
    /// </para>
    /// <para>
    /// ⚠ <b>Synchronous, unlike <c>InteractiveSession.NavigationGuard</c>'s async shape, and deliberately.</b>
    /// This runs on the resource path, which the mobile shells resolve SYNCHRONOUSLY — an async policy doing
    /// a DNS or directory lookup would block a webview callback on the network. A policy that needs I/O must
    /// precompute: resolve its allow-list at startup and consult it in memory here.
    /// </para>
    /// <para>
    /// ⚠ <b>A remote source is cached by its URL alone</b>, because nothing else is knowable without
    /// fetching it — unlike a local file, which is keyed by identity+length+mtime. So a url whose CONTENT
    /// can change while its address stays the same will serve a stale conversion. Version or
    /// content-address your urls, the way a CDN does.
    /// </para>
    /// </remarks>
    public Func<Uri, bool>? AllowRemoteSource { get; init; }

    /// <summary>Module the progress events are published on. Defaults to <c>MEDIA</c>.</summary>
    public string Module { get; init; } = "MEDIA";

    /// <summary>Diagnostics. Guarded — a throwing sink must not break serving.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Serving media the platform cannot decode: convert once, cache the result, serve it with ranges.
/// </summary>
public static class MediaConversionExtensions
{
    /// <summary>
    /// Register the conversion route. Layered OVER the file middleware rather than replacing it — a cache
    /// hit is served by exactly the same range-correct code path as any other local file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything slow happens in the MISSION, and that is forced rather than chosen.</b> The mobile
    /// interceptor resolves SYNCHRONOUSLY — both platforms need the status line and headers by the time the
    /// event returns — so this middleware can neither await a conversion nor probe on the request path. A
    /// process launch per request would be a webview callback blocked on an external tool. What the request
    /// path does is: resolve, authorise, compute the cache key, and either serve a hit or start the mission
    /// and answer immediately.
    /// </para>
    /// <para>
    /// <b>A miss answers <c>503</c> with <c>Retry-After</c>, and the page is expected to be event-driven.</b>
    /// It learns from <see cref="MediaConversionEvents.Ready"/> when to set its element's source, which is
    /// what the notification pipe was always for. A media element pointed at this URL before the file exists
    /// will error, and that is the honest outcome — the alternative is holding a webview callback open for
    /// minutes.
    /// </para>
    /// <para>
    /// <b>Composed, not built</b> — every hard part already existed:
    /// <see cref="IMissionScheduler"/> runs it without a thread of its own,
    /// <see cref="PathClaims.Exclusive(string)"/> means one source converts once even if twenty requests
    /// arrive, <see cref="MissionDefinition.Key"/> deduplicates the submissions themselves,
    /// <see cref="Files.BeginReplace"/> makes the output atomic so an interrupted run cannot leave a
    /// half-file that a later request would serve as a hit, and
    /// <see cref="DerivedCacheKey.For"/> keys on identity+length+mtime so replacing the source invalidates
    /// its conversion rather than serving yesterday's.
    /// </para>
    /// </remarks>
    /// <returns>Dispose to remove the route.</returns>
    public static IDisposable UseMediaConversion(this IWebViewInterceptor interceptor,
        IMissionScheduler scheduler, IEventBus events, MediaConversionOptions options)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Resolve);
        ArgumentNullException.ThrowIfNull(options.Convert);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CacheExtension);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Module);

        var delivery = interceptor.RangeDelivery;

        return interceptor.Use((request, next, cancellationToken) =>
        {
            if (options.Resolve(request.Uri) is not { } requested) return next(request, cancellationToken);

            // A source is either a REMOTE url the engine may read, or a LOCAL path. Both are page-supplied,
            // so both are authorised — by different rules, because the risks are different: a local path
            // can escape its roots, a remote one can reach the host's own network.
            string source;
            string key;
            if (IsRemote(requested, out var remote))
            {
                if (!AllowsRemote(options, remote))
                {
                    Log(options, () => "[Shenora.Media] conversion refused a remote source");
                    return Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.NotFound());
                }
                source = remote.AbsoluteUri;
                // The url is all there is to key on — see AllowRemoteSource's remarks.
                key = DerivedCacheKey.For(source, 0, DateTime.UnixEpoch, "remote");
            }
            else
            {
                // Containment runs BEFORE the filesystem is touched. A refusal is the same 404 as a missing
                // file, so nothing can probe for existence by comparing responses.
                if (WebViewFiles.ResolveContained(requested, options.AllowedRoots) is not { } contained)
                {
                    Log(options, () => "[Shenora.Media] conversion refused a source outside the allowed roots");
                    return Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.NotFound());
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(contained);
                    if (!info.Exists) return Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.NotFound());
                }
                catch (Exception ex)
                {
                    // No exception text on the wire, ever — a path is the likeliest thing it would carry.
                    Log(options, () => $"[Shenora.Media] could not stat a conversion source ({ex.GetType().Name})");
                    return Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.NotFound());
                }

                source = contained;
                key = DerivedCacheKey.For(source, info.Length, info.LastWriteTimeUtc);
            }
            var cachePath = Path.Combine(options.CacheRoot, key + options.CacheExtension);

            if (File.Exists(cachePath))
            {
                var contentType = options.ContentType?.Invoke(cachePath) ?? WebViewContentTypes.FromPath(cachePath);
                return Task.FromResult<WebViewResourceResponse?>(
                    WebViewFiles.Serve(request, cachePath, contentType, delivery));
            }

            StartConversion(scheduler, events, options, source, cachePath, key, isLocal: !IsRemote(source, out _));
            return Task.FromResult<WebViewResourceResponse?>(NotReadyYet());
        });
    }

    /// <summary>
    /// Is this source an absolute <c>http</c>/<c>https</c> url rather than a path?
    /// </summary>
    /// <remarks>
    /// ⚠ Only those two schemes count as remote. Everything else — including <c>file:</c>, <c>ftp:</c> and
    /// anything unrecognised — falls to the LOCAL branch, where containment refuses it. That direction is
    /// the safe one: a scheme this does not understand must not skip the path check by being called
    /// "remote" and then meeting a policy written to think about web addresses.
    /// </remarks>
    private static bool IsRemote(string source, out Uri remote)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            remote = parsed;
            return true;
        }
        remote = null!;
        return false;
    }

    /// <summary>
    /// Ask the app's policy. <b>Fail-CLOSED twice over</b>: no policy refuses, and a policy that THROWS
    /// refuses — because a check that could not be completed is not a check that passed, and this one
    /// stands between a page-supplied url and the host's own network position.
    /// </summary>
    private static bool AllowsRemote(MediaConversionOptions options, Uri remote)
    {
        if (options.AllowRemoteSource is not { } policy) return false;
        return AppCallback.RunOrDefault(() => policy(remote), fallback: false,
            ex => Log(options, () => $"[Shenora.Media] the remote-source policy threw ({ex.GetType().Name}); refusing"));
    }

    /// <summary>
    /// Submit the conversion without waiting for it. The submission is deduplicated by
    /// <see cref="MissionDefinition.Key"/>, so twenty requests for the same source while it converts cost
    /// twenty eager <c>Deduplicated</c> completions and ONE conversion.
    /// </summary>
    /// <remarks>
    /// ⚠ Fire-and-forget with a GUARD, never a bare discard: this runs on a platform event thread with no
    /// caller above it, so an unobserved fault would be an unhandled exception rather than a failed
    /// conversion. The mission's own body is guarded too — this covers the SUBMIT.
    /// </remarks>
    private static void StartConversion(IMissionScheduler scheduler, IEventBus events,
        MediaConversionOptions options, string source, string cachePath, string key, bool isLocal)
    {
        var definition = new MissionDefinition
        {
            Kind = "media-conversion",
            // Convert-once is this, not the claim: twenty requests while one runs cost twenty eager
            // Deduplicated completions and a single conversion.
            Key = new MissionKey(key),
            // The claim buys something DIFFERENT and only exists for a local source: it stops the
            // conversion racing a file update on the same path, because that queue takes the same claim.
            // ⚠ A remote url gets NO path claim — feeding a url to a path-shaped scope would have it
            // normalised as a path and made to conflict with unrelated urls sharing a prefix.
            Claims = isLocal ? [PathClaims.Exclusive(source)] : [],
            Run = (_, missionToken) => ConvertAsync(events, options, source, cachePath, missionToken),
        };

        _ = SubmitGuardedAsync(scheduler, definition, options);
    }

    /// <summary>
    /// The guard around the discarded task. <see cref="IMissionScheduler.SubmitAsync"/> reports a FAILED
    /// body through <c>MissionResult</c> rather than by throwing, so what this catches is a submit-time
    /// fault (an unregistered claim scope, a disposed scheduler) — which would otherwise be an unobserved
    /// exception on a platform event thread with nothing above it.
    /// </summary>
    private static async Task SubmitGuardedAsync(IMissionScheduler scheduler, MissionDefinition definition,
        MediaConversionOptions options)
    {
        try
        {
            await scheduler.SubmitAsync(definition, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log(options, () => $"[Shenora.Media] could not submit a conversion ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static async Task ConvertAsync(IEventBus events, MediaConversionOptions options,
        string source, string cachePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.CacheRoot);

        // Atomic: the engine writes to a temp beside the target and it is swapped in only on Commit. An
        // interrupted run therefore leaves NO cache entry rather than a truncated one that every later
        // request would happily serve as a hit — which is the failure this composition exists to prevent.
        using var replacement = Files.BeginReplace(cachePath);
        // NOT System.Progress<T>: that captures the SynchronizationContext at construction, and a mission
        // body can legitimately start on one — which would marshal every tick of a chatty encoder onto the
        // UI thread. Emit runs wherever the engine reported, and the bus is thread-safe.
        var progress = new DirectProgress(fraction =>
            events.Emit(options.Module, MediaConversionEvents.SourceProgress, new { source, progress = fraction }));

        try
        {
            await options.Convert(
                new MediaConversionRequest(source, replacement.TempPath, progress), cancellationToken);
            replacement.Commit();
            events.Emit(options.Module, MediaConversionEvents.Ready, new { source });
            Log(options, () => $"[Shenora.Media] converted -> {Path.GetFileName(cachePath)}");
        }
        catch (OperationCanceledException)
        {
            // A cancel is not a failure and must not tell the page one happened. Nothing is committed, so
            // the next request simply starts again.
            Log(options, () => "[Shenora.Media] conversion cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // The TYPE name only. The reason reaches the page, and page script can read it — the same
            // no-raw-exception-text boundary the IPC error contract enforces. Details go to the host log.
            events.Emit(options.Module, MediaConversionEvents.Failed, new { source, reason = ex.GetType().Name });
            Log(options, () => $"[Shenora.Media] conversion FAILED: {ex}");
            throw;
        }
    }

    /// <summary>
    /// The answer while a conversion is running: <c>503</c> with <c>Retry-After: 1</c>.
    /// <para>
    /// 503 rather than 404, because the distinction is real and a client can act on it — the resource is
    /// not missing, it is not ready. 404 would tell a media element to give up permanently.
    /// </para>
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

    private static void Log(MediaConversionOptions options, Func<string> message) =>
        AppCallback.Log(options.Log, message);

    /// <summary>
    /// An <see cref="IProgress{T}"/> that reports on the CALLER's thread — see the construction site for
    /// why <see cref="Progress{T}"/> is the wrong one here. Guarded, because the sink is ultimately an
    /// app-supplied bus subscriber and a throwing one must not fail the conversion it is only describing.
    /// </summary>
    private sealed class DirectProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => AppCallback.Run(() => report(value));
    }
}
