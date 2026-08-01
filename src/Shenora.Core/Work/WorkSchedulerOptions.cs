namespace Shenora.Core;

/// <summary>Inputs for <see cref="WorkScheduler"/>.</summary>
public sealed class WorkSchedulerOptions
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
    /// Claim scopes this scheduler understands. A <see cref="WorkClaim"/> naming an unregistered
    /// scope throws at submit — silently ignoring it would drop an exclusion the caller asked for,
    /// which is the one failure mode a scheduler must never have.
    /// </summary>
    public IReadOnlyList<IClaimScope> Scopes { get; init; } = [];

    /// <summary>
    /// The app's ordering and timing rules — <b>what</b> to pick up next and <b>when</b>. Null uses
    /// <see cref="PriorityWorkPolicy"/> (priority, then FIFO). A policy can only choose among items
    /// the scheduler has ALREADY found safe to run, so it can delay work but never corrupt it —
    /// see <see cref="IWorkPolicy"/>.
    /// </summary>
    public IWorkPolicy? Policy { get; init; }

    /// <summary>
    /// Lifecycle listeners — metrics, tracing, or attaching a progress registry. Each call is
    /// guarded, so a throwing observer cannot fail the work it is watching.
    /// </summary>
    public IReadOnlyList<IWorkObserver> Observers { get; init; } = [];

    /// <summary>Where durable work persists. Null = <see cref="WorkRequest.Durable"/> is ignored.</summary>
    public IWorkStore? Store { get; init; }

    /// <summary>
    /// Recovery decision per record, by <see cref="WorkRecord.Kind"/> and <see cref="WorkRecord.State"/>.
    /// Null uses the safe default: <see cref="RecoveryPolicy.Requeue"/> for
    /// <see cref="WorkState.Queued"/>, <see cref="RecoveryPolicy.Fail"/> for
    /// <see cref="WorkState.Running"/>.
    /// </summary>
    public Func<WorkRecord, RecoveryPolicy>? RecoveryPolicyFor { get; init; }

    /// <summary>
    /// Diagnostics sink. Guarded and lazily formatted through <see cref="AppCallback.Log"/>, so a
    /// throwing sink cannot take the scheduler down.
    /// </summary>
    public Action<string>? Log { get; init; }
}
