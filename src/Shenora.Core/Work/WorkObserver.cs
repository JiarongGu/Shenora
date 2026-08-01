namespace Shenora.Core;

/// <summary>
/// Lifecycle notifications for every item a scheduler handles — the seam for metrics, tracing, and
/// for wiring execution to a progress registry.
///
/// <para>
/// This is the composition point that keeps `Shenora.Core` free of any reporting dependency. The
/// kit's own progress surface (`Shenora.Ipc`'s operation registry) attaches by implementing this
/// ONCE, rather than every work body opening and closing an operation by hand — which is exactly
/// the boilerplate the family's apps wrote at every call site and occasionally forgot, leaving
/// operations stuck "running" forever. Layering is preserved: `Shenora.Ipc` may depend on
/// `Shenora.Core`, never the reverse (D19/D20).
/// </para>
///
/// <para>
/// Every method is invoked through <see cref="AppCallback"/>, so a throwing observer is logged and
/// swallowed rather than failing the work it was only watching. Implementations must be
/// thread-safe and should be cheap — they run on the scheduler's execution path.
/// </para>
/// </summary>
public interface IWorkObserver
{
    /// <summary>Accepted into the queue. Not called for a deduplicated submission.</summary>
    void OnQueued(in WorkView work) { }

    /// <summary>About to run. Called once per item, not once per retry attempt.</summary>
    void OnStarted(in WorkView work) { }

    /// <summary>Finished, however it ended.</summary>
    void OnFinished(in WorkView work, WorkResult result) { }
}

/// <summary>A point-in-time view of one item, from <see cref="IWorkScheduler.Snapshot"/>.</summary>
/// <param name="Work">Identity and ordering inputs.</param>
/// <param name="IsRunning">True if executing, false if still queued.</param>
public readonly record struct WorkSnapshot(WorkView Work, bool IsRunning);
