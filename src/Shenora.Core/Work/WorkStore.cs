namespace Shenora.Core;

/// <summary>The persisted state of a durable work item.</summary>
public enum WorkState
{
    /// <summary>Accepted but not started when the record was written.</summary>
    Queued = 0,

    /// <summary>Started and not finished when the record was written — see <see cref="RecoveryPolicy"/>.</summary>
    Running = 1,
}

/// <summary>What <see cref="IWorkStore"/> persists for one durable request.</summary>
/// <param name="WorkId">Scheduler-assigned id; stable across a restart.</param>
/// <param name="Kind">App-defined work type, from <see cref="WorkRequest.Kind"/>.</param>
/// <param name="Payload">App-serialized state, from <see cref="WorkRequest.Payload"/>. Never interpreted by the kit.</param>
/// <param name="State">Queued or Running as of the last write.</param>
/// <param name="CreatedUtc">Submission time.</param>
public sealed record WorkRecord(
    string WorkId,
    string? Kind,
    string? Payload,
    WorkState State,
    DateTimeOffset CreatedUtc);

/// <summary>
/// What happens to a durable record found at startup.
///
/// <para>
/// The distinction is not bureaucratic — it comes from a family incident. Work found in
/// <see cref="WorkState.Running"/> after a restart MAY BE WHAT KILLED THE PROCESS (a native GPU
/// render, in that case). Re-running it on every boot turns one crash into an unrecoverable loop the
/// user cannot escape from inside the app. So the default for Running records is
/// <see cref="Fail"/> — surface it and let a human retry — while Queued records, which by definition
/// never started, requeue safely.
/// </para>
/// </summary>
public enum RecoveryPolicy
{
    /// <summary>Re-submit. The safe default for <see cref="WorkState.Queued"/>.</summary>
    Requeue = 0,

    /// <summary>Report as failed without running. The safe default for <see cref="WorkState.Running"/>.</summary>
    Fail = 1,

    /// <summary>Drop the record silently.</summary>
    Discard = 2,
}

/// <summary>
/// Where durable work is persisted. <b>The kit ships no implementation</b> — not a SQLite one, not a
/// JSON one — because storage is the app's decision and `Shenora.Core` takes no storage dependency.
/// Supply one via <see cref="WorkSchedulerOptions.Store"/>; leave it null and every request behaves
/// as in-memory regardless of <see cref="WorkRequest.Durable"/>.
///
/// <para>
/// Implementations must tolerate being called concurrently, and should treat
/// <see cref="SaveAsync"/> as an upsert keyed on <see cref="WorkRecord.WorkId"/>.
/// </para>
/// </summary>
public interface IWorkStore
{
    /// <summary>Insert or update a record.</summary>
    Task SaveAsync(WorkRecord record, CancellationToken cancellationToken);

    /// <summary>Delete a record. Must not throw when the id is already gone.</summary>
    Task RemoveAsync(string workId, CancellationToken cancellationToken);

    /// <summary>Every unfinished record, for <see cref="IWorkScheduler.RecoverAsync"/>.</summary>
    Task<IReadOnlyList<WorkRecord>> LoadPendingAsync(CancellationToken cancellationToken);
}
