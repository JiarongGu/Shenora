using Shenora.Core.Events;
using Shenora.Core.WebView;
using Shenora.Engine;
using Shenora.Engine.Files;
using Shenora.Engine.Missions;

namespace Shenora.Modules.Media;

/// <summary>
/// What the app's engine is being asked to produce: read <see cref="SourcePath"/>, write a playable file at
/// <see cref="DestinationPath"/>, report progress.
/// </summary>
/// <param name="SourcePath">The original file. Already authorised against the allowed roots.</param>
/// <param name="DestinationPath">
/// Where to write. ⚠ A TEMPORARY path — the kit swaps it into place only once the delegate returns without
/// throwing (<see cref="Files.BeginReplace"/>), so a failed conversion leaves no half-written cache hit.
/// </param>
/// <param name="Progress">
/// Fraction complete, 0…1, forwarded to the page as <see cref="MediaConversionEvents.SourceProgress"/>.
/// Optional to call: a converter with no usable progress simply never reports.
/// </param>
/// <param name="Container">
/// The container the output must be, as a lowercase extension including the dot (<c>.mp4</c>).
/// ⚠ <b>Told, never inferred from <see cref="DestinationPath"/></b>, whose name ends <c>.tmp</c>: an engine
/// picking its muxer from the extension sees <c>.m4a.tmp</c> and refuses before writing a byte.
/// </param>
public sealed record MediaConversionRequest(
    string SourcePath,
    string DestinationPath,
    IProgress<double> Progress,
    string Container = ".mp4")
{
    /// <summary>
    /// Codecs the converter could not carry into the output. **A converter APPENDS to this, and a
    /// non-empty list FAILS the conversion** — the route caches nothing and reports
    /// <see cref="MediaConversionErrorCodes.UnsupportedCodec"/> with these codecs.
    /// <para>
    /// 🔴 <b>A conversion that SUCCEEDS having dropped the soundtrack is this kit's most dangerous
    /// outcome:</b> nothing throws, the file plays, and the user hears silence with no way to tell "this
    /// film has no audio" from "this device cannot play the audio it has". ⚠ So a converter that CAN carry
    /// a stream must not report it.
    /// </para>
    /// </summary>
    public IList<string> Dropped { get; } = new List<string>();

    /// <summary>
    /// What the PLANNER decided this file needs — <see cref="MediaPlaybackAction.Remux"/> (container only)
    /// or <see cref="MediaPlaybackAction.Transcode"/> (a stream must be re-encoded). Defaults to
    /// <see cref="MediaPlaybackAction.Remux"/>.
    /// <para>
    /// ⚠ A HINT about intent, not a guarantee about content: the file is still the authority on what it
    /// holds, and a converter that finds otherwise must report it (<see cref="Dropped"/>).
    /// </para>
    /// </summary>
    public MediaPlaybackAction Action { get; init; } = MediaPlaybackAction.Remux;
}

/// <summary>Stable <c>reason</c> codes on <see cref="MediaConversionEvents.Failed"/>.</summary>
/// <remarks>Anything not listed here is a TYPE name from an unexpected fault.</remarks>
public static class MediaConversionErrorCodes
{
    /// <summary>
    /// The output would have lost a stream, so nothing was cached. The event carries <c>dropped</c> — the
    /// codecs — and a page can name them. ⚠ It means "not playable HERE", not always "not supported": a run
    /// with no <see cref="MediaConversionOptions.Conversion"/> never asked the platform.
    /// </summary>
    public const string UnsupportedCodec = "UNSUPPORTED_CODEC";
}

/// <summary>
/// The converter produced a file missing a stream. Internal: it travels from the converter's report to the
/// route's own handler and never crosses the public surface.
/// </summary>
internal sealed class MediaStreamsDroppedException(string[] codecs, bool hadConversion)
    : Exception($"the conversion dropped {string.Join(", ", codecs)}")
{
    public string[] Codecs { get; } = codecs;

    /// <summary>Whether a codec seam was even supplied — the difference between "unsupported" and "unasked".</summary>
    public bool HadConversion { get; } = hadConversion;
}

/// <summary>Event types this middleware publishes, on <see cref="MediaAccessOptions.Module"/>.</summary>
public static class MediaConversionEvents
{
    /// <summary>Fraction complete: <c>{ source, progress }</c>. Throttle in the app if the engine is chatty.</summary>
    public const string SourceProgress = "SOURCE_PROGRESS";

    /// <summary>The converted file is servable: <c>{ source }</c>. The page may set its element's src now.</summary>
    /// <remarks>⚠ It carries no <c>dropped</c> list — a dropped stream FAILS the conversion, so the codecs
    /// travel on <see cref="Failed"/>.</remarks>
    public const string Ready = "READY";

    /// <summary>
    /// Conversion failed: <c>{ source, reason }</c>, plus <c>dropped</c> when <c>reason</c> is
    /// <see cref="MediaConversionErrorCodes.UnsupportedCodec"/>. <c>reason</c> is a stable token or a TYPE
    /// name — never exception text.
    /// </summary>
    public const string Failed = "FAILED";
}

/// <summary>Inputs for <see cref="MediaConversionExtensions.UseMediaConversion"/>.</summary>
/// <remarks>
/// ⚠ No probe, no codec policy and no engine: **whether a source needs converting is the APP's decision**,
/// made before it builds the URL (<c>MediaPlaybackPlanner</c>). A source that plays directly should be
/// pointed at <c>UseFiles</c> instead.
/// </remarks>
public sealed class MediaConversionOptions
{
    /// <summary>
    /// Where a source may be read from, where a finished conversion is cached, and which module the route's
    /// progress events publish on. <see cref="MediaAccessOptions.Resolve"/> returns null for "not a
    /// conversion request" so the pipeline falls through; whatever it returns is still authorised against
    /// <see cref="MediaAccessOptions.AllowedRoots"/>, so being generous here cannot widen what is reachable.
    /// </summary>
    public required MediaAccessOptions Access { get; init; }

    /// <summary>
    /// What the PLANNER decided for this request, read from the same URL <see cref="MediaAccessOptions.Resolve"/>
    /// reads. Unset means <see cref="MediaPlaybackAction.Remux"/>. See
    /// <see cref="MediaConversionRequest.Action"/>.
    /// </summary>
    public Func<Uri, MediaPlaybackAction>? ResolveAction { get; init; }

    /// <summary>
    /// The PLATFORM's codec seam, wired into the kit's default converter. Resolve it from DI
    /// (<c>services.GetService&lt;IMediaStreamConversion&gt;()</c>) — the mobile shells register one; leave
    /// it null and the default repairs containers only. Its reach is what the DEVICE decodes and its WEBVIEW
    /// refuses, and no wider (D59) — ask <see cref="IMediaCapability"/>, and write <see cref="Convert"/> for
    /// anything past that line. ⚠ Ignored when <see cref="Convert"/> is set, and setting both THROWS.
    /// </summary>
    public IMediaStreamConversion? Conversion { get; init; }

    /// <summary>
    /// Produce a playable file — <b>the OVERRIDE, for work past the platform's reach.</b> Leave it unset
    /// and the kit supplies its own: <see cref="Conversion"/> joined to <see cref="Mp4Remuxer"/>. Runs inside
    /// a mission, never on the request path, so it may take minutes; honouring its own cancellation token is
    /// what makes shutdown prompt.
    /// <code>
    /// // nothing at all: container repair, every platform
    /// // Conversion = conversion:  …and the device's own codecs (the default engine)
    /// Convert = myEngine.ToConverter(conversion),   // yours, for codecs no platform decodes
    /// </code>
    /// </summary>
    public Func<MediaConversionRequest, CancellationToken, Task>? Convert { get; init; }

    /// <summary>
    /// The converter this route will actually run: <see cref="Convert"/> when the app supplied one, else
    /// the kit's platform bridge. ⚠ Resolved ONCE at registration, never per request.
    /// </summary>
    internal Func<MediaConversionRequest, CancellationToken, Task> Converter()
    {
        if (Convert is not null && Conversion is not null)
            throw new InvalidOperationException(
                "MediaConversionOptions sets both Convert and Conversion. The seam configures the kit's "
                + "DEFAULT converter, so a custom Convert makes it dead configuration — pass the seam to "
                + "your own engine instead (myEngine.ToConverter(conversion)), or drop Convert to use "
                + "the kit's.");

        // ⚠ Constructed HERE rather than as a property initialiser: a property default would capture the
        // seams before the object initialiser had run and silently give every app container-repair-only.
        return Convert ?? new Mp4Remuxer().ToConverter(Conversion);
    }

    /// <summary>
    /// Extension for converted output, WITH the dot. It decides the served <c>Content-Type</c>, so it must
    /// match what <see cref="Convert"/> actually writes — a <c>.mp4</c> name on a WebM body plays in no
    /// <c>&lt;video&gt;</c>, and the failure looks like a broken converter rather than a wrong name here.
    /// </summary>
    public string CacheExtension { get; init; } = ".mp4";

    /// <summary>Override the content type derived from <see cref="CacheExtension"/>. Rarely needed.</summary>
    public Func<string, string>? ContentType { get; init; }

    /// <summary>
    /// May the app's engine read this REMOTE url on the page's behalf? <b>Fail-CLOSED: null refuses every
    /// remote source</b>, and so does a policy that throws.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>An SSRF boundary: the HOST can reach addresses the PAGE cannot.</b> The page supplies the
    /// source, so without a policy it could name <c>http://169.254.169.254/</c> or anything else behind the
    /// machine, and the engine would fetch it with the host's own network position. A predicate over a
    /// page-supplied url is strictly WEAKER than <see cref="MediaSourceRegistry"/>, where the page can only
    /// name a handle the app issued.
    /// <para>
    /// ⚠ <b>Synchronous</b>, on the resource path the mobile shells resolve SYNCHRONOUSLY — a policy that
    /// needs I/O must precompute its allow-list at startup and consult it in memory here.
    /// </para>
    /// <para>
    /// ⚠ <b>A remote source is cached by its URL alone</b>, since nothing else is knowable without fetching
    /// it, so a url whose CONTENT changes while its address does not will serve a stale conversion.
    /// </para>
    /// </remarks>
    public Func<Uri, bool>? AllowRemoteSource { get; init; }
}

/// <summary>Serving media the platform cannot decode: convert once, cache the result, serve it with ranges.</summary>
public static class MediaConversionExtensions
{
    /// <summary>
    /// How many unconvertible sources one registration remembers. Forgetting one costs a single conversion,
    /// never a wrong answer.
    /// </summary>
    private const int MaxRememberedFailures = 64;

    /// <summary>
    /// Register the conversion route. Layered OVER the file middleware rather than replacing it — a cache
    /// hit is served by exactly the same range-correct code path as any other local file.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>A miss answers <c>503</c> with <c>Retry-After</c>, and the page must be event-driven</b>,
    /// learning from <see cref="MediaConversionEvents.Ready"/> when to set its element's source: a media
    /// element pointed at this URL before the file exists will error and never retry.
    /// <para>
    /// Everything slow happens in the MISSION, because the mobile interceptor resolves SYNCHRONOUSLY. One
    /// source converts once however many requests arrive (<see cref="PathClaims.Exclusive(string)"/>), and
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
        ArgumentNullException.ThrowIfNull(options.Access);
        ArgumentNullException.ThrowIfNull(options.Access.Resolve);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Access.CacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CacheExtension);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Access.Module);

        var converter = options.Converter();

        // Ordering marker for `UseComputedRemux`, which must be registered BEFORE this route — see
        // `MediaAccessOptions.ConversionRegistered` for why it reports rather than throws.
        options.Access.ConversionRegistered = true;

        var delivery = interceptor.RangeDelivery;

        // 🔴 SOURCES WHOSE CONVERSION CANNOT SUCCEED, REMEMBERED: `request.Dropped` is populated only AFTER
        // the writer has finished, so without this a page's retry loop re-runs the WHOLE TRANSCODE once a
        // second, for ever. ⚠ ONLY DETERMINISTIC failures — a dropped stream is a property of the FILE,
        // while an IO error, an OOM or a cancellation says nothing about the source and must stay retryable.
        var unconvertible = new Dictionary<string, byte>(StringComparer.Ordinal);
        var unconvertibleGate = new object();

        bool IsKnownUnconvertible(string cacheKey)
        {
            lock (unconvertibleGate) return unconvertible.ContainsKey(cacheKey);
        }

        void RememberUnconvertible(string cacheKey)
        {
            lock (unconvertibleGate)
            {
                if (unconvertible.Count >= MaxRememberedFailures)
                {
                    // ⚠ Whichever entry enumerates first: a Dictionary that has had a removal no longer
                    // enumerates in insertion order, so this is arbitrary rather than oldest-first.
                    foreach (var oldest in unconvertible.Keys.Take(1).ToArray()) unconvertible.Remove(oldest);
                }
                unconvertible[cacheKey] = 0;
            }
        }

        return interceptor.Use((request, next, cancellationToken) =>
        {
            if (options.Access.Resolve(request.Uri) is not { } requested) return next(request, cancellationToken);

            // A source is either a REMOTE url the engine may read or a LOCAL path. Both are page-supplied
            // and both are authorised, by different rules — the roots for one, the SSRF policy for the other.
            string source;
            string key;
            if (IsRemote(requested, out var remote))
            {
                if (!AllowsRemote(options, remote))
                {
                    Log(options, () => "[Shenora.Modules.Media] conversion refused a remote source");
                    return Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.NotFound());
                }
                source = remote.AbsoluteUri;
                // The url is all there is to key on — see AllowRemoteSource's remarks.
                key = DerivedCacheKey.For(source, 0, DateTime.UnixEpoch, "remote");
            }
            else
            {
                // Containment runs BEFORE the filesystem is touched; a refusal is the same 404 as a missing
                // file, so nothing can probe for existence by comparing responses.
                if (WebViewFiles.ResolveContained(requested, options.Access.AllowedRoots) is not { } contained)
                {
                    Log(options, () => "[Shenora.Modules.Media] conversion refused a source outside the allowed roots");
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
                    Log(options, () => $"[Shenora.Modules.Media] could not stat a conversion source ({ex.GetType().Name})");
                    return Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.NotFound());
                }

                source = contained;
                key = DerivedCacheKey.For(source, info.Length, info.LastWriteTimeUtc);
            }
            var cachePath = Path.Combine(options.Access.CacheRoot, key + options.CacheExtension);

            if (File.Exists(cachePath))
            {
                var contentType = options.ContentType?.Invoke(cachePath) ?? WebViewContentTypes.FromPath(cachePath);
                return Task.FromResult<WebViewResourceResponse?>(
                    WebViewFiles.Serve(request, cachePath, contentType, delivery));
            }

            // 🔴 BEFORE submitting anything: a source already proven unconvertible answers 404 rather than
            // another whole transcode — the page was told why on the `FAILED` event the first attempt sent.
            if (IsKnownUnconvertible(key))
            {
                Log(options, () => "[Shenora.Modules.Media] conversion declines a source it has already "
                                 + "failed to carry — the codecs went out on FAILED when it first ran");
                return Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.NotFound());
            }

            // The planner's verdict travels with the job, so the converter is TOLD rather than re-deriving it.
            var action = options.ResolveAction?.Invoke(request.Uri) ?? MediaPlaybackAction.Remux;
            StartConversion(scheduler, events, options, converter, source, cachePath, key, action,
                isLocal: !IsRemote(source, out _), onUnconvertible: RememberUnconvertible);
            return Task.FromResult<WebViewResourceResponse?>(NotReadyYet());
        });
    }

    /// <summary>Is this source an absolute <c>http</c>/<c>https</c> url rather than a path?</summary>
    /// <remarks>
    /// ⚠ Only those two schemes count as remote. Everything else — including <c>file:</c>, <c>ftp:</c> and
    /// anything unrecognised — falls to the LOCAL branch, where containment refuses it.
    /// </remarks>
    internal static bool IsRemote(string source, out Uri remote)
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

    /// <summary>Ask the app's policy. Fail-CLOSED: no policy refuses, and a policy that THROWS refuses.</summary>
    private static bool AllowsRemote(MediaConversionOptions options, Uri remote)
    {
        if (options.AllowRemoteSource is not { } policy) return false;
        return AppCallback.RunOrDefault(() => policy(remote), fallback: false,
            ex => Log(options, () => $"[Shenora.Modules.Media] the remote-source policy threw ({ex.GetType().Name}); refusing"));
    }

    /// <summary>
    /// Submit the conversion without waiting for it. The submission is deduplicated by
    /// <see cref="MissionDefinition.Key"/>, so twenty requests for the same source while it converts cost
    /// twenty eager <c>Deduplicated</c> completions and ONE conversion.
    /// </summary>
    private static void StartConversion(IMissionScheduler scheduler, IEventBus events,
        MediaConversionOptions options, Func<MediaConversionRequest, CancellationToken, Task> converter,
        string source, string cachePath, string key,
        MediaPlaybackAction action, bool isLocal, Action<string> onUnconvertible)
    {
        var definition = new MissionDefinition
        {
            Kind = "media-conversion",
            Key = new MissionKey(key),
            // The claim exists only for a local source, stopping the conversion racing a file update on the
            // same path. ⚠ A remote url gets NO path claim — a url fed to a path-shaped scope is normalised
            // as a path and conflicts with unrelated urls sharing a prefix.
            Claims = isLocal ? [PathClaims.Exclusive(source)] : [],
            Run = (_, missionToken) => ConvertAsync(events, options, converter, source, cachePath, action,
                                                    () => onUnconvertible(key), missionToken),
        };

        _ = SubmitGuardedAsync(scheduler, definition, options);
    }

    /// <summary>
    /// The guard around the discarded task. <see cref="IMissionScheduler.SubmitAsync"/> reports a FAILED
    /// body through <c>MissionResult</c> rather than by throwing, so what this catches is a submit-time
    /// fault — otherwise an unobserved exception on a platform event thread with nothing above it.
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
            Log(options, () => $"[Shenora.Modules.Media] could not submit a conversion ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static async Task ConvertAsync(IEventBus events, MediaConversionOptions options,
        Func<MediaConversionRequest, CancellationToken, Task> converter,
        string source, string cachePath, MediaPlaybackAction action, Action onUnconvertible,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.Access.CacheRoot);

        // Atomic: the engine writes to a temp beside the target, swapped in only on Commit, so an
        // interrupted run leaves NO cache entry rather than a truncated one served as a cache hit.
        using var replacement = Files.BeginReplace(cachePath);
        // NOT System.Progress<T>: that captures the SynchronizationContext at construction, so every tick
        // of a chatty encoder would marshal onto the UI thread. Emit runs wherever the engine reported.
        var progress = new DirectProgress(fraction =>
            events.Emit(options.Access.Module, MediaConversionEvents.SourceProgress, new { source, progress = fraction }));

        try
        {
            var request = new MediaConversionRequest(source, replacement.TempPath, progress, options.CacheExtension)
            {
                Action = action,
            };
            await converter(request, cancellationToken);

            // 🔴 A DROPPED STREAM IS A FAILED CONVERSION — committing would cache a SILENT FILM and serve it
            // as a 200 for ever. See `MediaConversionRequest.Dropped`.
            var dropped = request.Dropped.ToArray();
            if (dropped.Length > 0) throw new MediaStreamsDroppedException(dropped, options.Conversion is not null);

            replacement.Commit();
            events.Emit(options.Access.Module, MediaConversionEvents.Ready, new { source });
            Log(options, () => $"[Shenora.Modules.Media] converted -> {Path.GetFileName(cachePath)}");
        }
        catch (MediaStreamsDroppedException dropped)
        {
            // 🔴 REMEMBERED BEFORE THE EVENT IS EMITTED. A page that reacts to `FAILED` by re-requesting
            // immediately would otherwise slip in ahead of the record and buy one more whole transcode.
            onUnconvertible();

            // The codecs reach the page; `reason` stays a stable token, as the IPC error contract wants.
            events.Emit(options.Access.Module, MediaConversionEvents.Failed,
                new { source, reason = MediaConversionErrorCodes.UnsupportedCodec, dropped = dropped.Codecs });

            // 🔴 THE TWO CAUSES NEED OPPOSITE RESPONSES, so the host log names which one it was.
            Log(options, () => dropped.HadConversion
                ? $"[Shenora.Modules.Media] conversion FAILED: this platform cannot decode "
                  + $"{string.Join(", ", dropped.Codecs)} — genuinely unsupported here"
                : $"[Shenora.Modules.Media] conversion FAILED: dropped {string.Join(", ", dropped.Codecs)} "
                  + "and NO IMediaStreamConversion was supplied, so the platform was never asked. Set "
                  + "MediaConversionOptions.Conversion (the shell registers one) before concluding "
                  + "the codec is unsupported.");
            throw;
        }
        catch (OperationCanceledException)
        {
            // A cancel is not a failure and must not tell the page one happened. Nothing is committed.
            Log(options, () => "[Shenora.Modules.Media] conversion cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // The TYPE name only — page script reads `reason`, so no raw exception text crosses.
            events.Emit(options.Access.Module, MediaConversionEvents.Failed, new { source, reason = ex.GetType().Name });
            Log(options, () => $"[Shenora.Modules.Media] conversion FAILED: {ex}");
            throw;
        }
    }

    /// <summary>
    /// The answer while work this route started is still running: <c>503</c> with <c>Retry-After: 1</c>. The
    /// ONE not-ready answer for all three delivery routes, because the retry INTERVAL is a contract with the
    /// page's own loop and separate copies would drift apart while every test still passed.
    /// <para>
    /// 🔴 <b>ONLY A <c>fetch</c> CLIENT CAN ACT ON IT — never a media element.</b> To an element the 503 and
    /// a 404 are indistinguishable: both raise <c>error</c> with <c>error.code 4</c>
    /// (<c>MEDIA_ERR_SRC_NOT_SUPPORTED</c>) and issue no further request, so a bare <c>&lt;video src&gt;</c>
    /// does not survive the wait. A page must re-point its element after
    /// <see cref="MediaConversionEvents.Ready"/>. Numbers: <c>docs/design/mobile-shells.md</c>.
    /// </para>
    /// </summary>
    internal static WebViewResourceResponse NotReadyYet() => new()
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
        AppCallback.Log(options.Access.Log, message);

    /// <summary>
    /// An <see cref="IProgress{T}"/> that reports on the CALLER's thread. Guarded, because the sink is
    /// ultimately an app-supplied bus subscriber and a throwing one must not fail the conversion.
    /// </summary>
    private sealed class DirectProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => AppCallback.Run(() => report(value));
    }
}
