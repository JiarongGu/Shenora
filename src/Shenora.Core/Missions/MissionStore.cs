namespace Shenora.Core;

/// <summary>The persisted state of a durable mission.</summary>
public enum MissionState
{
    /// <summary>Accepted but not started when the record was written.</summary>
    Queued = 0,

    /// <summary>Started and not finished when the record was written — see <see cref="RecoveryPolicy"/>.</summary>
    Running = 1,
}

/// <summary>What <see cref="IMissionStore"/> persists for one durable request.</summary>
/// <param name="MissionId">Scheduler-assigned id; stable across a restart.</param>
/// <param name="Kind">App-defined mission type, from <see cref="MissionDefinition.Kind"/>.</param>
/// <param name="Payload">App-serialized state, from <see cref="MissionDefinition.Payload"/>. Never interpreted by the kit.</param>
/// <param name="State">Queued or Running as of the last write.</param>
/// <param name="CreatedUtc">Submission time.</param>
public sealed record MissionRecord(
    string MissionId,
    string? Kind,
    string? Payload,
    MissionState State,
    DateTimeOffset CreatedUtc);

/// <summary>
/// What happens to a durable record found at startup.
///
/// <para>
/// The distinction is not bureaucratic — it comes from a family incident. Work found in
/// <see cref="MissionState.Running"/> after a restart MAY BE WHAT KILLED THE PROCESS (a native GPU
/// render, in that case). Re-running it on every boot turns one crash into an unrecoverable loop the
/// user cannot escape from inside the app. So the default for Running records is
/// <see cref="Fail"/> — surface it and let a human retry — while Queued records, which by definition
/// never started, requeue safely.
/// </para>
/// </summary>
public enum RecoveryPolicy
{
    /// <summary>Re-submit. The safe default for <see cref="MissionState.Queued"/>.</summary>
    Requeue = 0,

    /// <summary>Report as failed without running. The safe default for <see cref="MissionState.Running"/>.</summary>
    Fail = 1,

    /// <summary>Drop the record silently.</summary>
    Discard = 2,
}

/// <summary>
/// Where durable work is persisted. <b>The kit ships no implementation</b> — not a SQLite one, not a
/// JSON one — because storage is the app's decision and `Shenora.Core` takes no storage dependency.
/// Supply one via <see cref="MissionSchedulerOptions.Store"/>; leave it null and every request behaves
/// as in-memory regardless of <see cref="MissionDefinition.Durable"/>.
///
/// <para>
/// Implementations must tolerate being called concurrently, and should treat
/// <see cref="SaveAsync"/> as an upsert keyed on <see cref="MissionRecord.MissionId"/>.
/// </para>
/// </summary>
public interface IMissionStore
{
    /// <summary>Insert or update a record.</summary>
    Task SaveAsync(MissionRecord record, CancellationToken cancellationToken);

    /// <summary>Delete a record. Must not throw when the id is already gone.</summary>
    Task RemoveAsync(string missionId, CancellationToken cancellationToken);

    /// <summary>Every unfinished record, for <see cref="IMissionScheduler.RecoverAsync"/>.</summary>
    Task<IReadOnlyList<MissionRecord>> LoadPendingAsync(CancellationToken cancellationToken);
}
