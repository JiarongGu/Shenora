using Shenora.Modules.Requests;

namespace Shenora.Core.Ipc;

/// <summary>
/// Routes IPC requests through the middleware pipeline to module handlers/facades — the seam
/// services, modules and transports depend on. Programmatic senders (<see cref="SendAsync"/>)
/// travel the exact pipeline client requests do, middleware included. Implemented by
/// <see cref="MessageDispatcher"/>.
/// </summary>
public interface IMessageDispatcher
{
    /// <summary>
    /// Run a request through the pipeline. Never throws and never returns null — unhandled
    /// requests and escaped exceptions become structured error responses (see
    /// <see cref="MessageDispatcher.DispatchAsync"/>). This is the transports' entry point.
    /// <para>
    /// ⚠ <b>The token is the caller's LIFETIME, not a per-request client cancel.</b> A transport passes
    /// one tied to its own, so a handler still running when the page goes away learns nobody is
    /// listening. "The client changed its mind" is an app-level CANCEL route carrying the request id
    /// instead (D23; <see cref="IpcRequestsModule"/> ships it). Cancellation reaches the client as
    /// <see cref="IpcErrorCodes.OperationCancelled"/>, never as a throw.
    /// </para>
    /// </summary>
    Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken = default);

    /// <summary>Send a programmatic request and get the full response envelope.</summary>
    Task<IpcResponse> SendAsync(string module, string type, string? scope = null, object? payload = null,
                                CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a programmatic request and get typed response data. A failed response throws its
    /// structured error as an <see cref="ShenoraException"/>.
    /// </summary>
    Task<T?> SendAsync<T>(string module, string type, string? scope = null, object? payload = null,
                          CancellationToken cancellationToken = default);

    /// <summary>
    /// Append a middleware — the ONE composition primitive. Every mapping helper
    /// (<see cref="MessageDispatcherExtensions.MapModule(IMessageDispatcher, IIpcModule)"/> and
    /// friends) is an extension method over this, so all of them work on the interface. Safe to call
    /// while requests are in flight (<see cref="MessageDispatcher.Use"/>).
    /// <para>
    /// 🔴 <b>It is on the INTERFACE so that late mapping never needs a downcast.</b> With composition
    /// only on the concrete type, a caller writes <c>if (dispatcher is MessageDispatcher concrete)</c> —
    /// and that branch with no <c>else</c> silently drops every module the moment a decorator or an
    /// alternative registration is in play.
    /// </para>
    /// </summary>
    IMessageDispatcher Use(MessageMiddleware middleware);
}
