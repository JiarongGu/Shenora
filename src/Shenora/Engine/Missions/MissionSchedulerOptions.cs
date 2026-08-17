using Microsoft.Extensions.Logging;

namespace Shenora.Engine.Missions;

/// <summary>Inputs for <see cref="MissionScheduler"/>.</summary>
public sealed class MissionSchedulerOptions
{
    /// <summary>
    /// Permits in the lane every request draws from — the global concurrency bound
    /// (<see cref="IMissionScheduler.GlobalLane"/>). <b>Null = auto</b>:
    /// <c>clamp(ProcessorCount - 1, 1, 4)</c>. Any value below 1 THROWS, <c>0</c> included.
    /// <para>
    /// ⚠ <b>A CEILING over every named lane, not merely their starting value.</b> A named lane runs at
    /// <c>min(its own capacity, this)</c> — see <see cref="ILane.EffectiveCapacity"/> for what a lane will
    /// actually reach. Setting a lane's capacity above this bound is legal and logs why it will not take
    /// effect.
    /// </para>
    /// <para>
    /// Set it to the widest any lane will ever need. To move the bound at RUNTIME set
    /// <c>IMissionScheduler.GlobalLane.Capacity</c>, which is live-resizable like any other lane.
    /// </para>
    /// </summary>
    public int? GlobalLaneCapacity { get; set; }

    /// <summary>
    /// Claim scopes this scheduler understands. A <see cref="MissionClaim"/> naming an unregistered scope
    /// throws at submit rather than being ignored, which would silently drop the exclusion it asked for.
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
    public ILogger? Log { get; set; }
}
