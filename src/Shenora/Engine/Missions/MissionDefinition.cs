namespace Shenora.Engine.Missions;

/// <summary>
/// Caller-chosen identity used to DEDUPLICATE submissions: a request whose key matches one already
/// pending or in flight completes against that one instead of queueing a second.
/// </summary>
/// <param name="Value">The identity. Compared ordinally.</param>
public readonly record struct MissionKey(string Value)
{
    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>
/// WHAT should run, and the resources it needs — the reusable half, submittable more than once. Submit
/// it to an <see cref="IMissionScheduler"/>, which turns it into a <see cref="MissionExecution"/>: one
/// specific run with its own id, attempt count and lifetime.
/// </summary>
public sealed class MissionDefinition
{
    /// <summary>
    /// The body. Runs when every claim is free and every lane has a permit. Retried under
    /// <see cref="Retry"/> — UNLESS <see cref="Commit"/> is also set, in which case this runs once.
    /// </summary>
    public required Func<MissionExecution, CancellationToken, Task> Run { get; init; }

    /// <summary>
    /// Optional second phase. When set, <see cref="Run"/> executes ONCE and only this is retried — the
    /// shape for an expensive staging phase (write to a temp location) plus a cheap commit (move it into
    /// place), where only the commit needs a retry budget.
    /// </summary>
    public Func<MissionExecution, CancellationToken, Task>? Commit { get; init; }

    /// <summary>
    /// Resources this work needs. Admitted only when NONE conflicts with in-flight work or with
    /// earlier still-pending work. Empty = no exclusion, bounded only by lanes.
    /// </summary>
    public IReadOnlyList<MissionClaim> Claims { get; init; } = [];

    /// <summary>
    /// Lanes this work draws permits from, on top of the scheduler's default lane. Use for a scarce
    /// shared resource (a single GPU, a rate-limited endpoint, a memory budget) that a capacity, rather
    /// than a claim, describes.
    /// <para>
    /// ⚠ A name the scheduler has not seen creates that lane at the DEFAULT capacity rather than
    /// throwing, so a typo here costs the exclusivity you configured elsewhere and reports nothing.
    /// </para>
    /// </summary>
    public IReadOnlyList<MissionLane> Lanes { get; init; } = [];

    /// <summary>
    /// Admission order among items that are otherwise eligible: HIGHER runs first, default 0. Ties break
    /// by submission order, so the default is plain FIFO. Priority never overrides safety and never
    /// reorders conflicting work — it re-ranks only work that could legally run in any order.
    /// </summary>
    public int Priority { get; init; }

    /// <summary>Dedup identity. Null = never deduplicated.</summary>
    public MissionKey? Key { get; init; }

    /// <summary>Retry policy. Null = <see cref="RetryPolicy.None"/>.</summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>
    /// Write this mission through to <see cref="MissionSchedulerOptions.QueueStore"/> so it survives a
    /// restart. Per-mission: durable and in-memory missions share ONE queue. Ignored when no store is
    /// configured.
    /// </summary>
    public bool Durable { get; init; }

    /// <summary>
    /// App-defined mission type. Carried on the <see cref="MissionRecord"/> so recovery can rebuild the
    /// body and choose a <see cref="RecoveryPolicy"/> per kind.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>
    /// App-serialized state stored with a durable record, handed back at recovery. The kit never
    /// interprets it.
    /// </summary>
    public string? Payload { get; init; }
}
