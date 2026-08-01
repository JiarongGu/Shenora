namespace Shenora.Core;

/// <summary>
/// Lifecycle notifications for every item a scheduler handles — the seam for metrics, tracing, and
/// for wiring execution to a progress registry.
///
/// <para>
/// This is the composition point that keeps `Shenora.Core` free of any reporting dependency. An
/// observer that opens an operation in <see cref="OnStarted"/> and finishes it in
/// <see cref="OnFinished"/> binds execution to a progress surface — `Shenora.Ipc`'s operation registry,
/// say — ONCE, rather than every mission body opening and closing an operation by hand, which is exactly
/// the boilerplate the family's apps wrote at every call site and occasionally forgot, leaving
/// operations stuck "running" forever. **The kit ships no such adapter today**: nothing in
/// `Shenora.Ipc` implements this, so an app that wants the pairing writes those few lines itself.
/// Layering is preserved either way: `Shenora.Ipc` may depend on `Shenora.Core`, never the reverse
/// (D19/D20).
/// </para>
///
/// <para>
/// Every method is invoked through <see cref="AppCallback"/>, so a throwing observer is logged and
/// swallowed rather than failing the work it was only watching. Implementations must be
/// thread-safe and should be cheap — they run on the scheduler's execution path.
/// </para>
/// </summary>
public interface IMissionObserver
{
    /// <summary>Accepted into the queue. Not called for a deduplicated submission.</summary>
    void OnQueued(in MissionView mission) { }

    /// <summary>About to run. Called once per item, not once per retry attempt.</summary>
    void OnStarted(in MissionView mission) { }

    /// <summary>Finished, however it ended.</summary>
    void OnFinished(in MissionView mission, MissionResult result) { }
}

/// <summary>A point-in-time view of one item, from <see cref="IMissionScheduler.Snapshot"/>.</summary>
/// <param name="Mission">Identity and ordering inputs.</param>
/// <param name="IsRunning">True if executing, false if still queued.</param>
public readonly record struct MissionSnapshot(MissionView Mission, bool IsRunning);
