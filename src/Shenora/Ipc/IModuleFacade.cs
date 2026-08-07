namespace Shenora.Ipc;

/// <summary>
/// A module's IPC entry point, for polymorphic routing: register implementations in DI (as
/// <see cref="IModuleFacade"/>) and map them with
/// <see cref="MessageDispatcherExtensions.MapModule(IMessageDispatcher, IModuleFacade)"/>. <see cref="ModuleName"/> moved onto
/// this interface from the source's facade base (where it was protected) — it is what lets
/// facade objects be routed without a static registry.
/// </summary>
public interface IModuleFacade
{
    /// <summary>The module this facade owns (matched case-insensitively against <see cref="IpcRequest.Module"/>).</summary>
    string ModuleName { get; }

    /// <summary>
    /// Handle one request for this module. Always produces a response (see <see cref="BaseFacade"/>).
    /// The token carries the DISPATCHER's caller lifetime — see
    /// <see cref="IMessageDispatcher.DispatchAsync"/> for what it does and does not mean.
    /// </summary>
    Task<IpcResponse> HandleMessageAsync(IpcRequest request, CancellationToken cancellationToken = default);
}
