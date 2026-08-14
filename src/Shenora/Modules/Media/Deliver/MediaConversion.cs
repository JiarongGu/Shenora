using Shenora;
using Shenora.Core.Events;
using Shenora.Core.WebView;
using Shenora.Engine;
using Shenora.Engine.Files;
using Shenora.Engine.Missions;
using Shenora.Core.Ipc;

namespace Shenora.Modules.Media;

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
/// <param name="Container">
/// The container the output must be, as a lowercase extension including the dot (<c>.mp4</c>).
///
/// <para>
/// 🔴 <b>Told, never inferred from <see cref="DestinationPath"/> — and this is a bug the kit actually
/// caused.</b> The destination is a TEMPORARY path, so its name ends <c>.tmp</c>: an engine that picks its
/// muxer from the extension sees <c>.m4a.tmp</c>, recognises no format, and refuses before writing a byte.
/// The first adopter hit exactly that (ffmpeg, exit 234) and reported it. The name belongs to the CALLER,
/// so the format has to travel separately.
/// </para>
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
    /// 🔴 <b>It exists because a successful conversion that dropped the soundtrack is this kit's most
    /// dangerous outcome.</b> Nothing throws, the file plays, and the user hears silence — with no way for
    /// the page to know the difference between "this film has no audio" and "this device cannot play the
    /// audio it has". Filling this in turns that into something an app can SAY.
    /// </para>
    /// <para>
    /// ⚠ <b>Reporting here USED to leave the conversion successful</b>, with the list travelling beside
    /// <see cref="MediaConversionEvents.Ready"/> as a caveat. That still cached and served the silent film
    /// (owner, 2026-08-10: <i>"i dont think fail silently is good — if codec not support just not
    /// support"</i>). So appending here is now a refusal, and a converter that CAN carry a stream must not
    /// report it.
    /// </para>
    /// <para>
    /// A side channel rather than a return value, exactly like <see cref="Progress"/>: it keeps
    /// <see cref="MediaConversionOptions.Convert"/> a plain <c>Task</c>, so an app that already wrote one
    /// needs no change and simply reports nothing.
    /// </para>
    /// </summary>
    public IList<string> Dropped { get; } = new List<string>();

    /// <summary>
    /// What the PLANNER decided this file needs — <see cref="MediaPlaybackAction.Remux"/> (container only)
    /// or <see cref="MediaPlaybackAction.Transcode"/> (a stream must be re-encoded).
    /// <para>
    /// 🔴 <b>This is the last link in <c>policy → plan → converter</c>, and until 2026-08-07 it was
    /// missing.</b> The planner decided against the app's <see cref="MediaPlaybackPolicy"/> and the
    /// device's <see cref="IMediaCapability"/>, then that decision was thrown away when the plan became a
    /// URL, and the converter re-derived it from the file. Two places deciding the same thing is how they
    /// come to disagree — and only one of them had the policy.
    /// </para>
    /// <para>
    /// A converter MAY trust it: <see cref="MediaPlaybackAction.Remux"/> means no codec is needed, so a
    /// converter can skip building one. ⚠ It is a HINT about intent, not a guarantee about content — the
    /// file is still the authority on what it holds, and a converter that finds otherwise should do the
    /// honest thing and report it (<see cref="Dropped"/>).
    /// </para>
    /// <para>
    /// Defaults to <see cref="MediaPlaybackAction.Remux"/>: a request reaching a conversion route at all
    /// means something is wrong with the container, which is the cheaper repair to assume.
    /// </para>
    /// </summary>
    public MediaPlaybackAction Action { get; init; } = MediaPlaybackAction.Remux;
}

/// <summary>Stable <c>reason</c> codes on <see cref="MediaConversionEvents.Failed"/>.</summary>
/// <remarks>
/// A page branches on these, so they are constants rather than prose — the same rule the IPC error codes
/// follow. Anything not listed here is a TYPE name from an unexpected fault.
/// </remarks>
public static class MediaConversionErrorCodes
{
    /// <summary>
    /// The output would have lost a stream, so nothing was cached. The event carries <c>dropped</c> — the
    /// codecs — and a page can name them.
    /// <para>
    /// ⚠ It means "not playable HERE", which is not always "not supported": a conversion run with no
    /// <see cref="MediaConversionOptions.Conversion"/> never asked the platform at all. The host log
    /// distinguishes the two; only one of them is the file's fault.
    /// </para>
    /// </summary>
    public const string UnsupportedCodec = "UNSUPPORTED_CODEC";
}

/// <summary>
/// The converter produced a file missing a stream. Internal: it never crosses the public surface, it
/// travels from the converter's report to the route's own handler a few lines away.
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
    /// <remarks>⚠ It no longer carries <c>dropped</c>: a dropped stream is a FAILURE now, not a caveat on a
    /// success, so the codecs travel on <see cref="Failed"/> instead.</remarks>
    public const string Ready = "READY";

    /// <summary>
    /// Conversion failed: <c>{ source, reason }</c>, plus <c>dropped</c> when <c>reason</c> is
    /// <see cref="MediaConversionErrorCodes.UnsupportedCodec"/>. <c>reason</c> is a stable token or a TYPE
    /// name — never exception text.
    /// </summary>
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
    /// Where a source may be read from, where a finished conversion is cached, and which module the route's
    /// progress events publish on — stated ONCE for every media delivery path rather than declared here a
    /// third time. See <see cref="MediaAccessOptions"/> for the full reasoning, most of all why
    /// <see cref="MediaAccessOptions.AllowedRoots"/> has no default: the app supplies the containment
    /// boundary, the kit only enforces it.
    /// <para>
    /// <see cref="MediaAccessOptions.Resolve"/> is this route's <c>Resolve</c> — map a request to the SOURCE
    /// file it names, returning null for "not a conversion request" so the pipeline falls through. Whatever
    /// it returns is still authorised against <see cref="MediaAccessOptions.AllowedRoots"/>, so being
    /// generous here cannot widen what is reachable.
    /// </para>
    /// </summary>
    public required MediaAccessOptions Access { get; init; }

    /// <summary>
    /// What the PLANNER decided for this request, read from the same URL <see cref="MediaAccessOptions.Resolve"/> reads.
    /// Unset means <see cref="MediaPlaybackAction.Remux"/>.
    /// <para>
    /// <b>The last link in <c>policy → plan → converter</c>.</b> Without it the planner's decision — made
    /// against the app's policy and the device's capability — is lost when the plan becomes a URL, and the
    /// converter re-derives it from the file alone. See <see cref="MediaConversionRequest.Action"/>.
    /// </para>
    /// </summary>
    public Func<Uri, MediaPlaybackAction>? ResolveAction { get; init; }

    /// <summary>
    /// The PLATFORM's codec seam, wired into the kit's default converter. Resolve it from DI
    /// (<c>services.GetService&lt;IMediaStreamConversion&gt;()</c>) — the mobile shells register one; leave
    /// it null and the default repairs containers only.
    /// <para>
    /// 🔴 <b>THIS IS THE DEFAULT ENGINE, AND IT IS THE PLATFORM'S OWN HARDWARE.</b> Setting it is how an
    /// app gets a working converter without writing one: the kit's container writer plus whatever the
    /// device can already decode. Zero shipped codec bytes, zero licence weight, and it is the OS's patent
    /// problem rather than the kit's (D51's first preference).
    /// </para>
    /// <para>
    /// ⚠ <b>Its reach is exactly the gap it exists to bridge and no wider (D59):</b> what the DEVICE
    /// decodes and its WEBVIEW refuses. Ask <see cref="IMediaCapability"/> rather than assuming — measured
    /// 2026-08-10, an API 36 Android device decodes mp3/flac/vorbis and NOT ac3/eac3/dts/alac, while an
    /// iPhone decodes ac3/eac3. For anything past that line, write <see cref="Convert"/>.
    /// </para>
    /// <para>
    /// ⚠ Ignored when <see cref="Convert"/> is set — and setting both THROWS rather than silently
    /// preferring one, because two ways of saying the same thing is how a seam ends up unread (D63).
    /// </para>
    /// </summary>
    public IMediaStreamConversion? Conversion { get; init; }

    /// <summary>
    /// Produce a playable file — <b>the OVERRIDE, for work past the platform's reach.</b> Leave it unset
    /// and the kit supplies its own: <see cref="Conversion"/> joined to <see cref="Mp4Remuxer"/>.
    /// <code>
    /// // nothing at all: container repair, every platform
    /// // Conversion = conversion:  …and the device's own codecs (the default engine)
    /// Convert = myEngine.ToConverter(conversion),   // yours, for codecs no platform decodes
    /// </code>
    /// <para>
    /// <b>The kit vendors no codec and never will (D42/D51)</b> — the right encoder differs per app, and a
    /// bundled one is tens of megabytes plus a licence every consumer inherits. What it does ship is the
    /// wiring of the platform's own decoders, which is not a codec.
    /// </para>
    /// <para>
    /// 🔴 <b>THIS USED TO BE <c>required</c>, and the note here said defaulting it "would make the kit's
    /// choice look like the only one".</b> Superseded 2026-08-10 by the owner: the default's reach is
    /// BOUNDED by D59 — bridge the webview and the platform, nothing wider — and a boundary is better
    /// stated in docs than enforced by making every adopter type the same line. What is past the boundary
    /// is still theirs to write, which is what this property is for.
    /// </para>
    /// <para>
    /// Runs inside a mission, so it may take minutes; it is never on the request path. It receives its OWN
    /// cancellation token, and honouring it is what makes shutdown prompt.
    /// </para>
    /// </summary>
    public Func<MediaConversionRequest, CancellationToken, Task>? Convert { get; init; }

    /// <summary>
    /// The converter this route will actually run: <see cref="Convert"/> when the app supplied one, else
    /// the kit's platform bridge.
    /// <para>
    /// ⚠ Resolved ONCE at registration rather than per request — a converter that could change under a
    /// running mission is a race nobody needs, and it makes "which engine ran?" answerable.
    /// </para>
    /// </summary>
    internal Func<MediaConversionRequest, CancellationToken, Task> Converter()
    {
        if (Convert is not null && Conversion is not null)
            throw new InvalidOperationException(
                "MediaConversionOptions sets both Convert and Conversion. The seam configures the kit's "
                + "DEFAULT converter, so a custom Convert makes it dead configuration — pass the seam to "
                + "your own engine instead (myEngine.ToConverter(conversion)), or drop Convert to use "
                + "the kit's.");

        // ⚠ The default is CONSTRUCTED HERE rather than being a property initialiser, because a property
        // default would capture the seams before the object initialiser had run and silently give
        // every app container-repair-only.
        return Convert ?? new Mp4Remuxer().ToConverter(Conversion);
    }

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
}

/// <summary>
/// Serving media the platform cannot decode: convert once, cache the result, serve it with ranges.
/// </summary>
public static class MediaConversionExtensions
{
    /// <summary>
    /// How many unconvertible sources one registration remembers. Small on purpose: these are files an app
    /// cannot play at all, so a library full of them is a different problem, and forgetting one costs a
    /// single conversion rather than a wrong answer.
    /// </summary>
    private const int MaxRememberedFailures = 64;

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
        ArgumentNullException.ThrowIfNull(options.Access);
        ArgumentNullException.ThrowIfNull(options.Access.Resolve);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Access.CacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CacheExtension);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Access.Module);

        // 🔴 RESOLVED AT REGISTRATION, not per request, and that placement is the diagnostic: a
        // Convert-plus-AudioConversion mistake throws HERE — while the app is composing, with a stack that
        // names the call site — rather than inside a mission minutes later where it would surface as a
        // conversion that mysteriously did not use the codecs you configured.
        var converter = options.Converter();

        // Ordering marker for `UseComputedRemux`, which must be registered BEFORE this route — see
        // `MediaAccessOptions.ConversionRegistered` for why it reports rather than throws.
        options.Access.ConversionRegistered = true;

        var delivery = interceptor.RangeDelivery;

        // 🔴 SOURCES WHOSE CONVERSION CANNOT SUCCEED, REMEMBERED — because without this a page's own retry
        // loop re-runs the WHOLE TRANSCODE once per second, for ever. Found on the iOS simulator
        // 2026-08-13: `clip-mpeg4-aac.mkv` failed six times in six seconds (missions m19…m24), each one a
        // complete conversion, because `request.Dropped` is only populated AFTER the writer has finished —
        // so the cost of discovering "this codec cannot be carried" is the cost of converting the file. On a
        // small fixture that is a second; on a two-hour film it is minutes per cycle, and the cycle never
        // ends while the page polls.
        //
        // ⚠ ONLY DETERMINISTIC failures are remembered, which is the same split the computed-remux route
        // makes and for the same reason: a dropped stream is a property of the FILE and re-running cannot
        // change it, while an IO error, an OOM or a cancellation says nothing about the source and must stay
        // retryable. `MediaStreamsDroppedException` is the only one on the first side today.
        //
        // ⚠ Bounded, and evicting the OLDEST is safe here in a way it is not for a layout cache: forgetting
        // a refusal costs one extra conversion, never a wrong answer.
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
                    // Insertion order — the oldest refusal is the one least likely to be asked for again,
                    // and being wrong costs one conversion rather than a wrong answer.
                    foreach (var oldest in unconvertible.Keys.Take(1).ToArray()) unconvertible.Remove(oldest);
                }
                unconvertible[cacheKey] = 0;
            }
        }

        return interceptor.Use((request, next, cancellationToken) =>
        {
            if (options.Access.Resolve(request.Uri) is not { } requested) return next(request, cancellationToken);

            // A source is either a REMOTE url the engine may read, or a LOCAL path. Both are page-supplied,
            // so both are authorised — by different rules, because the risks are different: a local path
            // can escape its roots, a remote one can reach the host's own network.
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
                // Containment runs BEFORE the filesystem is touched. A refusal is the same 404 as a missing
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

            // 🔴 BEFORE submitting anything. A source already proven unconvertible answers 404 rather than
            // another `503` + another whole transcode — see `unconvertible` above. 404 rather than a
            // permanent 503 because the page has ALREADY been told why, by name, on the `FAILED` event that
            // the first attempt emitted; leaving it at 503 would invite the retry loop this exists to end.
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

    /// <summary>
    /// Is this source an absolute <c>http</c>/<c>https</c> url rather than a path?
    /// </summary>
    /// <remarks>
    /// ⚠ Only those two schemes count as remote. Everything else — including <c>file:</c>, <c>ftp:</c> and
    /// anything unrecognised — falls to the LOCAL branch, where containment refuses it. That direction is
    /// the safe one: a scheme this does not understand must not skip the path check by being called
    /// "remote" and then meeting a policy written to think about web addresses.
    /// <para>
    /// ⚠ <b>Internal rather than private because <see cref="ComputedRemuxExtensions"/> asks the same question
    /// and must get the same answer</b> — it declines a remote source so this route can still have it, and two
    /// implementations of "what counts as remote" would eventually disagree about a scheme, leaving a source
    /// that neither route serves and nothing to say why.
    /// </para>
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

    /// <summary>
    /// Ask the app's policy. <b>Fail-CLOSED twice over</b>: no policy refuses, and a policy that THROWS
    /// refuses — because a check that could not be completed is not a check that passed, and this one
    /// stands between a page-supplied url and the host's own network position.
    /// </summary>
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
    /// <remarks>
    /// ⚠ Fire-and-forget with a GUARD, never a bare discard: this runs on a platform event thread with no
    /// caller above it, so an unobserved fault would be an unhandled exception rather than a failed
    /// conversion. The mission's own body is guarded too — this covers the SUBMIT.
    /// </remarks>
    private static void StartConversion(IMissionScheduler scheduler, IEventBus events,
        MediaConversionOptions options, Func<MediaConversionRequest, CancellationToken, Task> converter,
        string source, string cachePath, string key,
        MediaPlaybackAction action, bool isLocal, Action<string> onUnconvertible)
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
            Run = (_, missionToken) => ConvertAsync(events, options, converter, source, cachePath, action,
                                                    () => onUnconvertible(key), missionToken),
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
            Log(options, () => $"[Shenora.Modules.Media] could not submit a conversion ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static async Task ConvertAsync(IEventBus events, MediaConversionOptions options,
        Func<MediaConversionRequest, CancellationToken, Task> converter,
        string source, string cachePath, MediaPlaybackAction action, Action onUnconvertible,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.Access.CacheRoot);

        // Atomic: the engine writes to a temp beside the target and it is swapped in only on Commit. An
        // interrupted run therefore leaves NO cache entry rather than a truncated one that every later
        // request would happily serve as a hit — which is the failure this composition exists to prevent.
        using var replacement = Files.BeginReplace(cachePath);
        // NOT System.Progress<T>: that captures the SynchronizationContext at construction, and a mission
        // body can legitimately start on one — which would marshal every tick of a chatty encoder onto the
        // UI thread. Emit runs wherever the engine reported, and the bus is thread-safe.
        var progress = new DirectProgress(fraction =>
            events.Emit(options.Access.Module, MediaConversionEvents.SourceProgress, new { source, progress = fraction }));

        try
        {
            var request = new MediaConversionRequest(source, replacement.TempPath, progress, options.CacheExtension)
            {
                Action = action,
            };
            await converter(request, cancellationToken);

            // 🔴 A DROPPED STREAM IS A FAILED CONVERSION, NOT A SUCCESSFUL ONE — and this used to Commit
            // FIRST and report READY with a `dropped` list beside it. That served a SILENT FILM as a 200
            // and cached it forever: the worst outcome this subsystem can produce, because nothing throws,
            // the video plays, and the user cannot tell "this film has no soundtrack" from "this device
            // could not play the soundtrack it has". Owner, 2026-08-10: *"i dont think fail silently is
            // good — if codec not support just not support"*. So it fails, it says which codec, and it
            // caches nothing for a later request to serve as a hit.
            var dropped = request.Dropped.ToArray();
            if (dropped.Length > 0) throw new MediaStreamsDroppedException(dropped, options.Conversion is not null);

            replacement.Commit();
            events.Emit(options.Access.Module, MediaConversionEvents.Ready, new { source });
            Log(options, () => $"[Shenora.Modules.Media] converted -> {Path.GetFileName(cachePath)}");
        }
        catch (MediaStreamsDroppedException dropped)
        {
            // 🔴 REMEMBERED BEFORE THE EVENT IS EMITTED, and the order is the point. A page that reacts to
            // `FAILED` by re-requesting immediately would otherwise slip in ahead of the record and buy
            // itself one more whole transcode — the exact cost this memory exists to stop. Recording first
            // makes the refusal true by the time anyone can learn of the failure.
            onUnconvertible();

            // The codecs reach the page, because "which one?" is the whole difference between a message a
            // user can act on and a shrug. `reason` stays a stable token, as the IPC error contract wants.
            events.Emit(options.Access.Module, MediaConversionEvents.Failed,
                new { source, reason = MediaConversionErrorCodes.UnsupportedCodec, dropped = dropped.Codecs });

            // 🔴 THE TWO CAUSES NEED OPPOSITE RESPONSES, so the host log names which one it was. Owner,
            // same message: *"we should not taking what's supported unsupported"* — a conversion that ran
            // with NO codec seam did not discover that the codec is unsupported, it only discovered that
            // nobody asked the platform. That is the adopter's composition, not the file's fault, and
            // reporting it as "unsupported" is exactly the false negative being warned about.
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
            // A cancel is not a failure and must not tell the page one happened. Nothing is committed, so
            // the next request simply starts again.
            Log(options, () => "[Shenora.Modules.Media] conversion cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // The TYPE name only. The reason reaches the page, and page script can read it — the same
            // no-raw-exception-text boundary the IPC error contract enforces. Details go to the host log.
            events.Emit(options.Access.Module, MediaConversionEvents.Failed, new { source, reason = ex.GetType().Name });
            Log(options, () => $"[Shenora.Modules.Media] conversion FAILED: {ex}");
            throw;
        }
    }

    /// <summary>
    /// The answer while work this route started is still running: <c>503</c> with <c>Retry-After: 1</c>.
    /// <para>
    /// 503 rather than 404, because the distinction is real and a client can act on it — the resource is
    /// not missing, it is not ready.
    /// 🔴 <b>⚠ AND "A CLIENT" MEANS A <c>fetch</c> CLIENT. It does NOT mean a media element, which is what
    /// this paragraph claimed until 2026-08-13: "404 would tell a media element to give up permanently".</b>
    /// So it does — and so does this 503, identically, on both shells. Measured with the two arms on the
    /// SAME <c>&lt;video&gt;</c> in the same document (<c>RemuxRouteProbe.CheckFirstRequestAsync</c>;
    /// numbers in <c>.claude/knowledge/mobile-shells.md</c>): each raises <c>error</c> within ~70 ms with
    /// <c>error.code 4</c> (<c>MEDIA_ERR_SRC_NOT_SUPPORTED</c>), <c>readyState 0</c>,
    /// <c>networkState 3</c> (<c>NETWORK_NO_SOURCE</c>), rejects <c>play()</c> with
    /// <c>NotSupportedError</c>, and issues no further request for at least 12 s — an element is not a
    /// polling loop and neither status code makes it one. The 503 is still the right answer, for two
    /// reasons that survive the measurement: a retrying <c>fetch</c> client CAN act on it, and a page that
    /// re-points its element after <see cref="MediaConversionEvents.Ready"/> is served a <c>206</c> where a
    /// remembered 404 would have to be invalidated. What it does NOT buy is a bare
    /// <c>&lt;video src&gt;</c> surviving the wait.
    /// </para>
    /// <para>
    /// ⚠ <b>Internal rather than private because it is the ONE not-ready answer for all THREE delivery
    /// routes</b> — this one, <see cref="ComputedRemuxExtensions"/> while it plans, and <c>UseSegmentStream</c>
    /// while a segment is produced. The same reason <see cref="IsRemote"/> is shared: the retry INTERVAL is a
    /// contract with the page's own loop, so copies of it are numbers that can drift apart while every test
    /// still passes, and a page tuned to one would poll another wrongly. ⚠ The segment route held a
    /// byte-identical private copy until 2026-08-13, which is what made this worth centralising rather than
    /// asserting.
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
    /// An <see cref="IProgress{T}"/> that reports on the CALLER's thread — see the construction site for
    /// why <see cref="Progress{T}"/> is the wrong one here. Guarded, because the sink is ultimately an
    /// app-supplied bus subscriber and a throwing one must not fail the conversion it is only describing.
    /// </summary>
    private sealed class DirectProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => AppCallback.Run(() => report(value));
    }
}
