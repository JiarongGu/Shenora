namespace Shenora;

/// <summary>
/// The host loop seam <see cref="ShenoraApplication.Run"/> delegates to. Core deliberately has no
/// idea what a message pump is — a host package (e.g. Shenora.Windows via its <c>UseWinForms</c>
/// builder extension) registers the implementation, which owns the full run sequence:
/// single-instance gate, process init, <see cref="IShenoraLifecycleHook"/> invocation, the blocking
/// loop, and ordered shutdown. Register exactly one.
/// </summary>
public interface IShenoraRunner
{
    /// <summary>Run the application to completion (blocks until shutdown).</summary>
    void Run(ShenoraApplication app);
}
