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

    /// <summary>
    /// Append a middleware — the ONE composition primitive. Every mapping helper
    /// (<see cref="MessageDispatcherExtensions.MapModule(IMessageDispatcher, IModuleFacade)"/> and
    /// friends) is an extension method over this, so all of them work on the interface.
    /// <para>
    /// WHY THIS IS ON THE INTERFACE (P5.5 H6). The interface previously exposed only dispatch/send, so
    /// a composition that maps a facade AFTER the container is built — the documented pattern for
    /// anything needing the live window — had to DOWNCAST to <see cref="MessageDispatcher"/>. The
    /// reference composition did exactly that, and its <c>if (dispatcher is MessageDispatcher concrete)</c>
    /// had no <c>else</c>: registering a different <see cref="IMessageDispatcher"/>, or wrapping it in a
    /// decorator, silently dropped three whole modules and the frameless title bar simply stopped
    /// working, with no error anywhere. Adopters copy that branch.
    /// </para>
    /// <para>
    /// Late mapping is safe while requests are in flight — see
    /// <see cref="MessageDispatcher.Use"/> for the concurrency contract.
    /// </para>
    /// </summary>
    IMessageDispatcher Use(MessageMiddleware middleware);
}
