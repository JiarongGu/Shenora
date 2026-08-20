using Microsoft.Extensions.Logging;
using Shenora.Core.WebView;
using Shenora.Engine;
using Shenora.Engine.Missions;

namespace Shenora.Modules.Media;

/// <summary>What planning a source ANSWERED. There is no <c>Planning</c> member.</summary>
public enum MediaPlanOutcome
{
    /// <summary>Planned. Point an element at the URL — its first request is a <c>206</c>, not a <c>503</c>.</summary>
    Ready,

    /// <summary>This route will never serve this source — it is remote (a plan needs a seekable local file),
    /// or the output would LOSE something (<see cref="Mp4Remuxer.Plan"/> answered null). Not an error.</summary>
    Unplannable,

    /// <summary>Outside <see cref="MediaAccessOptions.AllowedRoots"/>, or no such file — one outcome for
    /// both, so nothing can probe for a file's existence outside its roots. ⚠ An APP bug or an attack path.</summary>
    Refused,

    /// <summary>The walk produced no answer — IO, an out-of-memory, a cancelled or dropped mission. Nothing
    /// is remembered, so retrying is worthwhile.</summary>
    Failed,
}

/// <summary>The handle <see cref="ComputedRemuxExtensions.UseComputedRemux"/> returns: dispose to remove the
/// route, <see cref="PlanAsync"/> to WARM a source before a page asks for it.</summary>
public interface IComputedRemuxRoute : IDisposable
{
    /// <summary>Plan <paramref name="source"/> now, and do not return until there is an answer.</summary>
    /// <remarks>
    /// 🔴 <b>An unplanned source answers <c>503</c> while the walk runs, and a media element cannot ride that
    /// out</b>: on both mobile shells it errors within ~70 ms and never retries. There is no readiness event,
    /// so warm the source here and only then point an element at the url (D72).
    /// <para>
    /// ⚠ It applies the request path's authorisation, not a shortened one. ⚠ Cheap to call again: a planned
    /// source answers from the cache without touching the file, and two concurrent calls share ONE walk.
    /// ⚠ <b>Cancelling stops the WAIT, not the walk</b>, and nothing here times out —
    /// <see cref="CancellationToken.None"/> waits as long as the walk takes.
    /// </para>
    /// </remarks>
    /// <param name="source">The media file, as <see cref="MediaAccessOptions.Resolve"/> would have produced it.</param>
    /// <param name="cancellationToken">Stops WAITING; see the remarks.</param>
    Task<MediaPlanOutcome> PlanAsync(string source, CancellationToken cancellationToken = default);
}

/// <summary>
/// Serves a container repair as one ordinary URL — an MP4 answered over HTTP ranges that has never been
/// produced. Nothing is transcoded and nothing is written to disk. Design: <c>docs/design/media.md</c>.
/// <para>
/// 🔴 <b>THE FIRST REQUEST FOR AN UNPLANNED SOURCE ANSWERS <c>503</c>, NOT BYTES</b>, and to a media element
/// that 503 is indistinguishable from a 404 — warm the source with
/// <see cref="IComputedRemuxRoute.PlanAsync"/> first (D72).
/// </para>
/// <para>
/// ⚠ <b>REGISTER IT BEFORE the conversion route</b> — middleware run in registration order, and the other
/// way round leaves this one dead code that still passes every test of its own. Registered first it never
/// consults <see cref="MediaAccessOptions.CacheRoot"/>; register it AFTER if a cached artifact must win.
/// </para>
/// </summary>
public static class ComputedRemuxExtensions
{
    /// <summary>Register the computed-remux route on one interceptor.</summary>
    /// <param name="interceptor">
    /// The shell's interceptor; it supplies the platform's <see cref="IWebViewInterceptor.RangeDelivery"/> rule.
    /// </param>
    /// <param name="scheduler">
    /// Where the metadata WALK runs — never on a webview callback; the request answers
    /// <c>503 Retry-After: 1</c> until the plan lands. ⚠ <b>Pass the app's ONE scheduler.</b> Lanes are then
    /// shared, so on a one-permit device a film answers <c>503</c> until a running conversion finishes.
    /// </param>
    /// <param name="options">Where media may be read from and how a URL maps to a source.</param>
    /// <returns>
    /// The route handle. Dispose to remove the route, drop the layouts AND cancel a walk still in flight.
    /// ⚠ <b>Keep it for <see cref="IComputedRemuxRoute.PlanAsync"/> too</b> — the only way to warm a source.
    /// </returns>
    public static IComputedRemuxRoute UseComputedRemux(this IWebViewInterceptor interceptor,
        IMissionScheduler scheduler, MediaAccessOptions options)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Resolve);

        // 🔴 THE ORDER CHECK. Reports rather than throws: the two Resolve predicates may not overlap.
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
/// (once per source identity, in a mission) → answer a byte range out of the plan. Its one piece of state is
/// the layout cache, which is also its state MACHINE (<see cref="PlanState"/>).
/// </summary>
internal sealed class ComputedRemuxRoute : IDisposable
{
    /// <summary>⚠ <b>The type of what is SENT, never what the source file is</b> — a media element told the
    /// wrong container refuses before it has tried a byte.</summary>
    private const string OutputContentType = "video/mp4";

    /// <summary>
    /// How often <see cref="PlanAsync"/> re-reads the cache while ANOTHER walk owns the source. ⚠ A second
    /// copy of <see cref="MediaConversionExtensions.NotReadyYet"/>'s <c>Retry-After</c>, and moves with it.
    /// </summary>
    private static readonly TimeSpan PlanPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long <see cref="PlanAsync"/> waits on ANOTHER walk before saying so once in the host log.
    /// Not a timeout — nothing here gives up.</summary>
    private static readonly TimeSpan PlanWaitWarnAfter = TimeSpan.FromSeconds(30);

    /// <summary>The cache-key variant, so a PLAN is never confused with a conversion or a segment.</summary>
    private const string CacheVariant = "mp4-plan";

    /// <summary>How many planned layouts to keep. <see cref="Store"/> has the memory budget behind it.</summary>
    private const int MaxCachedLayouts = 4;

    /// <summary>The mission kind, as it appears in a queue view or a diagnostics snapshot.</summary>
    private const string PlanMissionKind = "media-remux-plan";

    private readonly MediaAccessOptions _options;
    private readonly WebViewRangeDelivery _delivery;
    private readonly IMissionScheduler _scheduler;

    /// <summary>What has been planned, or is being planned, by source identity. Guarded by <see cref="_gate"/>.</summary>
    private readonly Dictionary<string, Entry> _plans = new(StringComparer.Ordinal);

    /// <summary>Monotonic use counter for the eviction order. Guarded by <see cref="_gate"/>.</summary>
    private long _clock;

    private readonly object _gate = new();

    /// <summary>Cancels a walk still in flight when the route is disposed. <b>NOT the request's token</b> —
    /// see <see cref="StartPlanning"/>.</summary>
    private readonly CancellationTokenSource _closing = new();

    /// <summary>Whether the remote-decline line has been logged; it names no url, so once per route.</summary>
    private bool _loggedRemoteDecline;

    private bool _disposed;

    /// <summary>What turns a source stream into a layout — <see cref="Mp4Remuxer.Plan"/>, or a test stub.</summary>
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
    /// Build and register the middleware. <paramref name="plan"/> null means <see cref="Mp4Remuxer.Plan"/>,
    /// which is what every shipped call gets; ⚠ it is a TEST-ONLY seam, reached through
    /// <c>InternalsVisibleTo("Shenora.Tests")</c>.
    /// </summary>
    internal static IComputedRemuxRoute Use(IWebViewInterceptor interceptor, IMissionScheduler scheduler,
                                    MediaAccessOptions options,
                                    Func<Stream, CancellationToken, Mp4Layout?>? plan = null)
    {
        var route = new ComputedRemuxRoute(options, interceptor.RangeDelivery, scheduler, plan ?? Mp4Remuxer.Plan);
        var registration = interceptor.Use((request, next, cancellationToken) =>
            route.Answer(request, cancellationToken) is { } response
                ? Task.FromResult<WebViewResourceResponse?>(response)
                // Null from Answer means "not mine": the request continues down the rest of the pipeline.
                : next(request, cancellationToken));

        return new Registration(registration, route);
    }

    /// <summary>Answer one request, or null to decline it.</summary>
    /// <remarks>
    /// ⚠ <b>Never walk here (<see cref="PlanSource"/>) and never await</b>: both mobile shells resolve a
    /// resource SYNCHRONOUSLY, so a cache miss submits a walk and answers <c>503</c>. ⚠ <b>Remote is
    /// DECLINED, not 404'd</b> — the conversion route behind this one accepts a url behind its SSRF policy,
    /// and a 404 would make every remote conversion unreachable.
    /// </remarks>
    private WebViewResourceResponse? Answer(WebViewResourceRequest request, CancellationToken cancellationToken)
    {
        if (_options.Resolve(request.Uri) is not { } requested) return null;

        // ⚠ The SAME predicate the conversion route authorises with — two answers to "is this remote?" leave
        // a source neither route will serve.
        if (MediaConversionExtensions.IsRemote(requested, out _))
        {
            // ⚠ Logged ONCE per route: a page retries about once a second and every retry passes through
            // here. The containment refusal below stays per-request — how OFTEN it happens is the tell.
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
                // ⚠ Not logged: about one request per second per source for as long as the walk takes.
                return MediaConversionExtensions.NotReadyYet();

            case PlanState.Failed:
                // 🔴 A FAILED WALK MUST FALL THROUGH, or this source 503s for ever. The entry was CONSUMED by
                // `Claim`, so the next request plans again.
                Log(() => "[Shenora.Modules.Media] computed remux declines a source whose plan FAILED, so a "
                        + "conversion or segment route can have it; the next request will try planning again");
                return null;

            default:   // Unplannable — planned, and not this path's file
                return null;
        }

        FileStream source;
        try
        {
            // 🔴 `FileShare.Delete`, not the `FileShare.Read` a `File.OpenRead` gives: this handle stays open
            // for as long as the platform holds the response, and NTFS defers a delete until every handle
            // closes. ⚠ NOT widened to `ReadWrite` — a concurrent write would tear this read's bytes,
            // silently. The WALK opens the same file with the same flags (see `PlanSource`).
            source = new FileStream(contained, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception ex)
        {
            // Contained, present, planned and unreadable — a lock or a permission, so 404 with the reason in
            // the host log. ⚠ An open that fails BEFORE a plan exists happens inside the walk and DECLINES.
            Log(() => "[Shenora.Modules.Media] computed remux could not open a source", ex);
            return WebViewResourceResponse.NotFound();
        }

        // `produced` tracks ownership of `source`: the lazy body outlives this method, so every path that
        // does NOT reach `Produce` must close the handle here.
        var produced = false;
        try
        {
            return WebViewFiles.ServeRange(request, layout.TotalLength, OutputContentType, _delivery,
                (from, count) =>
                {
                    produced = true;   // `Produce` owns `source` from here — see its own remarks on disposal
                    return Produce(key, layout, source, from, count, cancellationToken);
                });
        }
        finally
        {
            // `ServeRange` does not always call `read` (a 416 never reaches `Produce`), so close it here.
            if (!produced) source.Dispose();
        }
    }

    /// <summary>
    /// Produce the output's bytes <c>[from, from + count)</c> out of the plan, as a LAZILY-read body:
    /// <see cref="Mp4LayoutRangeStream"/> copies only the source bytes a range actually touches, at the
    /// moment the PLATFORM asks for them. 🔴 There is NO output-size ceiling on this route.
    /// <para>
    /// 🔴 <b>Ownership of <paramref name="source"/> passes onward on every path out</b>: disposed here on
    /// decline (<paramref name="count"/> zero) and on a construction-time failure, otherwise owned by the
    /// <see cref="Mp4LayoutRangeStream"/>. ⚠ A read-time failure cannot be caught here — the read happens
    /// after the status line has gone out, so <c>onReadFailure</c> drops the cached plan instead.
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

        // Shared, so a construction-time failure and a read-time one log and forget identically.
        void OnReadFailure(Exception ex)
        {
            // What reaches here is a source replaced BETWEEN this stat and this read. ⚠ WHAT IT CANNOT
            // CATCH: a same-LENGTH, same-mtime rewrite in place. `DerivedCacheKey` is identity+length+mtime,
            // not a content hash, and every span still resolves to a valid offset — so a 206 with a correct
            // `Content-Range` goes out over bytes that are WRONG.
            Log(() => "[Shenora.Modules.Media] computed remux could not read a planned range "
                    + $"({ex.GetType().Name}) — dropping the cached plan so the next request re-plans");

            // 🔴 DROP THE CACHED PLAN, OR THIS FILM IS BROKEN FOREVER — the key has not changed, so every
            // later request would hit the same entry and fail the same way.
            Forget(key);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var range = new Mp4LayoutRangeStream(layout, source, from, from + count - 1,
                ownsSource: true, onReadFailure: OnReadFailure);
            return new BoundedBodyStream(range, count, _options.Log);
        }
        catch (OperationCanceledException)
        {
            // The caller went away. Not a response, and nothing else will ever own `source`.
            source.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            OnReadFailure(ex);
            source.Dispose();
            return null;
        }
    }

    /// <summary>Drop one entry so the next request for that source plans it afresh.</summary>
    private void Forget(string key)
    {
        lock (_gate) _plans.Remove(key);
    }

    /// <summary>
    /// What is known about one source — and, when nothing is, CLAIM the walk for this caller in the same
    /// atomic step. 🔴 iOS opens a container with HUNDREDS of range requests, and look-then-claim would let
    /// every request in the burst submit its own walk.
    /// <para>
    /// ⚠ <see cref="PlanState.Failed"/> is CONSUMED here rather than returned twice. Every hit also touches
    /// the eviction clock, including a <see cref="PlanState.Planning"/> one, so the retries arriving while a
    /// big film is walked keep its own entry from being evicted under it.
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

            // Disposed: a request that raced the registration out of the pipeline. No work for a dead route.
            if (_disposed) return PlanState.Unplannable;

            StoreLocked(key, PlanState.Planning, layout: null);
            return PlanState.Claimed;
        }
    }

    /// <summary>
    /// Contain a resolved source, stat it, and derive the cache key — the steps <see cref="Answer"/> and
    /// <see cref="PlanAsync"/> MUST take identically, in one implementation.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Returns false for BOTH "outside the roots" and "no such file", and only the first is logged.</b>
    /// Answering them differently is how a caller probes for a file's existence outside its own roots.
    /// </remarks>
    private bool TryContainAndKey(string requested, out string contained, out string key)
    {
        contained = string.Empty;
        key = string.Empty;

        if (WebViewFiles.ResolveContained(requested, _options.AllowedRoots) is not { } within)
        {
            // No path in the message: this one is reached by a page-supplied value.
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
            Log(() => "[Shenora.Modules.Media] computed remux could not stat a source", ex);
            return false;
        }

        // 🔴 IDENTITY, not the path. A layout carries none of its own, so film A's byte map over film B's
        // bytes resolves every span to a valid offset in the WRONG file and serves a garbage picture with no
        // error anywhere. Length AND mtime: neither alone catches a restore or an in-place edit.
        contained = within;
        key = DerivedCacheKey.For(within, info.Length, info.LastWriteTimeUtc, CacheVariant);
        return true;
    }

    /// <inheritdoc cref="IComputedRemuxRoute.PlanAsync"/>
    /// <remarks>
    /// Reuses the REQUEST path's machinery — the same <see cref="Claim"/>, the same mission, the same
    /// recorded answer — so a warmed source and a requested one cannot end up with different layouts; the
    /// only difference is that this one AWAITS the mission instead of answering <c>503</c>. ⚠ The loop
    /// cannot spin: every branch except <see cref="PlanState.Planning"/> returns, and a mission always
    /// records an answer even when its body never ran (<see cref="SubmitGuardedAsync"/>).
    /// </remarks>
    public async Task<MediaPlanOutcome> PlanAsync(string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        // ⚠ The SAME predicate, in the SAME order, as `Answer`. Remote is not refused — it BELONGS to the
        // conversion route behind this one.
        if (MediaConversionExtensions.IsRemote(source, out _)) return MediaPlanOutcome.Unplannable;
        if (!TryContainAndKey(source, out var contained, out var key)) return MediaPlanOutcome.Refused;

        // ⚠ NOTHING HERE TIMES OUT: the failure mode is an app thread waiting for ever on a `Planning` entry
        // nobody resolves, and that silent hang gets one log line.
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
                    // Consumed by `Claim`: not remembered, so the next call plans again.
                    return MediaPlanOutcome.Failed;

                case PlanState.Claimed:
                    // We own the walk. AWAITED here — unlike the request path, this is an app thread.
                    await SubmitGuardedAsync(PlanMission(key, contained), key).ConfigureAwait(false);
                    break;

                default:
                    // Planning: someone else's walk.
                    if (!warned && DateTime.UtcNow - waitingSince > PlanWaitWarnAfter)
                    {
                        warned = true;
                        Log(() => "[Shenora.Modules.Media] computed remux has been waiting "
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
    /// Submit the walk without waiting for it, guarded: this runs on a platform event thread with no caller
    /// above it, so an unobserved fault would be an unhandled exception rather than a failed plan.
    /// </summary>
    /// <remarks>
    /// Four properties of the mission, each of which breaks something if changed: the ROUTE's token
    /// (<see cref="_closing"/>) and never the request's, whose token would cancel the walk immediately;
    /// <see cref="MissionDefinition.Priority"/> 1, since at the default 0 a plan queues behind every pending
    /// transcode; no claim and no lane, since an unregistered claim scope makes
    /// <see cref="IMissionScheduler.SubmitAsync"/> throw; and 🔴 no <see cref="MissionDefinition.Key"/>,
    /// since keying would deduplicate a re-submission against a mission that has recorded its answer but not
    /// released the key, stranding a <see cref="PlanState.Planning"/> entry nobody resolves (a permanent 503).
    /// </remarks>
    private void StartPlanning(string key, string path)
    {
        Log(() => $"[Shenora.Modules.Media] computed remux is planning {Path.GetFileName(path)} in a mission "
                + "— answering 503 Retry-After: 1 until the walk lands");
        _ = SubmitGuardedAsync(PlanMission(key, path), key);
    }

    /// <summary>The walk, as a mission. ONE definition for both callers: <see cref="StartPlanning"/> fires
    /// it, <see cref="PlanAsync"/> awaits it.</summary>
    private MissionDefinition PlanMission(string key, string path) => new()
    {
        Kind = PlanMissionKind,
        // Above a conversion's default 0 — see StartPlanning's remarks.
        Priority = 1,
        Run = (_, missionToken) =>
        {
            PlanSource(key, path, missionToken);
            return Task.CompletedTask;
        },
    };

    /// <summary>
    /// The guard around the discarded task: <see cref="IMissionScheduler.SubmitAsync"/> reports a FAILED body
    /// through <c>MissionResult</c> rather than by throwing, so a <c>catch</c> here sees a submit-time fault
    /// — a disposed scheduler, an unregistered lane — that would otherwise go unobserved.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>The outcome is INSPECTED, not just awaited.</b> A mission that never RAN records nothing,
    /// leaving a <see cref="PlanState.Planning"/> entry nobody resolves — a permanent 503. So the test is
    /// <see cref="MissionOutcome.Completed"/> and nothing else: <b>NOT <c>result.Succeeded</c></b>, which is
    /// <c>Completed || Deduplicated</c>, and a deduplicated mission's body never ran.
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
            Log(() => "[Shenora.Modules.Media] computed remux could not submit a plan "
                    + $"({ex.GetType().Name}: {ex.Message})");
            Fail(key);
        }
    }

    /// <summary>
    /// Walk the source and record its layout. 🔴 Never on the request path, at any size: both mobile shells
    /// resolve a webview resource SYNCHRONOUSLY, and one blocking read there has DEADLOCKED the iOS main
    /// thread.
    /// <para>
    /// The answer — and a REFUSAL (<see cref="PlanState.Unplannable"/>) — is cached, because iOS issues
    /// HUNDREDS of range requests to read one container. ⚠ <b>A FAILURE is recorded but NOT remembered</b>
    /// (<see cref="PlanState.Failed"/>): the catch below sees an OOM or a file that could not be OPENED,
    /// neither of which says whether the source is plannable, and without the recording an unopenable file
    /// would answer 503 for ever.
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
            // The guard turns the mission's Cancelled outcome into a decline, so nothing waits on a 503.
            throw;
        }
        catch (Exception ex)
        {
            // A FAILURE (fall through once, then try again), not a refusal (fall through for ever).
            Log(() => $"[Shenora.Modules.Media] computed remux could not plan {Path.GetFileName(path)} "
                    + $"({ex.GetType().Name}) — declining without remembering it, since the next attempt "
                    + "may well succeed");
            Fail(key);
            return;
        }

        if (layout is null)
        {
            // 🔴 Declining, never 404: a 404 would keep the route registered behind from ever seeing the
            // film, leaving it unplayable by ANY route with one host-log line as the only tell.
            Log(() => $"[Shenora.Modules.Media] computed remux cannot plan {Path.GetFileName(path)} — "
                    + "declining, so a conversion or segment route can have it");
            Store(key, PlanState.Unplannable, layout: null);
            return;
        }

        Log(() => $"[Shenora.Modules.Media] computed remux planned {Path.GetFileName(path)}: "
                + $"{layout.TotalLength} bytes, {layout.Samples.Count} samples");
        Store(key, PlanState.Ready, layout);
    }

    /// <summary>Record that a walk produced no answer — fall through THIS time, plan again next time.</summary>
    /// <remarks>
    /// 🔴 It refuses to STORE in two cases, and either would decline a good film to the conversion route
    /// behind — a WHOLE TRANSCODE. An answer some other walk already recorded (<c>entry.State != Planning</c>)
    /// must not be demoted back to <see cref="PlanState.Failed"/>; and the key can be ABSENT, since the
    /// bounded cache (<see cref="Store"/>) can evict an entry whose walk is still running, so a
    /// <see cref="PlanState.Failed"/> nobody waits on is a phantom the next unrelated request consumes.
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
    /// Remember one answer, and keep the cache BOUNDED — a layout is ~24 bytes per sample, about 13 MB for a
    /// two-hour film. ⚠ <b>The bound is a file COUNT while the real budget is memory</b>: four ordinary
    /// layouts are tens of megabytes, four at the reader's four-million-sample ceiling would be hundreds.
    /// <para>
    /// ⚠ <b>A walk IN FLIGHT occupies an entry too</b>, so four films planned at once fill the cache while
    /// holding no layouts. An evicted marker costs a duplicate walk (<see cref="StartPlanning"/> declares no
    /// <see cref="MissionDefinition.Key"/>), and two concurrent walks of one film double its ~110–150 MB peak.
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

    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_options.Log, message, exception: failure);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _plans.Clear();
        }

        // ⚠ OUTSIDE the lock: cancelling runs the scheduler's own registrations INLINE on this thread, and
        // calling into another component under this one's lock deadlocks. The CTS itself is NOT disposed — a
        // walk may still be observing its token, and a disposed source makes `Token` throw.
        _closing.Cancel();
    }

    /// <summary>What is known about one source. <b>Four states, not a nullable layout</b> — collapsing
    /// "planning" and "the walk failed" into null walks a file on the request path, or 503s for ever.</summary>
    private enum PlanState
    {
        /// <summary>Planned. The layout is on the entry and the request is served out of it.</summary>
        Ready,

        /// <summary>A walk is in flight. Answer <c>503 Retry-After: 1</c> and let the page retry.</summary>
        Planning,

        /// <summary>Planned, and this source belongs on another path (<see cref="Mp4Remuxer.Plan"/> answered
        /// null). Declined, and remembered so the retries behind it cost a dictionary lookup.</summary>
        Unplannable,

        /// <summary>The walk produced no answer. Declined, and NOT remembered — see <see cref="Claim"/>.</summary>
        Failed,

        /// <summary>Never stored: <see cref="Claim"/> returns it to tell ONE caller that it now owns the walk
        /// and must submit it. Every other request sees <see cref="Planning"/>.</summary>
        Claimed,
    }

    /// <summary>One source's answer, plus when it was last asked for.</summary>
    private sealed class Entry
    {
        public PlanState State { get; init; }

        /// <summary>Non-null exactly when <see cref="State"/> is <see cref="PlanState.Ready"/>.</summary>
        public Mp4Layout? Layout { get; init; }

        /// <summary>The owner's use counter when this entry was last read.</summary>
        public long Used { get; set; }
    }

    /// <summary>Removing the route and releasing its layouts are one operation, so one disposable.</summary>
    private sealed class Registration(IDisposable route, ComputedRemuxRoute owner) : IComputedRemuxRoute
    {
        /// <inheritdoc />
        public Task<MediaPlanOutcome> PlanAsync(string source, CancellationToken cancellationToken = default)
            => owner.PlanAsync(source, cancellationToken);

        public void Dispose()
        {
            try { route.Dispose(); } catch (Exception) { /* the pipeline is going away anyway */ }
            owner.Dispose();
        }
    }
}
