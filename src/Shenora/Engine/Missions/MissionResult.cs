using Shenora;
using Shenora.Core.Ipc;

namespace Shenora.Engine.Missions;

/// <summary>How a submitted <see cref="MissionDefinition"/> ended.</summary>
public enum MissionOutcome
{
    /// <summary>The body ran to completion.</summary>
    Completed = 0,

    /// <summary>The body threw, and either had no retry budget or exhausted it.</summary>
    Failed = 1,

    /// <summary>Cancelled before finishing — while queued, or by the token while running.</summary>
    Cancelled = 2,

    /// <summary>
    /// An identical <see cref="MissionDefinition.Key"/> was already pending or in flight; this submission
    /// carries THAT work's outcome and its body never ran.
    /// </summary>
    Deduplicated = 3,
}

/// <summary>
/// The outcome of a submission.
///
/// <para>
/// A failing body does NOT throw out of <see cref="IMissionScheduler.SubmitAsync"/> — the failure is
/// reported here. That is deliberate: this is a queue, and a submitter is frequently a batch loop
/// that must survive one bad item, which is exactly how both of the family's planners modelled it.
/// Callers who do want the exception call <see cref="ThrowIfFailed"/>. Programming errors (an
/// unregistered claim scope, a disposed scheduler) still throw at submit, because those are bugs in
/// the caller rather than outcomes of the work. An unrecognized LANE name is not one of them — it is
/// created at the default capacity (see <see cref="IMissionScheduler.SubmitAsync"/>).
/// </para>
/// </summary>
public sealed class MissionResult
{
    internal MissionResult(MissionOutcome outcome, string missionId, int attempts, Exception? error)
    {
        Outcome = outcome;
        MissionId = missionId;
        Attempts = attempts;
        Error = error;
    }

    /// <summary>How it ended.</summary>
    public MissionOutcome Outcome { get; }

    /// <summary>Scheduler-assigned id of the work this result describes.</summary>
    public string MissionId { get; }

    /// <summary>Attempts actually made. 0 when deduplicated or cancelled while queued.</summary>
    public int Attempts { get; }

    /// <summary>The final exception when <see cref="Outcome"/> is <see cref="MissionOutcome.Failed"/>.</summary>
    public Exception? Error { get; }

    /// <summary>True for <see cref="MissionOutcome.Completed"/> or <see cref="MissionOutcome.Deduplicated"/>.</summary>
    public bool Succeeded => Outcome is MissionOutcome.Completed or MissionOutcome.Deduplicated;

    /// <summary>Rethrow the failure, preserving its original stack, for callers who prefer exceptions.</summary>
    public void ThrowIfFailed()
    {
        if (Outcome == MissionOutcome.Failed && Error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(Error).Throw();
        if (Outcome == MissionOutcome.Cancelled)
            throw new OperationCanceledException($"mission '{MissionId}' was cancelled");
    }
}
