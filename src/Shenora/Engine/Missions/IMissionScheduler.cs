using Shenora.Engine.Files;

namespace Shenora.Engine.Missions;

/// <summary>
/// A capacity-limited pool of permits. Work draws one per named lane before running; a lane with
/// <see cref="Capacity"/> 1 is an exclusive gate over a scarce shared resource.
/// </summary>
public interface ILane
{
    /// <summary>Lane name.</summary>
    string Name { get; }

    /// <summary>
    /// Permits available concurrently — <b>as requested</b>. Settable LIVE; lowering it never cancels
    /// running work, the surplus is swallowed as in-flight items finish.
    /// <para>
    /// ⚠ <b>This is what you asked for, not necessarily what the lane runs at.</b> Every mission also
    /// draws a permit from <see cref="IMissionScheduler.GlobalLane"/>, so a named lane achieves the
    /// SMALLER of the two — read <see cref="EffectiveCapacity"/> for that. Raising a lane above the
    /// global bound is legal and does nothing on its own; raise the global lane too.
    /// </para>
    /// </summary>
    int Capacity { get; set; }

    /// <summary>
    /// The width this lane can actually reach right now — in practice
    /// <c>min(Capacity, scheduler.GlobalLane.Capacity)</c>.
    /// </summary>
    int EffectiveCapacity { get; }

    /// <summary>True while <see cref="Hold"/> is in effect.</summary>
    bool IsHeld { get; }

    /// <summary>
    /// Stop admitting new work into this lane WITHOUT cancelling what is running. Re-entrant: N calls
    /// need N calls to <see cref="Release"/>.
    /// </summary>
    void Hold();

    /// <summary>Undo one <see cref="Hold"/>.</summary>
    void Release();
}

/// <summary>
/// Runs submitted work as soon as the resources it declared are free, in parallel up to a capacity,
/// serializing anything that overlaps: a filesystem operation planner is this with hierarchical path
/// keys (<see cref="PathClaims"/>), a job queue is this with lanes, an actor is this with a single
/// exclusive claim. See <c>docs/DECISIONS.md</c> D27–D31.
/// </summary>
public interface IMissionScheduler : IAsyncDisposable
{
    /// <summary>
    /// Queue work and complete when it finishes. A failing body is reported in the result rather
    /// than thrown (see <see cref="MissionResult"/>); a caller error — an unregistered claim scope, a
    /// disposed scheduler — throws here.
    /// <para>
    /// ⚠ A lane name never seen before is NOT an error: it is created at the default capacity, so a
    /// misspelled lane silently draws on a DIFFERENT lane than the one you configured. Keep lane names
    /// in constants.
    /// </para>
    /// </summary>
    /// <param name="definition">What to run and the resources it needs.</param>
    /// <param name="cancellationToken">
    /// Cancels this execution: removed from the queue if still pending, and surfaced as the token
    /// handed to <see cref="MissionDefinition.Run"/> if it is already running.
    /// </param>
    Task<MissionResult> SubmitAsync(MissionDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a lane by name, creating it on first use at <see cref="GlobalLane"/>'s current capacity, so a
    /// fresh lane never narrows anything until you narrow it.
    /// </summary>
    ILane Lane(string name);

    /// <summary>
    /// The lane EVERY mission draws one permit from — the scheduler's total concurrency bound, sized by
    /// <c>MissionSchedulerOptions.GlobalLaneCapacity</c> and live-resizable like any other lane.
    /// ⚠ <see cref="ILane.Hold"/> on it stops ALL admission, re-entrantly.
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
    /// Re-run admission now. ⚠ Dispatch is event-driven — submit, completion, lane change — so an
    /// <see cref="IMissionPolicy"/> that defers on an EXTERNAL condition (a clock, system load, a
    /// maintenance window) must call this when that condition changes, or the deferred item waits for
    /// unrelated traffic to wake it.
    /// </summary>
    void Reevaluate();

    /// <summary>
    /// Re-admit durable work left behind by a previous run. Explicit, never implicit: only the app knows
    /// when its own services are ready to receive recovered work. A delegate does not serialize, so
    /// <paramref name="rehydrate"/> maps a <see cref="MissionRecord"/> back to a
    /// <see cref="MissionDefinition"/>.
    /// </summary>
    /// <param name="rehydrate">Rebuilds a request from a persisted record, or returns null to drop it.</param>
    /// <param name="cancellationToken">Cancels recovery.</param>
    /// <returns>Records that were re-queued.</returns>
    Task<int> RecoverAsync(Func<MissionRecord, MissionDefinition?> rehydrate, CancellationToken cancellationToken = default);
}
