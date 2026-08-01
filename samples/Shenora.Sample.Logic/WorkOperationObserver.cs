using System.Collections.Concurrent;
using Shenora.Core;
using Shenora.Ipc;

namespace Shenora.Sample.Logic;

/// <summary>
/// Binds the EXECUTION half (<see cref="IWorkScheduler"/>, in <c>Shenora.Core</c>) to the REPORTING
/// half (<see cref="IOperationRegistry"/>, in <c>Shenora.Ipc</c>) so scheduled work shows up in the
/// page's operations list without a single work body opening an operation by hand.
///
/// <para>
/// <b>The kit ships no adapter like this on purpose</b>, and this file is the demonstration that it
/// does not need to: `Shenora.Core` must never learn what an operation is (D19/D20), so the two
/// compose through <see cref="IWorkObserver"/> in the APP — which also means an app that reports
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
public sealed class WorkOperationObserver(IOperationRegistry operations, string module) : IWorkObserver
{
    // A scheduler dispatches on the thread pool, so two items can enter and leave concurrently.
    private readonly ConcurrentDictionary<string, IOperation> _live = new();

    /// <summary>
    /// Opens the operation while the item is still QUEUED, then immediately parks it with
    /// <c>Wait("queued")</c> — the shape <see cref="IOperationRegistry.Start"/> documents for an app
    /// whose own queue sits in front of the registry. Without it, work waiting behind a claim would
    /// be invisible until it started, which is exactly when a user asks "is it stuck?".
    /// </summary>
    public void OnQueued(in WorkView work)
    {
        var operation = operations.Start(module, new OperationOptions
        {
            Kind = work.Kind ?? "WORK",
            Title = new OperationLabel { Text = work.Kind is { } kind ? $"{kind} ({work.WorkId})" : work.WorkId },
            // The scheduler owns cancellation through the token passed to SubmitAsync, so the registry
            // must not advertise a cancel it cannot perform — see IOperationRegistry.Cancel.
            Cancellable = false,
        });
        operation.Wait("queued");
        _live[work.WorkId] = operation;
    }

    /// <summary>Queued → running, on the same handle. Called once per item, not once per retry.</summary>
    public void OnStarted(in WorkView work)
    {
        if (_live.TryGetValue(work.WorkId, out var operation)) operation.Resume();
    }

    /// <summary>
    /// Terminal in both worlds. <see cref="WorkOutcome.Deduplicated"/> completes rather than fails:
    /// the caller's work DID happen — it was carried by an identical item already in flight.
    /// </summary>
    public void OnFinished(in WorkView work, WorkResult result)
    {
        if (!_live.TryRemove(work.WorkId, out var operation)) return;
        switch (result.Outcome)
        {
            case WorkOutcome.Completed or WorkOutcome.Deduplicated:
                operation.Complete();
                break;
            case WorkOutcome.Cancelled:
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
