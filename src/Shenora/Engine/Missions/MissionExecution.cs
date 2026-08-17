namespace Shenora.Engine.Missions;

/// <summary>
/// ONE specific run of a <see cref="MissionDefinition"/> — its identity, its place in the queue, and how
/// far it has got. The scheduler hands the same type to the mission body, to
/// <see cref="IMissionObserver"/>, to <see cref="IMissionPolicy"/> and out of
/// <see cref="IMissionScheduler.Snapshot"/>.
/// <para>
/// ⚠ It carries no <see cref="CancellationToken"/> deliberately — the body receives one as its own
/// argument. A token inside a snapshot value invites holding it and using it later.
/// </para>
/// </summary>
/// <param name="MissionId">
/// Scheduler-assigned id, stable across retries. ⚠ <b>PER-PROCESS — a recovered mission is resubmitted
/// under a NEW id</b>, so this cannot key state that must survive a restart. Use
/// <see cref="MissionDefinition.Key"/> for an identity you chose and can recognise afterwards.
/// </param>
/// <param name="Kind">App-defined mission type, from <see cref="MissionDefinition.Kind"/>.</param>
/// <param name="Priority">From <see cref="MissionDefinition.Priority"/>.</param>
/// <param name="QueuedUtc">When the submission was accepted.</param>
/// <param name="Sequence">Monotonic submission counter — the tie-break that makes ordering total and stable.</param>
/// <param name="Attempt">
/// 1-based attempt number while running; 0 before the first attempt starts, which is the value an
/// <see cref="IMissionPolicy"/> always sees, because a policy is only ever asked about work that has
/// not started yet.
/// </param>
/// <param name="IsRunning">True once the body is executing. False while queued.</param>
/// <param name="Key">
/// The caller-chosen identity from <see cref="MissionDefinition.Key"/>, or null when the submission
/// named none.
/// <para>
/// 🔴 <b>The only thing here an app can RECOGNISE.</b> <see cref="MissionId"/> is the scheduler's and is
/// per-process, so without this an app could not map a progress report, an observer callback or a
/// <see cref="IMissionScheduler.Snapshot"/> row back to the item it submitted — a queue UI could list
/// work and not say what any of it was. The only workaround was to encode instance identity into
/// <see cref="Kind"/>, which is documented as a TYPE.
/// </para>
/// </param>
public readonly record struct MissionExecution(
    string MissionId,
    string? Kind,
    int Priority,
    DateTimeOffset QueuedUtc,
    long Sequence,
    int Attempt = 0,
    bool IsRunning = false,
    MissionKey? Key = null);
