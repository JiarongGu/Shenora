namespace Shenora.Engine.Missions;

/// <summary>
/// Lifecycle notifications for every item a scheduler handles — the seam for metrics, tracing, and
/// for wiring execution to a progress registry.
///
/// <para>
/// This is the composition point that keeps the mission engine free of any reporting dependency. An
/// observer that starts tracking in <see cref="OnStarted"/> and completes it in
/// <see cref="OnFinished"/> binds execution to a progress surface — <c>Shenora.Core.Ipc</c>'s REQUEST
/// tracking, say — ONCE, rather than every mission body opening and closing a tracked item by hand,
/// which is exactly the boilerplate the family's apps wrote at every call site and occasionally forgot,
/// leaving work stuck "running" forever. **The kit ships no such adapter today**: nothing in
/// <c>Shenora.Core.Ipc</c> implements this, so an app that wants the pairing writes those few lines
/// itself. Layering is preserved either way: the modules may depend on the cores, never the reverse
/// (D19/D20).
/// <para>
/// ⚠ This named <c>Shenora.Ipc</c> — a package D65 folded in — and its "operation registry", which D66
/// DELETED (operations merged into <c>IpcRequest</c>). Both were stated in the present tense until
/// 2026-08-10, pointing a reader at two things that no longer exist.
/// </para>
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
    void OnQueued(in MissionExecution mission) { }

    /// <summary>About to run. Called once per execution, not once per retry attempt.</summary>
    void OnStarted(in MissionExecution mission) { }

    /// <summary>Finished, however it ended.</summary>
    void OnFinished(in MissionExecution mission, MissionResult result) { }
}
