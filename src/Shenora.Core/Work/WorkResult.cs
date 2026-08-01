namespace Shenora.Core;

/// <summary>How a submitted <see cref="WorkRequest"/> ended.</summary>
public enum WorkOutcome
{
    /// <summary>The body ran to completion.</summary>
    Completed = 0,

    /// <summary>The body threw, and either had no retry budget or exhausted it.</summary>
    Failed = 1,

    /// <summary>Cancelled before finishing — while queued, or by the token while running.</summary>
    Cancelled = 2,

    /// <summary>
    /// An identical <see cref="WorkRequest.Key"/> was already pending or in flight; this submission
    /// carries THAT work's outcome and its body never ran.
    /// </summary>
    Deduplicated = 3,
}

/// <summary>
/// The outcome of a submission.
///
/// <para>
/// A failing body does NOT throw out of <see cref="IWorkScheduler.SubmitAsync"/> — the failure is
/// reported here. That is deliberate: this is a queue, and a submitter is frequently a batch loop
/// that must survive one bad item, which is exactly how both of the family's planners modelled it.
/// Callers who do want the exception call <see cref="ThrowIfFailed"/>. Programming errors (unknown
/// lane, unknown claim scope, disposed scheduler) still throw at submit, because those are bugs in
/// the caller rather than outcomes of the work.
/// </para>
/// </summary>
public sealed class WorkResult
{
    internal WorkResult(WorkOutcome outcome, string workId, int attempts, Exception? error)
    {
        Outcome = outcome;
        WorkId = workId;
        Attempts = attempts;
        Error = error;
    }

    /// <summary>How it ended.</summary>
    public WorkOutcome Outcome { get; }

    /// <summary>Scheduler-assigned id of the work this result describes.</summary>
    public string WorkId { get; }

    /// <summary>Attempts actually made. 0 when deduplicated or cancelled while queued.</summary>
    public int Attempts { get; }

    /// <summary>The final exception when <see cref="Outcome"/> is <see cref="WorkOutcome.Failed"/>.</summary>
    public Exception? Error { get; }

    /// <summary>True for <see cref="WorkOutcome.Completed"/> or <see cref="WorkOutcome.Deduplicated"/>.</summary>
    public bool Succeeded => Outcome is WorkOutcome.Completed or WorkOutcome.Deduplicated;

    /// <summary>Rethrow the failure, preserving its original stack, for callers who prefer exceptions.</summary>
    public void ThrowIfFailed()
    {
        if (Outcome == WorkOutcome.Failed && Error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(Error).Throw();
        if (Outcome == WorkOutcome.Cancelled)
            throw new OperationCanceledException($"work '{WorkId}' was cancelled");
    }
}
