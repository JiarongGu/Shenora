using Shenora.Core.Events;
using Shenora.Engine.Missions;

namespace Shenora.Sample.Logic;

/// <summary>
/// Publishes the scheduler's own lifecycle as EVENTS, so a page can render a queue without the kit
/// learning what a mission is.
/// <para>
/// 🔴 <b>This replaced a class that reported missions as tracked "operations" (D66, 2026-08-08), and the
/// swap is the decision made concrete.</b> A mission is HOST-INITIATED work: nobody sent a request for it,
/// so it has no request id, no response to wait for, and nothing to abort from the page's side. Squeezing
/// it into the request model is what gave the old design two unrelated things sharing one bucket — and it
/// was the only code anywhere that needed a "waiting" state, which is precisely why that state existed and
/// why cutting it was safe.
/// </para>
/// <para>
/// <b>The kit ships no adapter like this, on purpose</b>, and this file is the demonstration that it does
/// not need to: execution (<see cref="IMissionScheduler"/>) must never learn about reporting (D19/D20), so
/// the two compose through <see cref="IMissionObserver"/> in the APP. An app that reports some other way —
/// a status bar, a log, OpenTelemetry — writes its own instead and owes the kit nothing.
/// </para>
/// <para>
/// It lives in the PORTABLE sample project, which is itself the point: execution, reporting and the seam
/// between them are all <c>net10.0</c>, so this composition carries no Windows dependency.
/// </para>
/// </summary>
/// <param name="events">The bus the page already subscribes to.</param>
/// <param name="module">Module name every mission event is published under.</param>
public sealed class MissionEventPublisher(IEventBus events, string module) : IMissionObserver
{
    /// <summary>The event type a page folds by <c>missionId</c>. One type for every transition.</summary>
    public const string MissionUpdated = "MISSION_UPDATED";

    /// <summary>
    /// QUEUED IS VISIBLE HERE, and that is the whole reason this shape is better than the old one. A queue
    /// is a real thing a user asks about ("is it stuck?"), and it belongs to the scheduler — where the
    /// answer actually lives — rather than being smuggled in as a parked request.
    /// </summary>
    public void OnQueued(in MissionExecution mission) => Emit(mission, "queued");

    /// <inheritdoc />
    public void OnStarted(in MissionExecution mission) => Emit(mission, "running");

    /// <summary>
    /// Terminal in both worlds. <see cref="MissionOutcome.Deduplicated"/> reports as completed rather than
    /// failed: the caller's work DID happen — it was carried by an identical item already in flight.
    /// </summary>
    public void OnFinished(in MissionExecution mission, MissionResult result) => Emit(mission, result.Outcome switch
    {
        MissionOutcome.Completed or MissionOutcome.Deduplicated => "completed",
        MissionOutcome.Cancelled => "cancelled",
        _ => "failed",
    });

    /// <summary>
    /// ⚠ The exception itself never leaves the host — only its TYPE NAME, the same rule every error path in
    /// the IPC stack follows. A page rendering a queue has no use for a stack trace, and a raw exception
    /// message is a disclosure surface.
    /// </summary>
    private void Emit(in MissionExecution mission, string state) =>
        events.Emit(module, MissionUpdated, new
        {
            missionId = mission.MissionId,
            kind = mission.Kind ?? "WORK",
            state,
            attempt = mission.Attempt,
        });
}
