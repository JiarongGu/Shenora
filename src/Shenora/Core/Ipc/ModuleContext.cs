using Microsoft.Extensions.Logging;
using Shenora.Core.Events;

namespace Shenora.Core.Ipc;

/// <summary>
/// The <see cref="IModuleContext"/> a <see cref="ModuleBase"/> builds FOR ONE REQUEST.
/// <para>
/// 🔴 <b>It used to be built once per module and reused, explicitly "because rebuilding it per request
/// would allocate on the IPC hot path".</b> D66 makes that trade the wrong way round: a per-request
/// context is what lets <see cref="Report"/> take no id, and the allocation it avoided is one small
/// object next to the <see cref="IpcRequest"/>, its payload and its response — all of which are already
/// allocated per request. Paying it buys away a whole second identity.
/// </para>
/// </summary>
internal sealed class ModuleContext(string module, string requestId, ILogger logger,
                                    IEventBus? events, IIpcRequestScope? scope) : IModuleContext
{
    public string Module { get; } = module;

    public string RequestId { get; } = requestId;

    public ILogger Logger { get; } = logger;

    public void Publish(string type, object? payload = null, string? eventScope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        // LOUD, not silent. A no-op here would drop an app's event stream with no error and no log line.
        // The message names the fix, because the error is a COMPOSITION mistake, not a code bug.
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
    /// ⚠ <b>A missing tracker is a silent no-op here, unlike <see cref="Publish"/>'s missing bus — and the
    /// asymmetry is deliberate.</b> Publishing is the route's OWN output: dropping it loses app data, so it
    /// must be loud. Progress is the kit's own bookkeeping about a request the kit is tracking; a host
    /// composed without a tracker simply has no in-flight list to update, and faulting a working route over
    /// that would turn an optional facility into a required one.
    /// </para>
    /// </summary>
    public void Report(IpcProgress? progress = null, IpcLabel? detail = null) =>
        scope?.Report(progress, detail);
}
