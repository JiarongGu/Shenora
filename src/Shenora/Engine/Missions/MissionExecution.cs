using Shenora;

namespace Shenora.Engine.Missions;

/// <summary>
/// ONE specific run of a <see cref="MissionDefinition"/> — its identity, its place in the queue, and
/// how far it has got. The scheduler hands this to the mission body, to every
/// <see cref="IMissionObserver"/> callback, to <see cref="IMissionPolicy"/>, and back out of
/// <see cref="IMissionScheduler.Snapshot"/>: four views of the same run, one type.
///
/// <para>
/// It carries no <see cref="CancellationToken"/> on purpose. The body receives its token as a
/// separate parameter — matching the rest of the kit, where a token is always its own argument — so
/// this stays a pure value that is safe to copy, hold, and hand to a diagnostics view. A token inside
/// a snapshot record would invite exactly the "hold it and use it later" mistake the scheduler's
/// lifetime rules exist to prevent.
/// </para>
/// </summary>
/// <param name="MissionId">Scheduler-assigned id. Stable across retries and durable across a restart.</param>
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
public readonly record struct MissionExecution(
    string MissionId,
    string? Kind,
    int Priority,
    DateTimeOffset QueuedUtc,
    long Sequence,
    int Attempt = 0,
    bool IsRunning = false);
