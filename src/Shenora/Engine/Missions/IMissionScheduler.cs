using Shenora;
using Shenora.Engine.Files;

namespace Shenora.Engine.Missions;

/// <summary>
/// A capacity-limited pool of permits. Work draws one per named lane before running.
///
/// <para>
/// A lane with <see cref="Capacity"/> 1 is an exclusive gate over a scarce shared resource — the
/// shape the family previously had as a static singleton semaphore reachable from unrelated
/// features, which made it impossible to test and impossible to have two of.
/// </para>
/// </summary>
public interface ILane
{
    /// <summary>Lane name.</summary>
    string Name { get; }

    /// <summary>
    /// Permits available concurrently — <b>as requested</b>. Settable LIVE.
    ///
    /// <para>
    /// Lowering it never cancels running work: the surplus is swallowed as in-flight items finish.
    /// A user dragging a concurrency slider down means "run less from now on", never "kill what is
    /// already going" — getting this wrong destroys work the user did not ask to lose.
    /// </para>
    /// <para>
    /// ⚠ <b>This is what you asked for, not necessarily what the lane runs at.</b> Every mission also
    /// draws a permit from the scheduler's <see cref="IMissionScheduler.GlobalLane"/>, so the width a
    /// named lane actually achieves is the SMALLER of the two — read
    /// <see cref="EffectiveCapacity"/> for that. Raising a lane above the global bound is therefore
    /// legal and does nothing on its own; raise <see cref="IMissionScheduler.GlobalLane"/> too. The
    /// requested value is kept rather than clamped so that a later widening of the global bound gives
    /// you the width you asked for, instead of silently having discarded it.
    /// </para>
    /// </summary>
    int Capacity { get; set; }

    /// <summary>
    /// The width this lane can actually reach right now: <see cref="Capacity"/> bounded by every other
    /// limit the scheduler applies to it — in practice
    /// <c>min(Capacity, scheduler.GlobalLane.Capacity)</c>.
    ///
    /// <para>
    /// It exists because the alternative was measuring wall-clock time. A lane set to 3 under a global
    /// bound of 1 runs at 1 while <see cref="Capacity"/> answers 3, and nothing an app could ASK
    /// distinguished that from a lane genuinely running at 3 — so a governor that widened a lane had no
    /// way to notice its request had no effect (found by the first adopter, 2026-08-05).
    /// </para>
    /// </summary>
    int EffectiveCapacity { get; }

    /// <summary>True while <see cref="Hold"/> is in effect.</summary>
    bool IsHeld { get; }

    /// <summary>
    /// Stop admitting new work into this lane WITHOUT cancelling what is running. The mechanism
    /// behind "yield the GPU while the user is gaming"; the kit ships no policy that decides when.
    /// Re-entrant: N calls to Hold need N calls to <see cref="Release"/>.
    /// </summary>
    void Hold();

    /// <summary>Undo one <see cref="Hold"/>.</summary>
    void Release();
}

/// <summary>
/// Runs submitted work as soon as the resources it declared are free, in parallel up to a capacity,
/// serializing anything that overlaps.
///
/// <para>
/// One engine covers what the family had built five times: a filesystem operation planner is this
/// with hierarchical path keys (<see cref="PathClaims"/>), a job queue is this with lanes, and an
/// actor is this with a single exclusive claim. See
/// <c>docs/DECISIONS.md</c> D27–D31.
/// </para>
///
/// <para>
/// This is the EXECUTION half of long-running work. The REPORTING half already exists as
/// <c>Shenora.Core.Ipc</c>'s REQUEST tracking; a mission body reports progress into it. The two compose
/// and must not be merged — the modules may depend on the cores, never the reverse (D19/D20).
/// ⚠ This said <c>Shenora.Ipc</c>'s "operation registry" until 2026-08-10: a package D65 folded in, and
/// a subsystem D66 deleted when operations merged into <c>IpcRequest</c>.
/// </para>
/// </summary>
public interface IMissionScheduler : IAsyncDisposable
{
    /// <summary>
    /// Queue work and complete when it finishes. A failing body is reported in the result rather
    /// than thrown (see <see cref="MissionResult"/>); a caller error — an unregistered claim scope, a
    /// disposed scheduler — throws here.
    ///
    /// <para>
    /// A lane name never seen before is NOT an error: it is created at the default capacity, exactly
    /// as <see cref="Lane"/> does. So a misspelled lane silently draws on a DIFFERENT lane instead of
    /// the one whose capacity you configured — keep lane names in constants.
    /// </para>
    /// </summary>
    /// <param name="definition">What to run and the resources it needs.</param>
    /// <param name="cancellationToken">
    /// Cancels this execution: removed from the queue if still pending, and surfaced as the token
    /// handed to <see cref="MissionDefinition.Run"/> if it is already running.
    /// </param>
    Task<MissionResult> SubmitAsync(MissionDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a lane by name, creating it on first use with the same capacity as <see cref="GlobalLane"/>
    /// currently has — so a fresh lane never narrows anything until you narrow it.
    /// </summary>
    ILane Lane(string name);

    /// <summary>
    /// The lane EVERY mission draws one permit from — the scheduler's total concurrency bound, sized by
    /// <c>MissionSchedulerOptions.GlobalLaneCapacity</c>.
    ///
    /// <para>
    /// It has always bounded everything; it just was not reachable, so the bound could be set once at
    /// construction and never afterwards. That made a runtime capacity governor unbuildable in one
    /// direction: it could throttle a named lane and could never restore it past the bound chosen at
    /// startup. Exposing the lane rather than adding a bespoke setter means <see cref="ILane.Hold"/>
    /// works on it too — which is "pause the whole scheduler without cancelling anything", a capability
    /// the machinery already had and could not be asked for.
    /// </para>
    /// <para>
    /// ⚠ Holding this lane stops ALL admission. That is the point, and it is re-entrant like any other
    /// hold — N calls to <see cref="ILane.Hold"/> need N calls to <see cref="ILane.Release"/>.
    /// </para>
    /// </summary>
    ILane GlobalLane { get; }

    /// <summary>Accepted but not yet started.</summary>
    int PendingCount { get; }

    /// <summary>Currently executing.</summary>
    int RunningCount { get; }

    /// <summary>
    /// Whether work with this key is pending or in flight — so a caller can skip building an
    /// expensive request it knows would only be deduplicated.
    /// </summary>
    bool IsActive(MissionKey key);

    /// <summary>
    /// Everything queued or running right now, for a diagnostics view or a queue UI. A copy: safe to
    /// hold, stale the moment it returns.
    /// </summary>
    IReadOnlyList<MissionExecution> Snapshot();

    /// <summary>
    /// Re-run admission now.
    ///
    /// <para>
    /// Dispatch is event-driven — it happens on submit and on completion — which covers everything
    /// the scheduler can see for itself. An <see cref="IMissionPolicy"/> that defers work on an EXTERNAL
    /// condition (a clock, system load, a maintenance window) must call this when that condition
    /// changes, or the deferred item waits for unrelated traffic to wake it. The kit owns no timer
    /// on purpose: polling belongs to whoever knows what is being polled.
    /// </para>
    /// </summary>
    void Reevaluate();

    /// <summary>
    /// Re-admit durable work left behind by a previous run. Explicit, never implicit: only the app
    /// knows when its own services are ready to receive recovered work.
    ///
    /// <para>
    /// The kit cannot rebuild a body from a record — a delegate does not serialize — so
    /// <paramref name="rehydrate"/> maps a <see cref="MissionRecord"/> back to a
    /// <see cref="MissionDefinition"/>. Returning null drops that record. This is also why the kit ships
    /// no handler registry: the app already owns the record-to-body mapping here.
    /// </para>
    /// </summary>
    /// <param name="rehydrate">Rebuilds a request from a persisted record, or returns null to drop it.</param>
    /// <param name="cancellationToken">Cancels recovery.</param>
    /// <returns>Records that were re-queued.</returns>
    Task<int> RecoverAsync(Func<MissionRecord, MissionDefinition?> rehydrate, CancellationToken cancellationToken = default);
}
