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
/// WHAT should run, and the resources it needs — the reusable half. Submit it to an
/// <see cref="IMissionScheduler"/>, which turns it into a <see cref="MissionExecution"/>.
///
/// <para>
/// The definition/execution split is deliberate and it is the shape the rest of this layer is built
/// on: a definition is a description that can be submitted more than once (and, when
/// <see cref="Durable"/>, rebuilt from a <see cref="MissionRecord"/> after a restart), while an
/// execution is one specific run of it with its own id, attempt count and lifetime. Today one submit
/// produces one execution; the split is here from the start because introducing it later would change
/// <see cref="IMissionScheduler.SubmitAsync"/>, every mission body's parameter, all three
/// <see cref="IMissionObserver"/> methods and both <see cref="IMissionPolicy"/> methods at once.
/// </para>
/// </summary>
public sealed class MissionDefinition
{
    /// <summary>
    /// The body. Runs when every claim is free and every lane has a permit.
    ///
    /// <para>
    /// Retried under <see cref="Retry"/> — UNLESS <see cref="Commit"/> is also set, in which case
    /// this runs exactly once. See <see cref="Commit"/> for why that distinction is load-bearing.
    /// </para>
    /// </summary>
    public required Func<MissionExecution, CancellationToken, Task> Run { get; init; }

    /// <summary>
    /// Optional second phase. When set, <see cref="Run"/> executes ONCE and only this is retried.
    ///
    /// <para>
    /// This exists because of a measured lesson, not for symmetry. A compress-then-replace operation
    /// whose REPLACE failed on a locked target used to retry the whole thing — recompressing several
    /// seconds of work to redo a file move that takes microseconds, and doing it up to three times.
    /// Split the expensive, idempotent-by-staging phase (write to a temp location) from the cheap
    /// commit (move it into place) and only the commit needs the retry budget. Left as advice this
    /// lesson does not survive; modelled here, the shape is the API.
    /// </para>
    /// </summary>
    public Func<MissionExecution, CancellationToken, Task>? Commit { get; init; }

    /// <summary>
    /// Resources this work needs. Admitted only when NONE conflicts with in-flight work or with
    /// earlier still-pending work. Empty = no exclusion, bounded only by lanes.
    /// </summary>
    public IReadOnlyList<MissionClaim> Claims { get; init; } = [];

    /// <summary>
    /// Lanes this work draws permits from, on top of the scheduler's default lane. Use for a scarce
    /// shared resource (a single GPU, a rate-limited endpoint, a memory budget) that a capacity,
    /// rather than a claim, describes.
    ///
    /// <para>
    /// A name the scheduler has not seen creates that lane at the DEFAULT capacity rather than
    /// throwing, so a typo here costs the exclusivity you configured elsewhere and reports nothing.
    /// </para>
    /// </summary>
    public IReadOnlyList<MissionLane> Lanes { get; init; } = [];

    /// <summary>
    /// Admission order among items that are otherwise eligible: HIGHER runs first, default 0.
    ///
    /// <para>
    /// Ties break by submission order, so the default gives plain FIFO. Priority never overrides
    /// SAFETY — a higher-priority item still waits for a conflicting claim to clear — and it never
    /// reorders work that conflicts with an earlier item, so raising a priority cannot corrupt an
    /// ordering the caller depended on. It only re-ranks work that could legally run in any order.
    /// </para>
    /// </summary>
    public int Priority { get; init; }

    /// <summary>Dedup identity. Null = never deduplicated.</summary>
    public MissionKey? Key { get; init; }

    /// <summary>Retry policy. Null = <see cref="RetryPolicy.None"/>.</summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>
    /// Write this mission through to <see cref="MissionSchedulerOptions.QueueStore"/> so it survives a
    /// restart. Per-mission, not per-scheduler: cheap in-memory missions and durable ones share ONE
    /// queue, and only the durable ones reach storage. Ignored when no store is configured.
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
