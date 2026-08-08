using Shenora.Modules.Requests;

namespace Shenora.Core.Ipc;

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
    /// <para>
    /// WHAT THE TOKEN IS FOR, and what it is NOT (added P6.4). It carries the caller's
    /// LIFETIME — a transport passes one tied to its own, so handlers still running when the page
    /// navigates away or the host shuts down learn that nobody is listening any more. Before this
    /// existed the whole pipeline was uncancellable, and a handler could not observe a token it was
    /// never given. It is deliberately NOT per-request client cancellation: a one-way
    /// <c>post</c> has no caller waiting, so "the client changed its mind" is an app-level CANCEL
    /// route carrying the operation id, not a transport concern (see <c>docs/DECISIONS.md</c> D23,
    /// and <see cref="IpcRequestsModule"/>, which ships that route). Cancellation surfaces to the client as
    /// <see cref="IpcErrorCodes.OperationCancelled"/>, never as a thrown exception — the
    /// never-throws contract holds.
    /// </para>
    /// </summary>
    Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken = default);

    /// <summary>Send a programmatic request and get the full response envelope.</summary>
    Task<IpcResponse> SendAsync(string module, string type, string? scope = null, object? payload = null,
                                CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a programmatic request and get typed response data. A failed response throws its
    /// structured error as an <see cref="OperationException"/>.
    /// </summary>
    Task<T?> SendAsync<T>(string module, string type, string? scope = null, object? payload = null,
                          CancellationToken cancellationToken = default);

    /// <summary>
    /// Append a middleware — the ONE composition primitive. Every mapping helper
    /// (<see cref="MessageDispatcherExtensions.MapModule(IMessageDispatcher, IIpcModule)"/> and
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
