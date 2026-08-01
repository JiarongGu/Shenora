namespace Shenora.Core;

/// <summary>
/// Caller-chosen identity used to DEDUPLICATE submissions: a request whose key matches one already
/// pending or in flight completes against that one instead of queueing a second.
/// </summary>
/// <param name="Value">The identity. Compared ordinally.</param>
public readonly record struct WorkKey(string Value)
{
    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>
/// How a failed attempt is retried. Defaults match the value the family's planners independently
/// settled on for transient filesystem locks held by an external process.
/// </summary>
public sealed class RetryPolicy
{
    /// <summary>Total attempts including the first. 1 = no retry. Default 3.</summary>
    public int Attempts { get; init; } = 3;

    /// <summary>Base delay, multiplied by the attempt number (500ms, 1s, 1.5s…). Default 500ms.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Whether a failure is worth retrying. Default: retry <see cref="IOException"/> only —
    /// retrying a <see cref="NullReferenceException"/> three times just delays the report.
    /// </summary>
    public Func<Exception, bool> IsTransient { get; init; } = static ex => ex is IOException;

    /// <summary>A policy that never retries.</summary>
    public static RetryPolicy None { get; } = new() { Attempts = 1 };
}

/// <summary>What the scheduler hands a running body.</summary>
/// <param name="WorkId">Scheduler-assigned id, stable across retries and durable across a restart.</param>
/// <param name="Attempt">1-based attempt number.</param>
/// <param name="Cancellation">Observed for cancellation; the body MUST honour it.</param>
public readonly record struct WorkContext(string WorkId, int Attempt, CancellationToken Cancellation);

/// <summary>
/// A unit of work plus the resources it needs. Submit it to an <see cref="IWorkScheduler"/>.
/// </summary>
public sealed class WorkRequest
{
    /// <summary>
    /// The body. Runs when every claim is free and every lane has a permit.
    ///
    /// <para>
    /// Retried under <see cref="Retry"/> — UNLESS <see cref="Commit"/> is also set, in which case
    /// this runs exactly once. See <see cref="Commit"/> for why that distinction is load-bearing.
    /// </para>
    /// </summary>
    public required Func<WorkContext, Task> Run { get; init; }

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
    public Func<WorkContext, Task>? Commit { get; init; }

    /// <summary>
    /// Resources this work needs. Admitted only when NONE conflicts with in-flight work or with
    /// earlier still-pending work. Empty = no exclusion, bounded only by lanes.
    /// </summary>
    public IReadOnlyList<WorkClaim> Claims { get; init; } = [];

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
    public IReadOnlyList<WorkLane> Lanes { get; init; } = [];

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
    public WorkKey? Key { get; init; }

    /// <summary>Retry policy. Null = <see cref="RetryPolicy.None"/>.</summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>
    /// Persist this request through <see cref="IWorkStore"/> so it survives a restart. Per-request,
    /// not per-scheduler: cheap in-memory work and durable work share one queue.
    /// </summary>
    public bool Durable { get; init; }

    /// <summary>
    /// App-defined work type. Carried on the <see cref="WorkRecord"/> so recovery can rebuild the
    /// body and choose a <see cref="RecoveryPolicy"/> per kind.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>
    /// App-serialized state stored with a durable record, handed back at recovery. The kit never
    /// interprets it.
    /// </summary>
    public string? Payload { get; init; }
}
