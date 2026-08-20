namespace Shenora.Engine.Missions;

/// <summary>
/// Lifecycle notifications for every item a scheduler handles — the seam for metrics, tracing, and for
/// wiring execution to a progress surface such as <c>Shenora.Core.Ipc</c>'s REQUEST tracking. The kit
/// ships no such adapter: an app that wants the pairing writes it (D19/D20 keep the dependency out of
/// the engine).
/// <para>
/// Every method is invoked through <see cref="AppCallback"/>, so a throwing observer is logged and
/// swallowed rather than failing the work it was only watching. Implementations must be thread-safe and
/// should be cheap — they run on the scheduler's execution path.
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
