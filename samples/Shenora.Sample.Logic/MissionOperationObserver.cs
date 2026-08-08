using System.Collections.Concurrent;
using Shenora;
using Shenora.Engine.Missions;
using Shenora.Core.Ipc;

namespace Shenora.Sample.Logic;

/// <summary>
/// Binds the EXECUTION half (<see cref="IMissionScheduler"/>, in <c>Shenora</c>) to the REPORTING
/// half (<see cref="IOperationRegistry"/>, in <c>Shenora.Ipc</c>) so scheduled work shows up in the
/// page's operations list without a single mission body opening an operation by hand.
///
/// <para>
/// <b>The kit ships no adapter like this on purpose</b>, and this file is the demonstration that it
/// does not need to: `Shenora` must never learn what an operation is (D19/D20), so the two
/// compose through <see cref="IMissionObserver"/> in the APP — which also means an app that reports
/// progress some other way (a status bar, a log, OpenTelemetry) writes its own instead and owes the
/// kit nothing. `docs/ADOPTION.md` claims this costs an adopter a few lines; the class below is that
/// claim, executable.
/// </para>
///
/// <para>
/// It lives in the PORTABLE sample project, which is itself the point: execution, reporting and the
/// seam between them are all `net10.0`, so this composition carries no Windows dependency.
/// </para>
/// </summary>
/// <param name="operations">The registry the page already watches through <c>useShenoraOperations</c>.</param>
/// <param name="module">Owning module name for every operation this observer opens.</param>
public sealed class MissionOperationObserver(IOperationRegistry operations, string module) : IMissionObserver
{
    // A scheduler dispatches on the thread pool, so two items can enter and leave concurrently.
    private readonly ConcurrentDictionary<string, IOperation> _live = new();

    /// <summary>
    /// 🔴 <b>Deliberately does NOTHING now (D66, 2026-08-08).</b> It used to open the operation here and
    /// park it with <c>Wait("queued")</c> — and this observer was the ONLY code in the repo that ever
    /// drove <c>Wait</c>/<c>Resume</c>, which is precisely what settled the decision: a queued mission is
    /// host-initiated work, not a request, and a request is in flight or done.
    /// <para>
    /// So queue depth is the MISSION stream's business, not the request list's. An app that wants to show
    /// "2 running, the rest queued" reads it from <see cref="IMissionObserver"/> — which it is already
    /// implementing, right here — rather than borrowing a request handle to hold a state requests do not
    /// have.
    /// </para>
    /// </summary>
    public void OnQueued(in MissionExecution mission) { }

    /// <summary>
    /// Opens the operation when the work actually STARTS, which is the moment it becomes something in
    /// flight. Called once per item, not once per retry.
    /// </summary>
    public void OnStarted(in MissionExecution mission)
    {
        _live[mission.MissionId] = operations.Start(module, new OperationOptions
        {
            Kind = mission.Kind ?? "WORK",
            Title = new OperationLabel { Text = mission.Kind is { } kind ? $"{kind} ({mission.MissionId})" : mission.MissionId },
            // The scheduler owns cancellation through the token passed to SubmitAsync, so the registry
            // must not advertise a cancel it cannot perform — see IOperationRegistry.Cancel.
            Cancellable = false,
        });
    }

    /// <summary>
    /// Terminal in both worlds. <see cref="MissionOutcome.Deduplicated"/> completes rather than fails:
    /// the caller's work DID happen — it was carried by an identical item already in flight.
    /// </summary>
    public void OnFinished(in MissionExecution mission, MissionResult result)
    {
        if (!_live.TryRemove(mission.MissionId, out var operation)) return;
        switch (result.Outcome)
        {
            case MissionOutcome.Completed or MissionOutcome.Deduplicated:
                operation.Complete();
                break;
            case MissionOutcome.Cancelled:
                operation.Cancel();
                break;
            default:
                // The exception itself stays host-side: the registry maps a code, never raw text.
                operation.Fail("WORK_FAILED", new Dictionary<string, string>
                {
                    ["attempts"] = result.Attempts.ToString(),
                    ["exception"] = result.Error?.GetType().Name ?? "unknown",
                });
                break;
        }
    }
}
