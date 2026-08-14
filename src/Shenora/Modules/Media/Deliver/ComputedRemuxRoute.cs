using Shenora;
using Shenora.Core.WebView;
using Shenora.Engine;
using Shenora.Engine.Missions;

namespace Shenora.Modules.Media;

/// <summary>
/// What planning a source ANSWERED — the four outcomes an app acts on differently.
/// </summary>
/// <remarks>
/// ⚠ There is deliberately no <c>Planning</c> member: <see cref="IComputedRemuxRoute.PlanAsync"/> does not
/// return until the walk has an answer, so a caller never has to interpret an in-between state. The route's
/// own state machine has one, and it is private for the same reason.
/// </remarks>
public enum MediaPlanOutcome
{
    /// <summary>
    /// Planned. Point an element at the URL — its first request is a <c>206</c>, not a <c>503</c>.
    /// </summary>
    Ready,

    /// <summary>
    /// This route will never serve this source, and that is a routing answer rather than an error: it is
    /// remote (a plan needs a seekable local file), or the output would LOSE something
    /// (<see cref="Mp4Remuxer.Plan"/> answered null). Use the conversion or segment path instead.
    /// </summary>
    Unplannable,

    /// <summary>
    /// Outside <see cref="MediaAccessOptions.AllowedRoots"/>, or no such file. ⚠ An APP bug or an attack
    /// path — not something to retry. The two are one outcome on purpose: distinguishing them would let a
    /// caller probe for a file's existence outside its own roots.
    /// </summary>
    Refused,

    /// <summary>
    /// The walk produced no answer — IO, an out-of-memory, a cancelled or dropped mission. Says nothing
    /// about the source, so it is worth retrying; nothing is remembered, exactly as on the request path.
    /// </summary>
    Failed,
}

/// <summary>
/// The handle <see cref="ComputedRemuxExtensions.UseComputedRemux"/> returns: dispose to remove the route,
/// and <see cref="PlanAsync"/> to WARM a source before a page asks for it.
/// </summary>
public interface IComputedRemuxRoute : IDisposable
{
    /// <summary>
    /// Plan <paramref name="source"/> now, and do not return until there is an answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>This is how a page keeps ONE plain <c>&lt;video src&gt;</c> (D72).</b> A source nobody has
    /// planned answers <c>503</c> while the walk runs, and a media element cannot ride that out — measured on
    /// both mobile shells, it errors within ~70 ms and never retries. The kit deliberately publishes no
    /// readiness event, because a page that must subscribe to one and set <c>src</c> from a handler is no
    /// longer a plain element, and at that integration cost segments are strictly more capable. So the wait
    /// moves EARLIER than the request, to the app — which already knows what it is about to play, since it
    /// built the URL.
    /// </para>
    /// <code>
    /// if (await route.PlanAsync(path, ct) is MediaPlanOutcome.Ready)
    ///     ShowPlayer(url);          // its first request is a 206
    /// </code>
    /// <para>
    /// ⚠ <b>It applies the request path's authorisation, not a shortened one</b> — the remote check, then
    /// containment against <see cref="MediaAccessOptions.AllowedRoots"/>, then the same identity key. A warm
    /// entry point that skipped it would be a way to make the kit walk any file the process can read, from
    /// app code that believed it was only hinting.
    /// </para>
    /// <para>
    /// ⚠ <b>Cheap to call again.</b> A planned source answers from the cache without touching the file, and
    /// two concurrent calls for the same source share ONE walk — the same <c>Claim</c> that stops an iOS
    /// burst of hundreds of requests submitting hundreds of walks.
    /// </para>
    /// <para>
    /// ⚠ <b>Cancelling stops the WAIT, not the walk.</b> The mission lives as long as the route does, so a
    /// cancelled call leaves the answer to be recorded anyway and the next caller gets it for free. Passing
    /// <see cref="CancellationToken.None"/> means waiting as long as the walk takes — seconds for a film,
    /// longer if a scheduler with one permit is busy with a transcode.
    /// </para>
    /// </remarks>
    /// <param name="source">The media file, as <see cref="MediaAccessOptions.Resolve"/> would have produced it.</param>
    /// <param name="cancellationToken">Stops WAITING; see the remarks.</param>
    Task<MediaPlanOutcome> PlanAsync(string source, CancellationToken cancellationToken = default);
}

/// <summary>
/// Serving a container repair as one ordinary URL: <b>an MP4 answered over HTTP ranges that has never been
/// produced</b>.
///
/// <para>
/// 🔴 <b>This is the payoff of D71's design, and the reason <see cref="Mp4Remuxer.Plan"/> exists.</b> A remux
/// copies frames, so the output follows from the source's frame index: the total length is known before any
/// work, and every byte of the output is either a header byte or a known frame's bytes. So a page needs one
/// <c>&lt;video src&gt;</c> and no manifest — a request answers <c>206</c> with a REAL <c>Content-Range</c>
/// total, the whole timeline is seekable, and a seek to the last minute is serviceable cold — measured on both
/// mobile shells 2026-08-12 (D71). Nothing is transcoded, nothing is written to disk, and there is no
/// production frontier to stall on — which is what separates this from the segment path. ⚠ <b>Once the source
/// has been PLANNED</b>: the first request for one nobody has planned yet is a <c>503</c>, which is the next
/// paragraph and the one thing a page has to be written for.
/// ⚠ <b>NOT "while almost nothing is buffered", which this paragraph claimed until 2026-08-12.</b> That figure
/// (<c>buffered=[0–8.3]</c>) came from D71's THROTTLED-body experiment, a different run; the computed route's
/// own run measured <c>buffered</c> equal to <c>seekable</c>, and it could hardly do otherwise: at the time,
/// the body was materialised per request, and anything over 64 MiB was declined outright.
/// ⚠ <b>Both halves of that last clause — the per-request buffer AND the 64 MiB decline — were retired on
/// 2026-08-13, by two INDEPENDENT changes.</b> <see cref="ComputedRemuxRoute.Produce"/> answers a range lazily,
/// through <see cref="Mp4LayoutRangeStream"/>, reading only the source bytes the platform actually asks for —
/// and that alone left the ceiling with nothing to justify it, because <b>the ceiling never bounded the WALK</b>
/// (it was tested against <see cref="Mp4Layout.TotalLength"/>, a number the walk PRODUCES, so a two-hour film
/// paid its whole walk and was then declined). Moving the walk into a mission is required for its own separate
/// reason — never block the platform's resource thread, at any size — and is not what made the ceiling
/// unjustified. See <see cref="ComputedRemuxExtensions.UseComputedRemux"/>'s <c>scheduler</c> parameter.
/// </para>
///
/// <para>
/// 🔴 <b>THE FIRST REQUEST FOR A SOURCE ANSWERS <c>503</c> WITH <c>Retry-After: 1</c>, NOT BYTES — and an
/// adopter has to know that, because it decides how the page is written.</b> A cache miss walks the whole
/// source's metadata (110–150 MB peak and seconds of IO for a two-hour film), and both mobile shells resolve
/// a webview resource SYNCHRONOUSLY — they need the status line and headers by the time the platform event
/// returns — so that walk cannot happen on the request path at any size. It runs in an
/// <see cref="IMissionScheduler"/> mission instead, exactly as <see cref="MediaConversionExtensions.UseMediaConversion"/>
/// runs a conversion, and every request that arrives meanwhile is answered <c>503</c> with
/// <c>Retry-After: 1</c> — the identical answer, from the identical helper. A page retrying about once a
/// second gets its <c>206</c> as soon as the plan lands; a bare <c>&lt;video src&gt;</c> pointed at a source
/// nobody has planned yet will raise <c>error</c> on that first 503 and must be re-pointed, which is the same
/// contract — and the same honest outcome — the conversion route already states.
/// 🔴 <b>THAT SENTENCE IS MEASURED NOW, on both shells (2026-08-13,
/// <c>.claude/knowledge/mobile-shells.md</c>), and it is worse than it sounds: to a media element this 503 is
/// INDISTINGUISHABLE FROM A 404.</b> Both arms on the same element give <c>error.code 4</c>,
/// <c>readyState 0</c>, <c>networkState 3</c>, a <c>play()</c> rejected <c>NotSupportedError</c> — within
/// ~70 ms — and NO further request for at least 12 s, on Android and on iOS alike (iOS's two requests are
/// AVFoundation's <c>bytes=0-1</c> + <c>bytes=0-1445</c> sniff pair, issued 30 ms apart, not a retry).
/// Re-pointing <c>src</c> once the plan has landed plays it immediately. So the page contract is real and the
/// element's own recovery is not: <b>whoever writes the page half must re-point after some signal</b> — which
/// is the argument for the event the next paragraph declines to add, now with a measurement behind it rather
/// than a preference.
/// ⚠ <b>No event is emitted when a plan becomes ready, and the honest reason is D63 rather than
/// proportion.</b> Nothing consults one yet, and a seam nothing reads is the defect that rule exists for — so
/// it should arrive WITH its page-side consumer. The positive case for the 503 alone is that an event here
/// would have to be a SECOND kind of "ready" for the same source on the same
/// <see cref="MediaAccessOptions.Module"/> as <see cref="MediaConversionEvents.Ready"/>, which a page could not
/// tell apart from the conversion route's — and a plan is one metadata walk rather than a transcode, so the
/// wait is seconds.
/// 🔴 <b>What must NOT be inferred from that: this kit has no page-side retry loop, and its established
/// contract for a 503 is EVENT-driven.</b> <see cref="MediaConversionExtensions.UseMediaConversion"/>'s own
/// remarks say a page "learns from <see cref="MediaConversionEvents.Ready"/> when to set its element's source",
/// and every retry loop in this repo today is either a HOST-side probe or a test helper. So whoever writes the
/// page half of this route is choosing between teaching a page to poll and adding the event — and if it is the
/// event, this paragraph is the argument to overturn, not evidence that the question was already settled.
/// </para>
///
/// <para>
/// 🔴 <b>A source it cannot plan FALLS THROUGH rather than failing, and that fall-through IS the D71 split.</b>
/// <see cref="Mp4Remuxer.Plan"/> answers null for anything whose output would LOSE something — a stream needing
/// a re-encode, a second dub, a track that declares itself and holds no frames — because a layout is a length
/// and a byte map with no channel for what it cost. Those sources belong on the conversion or segment path,
/// where a re-encoder can help and the loss is reportable. ⚠ Answering 404 for them instead would make every
/// <see cref="MediaPlaybackAction.Transcode"/> source permanently unplayable, and it would look like a working
/// route until someone opened a film with AC-3 sound. <b>A walk that FAILS falls through too</b> — see
/// <see cref="ComputedRemuxRoute.PlanSource"/>: a source that 503s for ever would be that same unplayable film
/// wearing a different status code.
/// </para>
///
/// <para>
/// ⚠ <b>REGISTER IT BEFORE the conversion route</b> (middleware run in registration order):
/// <code>
/// using var computed   = interceptor.UseComputedRemux(scheduler, access);             // FIRST — serves what it can plan
/// using var conversion = interceptor.UseMediaConversion(scheduler, events, options);   // then the rest
/// </code>
/// Registered the other way round, the conversion route answers every request its own
/// <see cref="MediaAccessOptions.Resolve"/> matches — so a plannable film would answer <c>503</c> while a whole
/// transcode ran, and this route would be dead code that still passed every test of its own. ⚠ Both routes
/// take the SAME scheduler in that composition, and should: a plan and a conversion for the same library are
/// competing for the same device, so one global concurrency bound over both is the point of the scheduler
/// existing (<c>IMissionScheduler.GlobalLane</c>).
/// </para>
///
/// <para>
/// ⚠ <b>What that order changes for an app whose cache is ALREADY POPULATED, said here rather than left to be
/// discovered:</b> a source with a finished artifact under <see cref="MediaAccessOptions.CacheRoot"/> is now
/// PLANNED and served from the plan, because this route answers before the conversion route and never consults
/// that cache. Nothing plays differently — a plannable source is one every stream of which is copied untouched,
/// so the plan describes exactly the file the converter would have written — but the ready artifact stops being
/// read, and a metadata walk is paid once per source instead. ⚠ <b>That USED to be bounded, and is not any
/// more:</b> until 2026-08-13 only outputs of at most 64 MiB reached this path, so anything big enough for a
/// cache to be worth much was declined straight back to the route that owns it. With the ceiling gone this
/// applies at ANY size — a two-hour film with a finished conversion beside it is planned and served from the
/// plan. ⚠ If the app supplied its OWN <see cref="IMediaContainerWriter"/>, the plan describes the KIT's output
/// rather than that artifact's — register this route after the conversion one, or not at all, if a cached
/// artifact must win.
/// </para>
/// </summary>
public static class ComputedRemuxExtensions
{
    /// <summary>
    /// Register the computed-remux route on one interceptor.
    /// </summary>
    /// <param name="interceptor">
    /// The shell's interceptor. It supplies the platform's <see cref="IWebViewInterceptor.RangeDelivery"/>
    /// rule, which is read here rather than passed in — it is a measured platform fact, and a value an app
    /// could set is a value an app can copy from the wrong shell (D44).
    /// </param>
    /// <param name="scheduler">
    /// Where the metadata WALK runs. <b>This is the whole reason the route has a scheduler at all, and it is
    /// not an optimisation:</b> planning a source peaks on the order of 110–150 MB and takes seconds for a
    /// two-hour film, both mobile shells resolve a resource synchronously, and a webview callback is therefore
    /// the one place that walk may not happen. So it is submitted as a mission and the request answers
    /// <c>503 Retry-After: 1</c> until the plan lands — the same shape, for the same reason, as
    /// <see cref="MediaConversionExtensions.UseMediaConversion"/>.
    /// <para>
    /// ⚠ <b>Pass the app's ONE scheduler</b> — the same instance the conversion route and everything else
    /// gets, resolved from DI (<c>services.GetRequiredService&lt;IMissionScheduler&gt;()</c>). A scheduler per
    /// route is a concurrency bound per route, which is exactly the "three of these at once on a phone" case
    /// the global lane exists to prevent.
    /// </para>
    /// <para>
    /// ⚠ <b>What sharing it costs, so it is a decision rather than a surprise:</b> a plan competes for the same
    /// permits as the app's conversions. A walk is submitted at <see cref="MissionDefinition.Priority"/> 1 so it
    /// outranks anything QUEUED at the default 0 — see <c>StartPlanning</c> — but a transcode already RUNNING
    /// holds its permit to the end, and <c>MissionSchedulerOptions.GlobalLaneCapacity</c> defaults to
    /// <c>clamp(cores-1, 1, 4)</c>, which is ONE on a small device. There, a film that could be planned in
    /// seconds answers <c>503</c> until a running conversion finishes. If an app converts in the background
    /// while a page plays, give the scheduler more than one permit, or put its conversions on a narrow lane of
    /// their own.
    /// </para>
    /// <para>
    /// ⚠ It needs no claim scope registered: a plan writes nothing, so the mission declares no
    /// <c>PathClaims</c> and a scheduler configured with no <c>Scopes</c> at all still works. That is
    /// deliberate — a submit-time throw for an unregistered scope would be a source this route could never
    /// plan, and the diagnostic would be one host-log line.
    /// </para>
    /// </param>
    /// <param name="options">
    /// Where media may be read from and how a URL maps to a source — the same object the app's other delivery
    /// routes take, so containment is stated ONCE (D71). ⚠ <see cref="MediaAccessOptions.CacheRoot"/> is
    /// unused by this route: there is no artifact. It is on the shared object because the routes that DO write
    /// need it.
    /// </param>
    /// <returns>
    /// The route handle. Dispose to remove the route, drop the layouts it is holding AND cancel a walk still
    /// in flight — a route outliving the page it served would answer for the next one, a layout outliving the
    /// route is tens of megabytes nobody can reach, and a walk outliving both is seconds of IO for an answer
    /// nobody will read. ⚠ <b>Keep it for <see cref="IComputedRemuxRoute.PlanAsync"/> as well as for disposal
    /// (D72)</b>: warming a source before the page asks is what keeps the page one plain
    /// <c>&lt;video src&gt;</c>, and the handle is the only way to reach it.
    /// </returns>
    public static IComputedRemuxRoute UseComputedRemux(this IWebViewInterceptor interceptor,
        IMissionScheduler scheduler, MediaAccessOptions options)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Resolve);

        // 🔴 THE ORDER CHECK, which existed only as prose until 2026-08-13. Middleware run in registration
        // order, so a conversion route registered FIRST answers everything its own `Resolve` matches: a
        // plannable film 503s through a whole transcode and THIS route becomes dead code that still passes
        // every test of its own. Reports rather than throws because the two predicates may not overlap at
        // all — see `MediaAccessOptions.ConversionRegistered`.
        if (options.ConversionRegistered)
        {
            AppCallback.Log(options.Log, () =>
                "[Shenora.Modules.Media] ⚠ UseComputedRemux was registered AFTER UseMediaConversion on the "
                + "same MediaAccessOptions. Middleware run in registration order, so if their Resolve "
                + "predicates overlap the conversion route answers first and this route never serves — a "
                + "plannable film would transcode instead of being remuxed. Register this one FIRST, or "
                + "give the two routes non-overlapping paths (see docs/guides/media.md).");
        }

        return ComputedRemuxRoute.Use(interceptor, scheduler, options);
    }
}

/// <summary>
/// The middleware behind <see cref="ComputedRemuxExtensions.UseComputedRemux"/>: resolve → authorise → plan
/// (once per source identity, in a mission) → answer a byte range out of the plan.
/// </summary>
/// <remarks>
/// <para>
/// It owns exactly one piece of state — the layout cache — and that is the whole reason it is a class rather
/// than a closure. That cache is also the route's whole state MACHINE now: an entry says planned, planning,
/// unplannable or failed (<see cref="PlanState"/>), which is what lets the request path answer without
/// waiting for anything. See <see cref="PlanSource"/> for why planning cannot happen on the request path at
/// all, and <see cref="Store"/> for what bounds the cache.
/// </para>
/// </remarks>
internal sealed class ComputedRemuxRoute : IDisposable
{
    /// <summary>
    /// ⚠ <b>The type of what is SENT, never what the source file is.</b> The source is Matroska, so a type
    /// derived from its path would say <c>video/x-matroska</c> for a body that is MP4 — and a media element
    /// told the wrong container refuses before it has tried a byte, which reads as "this file will not play"
    /// rather than as a wrong header.
    /// </summary>
    private const string OutputContentType = "video/mp4";

    /// <summary>
    /// How often <see cref="PlanAsync"/> re-reads the cache while ANOTHER walk owns the source.
    /// <para>
    /// ⚠ Deliberately the same one second as the <c>Retry-After</c> a page is told to wait
    /// (<see cref="MediaConversionExtensions.NotReadyYet"/>), because the thing being waited on is the same
    /// walk. It is not read off that response — an internal poll and a wire header are different contracts —
    /// but they should move together, and a second copy of the NUMBER is the drift this comment exists to
    /// catch.
    /// </para>
    /// </summary>
    private static readonly TimeSpan PlanPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long <see cref="PlanAsync"/> waits on ANOTHER walk before saying so once in the host log. Not a
    /// timeout — see the comment at the wait — just the point past which silence stops being reasonable.
    /// </summary>
    private static readonly TimeSpan PlanWaitWarnAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The cache-key variant, so a source's PLAN can never be confused with its conversion or its segments —
    /// the same reason <c>SegmentStream</c> passes <c>"hls"</c>.
    /// </summary>
    private const string CacheVariant = "mp4-plan";

    /// <summary>
    /// How many planned layouts to keep. See <see cref="Store"/> for the reasoning, which is the memory
    /// budget rather than a round number.
    /// </summary>
    private const int MaxCachedLayouts = 4;

    /// <summary>
    /// The mission kind, as it appears in a queue view or a diagnostics snapshot. Named for the WORK rather
    /// than for the route, so "why is this phone busy?" is answerable from the scheduler alone.
    /// </summary>
    private const string PlanMissionKind = "media-remux-plan";

    private readonly MediaAccessOptions _options;
    private readonly WebViewRangeDelivery _delivery;
    private readonly IMissionScheduler _scheduler;

    /// <summary>What has been planned, or is being planned, by source identity. Guarded by <see cref="_gate"/>.</summary>
    private readonly Dictionary<string, Entry> _plans = new(StringComparer.Ordinal);

    /// <summary>Monotonic use counter for the eviction order. Guarded by <see cref="_gate"/>.</summary>
    private long _clock;

    private readonly object _gate = new();

    /// <summary>
    /// Cancels a walk still in flight when the route is disposed. <b>NOT the request's token</b> — see
    /// <see cref="StartPlanning"/> for why tying a plan to the request that triggered it would cancel every
    /// plan the moment its 503 went out.
    /// </summary>
    private readonly CancellationTokenSource _closing = new();

    /// <summary>
    /// Whether the "remote sources are not this route's" line has already been logged. A bool rather than a
    /// set of urls: the line names no url, so once is all it is worth, and a set would be unbounded state for a
    /// source this route deliberately keeps none for.
    /// </summary>
    private bool _loggedRemoteDecline;

    private bool _disposed;

    /// <summary>
    /// What turns a source stream into a layout. <see cref="Mp4Remuxer.Plan"/> in every shipped path; a stub in
    /// four tests — see <see cref="Use"/>.
    /// </summary>
    private readonly Func<Stream, CancellationToken, Mp4Layout?> _plan;

    private ComputedRemuxRoute(MediaAccessOptions options, WebViewRangeDelivery delivery,
                               IMissionScheduler scheduler, Func<Stream, CancellationToken, Mp4Layout?> plan)
    {
        _options = options;
        _delivery = delivery;
        _scheduler = scheduler;
        _plan = plan;
    }

    /// <summary>
    /// Build and register the middleware.
    /// </summary>
    /// <param name="interceptor">The shell's interceptor; supplies the platform's range-delivery rule.</param>
    /// <param name="scheduler">Where the metadata walk runs — see <see cref="PlanSource"/>.</param>
    /// <param name="options">The app's resolver, roots and log.</param>
    /// <param name="plan">
    /// How to plan a source. Null means <see cref="Mp4Remuxer.Plan"/>, which is what every shipped call gets.
    /// <para>
    /// ⚠ <b>A TEST-ONLY seam, and it exists because these invariants were otherwise held by a comment alone.</b>
    /// "The walk does not run on the caller's thread" needs a plan that can be held STILL while the request is
    /// asserted about; "a film already playing keeps being served while another is planned" needs the same;
    /// "a planning FAILURE falls through rather than 503ing for ever" needs a walk that throws on demand; and
    /// the read-failure recovery needs a layout that does not describe its source. None of the four is
    /// reachable through a fixture a unit test can stage.
    /// </para>
    /// <para>
    /// It is not configuration and it is not public: <c>UseComputedRemux</c> has no parameter for it, and
    /// <c>InternalsVisibleTo("Shenora.Tests")</c> is how it is reached — the same shape, and the same
    /// justification, as the test seams on <c>ShenoraEnvironment</c> and <c>ShenoraPaths</c>. D63's rule is
    /// about a seam nothing consults; a test consuming this one is the consultation.
    /// </para>
    /// </param>
    internal static IComputedRemuxRoute Use(IWebViewInterceptor interceptor, IMissionScheduler scheduler,
                                    MediaAccessOptions options,
                                    Func<Stream, CancellationToken, Mp4Layout?>? plan = null)
    {
        var route = new ComputedRemuxRoute(options, interceptor.RangeDelivery, scheduler, plan ?? Mp4Remuxer.Plan);
        var registration = interceptor.Use((request, next, cancellationToken) =>
            route.Answer(request, cancellationToken) is { } response
                ? Task.FromResult<WebViewResourceResponse?>(response)
                // Null from Answer means "not mine" — the request must continue down the REST of the pipeline,
                // which is the whole difference between a middleware and a handler, and here it is also the
                // D71 routing decision: a source this path cannot serve belongs to the one behind it.
                : next(request, cancellationToken));

        return new Registration(registration, route);
    }

    /// <summary>
    /// Answer one request, or null to decline it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order of the steps is the design, and each one is a cost or a hazard:
    /// </para>
    /// <list type="number">
    /// <item><b>Resolve</b> — not ours, nothing else runs.</item>
    /// <item><b>Remote?</b> Declined, because a plan needs a seekable local file — and because the conversion
    /// route behind this one DOES accept a url behind its SSRF policy. Answering 404 here would make every
    /// remote conversion unreachable the moment this route is registered in front of it.</item>
    /// <item><b>Containment, BEFORE anything touches the file</b> — a page-supplied path that escapes its
    /// roots must not cost a metadata walk of whatever it names, and the refusal is the same fixed 404 as a
    /// missing file so nothing can probe for existence by comparing responses.</item>
    /// <item><b>Identity</b> — length and mtime, which is what the cached plan is keyed on.</item>
    /// <item><b>The cached answer</b> — a layout, a walk already in flight (<c>503</c>), a remembered refusal,
    /// or a walk that failed. All four cost one dictionary lookup, and the three that are not a layout answer
    /// without the file being opened at all — which matters because a 503 is answered about once a second per
    /// source for as long as a walk takes, and a remembered refusal about once a second for as long as the
    /// CONVERSION behind it takes.</item>
    /// <item><b>On a miss: submit the walk and answer <c>503</c></b> — never walk here. See
    /// <see cref="PlanSource"/>.</item>
    /// <item><b>Only with a layout in hand: open the source and answer the range out of it.</b></item>
    /// </list>
    /// <para>
    /// ⚠ <b>Nothing here is <c>async</c>, deliberately.</b> Both mobile shells resolve a resource
    /// SYNCHRONOUSLY — they need the status line and headers by the time the platform event returns — so this
    /// path may not await anything. What it does instead is bounded, and now bounded by construction rather
    /// than by a size ceiling: a dictionary lookup, plus a <c>stat</c>, plus (only once a layout exists) one
    /// file open. The walk that used to happen here on a cache miss is a mission's job now.
    /// </para>
    /// <para>
    /// ⚠ An <see cref="OperationCanceledException"/> propagates rather than becoming a 404: the caller
    /// abandoned the request, and turning that into a response would tell a media element the file is missing.
    /// <b>Both shells pass <see cref="CancellationToken.None"/> today</b> (<c>WebViewHost</c>,
    /// <c>MobileWebViewInterceptor</c>), so nothing can currently cancel — the token is threaded and honoured
    /// through the single check <see cref="Produce"/> makes before handing out a body, and this paragraph
    /// describes that contract rather than something observable yet. ⚠ It is NOT the token the WALK observes;
    /// that one belongs to the mission (<see cref="StartPlanning"/>).
    /// </para>
    /// <para>
    /// ⚠ <b>What it does NOT cover any more: the lazy body's own reads.</b> <see cref="Produce"/> used to read
    /// the whole range synchronously, under this token's cover, inside this call. Now the bytes are pulled
    /// later — by the platform, through <see cref="Mp4LayoutRangeStream"/> and <see cref="BoundedBodyStream"/>,
    /// neither of which takes a <see cref="CancellationToken"/> on <c>Read</c> — so a cancellation arriving
    /// after this method has already returned a response has nothing left here to observe it. That is the same
    /// shape <see cref="BoundedBodyStream"/> already accepted for the plain file path, not a gap this route
    /// introduced on its own.
    /// </para>
    /// </remarks>
    private WebViewResourceResponse? Answer(WebViewResourceRequest request, CancellationToken cancellationToken)
    {
        if (_options.Resolve(request.Uri) is not { } requested) return null;

        // ⚠ The SAME predicate the conversion route authorises with, on purpose — see MediaConversion's own
        // remarks. Two answers to "is this remote?" would eventually leave a source neither route will serve.
        if (MediaConversionExtensions.IsRemote(requested, out _))
        {
            // ⚠ Logged ONCE per route, not once per request, and the asymmetry with the refusal below is
            // deliberate. A remote source is a NORMAL request here — the conversion route behind answers
            // `503 Retry-After: 1`, so a page retries about once a second for the whole conversion and every
            // retry passes through this branch. One line answers "why is the computed route not taking this?";
            // six hundred of them bury the answer, which is the same trap as a spin loop logging its wait.
            // The containment refusal below stays per-request on purpose: that one is an error or an attack,
            // and how OFTEN it happens is the interesting part.
            if (!Interlocked.Exchange(ref _loggedRemoteDecline, true))
            {
                Log(() => "[Shenora.Modules.Media] computed remux declines remote sources — a plan needs a "
                        + "seekable local file, so the conversion route can have them (logged once)");
            }
            return null;
        }

        if (!TryContainAndKey(requested, out var contained, out var key)) return WebViewResourceResponse.NotFound();

        Mp4Layout layout;
        switch (Claim(key, out var known))
        {
            case PlanState.Ready:
                layout = known!;
                break;

            case PlanState.Claimed:
                // Submitted OUTSIDE the lock, and after the entry is already marked — see `Claim`.
                StartPlanning(key, contained);
                return MediaConversionExtensions.NotReadyYet();

            case PlanState.Planning:
                // ⚠ Not logged. This is the answer to about one request per second per source for as long as
                // the walk takes, and the line that IS worth having was written when the walk was claimed.
                return MediaConversionExtensions.NotReadyYet();

            case PlanState.Failed:
                // 🔴 A FAILED WALK MUST FALL THROUGH, or this source 503s for ever — which is the same
                // "unplayable by every route" outcome the null-plan fall-through exists to prevent, wearing a
                // different status code. The entry was CONSUMED by `Claim`, so the next request plans again:
                // an OOM or a transient IO error says nothing about the source (see `PlanSource`).
                Log(() => "[Shenora.Modules.Media] computed remux declines a source whose plan FAILED, so a "
                        + "conversion or segment route can have it; the next request will try planning again");
                return null;

            default:   // Unplannable — planned, and not this path's file
                return null;
        }

        FileStream source;
        try
        {
            // 🔴 `FileShare.Delete`, not the plain `FileShare.Read` a `File.OpenRead` would give — a DECISION,
            // not an oversight, made once a lazy body existed to raise the question. This handle can now stay
            // open for as long as the platform holds the response, including a request the page ABANDONS
            // before EOF (a seek away, a navigation) — see `Produce`'s own remarks on when it closes. A
            // buffered body never had this problem: it closed the handle before `Answer` returned, so an app
            // deleting or replacing the same film mid-stream got a clean OS-level retry rather than a sharing
            // violation held open by US.
            // ⚠ NOT widened to `FileShare.ReadWrite` too, and that is the half that matters: `FileShare.Read`
            // (what this keeps) still refuses a SECOND writer while our handle is open, which is what keeps
            // the already-documented "replaced between stat and read" hazard (see `Produce.OnReadFailure`)
            // NARROW — it needs two separate opens racing each other, not a rewrite arriving mid-stream. Adding
            // write-sharing would let a concurrent WRITE tear the very bytes this open read is mid-way through,
            // turning that same-length-same-mtime blind spot from a rare double-open race into the routine case
            // for any file a lazy body is actively serving — strictly worse, and silent. `FileShare.Delete`
            // carries none of that risk: NTFS defers an actual delete until every handle closes, so a read
            // already in flight keeps seeing the same, unmodified bytes regardless. It also costs nothing on
            // POSIX (Android/iOS), where unlink-while-open needs no share flag to permit at all.
            // ⚠ The WALK opens the same file with the same flags (see `PlanSource`) — one rule, two openers.
            source = new FileStream(contained, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception ex)
        {
            // Contained, present, planned and unreadable — a lock or a permission, not a routing decision. 404
            // with the reason in the host log, the same answer the file middleware gives for an unreadable
            // file. ⚠ An open that fails BEFORE a plan exists is a different case and answers differently: it
            // happens inside the walk, so it is a declined source rather than a 404 (see `PlanSource`).
            Log(() => $"[Shenora.Modules.Media] computed remux could not open a source ({ex.GetType().Name})");
            return WebViewResourceResponse.NotFound();
        }

        // Ownership of `source` starts here. A lazily-read body (see `Produce`) needs the handle to outlive
        // this method — the platform reads it AFTER `Answer` returns — so ownership is tracked explicitly via
        // `produced` rather than closed unconditionally the way a buffered body could afford to. Every path
        // that leaves WITHOUT handing `source` to `Produce` still owns it and must close it itself here; every
        // path that DOES reach `Produce` leaves the closing to it (see that method's own remarks).
        var produced = false;
        try
        {
            // The range arithmetic, the status line and the platform's body rule all come from
            // WebViewFiles.ServeRange — the ONE implementation of D44 — and only the bytes are this route's.
            return WebViewFiles.ServeRange(request, layout.TotalLength, OutputContentType, _delivery,
                (from, count) =>
                {
                    produced = true;   // `Produce` owns `source` from here — see its own remarks on disposal
                    return Produce(key, layout, source, from, count, cancellationToken);
                });
        }
        finally
        {
            // `ServeRange` does not always call `read` — a range past the end (416) answers without ever
            // reaching `Produce` — so this is the only remaining chance to close the handle; every path that
            // DID reach `Produce` already had it closed there (on decline, on a construction-time failure) or
            // handed to the returned body (on success).
            if (!produced) source.Dispose();
        }
    }

    /// <summary>
    /// Produce the output's bytes <c>[from, from + count)</c> out of the plan, as a LAZILY-read body rather
    /// than a buffered one.
    ///
    /// <para>
    /// 🔴 <b>This used to buffer the WHOLE OUTPUT into a <see cref="MemoryStream"/> on every request, on every
    /// platform — not just on Android.</b> Under <see cref="WebViewRangeDelivery.Unsliced"/> (Android) the
    /// body is the whole output by definition, since the platform applies the range start itself; under
    /// <see cref="WebViewRangeDelivery.Sliced"/> a FASTSTART file, which this output always is (<c>moov</c>
    /// before <c>mdat</c>), "only ever requests <c>bytes=0-</c>" (D44). So the memory to plan for was always
    /// the film, not the window — which is exactly what <see cref="Mp4LayoutRangeStream"/> now removes: it
    /// answers the same range by seeking and copying only the source bytes actually touched, the moment the
    /// PLATFORM asks for them, never before.
    /// </para>
    /// <para>
    /// 🔴 <b>There is no size ceiling on this route any more (2026-08-13). A 64 MiB one used to decline any
    /// source planning past it, and laziness (above) is what retired its whole justification</b> — the number
    /// was sized for a buffered body's footprint, and once no body is buffered it bounds nothing that exists.
    /// ⚠ <b>It did NOT survive as a bound on the metadata walk, and the version of this paragraph that said so
    /// was WRONG about the mechanism</b>: the check ran against <see cref="Mp4Layout.TotalLength"/>, which the
    /// walk PRODUCES, so a two-hour film paid its entire walk on the platform's resource thread and was only
    /// then declined. An output-size ceiling cannot bound a walk, for the arithmetical reason that the walk
    /// computes the size — anyone reintroducing a bound wants a PRE-walk figure (the source file's own length),
    /// not this one. The walk moving into a mission (<see cref="PlanSource"/>) is a separate, independently
    /// necessary change: a webview's resource thread must not be blocked at any size. What replaced the ceiling
    /// is a <c>503</c>, not a bigger constant.
    /// </para>
    /// <para>
    /// 🔴 <b>Ownership of <paramref name="source"/> passes onward on every path out of this method — there is
    /// no path that leaves it for a caller to close.</b> On decline (<paramref name="count"/> zero) and on a
    /// construction-time failure this method disposes it directly; on success it hands
    /// <paramref name="source"/> to a new <see cref="Mp4LayoutRangeStream"/> built with <c>ownsSource: true</c>,
    /// wrapped in a <see cref="BoundedBodyStream"/> exactly as <see cref="WebViewFiles.Read"/> wraps a plain
    /// file — the same seam, the same measured contract: closes itself the instant its bound is reached
    /// (Android disposes a response's <c>Content</c> after reading it to EOF; iOS never does — measured
    /// 2026-08-12) and survives being disposed again by whichever platform still bothers to.
    /// </para>
    /// <para>
    /// ⚠ <b>A read-time failure — the source moved out from under a cached plan — can no longer be caught
    /// HERE, and that is an accepted, unavoidable consequence of being genuinely lazy</b> (see
    /// <see cref="WebViewFiles.Read"/>'s own doc, which states the identical tradeoff for the plain file path).
    /// This method's own try/catch only ever sees a CONSTRUCTION-time failure now; the actual read happens
    /// later, after a status line and <c>Content-Length</c> have already gone out. What still has to happen —
    /// dropping the cached plan so the NEXT request re-plans instead of repeating the same broken read forever
    /// — is wired through <see cref="Mp4LayoutRangeStream"/>'s <c>onReadFailure</c> callback instead, which
    /// fires at the point the failure is actually discovered and calls the exact same <see cref="Forget"/> this
    /// method used to call inline.
    /// </para>
    /// </summary>
    private Stream? Produce(string key, Mp4Layout layout, Stream source, long from, long count,
                            CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            // Nothing will ever read this handle — this is the only chance left to close it.
            source.Dispose();
            return new MemoryStream([], writable: false);
        }

        // Shared with the read-failure callback below, so a construction-time failure (caught here) and a
        // read-time one (discovered later, inside Mp4LayoutRangeStream) log and forget identically — the two
        // are the same failure, only DISCOVERED at different points now that the read is lazy.
        void OnReadFailure(Exception ex)
        {
            // 🔴 WHAT THIS CATCHES, AND — MORE IMPORTANTLY — WHAT IT CANNOT. `Mp4LayoutRangeStream` throws
            // `EndOfStreamException` only when the source is SHORTER than a planned span needs, and a
            // truncation changes the length, which changes the key, which re-plans. So the case that reaches
            // here is the narrow one where the source is replaced BETWEEN this request's stat and its read.
            // ⚠ A same-LENGTH, same-mtime rewrite in place is invisible to every layer: the key cannot see it
            // (`DerivedCacheKey` is identity+length+mtime and says so — "not a content hash, and not trying to
            // be"), and every span still resolves to a valid offset, so the response committed a 206 with a
            // correct `Content-Range` over bytes that turn out WRONG once actually read. That is the
            // mechanism's documented limit rather than this route's bug — `MediaConversion` serves a cached
            // conversion under exactly the same rule — and it is stated here because this is where a reader
            // will look for it. A real guarantee needs a content hash, which the cache key deliberately
            // refuses to pay for.
            Log(() => $"[Shenora.Modules.Media] computed remux could not read a planned range "
                    + $"({ex.GetType().Name}) — dropping the cached plan so the next request re-plans");

            // 🔴 DROP THE CACHED PLAN, OR THIS FILM IS BROKEN FOREVER. The layout is still cached and the key
            // (path + length + mtime) has not changed, so every later request would hit the same entry and
            // fail the same way until eviction or route disposal — a PERMANENTLY unplayable film, which is
            // exactly the outcome this route's contract exists to prevent. The fall-through rule held for
            // PLANNING failures and must hold for PRODUCTION ones too, even now that the failure surfaces at
            // read time instead of here.
            // ⚠ Forgetting, not remembering a refusal, and the cause is why: a transient IO error or an OOM
            // says nothing about the source, so the next request must ask again. (A same-length rewrite simply
            // fails again — correctly — at the cost of one more attempt.)
            Forget(key);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var range = new Mp4LayoutRangeStream(layout, source, from, from + count - 1,
                ownsSource: true, onReadFailure: OnReadFailure);
            return new BoundedBodyStream(range, count, msg => Log(() => msg));
        }
        catch (OperationCanceledException)
        {
            // The caller went away. Not a response, and nothing else will ever own `source`.
            source.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            // A construction-time failure only — see the type's own remarks above on why a READ failure can
            // no longer land here.
            OnReadFailure(ex);
            source.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Drop one entry so the next request for that source plans it afresh — the inverse of <see cref="Store"/>.
    /// Why it exists is written at its call sites: a failure that leaves its bad answer CACHED is a permanent
    /// failure.
    /// </summary>
    private void Forget(string key)
    {
        lock (_gate) _plans.Remove(key);
    }

    /// <summary>
    /// What is known about one source — and, when nothing is, CLAIM the walk for this caller in the same
    /// atomic step.
    ///
    /// <para>
    /// 🔴 <b>Deciding and claiming cannot be two calls, and that is why this reads as one odd method rather
    /// than a tidy <c>TryGet</c>.</b> iOS opens a container with HUNDREDS of range requests, so the arrival
    /// pattern this route really sees is a burst for one source: look-then-claim would let every request in a
    /// burst find the same miss and submit its own walk. The mission's <see cref="MissionKey"/> would collapse
    /// them anyway, but relying on that puts the correctness of "plan once" in another component.
    /// </para>
    /// <para>
    /// ⚠ <b><see cref="PlanState.Failed"/> is CONSUMED here rather than returned twice.</b> A failed walk must
    /// make exactly this request fall through — so the route behind gets its chance — while leaving the source
    /// re-plannable, because an OOM or a transient IO error says nothing about it. Removing the entry as it is
    /// read is what gives both: this request declines, the next one submits a fresh walk.
    /// </para>
    /// <para>
    /// ⚠ Every hit also touches the eviction clock, including a <see cref="PlanState.Planning"/> one: the
    /// retries arriving while a big film is walked are what keep its own entry from being evicted underneath
    /// it.
    /// </para>
    /// </summary>
    /// <param name="key">The source's identity key.</param>
    /// <param name="layout">The layout, when the answer is <see cref="PlanState.Ready"/>; null otherwise.</param>
    private PlanState Claim(string key, out Mp4Layout? layout)
    {
        layout = null;
        lock (_gate)
        {
            if (_plans.TryGetValue(key, out var entry))
            {
                entry.Used = ++_clock;
                switch (entry.State)
                {
                    case PlanState.Ready:
                        layout = entry.Layout;
                        return PlanState.Ready;
                    case PlanState.Failed:
                        _plans.Remove(key);
                        return PlanState.Failed;
                    default:
                        return entry.State;   // Planning or Unplannable, both answered without the file
                }
            }

            // Disposed: the registration is already out of the pipeline, so this is a request that raced it.
            // Declining hands it to whatever is left behind rather than starting work for a dead route.
            if (_disposed) return PlanState.Unplannable;

            StoreLocked(key, PlanState.Planning, layout: null);
            return PlanState.Claimed;
        }
    }

    /// <summary>
    /// Contain a resolved source, stat it, and derive the cache key — the steps <see cref="Answer"/> and
    /// <see cref="PlanAsync"/> MUST take identically.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Shared because a warm entry point with its own copy of this is a security hole waiting to
    /// drift.</b> `PlanAsync` reaches the same walk from app code; if its containment check were a second
    /// implementation, the two would eventually disagree about which files may be opened, and the one an app
    /// calls directly is the one nobody tests against a hostile path. One implementation, one order.
    /// ⚠ <b>Returns false for BOTH "outside the roots" and "no such file", and only the first is logged</b> —
    /// exactly as before this was factored out. Answering them differently is how a caller probes for a
    /// file's existence outside its own roots by comparing responses.
    /// </remarks>
    private bool TryContainAndKey(string requested, out string contained, out string key)
    {
        contained = string.Empty;
        key = string.Empty;

        if (WebViewFiles.ResolveContained(requested, _options.AllowedRoots) is not { } within)
        {
            // No path in the message: this one is reached by a page-supplied value, which is exactly the kind
            // that must not be echoed anywhere.
            Log(() => "[Shenora.Modules.Media] computed remux refused a source outside the allowed roots");
            return false;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(within);
            if (!info.Exists) return false;
        }
        catch (Exception ex)
        {
            // No exception text on the wire, ever — a path is the likeliest thing it would carry.
            Log(() => $"[Shenora.Modules.Media] computed remux could not stat a source ({ex.GetType().Name})");
            return false;
        }

        // 🔴 IDENTITY, not the path. A layout carries none of its own (`Plan` takes a Stream), so applying
        // film A's byte map to film B's bytes resolves every span to a valid offset in the WRONG file and
        // serves a garbage picture with no error anywhere. Length AND mtime, because they fail differently:
        // a restore can preserve an mtime while changing the bytes, an edit can change the bytes without
        // moving the length.
        contained = within;
        key = DerivedCacheKey.For(within, info.Length, info.LastWriteTimeUtc, CacheVariant);
        return true;
    }

    /// <inheritdoc cref="IComputedRemuxRoute.PlanAsync"/>
    /// <remarks>
    /// 🔴 <b>It reuses the REQUEST path's machinery rather than planning on its own</b> — the same
    /// <see cref="Claim"/>, the same mission, the same recorded answer — so a warmed source and a source
    /// planned by a request cannot end up with different layouts, and there is exactly one place that decides
    /// what a plan means. The only difference is that this one AWAITS the mission instead of firing it and
    /// answering <c>503</c>.
    /// <para>
    /// ⚠ <b>The <see cref="PlanState.Planning"/> branch POLLS, and the alternative was worse.</b> It is
    /// reached only when a request (or another caller) already owns the walk, so signalling it would mean a
    /// completion source on every cache entry — including entries the LRU evicts mid-walk, whose waiters
    /// would then have to be released by the eviction path. That is new state on the hot path to serve a case
    /// that, in the warm-ahead flow this exists for, means the page beat the app to its own source.
    /// </para>
    /// <para>
    /// ⚠ <b>The loop cannot spin.</b> Every branch except <see cref="PlanState.Planning"/> returns, and the
    /// two that continue both make progress: <see cref="PlanState.Claimed"/> awaits a mission that records an
    /// answer — <see cref="SubmitGuardedAsync"/> records <see cref="Fail"/> even when the body never ran — and
    /// <see cref="PlanState.Planning"/> waits on a walk with the same guarantee behind it. A disposed route
    /// ends it too: <see cref="Claim"/> answers <see cref="PlanState.Unplannable"/> once the entry is gone,
    /// and disposal cancels the walk that would still be holding one.
    /// </para>
    /// </remarks>
    public async Task<MediaPlanOutcome> PlanAsync(string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        // ⚠ The SAME predicate, in the SAME order, as `Answer` — see `TryContainAndKey`. Remote first,
        // because a remote source is not refused, it BELONGS to the conversion route behind this one.
        if (MediaConversionExtensions.IsRemote(source, out _)) return MediaPlanOutcome.Unplannable;
        if (!TryContainAndKey(source, out var contained, out var key)) return MediaPlanOutcome.Refused;

        // ⚠ A WAIT THIS LONG IS LOGGED ONCE RATHER THAN LEFT SILENT. Nothing here times out — the caller owns
        // that, through the token, because a legitimate walk of a two-hour film on a busy one-permit scheduler
        // IS minutes and a kit-invented deadline would abort it. But the failure mode this branch can produce
        // is an app thread waiting for ever on a `Planning` entry nobody will resolve, and a silent hang is
        // the least diagnosable outcome available. (It should be unreachable: every `Planning` entry is
        // inserted by `Claim` immediately before a `SubmitGuardedAsync`, which records an answer even when the
        // mission body never runs. This says so out loud instead of trusting it — the sabotage that proved
        // these tests can fail produced exactly this hang, in seconds.)
        var waitingSince = DateTime.UtcNow;
        var warned = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) return MediaPlanOutcome.Failed;

            switch (Claim(key, out _))
            {
                case PlanState.Ready:
                    return MediaPlanOutcome.Ready;

                case PlanState.Unplannable:
                    return MediaPlanOutcome.Unplannable;

                case PlanState.Failed:
                    // Consumed by `Claim`, exactly as on the request path: a failure says nothing about the
                    // source, so it is not remembered and the next call plans again.
                    return MediaPlanOutcome.Failed;

                case PlanState.Claimed:
                    // We own the walk. AWAITED here — the request path's whole reason for firing and
                    // forgetting is that it may not block a platform event thread, and this is an app thread.
                    await SubmitGuardedAsync(PlanMission(key, contained), key).ConfigureAwait(false);
                    break;

                default:
                    // Planning: someone else's walk. See the remarks for why this polls.
                    if (!warned && DateTime.UtcNow - waitingSince > PlanWaitWarnAfter)
                    {
                        warned = true;
                        Log(() => $"[Shenora.Modules.Media] computed remux has been waiting "
                                + $"{PlanWaitWarnAfter.TotalSeconds:F0}s for another walk of "
                                + $"{Path.GetFileName(contained)} — normal for a long film or a busy "
                                + "scheduler, and a stuck plan looks exactly the same from here (logged once)");
                    }
                    await Task.Delay(PlanPollInterval, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>
    /// Submit the walk without waiting for it — the same fire-and-forget-with-a-GUARD shape
    /// <c>MediaConversion.StartConversion</c> uses, for the same reason: this runs on a platform event thread
    /// with no caller above it, so an unobserved fault would be an unhandled exception rather than a failed
    /// plan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>The mission is NOT tied to the request's cancellation token, and that is a trap worth naming.</b>
    /// The request that triggers a walk answers <c>503</c> immediately and is over; handing it its own token
    /// would cancel the walk at that moment on any shell that cancels a completed request — and both shells
    /// pass <see cref="CancellationToken.None"/> today, so the bug would be invisible here and appear on
    /// whichever platform starts honouring it. The walk lives as long as the ROUTE does
    /// (<see cref="_closing"/>).
    /// </para>
    /// <para>
    /// 🔴 <b><see cref="MissionDefinition.Priority"/> 1, NOT the default 0, and this is a functional fix rather
    /// than tuning.</b> The class doc tells an app to pass the SAME scheduler it gives
    /// <see cref="MediaConversionExtensions.UseMediaConversion"/>, whose missions are minutes-long transcodes at
    /// priority 0 — and the global lane auto-sizes to <c>clamp(cores-1, 1, 4)</c>, so on a small device it is
    /// ONE permit. At priority 0 a plan queues FIFO behind every transcode already waiting, and this route
    /// answers <c>503</c> for the whole of that: a film that used to play on the first request would wait on an
    /// unrelated conversion, with no timeout and no escape for the page. A walk is seconds and something is
    /// WAITING on it; a transcode is minutes and nothing is. So the walk outranks it.
    /// (<c>PriorityMissionPolicy</c> is the scheduler's default policy, so this needs no configuration.)
    /// </para>
    /// <para>
    /// ⚠ <b>What priority does NOT fix, said here because it looks like it should:</b> admission order only
    /// re-ranks work that is PENDING. A transcode already RUNNING holds its permit until it finishes, so on a
    /// one-permit scheduler a plan still waits for it — bounded by that transcode, not unbounded, and answered
    /// as <c>503</c> throughout, which is at least the honest status. There is nothing this route can do about
    /// it from here: every mission draws from the global lane, so a lane of its own could only NARROW the width,
    /// never bypass the bound (<see cref="ILane.EffectiveCapacity"/>). The fix is the app's —
    /// <c>MissionSchedulerOptions.GlobalLaneCapacity</c> above 1, or a narrow lane for its conversions.
    /// </para>
    /// <para>
    /// ⚠ <b>No claim, and no lane.</b> A walk writes nothing, so there is nothing for a
    /// <c>PathClaims.Exclusive</c> to protect — and requiring one would mean requiring the app to register
    /// that scope, whose absence <see cref="IMissionScheduler.SubmitAsync"/> reports by THROWING at submit.
    /// A source that could never be planned because of a scheduler's configuration is exactly the outcome the
    /// fall-through rule exists to avoid. ⚠ <b>A lane was CONSIDERED and refused for a second reason worth
    /// recording, because the hazard it would address is real:</b> a walk peaks at 110–150 MB, the global lane
    /// is up to 4 wide, and four concurrent walks would be ~half a gigabyte on a phone. A kit-named lane could
    /// bound that — but only if the app SET its capacity, and a knob nothing sets is dead configuration (D63).
    /// It is the same open question, for the same owner, as the byte budget <see cref="Store"/> declines to
    /// invent; whoever answers one should answer both.
    /// </para>
    /// <para>
    /// 🔴 <b>AND NO <see cref="MissionDefinition.Key"/>, which looks like a missed optimisation and is
    /// deliberate.</b> Keying on the source's identity would let the scheduler deduplicate a second
    /// submission — but <see cref="MissionOutcome.Deduplicated"/> means the body never ran, and there is a
    /// window in which nobody else will run one either: a walk records its answer (<see cref="Store"/> or
    /// <see cref="Fail"/>) a moment BEFORE the scheduler releases its key, so a request that consumes a
    /// <see cref="PlanState.Failed"/> entry in that window and re-submits would be deduplicated against a
    /// mission that has already finished, leaving its own <see cref="PlanState.Planning"/> entry for nobody to
    /// resolve. ⚠ <b>That is a hazard this route now blocks TWICE, and neither guard is redundant:</b>
    /// <see cref="SubmitGuardedAsync"/> would catch the dangling entry anyway (it treats every outcome except
    /// <see cref="MissionOutcome.Completed"/> as "no answer recorded"), and declaring no key stops the
    /// situation arising at all. Belt and braces, on a failure mode whose symptom is a permanent 503.
    /// So dedup is <see cref="Claim"/>'s job alone: it inserts that <c>Planning</c> entry atomically, so an iOS
    /// burst of hundreds of requests submits exactly ONE walk. What is given up is the case where an entry was
    /// EVICTED while its walk ran, which can start a second walk for the same source — a doubled peak in a rare
    /// case, and a cost rather than a wrong answer.
    /// </para>
    /// </remarks>
    private void StartPlanning(string key, string path)
    {
        Log(() => $"[Shenora.Modules.Media] computed remux is planning {Path.GetFileName(path)} in a mission "
                + "— answering 503 Retry-After: 1 until the walk lands");
        _ = SubmitGuardedAsync(PlanMission(key, path), key);
    }

    /// <summary>
    /// The walk, as a mission. ONE definition for both callers: <see cref="StartPlanning"/> fires it and
    /// answers <c>503</c>, <see cref="PlanAsync"/> awaits it — and a warmed plan must be the same plan a
    /// request would have produced, at the same priority, or the two paths would race differently under a
    /// busy scheduler.
    /// </summary>
    private MissionDefinition PlanMission(string key, string path) => new()
    {
        Kind = PlanMissionKind,
        // Above a conversion's default 0 — see StartPlanning's remarks. Something is WAITING on this one.
        Priority = 1,
        Run = (_, missionToken) =>
        {
            PlanSource(key, path, missionToken);
            return Task.CompletedTask;
        },
    };

    /// <summary>
    /// The guard around the discarded task. <see cref="IMissionScheduler.SubmitAsync"/> reports a FAILED body
    /// through <c>MissionResult</c> rather than by throwing, so what a <c>catch</c> here sees is a submit-time
    /// fault — a disposed scheduler, an unregistered lane — which would otherwise be an unobserved exception
    /// on a platform event thread with nothing above it.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>The outcome is INSPECTED, not just awaited, and that is the difference between a decline and a
    /// source that 503s for ever.</b> <see cref="PlanSource"/> records its own answer, including its own
    /// failure — but a mission that never RAN records nothing: cancelled at shutdown, or dropped by an app's
    /// <c>IMissionPolicy</c>. That would leave a <see cref="PlanState.Planning"/> entry nobody will ever
    /// resolve, which is a permanent 503. So the test is <see cref="MissionOutcome.Completed"/> and nothing
    /// else — <b>deliberately NOT <c>result.Succeeded</c>, which is <c>Completed || Deduplicated</c></b>, and
    /// <see cref="MissionOutcome.Deduplicated"/> is by definition an outcome whose body never ran.
    /// <see cref="StartPlanning"/> declares no key, so that outcome is not reachable today; this is the guard
    /// that makes it SAFE if one is ever added, and it also covers cancellation and an app policy that drops
    /// work — neither of which a key has anything to do with.
    /// <para>
    /// ⚠ <b>What a decline COSTS, since this is the method that hands one out, and it is written down nowhere
    /// else:</b> the request falls through to whatever is registered behind, which is ordinarily the conversion
    /// route — so a spurious decline of a plannable film starts a WHOLE TRANSCODE of it, minutes of work and a
    /// cached artifact, for a source the next request would have planned in seconds. "The route behind gets its
    /// chance" is the right rule and it is not free, which is why a failure is recorded rather than guessed at
    /// and why <see cref="Fail"/> refuses to demote an answer another walk already recorded.
    /// </para>
    /// </remarks>
    private async Task SubmitGuardedAsync(MissionDefinition definition, string key)
    {
        try
        {
            var result = await _scheduler.SubmitAsync(definition, _closing.Token).ConfigureAwait(false);
            if (result.Outcome == MissionOutcome.Completed) return;   // the body recorded its answer
            Log(() => $"[Shenora.Modules.Media] computed remux's planning mission ended {result.Outcome} "
                    + "without an answer — declining the source so another route can have it");
            Fail(key);
        }
        catch (Exception ex)
        {
            Log(() => $"[Shenora.Modules.Media] computed remux could not submit a plan "
                    + $"({ex.GetType().Name}: {ex.Message})");
            Fail(key);
        }
    }

    /// <summary>
    /// Walk the source and record its layout — <b>the expensive half of this route, and the whole reason it
    /// takes an <see cref="IMissionScheduler"/>.</b>
    ///
    /// <para>
    /// 🔴 <b>WHY THIS MAY NOT RUN ON THE REQUEST PATH, AT ANY SIZE.</b> A plan walks the source's whole
    /// metadata and peaks on the order of 110–150 MB for a two-hour film (and near a gigabyte at
    /// <c>MatroskaSampleReader</c>'s four-million-sample ceiling), taking seconds of IO — arithmetic from the
    /// struct layouts, not a measurement, but certain in direction. Both mobile shells resolve a webview
    /// resource SYNCHRONOUSLY, so <see cref="Answer"/> may not await anything, and a walk done there is that
    /// cost paid on the platform's own resource thread with the webview waiting on it.
    /// ⚠ <b>"Not merely slow" is measured, not inferred:</b> <c>MobileWebViewInterceptor</c> resolves with a
    /// blocking <c>.GetAwaiter().GetResult()</c> inside the platform handler, and its own remarks record an iOS
    /// MAIN-THREAD DEADLOCK caused by exactly one blocking read in that position. Holding the request while a
    /// walk runs is not a tradeoff available to be made.
    /// ⚠ <b>And the 64 MiB ceiling this replaced was never the alternative bound</b>, which is worth saying
    /// because it reads like one: it was tested against the layout's <c>TotalLength</c> — a number this walk
    /// PRODUCES — so a two-hour film paid the whole walk on that thread and was declined afterwards. There was
    /// no bound. A pre-walk one would have to read the SOURCE's length.
    /// </para>
    /// <para>
    /// 🔴 <b>Caching the answer is not an optimisation either, it is what makes the route usable.</b> iOS
    /// issues HUNDREDS of range requests to read one container, so planning per request would be hundreds of
    /// full metadata walks for one film — and now that the walk is off the request path, it would be hundreds
    /// of MISSIONS, each answering 503 to the request that started it. One walk per source identity, recorded
    /// in <see cref="Store"/>.
    /// </para>
    /// <para>
    /// ⚠ <b>A REFUSAL is cached too</b> (<see cref="PlanState.Unplannable"/>). The route behind this one
    /// answers <c>503 Retry-After: 1</c> while it produces, so a page retries about once a second — and
    /// re-planning an unplannable multi-gigabyte film on every retry is a bigger cost than the one this cache
    /// exists to remove.
    /// </para>
    /// <para>
    /// ⚠ <b>A FAILURE is recorded but NOT remembered</b> (<see cref="PlanState.Failed"/>), and the difference
    /// from a refusal is the cause. `Plan` swallows everything but cancellation, so what lands in the catch
    /// below is narrow — an OOM while allocating the span array, or a file that could not be OPENED at all —
    /// and neither says anything about whether the source is plannable. So the next request walks it again,
    /// while the request that finds the failure falls through so the route behind gets its chance
    /// (<see cref="Claim"/>). ⚠ Without the recording, an unopenable file would answer 503 to every request
    /// for ever: the failure has to reach the request path somehow, and a route that cannot say "not mine"
    /// makes the film unplayable by every route.
    /// </para>
    /// <para>
    /// ⚠ <b>The open is the SAME open <see cref="Answer"/> uses</b> — <c>FileShare.Read | FileShare.Delete</c>
    /// — and it is a separate handle rather than a shared one, because the request that triggered this walk is
    /// already over. It closes before this method returns; a serve-time handle is the one that outlives its
    /// request (see <see cref="Produce"/>).
    /// </para>
    /// </summary>
    private void PlanSource(string key, string path, CancellationToken cancellationToken)
    {
        Mp4Layout? layout;
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            layout = _plan(source, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The route is going away (or the scheduler is). Not an answer about the source — and the guard
            // turns the mission's Cancelled outcome into a decline, so nothing is left waiting on a 503.
            throw;
        }
        catch (Exception ex)
        {
            // ⚠ Narrow by construction: `Plan` swallows everything but cancellation, so the realistic causes
            // are an OOM allocating the span array and a file that could not be opened. Neither is an answer
            // about the source, so it is recorded as a FAILURE (fall through once, then try again) rather than
            // as a refusal (fall through for ever).
            Log(() => $"[Shenora.Modules.Media] computed remux could not plan {Path.GetFileName(path)} "
                    + $"({ex.GetType().Name}) — declining without remembering it, since the next attempt "
                    + "may well succeed");
            Fail(key);
            return;
        }

        if (layout is null)
        {
            // 🔴 Declining, never 404 — answering 404 for an unplannable source would be the exact failure
            // this route's own contract forbids: the conversion or segment route registered behind would never
            // see the film, and it would be unplayable by ANY route with one host-log line as the only tell.
            // Caching the refusal keeps the retry loop behind it cheap.
            Log(() => $"[Shenora.Modules.Media] computed remux cannot plan {Path.GetFileName(path)} — "
                    + "declining, so a conversion or segment route can have it");
            Store(key, PlanState.Unplannable, layout: null);
            return;
        }

        Log(() => $"[Shenora.Modules.Media] computed remux planned {Path.GetFileName(path)}: "
                + $"{layout.TotalLength} bytes, {layout.Samples.Count} samples");
        Store(key, PlanState.Ready, layout);
    }

    /// <summary>
    /// Record that a walk produced no answer — fall through THIS time, plan again next time. See
    /// <see cref="Claim"/> for the consumption rule and <see cref="PlanSource"/> for why a failure and a
    /// refusal are recorded differently.
    /// </summary>
    /// <remarks>
    /// It refuses to STORE in two cases, not one.
    /// <para>
    /// The one this method was written for: an answer some other walk already recorded
    /// (<c>entry.State != Planning</c>) must not be demoted back to <see cref="PlanState.Failed"/> — a late
    /// failure from a stale mission whose entry has since been replaced by a good layout would otherwise turn
    /// a working film into a declined one, which is the sort of race that only shows up under a burst.
    /// </para>
    /// <para>
    /// 🔴 <b>The one a whole-branch review found missing: the key can also be ABSENT.</b> Four films planned
    /// at once fill the bounded cache (<see cref="Store"/>) with nothing but in-flight markers, so the FIFTH
    /// evicts one of them — and the walk behind that evicted marker is still running. When it finishes and
    /// fails, <c>TryGetValue</c> returns <c>false</c>: there is no entry to compare a state against, and the
    /// old (`&amp;&amp;`-only) check let that fall straight through to storing <see cref="PlanState.Failed"/>
    /// for a key nobody is waiting on. The NEXT, unrelated request for that source would then consume the
    /// phantom entry and decline to whatever route is registered behind this one — which, per
    /// <see cref="SubmitGuardedAsync"/>'s own remarks, starts a WHOLE TRANSCODE for a source it could have
    /// planned in seconds. Absent means "start fresh", exactly as it already does in <see cref="Claim"/>; this
    /// method now agrees with it instead of inventing an answer for a walk nobody asked for any more.
    /// </para>
    /// </remarks>
    private void Fail(string key)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_plans.TryGetValue(key, out var entry) || entry.State != PlanState.Planning) return;
            StoreLocked(key, PlanState.Failed, layout: null);
        }
    }

    /// <summary>
    /// Remember one answer, and keep the cache BOUNDED.
    ///
    /// <para>
    /// 🔴 <b>Why bounded at all:</b> a finished layout is ~24 bytes per sample across both tracks — about
    /// 13 MB for a two-hour film, plus a header that grows with the sample tables — and an unbounded dictionary
    /// keyed on identity keeps every distinct version of every file a page ever names. Browsing a library would
    /// accumulate them, and REPLACING a file adds a new key rather than reusing one, so even a single path can
    /// contribute several. On a phone that is the process being killed, later, for a reason nothing points at.
    /// </para>
    /// <para>
    /// <b>Why four, and why a COUNT:</b> a page plays one film at a time, so one would do for playback — but
    /// eviction costs a full re-plan, which is the exact cost this cache exists to avoid — and a re-plan now
    /// costs the page a <c>503</c> and a retry as well — so a cap of one or two would THRASH for a page with
    /// several elements interleaving requests. Four covers that without holding much. ⚠ <b>The honest bound is
    /// memory, not a file count</b>: four layouts of ordinary films are tens of megabytes, while four at the
    /// reader's four-million-sample ceiling would be hundreds — though planning even ONE of those already peaks
    /// near a gigabyte, so that case is out of budget before this cache is reached. A real byte budget (and
    /// whether it belongs beside <c>SegmentStreamOptions.CacheCapBytes</c>, which answers the same question for
    /// a different cache) is a deliberate decision left to whoever owns the media memory budget; it is not an
    /// option here, because a knob nothing sets is dead configuration (D63).
    /// </para>
    /// <para>
    /// ⚠ <b>A walk IN FLIGHT occupies an entry too</b>, so four films being planned at once fill the cache
    /// while holding no layouts. That is the right direction — the alternative is an unbounded set of in-flight
    /// markers — and an evicted marker is not a lost walk: the mission still records its answer, which is what
    /// the next request reads. <b>What an evicted marker DOES cost is a duplicate walk</b>: the request that
    /// finds the hole claims it again and submits a second mission for the same source, because
    /// <see cref="StartPlanning"/> deliberately declares no <see cref="MissionDefinition.Key"/> (that decision,
    /// and the permanent-503 hazard that motivates it, are written out there). Two concurrent walks of one film
    /// double its ~110–150 MB peak, which is the reason this cap is four rather than one.
    /// </para>
    /// <para>
    /// Eviction is least-recently-USED rather than least-recently-added: the film being played is touched by
    /// every one of its range requests, so use-order keeps it and drops what nobody is watching. An
    /// mtime-style ordering would evict exactly the entry earning its keep — the same lesson
    /// <c>SegmentStream</c>'s sweep records.
    /// </para>
    /// </summary>
    private void Store(string key, PlanState state, Mp4Layout? layout)
    {
        lock (_gate)
        {
            if (_disposed) return;
            StoreLocked(key, state, layout);
        }
    }

    /// <summary>The body of <see cref="Store"/>, for the callers that are already holding <see cref="_gate"/>.</summary>
    private void StoreLocked(string key, PlanState state, Mp4Layout? layout)
    {
        _plans[key] = new Entry { State = state, Layout = layout, Used = ++_clock };
        if (_plans.Count <= MaxCachedLayouts) return;

        var coldest = key;
        var coldestUse = long.MaxValue;
        foreach (var (candidate, entry) in _plans)
        {
            if (entry.Used >= coldestUse) continue;
            coldest = candidate;
            coldestUse = entry.Used;
        }

        _plans.Remove(coldest);
    }

    private void Log(Func<string> message) => AppCallback.Log(_options.Log, message);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // The layouts are the only thing here worth releasing, and they are the biggest thing the route
            // ever holds.
            _plans.Clear();
        }

        // ⚠ OUTSIDE the lock, and that is not tidiness: cancelling runs the scheduler's own registrations
        // INLINE on this thread, and calling into another component while holding this one's lock is how a
        // deadlock gets built. The CTS itself is deliberately not disposed — a walk may still be observing its
        // token, and a disposed source makes reading `Token` throw.
        _closing.Cancel();
    }

    /// <summary>
    /// What is known about one source. <b>Four states, not a nullable layout</b> — "planning" and "the walk
    /// failed" are real answers that a null layout used to have no way to express, and collapsing them is how
    /// a route ends up either walking a file on the request path or 503ing for ever.
    /// </summary>
    private enum PlanState
    {
        /// <summary>Planned. The layout is on the entry and the request is served out of it.</summary>
        Ready,

        /// <summary>A walk is in flight. Answer <c>503 Retry-After: 1</c> and let the page retry.</summary>
        Planning,

        /// <summary>
        /// Planned, and this source belongs on another path (<see cref="Mp4Remuxer.Plan"/> answered null).
        /// Declined, and remembered so the retries behind it cost a dictionary lookup.
        /// </summary>
        Unplannable,

        /// <summary>
        /// The walk produced no answer. Declined, and NOT remembered — see <see cref="Claim"/>.
        /// </summary>
        Failed,

        /// <summary>
        /// Never stored: <see cref="Claim"/> returns it to tell ONE caller that it now owns the walk and must
        /// submit it. Distinct from <see cref="Planning"/>, which is what every other request sees.
        /// </summary>
        Claimed,
    }

    /// <summary>One source's answer, plus when it was last asked for.</summary>
    private sealed class Entry
    {
        /// <summary>Which of the four answers this is.</summary>
        public PlanState State { get; init; }

        /// <summary>The plan — non-null exactly when <see cref="State"/> is <see cref="PlanState.Ready"/>.</summary>
        public Mp4Layout? Layout { get; init; }

        /// <summary>The value of the owner's use counter when this entry was last read.</summary>
        public long Used { get; set; }
    }

    /// <summary>
    /// Removing the route and releasing its layouts are one operation, so they are one disposable — the same
    /// shape <c>SegmentStream</c>'s registration uses.
    /// </summary>
    private sealed class Registration(IDisposable route, ComputedRemuxRoute owner) : IComputedRemuxRoute
    {
        /// <inheritdoc />
        /// <remarks>
        /// Straight through to the route. The registration exists to make removal and release ONE operation;
        /// it is not a second place where planning decides anything.
        /// </remarks>
        public Task<MediaPlanOutcome> PlanAsync(string source, CancellationToken cancellationToken = default)
            => owner.PlanAsync(source, cancellationToken);

        public void Dispose()
        {
            try { route.Dispose(); } catch (Exception) { /* the pipeline is going away anyway */ }
            owner.Dispose();
        }
    }
}
