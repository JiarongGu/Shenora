namespace Shenora.Core;

/// <summary>Inputs for <see cref="MissionScheduler"/>.</summary>
public sealed class MissionSchedulerOptions
{
    /// <summary>
    /// Permits in the default lane every request draws from — the global concurrency bound.
    /// 0 = auto: <c>clamp(ProcessorCount - 1, 1, 4)</c>, the value the family's planners
    /// independently arrived at for disk-IO-bound work.
    ///
    /// <para>
    /// Pass an explicit value in tests. Concurrency assertions keyed off the host's core count pass
    /// or fail depending on the machine, which is how a parallelism regression hides on the one box
    /// that happens to have two cores.
    /// </para>
    /// </summary>
    public int DefaultLaneCapacity { get; init; }

    /// <summary>
    /// Claim scopes this scheduler understands. A <see cref="MissionClaim"/> naming an unregistered
    /// scope throws at submit — silently ignoring it would drop an exclusion the caller asked for,
    /// which is the one failure mode a scheduler must never have.
    /// </summary>
    public IReadOnlyList<IClaimScope> Scopes { get; init; } = [];

    /// <summary>
    /// The app's ordering and timing rules — <b>what</b> to pick up next and <b>when</b>. Null uses
    /// <see cref="PriorityMissionPolicy"/> (priority, then FIFO). A policy can only choose among items
    /// the scheduler has ALREADY found safe to run, so it can delay work but never corrupt it —
    /// see <see cref="IMissionPolicy"/>.
    /// </summary>
    public IMissionPolicy? Policy { get; init; }

    /// <summary>
    /// Lifecycle listeners — metrics, tracing, or attaching a progress registry. Each call is
    /// guarded, so a throwing observer cannot fail the work it is watching.
    /// </summary>
    public IReadOnlyList<IMissionObserver> Observers { get; init; } = [];

    /// <summary>
    /// Where the pending queue lives across restarts. Null (the default) keeps the queue entirely in
    /// memory, and <see cref="MissionDefinition.Durable"/> is then ignored — a mission is durable
    /// because the queue holding it is backed by a store.
    /// </summary>
    public IMissionQueueStore? QueueStore { get; init; }

    /// <summary>
    /// Recovery decision per record, by <see cref="MissionRecord.Kind"/> and <see cref="MissionRecord.State"/>.
    /// Null uses the safe default: <see cref="RecoveryPolicy.Requeue"/> for
    /// <see cref="MissionState.Queued"/>, <see cref="RecoveryPolicy.Fail"/> for
    /// <see cref="MissionState.Running"/>.
    /// </summary>
    public Func<MissionRecord, RecoveryPolicy>? RecoveryPolicyFor { get; init; }

    /// <summary>
    /// Diagnostics sink. Guarded and lazily formatted through <see cref="AppCallback.Log"/>, so a
    /// throwing sink cannot take the scheduler down.
    /// </summary>
    public Action<string>? Log { get; init; }
}
