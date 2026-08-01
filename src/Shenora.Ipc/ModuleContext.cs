using Microsoft.Extensions.Logging;
using Shenora.Core;

namespace Shenora.Ipc;

/// <summary>
/// The <see cref="IModuleContext"/> a <see cref="BaseFacade"/> builds once, at construction — the
/// module name is known then, and rebuilding it per request would allocate on the IPC hot path.
/// </summary>
internal sealed class ModuleContext(string module, ILogger logger, IEventBus? events, IOperationRegistry? operations) : IModuleContext
{
    public string Module { get; } = module;

    public ILogger Logger { get; } = logger;

    public void Publish(string type, object? payload = null, string? scope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        // LOUD, not silent. A no-op here would drop an app's progress stream with no error and no
        // log line — the same shape as MaxQueuedNotifications = 0 discarding every notification.
        // The message names the fix, because the error is a COMPOSITION mistake, not a code bug.
        if (events is null)
            throw new InvalidOperationException(
                $"Module '{Module}' called IModuleContext.Publish but no IEventBus was supplied to " +
                "BaseFacade. Pass one (ShenoraApplication.CreateBuilder registers an IEventBus by default, " +
                "so inject IEventBus into the facade and forward it: base(logger, events)).");
        events.Emit(Module, type, payload, scope);
    }

    public IOperation Start(OperationOptions options) =>
        operations is null ? throw NoOperationsRegistry(nameof(Start)) : operations.Start(Module, options);

    public string Run(OperationOptions options, Func<IOperation, CancellationToken, Task> work) =>
        operations is null ? throw NoOperationsRegistry(nameof(Run)) : operations.Run(Module, options, work);

    /// <summary>
    /// Same LOUD shape as <see cref="Publish"/>'s missing-bus check: a registry-less
    /// <see cref="IModuleContext.Start"/>/<see cref="IModuleContext.Run"/> is a COMPOSITION mistake
    /// (<c>services.AddShenoraOperations()</c> was never called), not a silent no-op. The context is
    /// the module's context, not an operations entry point — a module that never touches
    /// <see cref="Start"/>/<see cref="Run"/> pays nothing for this (see <see cref="Publish"/>, which
    /// has no such dependency at all).
    /// </summary>
    private InvalidOperationException NoOperationsRegistry(string member) =>
        new($"Module '{Module}' called IModuleContext.{member} but no IOperationRegistry was supplied to " +
            "BaseFacade. Call services.AddShenoraOperations() and pass the registry to the facade " +
            "(base(logger, events, operations)).");
}
