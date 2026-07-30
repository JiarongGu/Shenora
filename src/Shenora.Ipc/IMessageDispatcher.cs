namespace Shenora.Ipc;

/// <summary>
/// Routes IPC requests through the middleware pipeline to module handlers/facades — the seam
/// services, modules, and transports depend on. Programmatic senders (<see cref="SendAsync"/>)
/// travel the exact pipeline client requests do, middleware included, so the two entry paths
/// cannot diverge. Implemented by <see cref="MessageDispatcher"/>.
/// </summary>
public interface IMessageDispatcher
{
    /// <summary>
    /// Run a request through the pipeline. Never throws and never returns null — unhandled
    /// requests and escaped exceptions become structured error responses (see
    /// <see cref="MessageDispatcher.DispatchAsync"/>). This is the transports' entry point.
    /// </summary>
    Task<IpcResponse> DispatchAsync(IpcRequest request);

    /// <summary>Send a programmatic request and get the full response envelope.</summary>
    Task<IpcResponse> SendAsync(string module, string type, string? scope = null, object? payload = null);

    /// <summary>
    /// Send a programmatic request and get typed response data. A failed response throws its
    /// structured error as an <see cref="OperationException"/>.
    /// </summary>
    Task<T?> SendAsync<T>(string module, string type, string? scope = null, object? payload = null);
}
