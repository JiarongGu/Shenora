using Microsoft.Extensions.Logging;
using Shenora.Core.Events;

namespace Shenora.Core.Ipc;

/// <summary>The <see cref="IModuleContext"/> a <see cref="ModuleBase"/> builds FOR ONE REQUEST.</summary>
internal sealed class ModuleContext(string module, string requestId, ILogger logger,
                                    IEventBus? events, IIpcRequestScope? scope) : IModuleContext
{
    public string Module { get; } = module;

    public string RequestId { get; } = requestId;

    public ILogger Logger { get; } = logger;

    public void Publish(string type, object? payload = null, string? eventScope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        // 🔴 LOUD, not silent: a no-op here would drop an app's event stream with no error and no log
        // line. The message names the fix, because this is a COMPOSITION mistake.
        if (events is null)
            throw new InvalidOperationException(
                $"Module '{Module}' called IModuleContext.Publish but no IEventBus was supplied to " +
                "ModuleBase. Pass one (ShenoraApplication.CreateBuilder registers an IEventBus by default, " +
                "so inject IEventBus into the facade and forward it: base(logger, events)).");
        events.Emit(Module, type, payload, eventScope);
    }

    /// <summary>
    /// <inheritdoc />
    /// <para>
    /// ⚠ <b>An absent scope is a silent no-op here, unlike <see cref="Publish"/>'s missing bus.</b>
    /// Publishing is the route's OWN output, so dropping it must be loud; progress is the kit's
    /// bookkeeping, and a module invoked outside dispatch has no request to report on. Reporting after
    /// the request finished is a no-op for the same reason.
    /// </para>
    /// </summary>
    public void Report(IpcProgress? progress = null, IpcLabel? detail = null) =>
        scope?.Report(progress, detail);
}
