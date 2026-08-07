using Shenora;

namespace Shenora.Engine.Missions;

/// <summary>Inputs for <see cref="MissionScheduler"/>.</summary>
public sealed class MissionSchedulerOptions
{
    /// <summary>
    /// Permits in the lane every request draws from — the global concurrency bound
    /// (<see cref="IMissionScheduler.GlobalLane"/>). <b>Null = auto</b>:
    /// <c>clamp(ProcessorCount - 1, 1, 4)</c>, the value the family's planners independently arrived at
    /// for disk-IO-bound work. Any value below 1 THROWS.
    ///
    /// <para>
    /// ⚠ <b>It is a CEILING over every named lane, not merely their starting value.</b> A named lane runs
    /// at <c>min(its own capacity, this)</c>, so setting this to 1 and a lane to 3 gives a lane that runs
    /// at 1 — read <see cref="ILane.EffectiveCapacity"/> for what a lane will actually reach. Setting a
    /// lane's capacity above this bound is legal and logs why it will not take effect.
    /// </para>
    /// <para>
    /// <b>Set this to the widest any lane will ever need</b> and use each lane's own capacity to narrow
    /// from there. It is <c>init</c>-only because it is a starting value: to move the bound at RUNTIME —
    /// a governor throttling under load and restoring afterwards — set
    /// <c>IMissionScheduler.GlobalLane.Capacity</c>, which is live-resizable like any other lane.
    /// </para>
    /// <para>
    /// Pass an explicit value in tests. Concurrency assertions keyed off the host's core count pass
    /// or fail depending on the machine, which is how a parallelism regression hides on the one box
    /// that happens to have two cores.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Renamed from <c>DefaultLaneCapacity</c>, which no longer exists — a documented break, migrated by
    /// renaming the assignment. The old name is what CAUSED the defect that produced this one: it reads as
    /// "the default capacity a lane gets" and it is really the global ceiling over every lane, so the first
    /// adopter set it to 1 believing it was a per-lane default, gave a named lane 3, and got a lane that ran
    /// at 1 (2026-08-05). No compatibility alias was kept, deliberately: a warning-level alias leaves both
    /// names in the surface for years and the misleading one keeps being written, which is the whole thing
    /// the rename is for. A compile error naming the new property is a better outcome than a warning.
    /// </para>
    /// <para>
    /// ⚠ <b><c>int?</c> rather than <c>0 = auto</c>, and it was the last magic sentinel on the kit's
    /// surface.</b> Every other option here carries a real default and REJECTS nonsense
    /// (<c>LeaseTimeout</c> 30 s, <c>PollInterval</c> 50 ms, <c>MaxQueuedNotifications</c> 10 000 — the IPC
    /// options throw rather than reinterpret). A sentinel makes one legal-looking value mean something else
    /// entirely, so <c>0</c> silently became "auto" when what it actually describes is a scheduler that can
    /// never run anything. Now <c>null</c> says "choose for me" and <c>0</c> is the error it always was.
    /// </para>
    /// </remarks>
    public int? GlobalLaneCapacity { get; set; }

    /// <summary>
    /// Claim scopes this scheduler understands. A <see cref="MissionClaim"/> naming an unregistered
    /// scope throws at submit — silently ignoring it would drop an exclusion the caller asked for,
    /// which is the one failure mode a scheduler must never have.
    /// </summary>
    public IReadOnlyList<IClaimScope> Scopes { get; set; } = [];

    /// <summary>
    /// The app's ordering and timing rules — <b>what</b> to pick up next and <b>when</b>. Null uses
    /// <see cref="PriorityMissionPolicy"/> (priority, then FIFO). A policy can only choose among items
    /// the scheduler has ALREADY found safe to run, so it can delay work but never corrupt it —
    /// see <see cref="IMissionPolicy"/>.
    /// </summary>
    public IMissionPolicy? Policy { get; set; }

    /// <summary>
    /// Lifecycle listeners — metrics, tracing, or attaching a progress registry. Each call is
    /// guarded, so a throwing observer cannot fail the work it is watching.
    /// </summary>
    public IReadOnlyList<IMissionObserver> Observers { get; set; } = [];

    /// <summary>
    /// Where the pending queue lives across restarts. Null (the default) keeps the queue entirely in
    /// memory, and <see cref="MissionDefinition.Durable"/> is then ignored — a mission is durable
    /// because the queue holding it is backed by a store.
    /// </summary>
    public IMissionQueueStore? QueueStore { get; set; }

    /// <summary>
    /// Recovery decision per record, by <see cref="MissionRecord.Kind"/> and <see cref="MissionRecord.State"/>.
    /// Null uses the safe default: <see cref="RecoveryPolicy.Requeue"/> for
    /// <see cref="MissionState.Queued"/>, <see cref="RecoveryPolicy.Fail"/> for
    /// <see cref="MissionState.Running"/>.
    /// </summary>
    public Func<MissionRecord, RecoveryPolicy>? RecoveryPolicyFor { get; set; }

    /// <summary>
    /// Diagnostics sink. Guarded and lazily formatted through <see cref="AppCallback.Log"/>, so a
    /// throwing sink cannot take the scheduler down.
    /// </summary>
    public Action<string>? Log { get; set; }
}
