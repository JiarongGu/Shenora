using Shenora;

namespace Shenora.Missions;

/// <summary>Where a queued mission had got to when its record was last written.</summary>
public enum MissionState
{
    /// <summary>Accepted but not started when the record was written.</summary>
    Queued = 0,

    /// <summary>Started and not finished when the record was written — see <see cref="RecoveryPolicy"/>.</summary>
    Running = 1,
}

/// <summary>What <see cref="IMissionQueueStore"/> persists for one durable mission.</summary>
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
/// What happens to a record found in the queue's store at startup.
///
/// <para>
/// The distinction is not bureaucratic — it comes from a family incident. A mission found in
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
/// Where the pending queue LIVES when it must survive a restart. The queue itself is the scheduler's
/// own, in memory; supply this and the durable half of it is written through to storage.
///
/// <para>
/// <b>The kit ships no implementation</b> — not a SQLite one, not a JSON one — because storage is the
/// app's decision and <c>Shenora</c> takes no storage dependency. Leave
/// <see cref="MissionSchedulerOptions.QueueStore"/> null and every mission behaves as in-memory
/// regardless of <see cref="MissionDefinition.Durable"/>.
/// </para>
///
/// <para>
/// <b>Why this is the queue's store and not a separate "durable missions" service</b> (renamed from
/// <c>IMissionStore</c>): durability is not a second concept sitting beside the queue — it is where
/// the queue's entries are kept. Describing it as its own thing is what made recovery read oddly, as
/// though records arrived from somewhere other than the queue they were enqueued into.
/// </para>
///
/// <para>
/// A pluggable QUEUE — one that also owned ordering and could be read asynchronously — was designed
/// and rejected: it puts an <c>await</c> in the dispatch path, which cannot run under the scheduler's
/// lock, forcing admission to re-validate against a collection that may have changed underneath. That
/// is a race in the one place where a race corrupts rather than delays, bought for a capability no
/// consumer has asked for. Ordering is already the app's through <see cref="IMissionPolicy"/>.
/// </para>
///
/// <para>
/// Implementations must tolerate being called concurrently, and should treat
/// <see cref="AppendAsync"/> as an upsert keyed on <see cref="MissionRecord.MissionId"/> — it is
/// called again when a mission moves from <see cref="MissionState.Queued"/> to
/// <see cref="MissionState.Running"/>.
/// </para>
/// </summary>
public interface IMissionQueueStore
{
    /// <summary>Add the record, or update it in place if the id is already stored.</summary>
    Task AppendAsync(MissionRecord record, CancellationToken cancellationToken);

    /// <summary>Remove a record. Must not throw when the id is already gone.</summary>
    Task RemoveAsync(string missionId, CancellationToken cancellationToken);

    /// <summary>
    /// Everything still stored, for <see cref="IMissionScheduler.RecoverAsync"/> — i.e. what the
    /// queue held when the process last stopped.
    /// </summary>
    Task<IReadOnlyList<MissionRecord>> LoadAsync(CancellationToken cancellationToken);
}
