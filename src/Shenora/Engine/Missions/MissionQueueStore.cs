namespace Shenora.Engine.Missions;

/// <summary>Where a queued mission had got to when its record was last written.</summary>
public enum MissionState
{
    /// <summary>Accepted but not started when the record was written.</summary>
    Queued = 0,

    /// <summary>Started and not finished when the record was written — see <see cref="RecoveryPolicy"/>.</summary>
    Running = 1,
}

/// <summary>What <see cref="IMissionQueueStore"/> persists for one durable mission.</summary>
/// <param name="MissionId">
/// Scheduler-assigned id, and the key <see cref="IMissionQueueStore.RemoveAsync"/> is called with.
/// ⚠ <b>PER-PROCESS.</b> Recovery resubmits under a NEW id and removes this record, so a store must not
/// treat it as a durable identity for the work.
/// </param>
/// <param name="Kind">App-defined mission type, from <see cref="MissionDefinition.Kind"/>.</param>
/// <param name="Payload">App-serialized state, from <see cref="MissionDefinition.Payload"/>. Never interpreted by the kit.</param>
/// <param name="State">Queued or Running as of the last write.</param>
/// <param name="CreatedUtc">Submission time.</param>
/// <param name="Key">
/// The caller-chosen identity from <see cref="MissionDefinition.Key"/>, or null when the submission
/// named none.
/// <para>
/// 🔴 <b>The DURABLE half of the identity problem.</b> <see cref="MissionId"/> is per-process, so on the
/// next boot a store has nothing an app recognises — and the kit ships no store (D28), which makes this
/// record the wire format between the kit and EVERY adopter's storage. A rehydrate callback can now key
/// on what the app itself chose rather than on an id it has never seen.
/// </para>
/// </param>
public sealed record MissionRecord(
    string MissionId,
    string? Kind,
    string? Payload,
    MissionState State,
    DateTimeOffset CreatedUtc,
    MissionKey? Key = null);

/// <summary>
/// What happens to a record found in the queue's store at startup.
/// <para>
/// ⚠ A record found in <see cref="MissionState.Running"/> MAY BE WHAT KILLED THE PROCESS, so
/// re-running it on every boot turns one crash into a loop the user cannot escape from inside the app.
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
/// Where the pending queue LIVES when it must survive a restart. The queue itself is the scheduler's
/// own, in memory; supply this and the durable half of it is written through to storage.
/// <para>
/// <b>The kit ships no implementation</b> — storage is the app's decision and <c>Shenora</c> takes no
/// storage dependency (D28). Leave <see cref="MissionSchedulerOptions.QueueStore"/> null and every
/// mission behaves as in-memory regardless of <see cref="MissionDefinition.Durable"/>; ordering stays
/// the app's through <see cref="IMissionPolicy"/>.
/// </para>
/// <para>
/// Implementations must tolerate concurrent calls. <see cref="AppendAsync"/> is called AGAIN when a
/// mission moves from <see cref="MissionState.Queued"/> to <see cref="MissionState.Running"/>.
/// </para>
/// </summary>
public interface IMissionQueueStore
{
    /// <summary>Add the record, or update it in place if the id is already stored.</summary>
    Task AppendAsync(MissionRecord record, CancellationToken cancellationToken);

    /// <summary>Remove a record. Must not throw when the id is already gone.</summary>
    Task RemoveAsync(string missionId, CancellationToken cancellationToken);

    /// <summary>Everything still stored, for <see cref="IMissionScheduler.RecoverAsync"/>.</summary>
    Task<IReadOnlyList<MissionRecord>> LoadAsync(CancellationToken cancellationToken);
}
