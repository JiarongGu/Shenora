namespace Shenora.Core.Ipc;

/// <summary>
/// A module's IPC entry point, for polymorphic routing: register implementations in DI (as
/// <see cref="IIpcModule"/>) and map them with
/// <see cref="MessageDispatcherExtensions.MapModule(IMessageDispatcher, IIpcModule)"/>.
/// </summary>
public interface IIpcModule
{
    /// <summary>The module this facade owns (matched case-insensitively against <see cref="IpcRequest.Module"/>).</summary>
    string ModuleName { get; }

    /// <summary>
    /// Handle one request for this module. Always produces a response (see <see cref="ModuleBase"/>).
    /// The token carries the DISPATCHER's caller lifetime — see
    /// <see cref="IMessageDispatcher.DispatchAsync"/>.
    /// </summary>
    Task<IpcResponse> HandleMessageAsync(IpcRequest request, CancellationToken cancellationToken = default);
}
