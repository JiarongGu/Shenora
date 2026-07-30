namespace Shenora.Ipc;

/// <summary>
/// A module's IPC entry point, for polymorphic routing: register implementations in DI (as
/// <see cref="IModuleFacade"/>) and map them with
/// <see cref="MessageDispatcher.MapModule(IModuleFacade)"/>. <see cref="ModuleName"/> moved onto
/// this interface from the source's facade base (where it was protected) — it is what lets
/// facade objects be routed without a static registry.
/// </summary>
public interface IModuleFacade
{
    /// <summary>The module this facade owns (matched case-insensitively against <see cref="IpcRequest.Module"/>).</summary>
    string ModuleName { get; }

    /// <summary>Handle one request for this module. Always produces a response (see <see cref="BaseFacade"/>).</summary>
    Task<IpcResponse> HandleMessageAsync(IpcRequest request);
}
