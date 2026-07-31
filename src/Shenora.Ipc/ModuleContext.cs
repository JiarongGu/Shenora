using Microsoft.Extensions.Logging;
using Shenora.Core;

namespace Shenora.Ipc;

/// <summary>
/// The <see cref="IModuleContext"/> a <see cref="BaseFacade"/> builds once, at construction — the
/// module name is known then, and rebuilding it per request would allocate on the IPC hot path.
/// </summary>
internal sealed class ModuleContext(string module, ILogger logger, IEventBus? events) : IModuleContext
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
}
