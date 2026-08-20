namespace Shenora;

/// <summary>
/// The host loop seam <see cref="ShenoraApplication.Run"/> delegates to — Core has no idea what a
/// message pump is. A host package (Shenora.Windows via <c>UseWindows</c>) registers the implementation,
/// which owns the full run sequence: single-instance gate, process init,
/// <see cref="IShenoraLifecycleHook"/> invocation, the blocking loop, and ordered shutdown. Register
/// exactly one.
/// </summary>
public interface IShenoraRunner
{
    /// <summary>Run the application to completion (blocks until shutdown).</summary>
    void Run(ShenoraApplication app);
}
