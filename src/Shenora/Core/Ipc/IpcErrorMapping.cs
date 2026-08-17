using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shenora.Core.Ipc;

/// <summary>
/// The ONE implementation of the kit's most load-bearing invariant: <b>an exception becomes a
/// structured wire error, and its text never crosses the bridge</b>.
/// <para>
/// Only two things ever reach the client: an <see cref="ShenoraException"/>'s own structured error (the
/// app chose those words deliberately, and the code is the client's i18n key), or
/// <see cref="IpcErrorCodes.UnknownError"/> plus the exception's TYPE NAME. The message, the stack and
/// any inner exception stay host-side in the log.
/// </para>
/// <para>
/// 🔴 <b>ONE implementation, deliberately.</b> This was four byte-identical
/// <c>catch (ShenoraException) / catch (Exception)</c> pairs, and that many copies of a rule that must
/// never be broken is how it gets broken: the next error path is written by copy-paste, and the copy that
/// passes <c>ex.Message</c> instead of <c>ex.GetType().Name</c> leaks a filesystem path or a connection
/// string to the page.
/// </para>
/// <para>
/// PUBLIC because the next copy turned out to be an ADOPTER's. A facade gets this free through
/// <see cref="ModuleBase"/>, but an app whose own IPC surface reports failures as EVENTS — the shape an
/// adoption shim preserves — needs a wire error where there is no response to attach one to. Retyping the
/// policy is exactly the copy this type exists to prevent, so it is surface rather than a rule.
/// </para>
/// </summary>
public static class IpcErrorMapping
{
    /// <summary>
    /// Map <paramref name="exception"/> to the wire error a client may see, logging the full detail
    /// host-side.
    /// </summary>
    /// <param name="exception">The failure to translate.</param>
    /// <param name="logger">Where the full detail goes. Null discards it — pass a real logger.</param>
    /// <param name="context">
    /// Names the boundary for the log line (e.g. "dispatching", "APP handling"). It is only ever
    /// logged, never sent.
    /// </param>
    /// <param name="module">Logged for correlation; optional because a caller outside a route may not have one.</param>
    /// <param name="type">Logged for correlation; optional for the same reason.</param>
    public static IpcError ToError(Exception exception, ILogger? logger = null,
                                   string context = "handling", string? module = null, string? type = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var log = logger ?? NullLogger.Instance;
        var where = module ?? "-";
        var what = type ?? "-";

        if (exception is ShenoraException operation)
        {
            // Expected failure the app described itself — pass its code/parameters through verbatim.
            // NOTE the sharp edge this creates, and do not "helpfully" round off: the MESSAGE crosses
            // too, because these are the app's own words. So never construct an ShenoraException
            // from `ex.Message` of an arbitrary exception — that turns the one sanctioned channel into
            // a bypass of the whole boundary (P6.4; there is a knowledge rule and a probe for it).
            log.LogWarning(operation, "Operation error {Context} {Module}/{Type}: [{Code}]",
                context, where, what, operation.Code);
            return operation.ToError();
        }

        // Cancellation is a NORMAL outcome, not an unknown failure (P5.5 H6). It used to fall through to
        // UNKNOWN_ERROR, so a client could not tell "you cancelled this" from "something broke" — and a
        // cancel is the one failure a UI should NOT show an error for. The reference composition had
        // already hand-rolled this arm, which is the tell that every adopting app would have to.
        // Checked AFTER ShenoraException on purpose: an app that models cancellation with its own code
        // keeps its own words.
        if (exception is OperationCanceledException)
        {
            log.LogDebug("Cancelled {Context} {Module}/{Type}", context, where, what);
            return new IpcError { Code = IpcErrorCodes.OperationCancelled };
        }

        // Unexpected: the client learns only THAT it failed and the exception's type name.
        log.LogError(exception, "Unhandled error {Context} {Module}/{Type}", context, where, what);
        return new IpcError
        {
            Code = IpcErrorCodes.UnknownError,
            Parameters = new Dictionary<string, string> { ["exceptionType"] = exception.GetType().Name },
        };
    }

    /// <summary>
    /// Map <paramref name="exception"/> to the response for <paramref name="request"/>, logging the
    /// full detail host-side. <paramref name="context"/> names the boundary for the log line
    /// (e.g. "dispatching", "APP handling").
    /// </summary>
    public static IpcResponse ToErrorResponse(IpcRequest request, Exception exception,
                                              ILogger? logger = null, string context = "handling")
    {
        ArgumentNullException.ThrowIfNull(request);
        return IpcResponse.CreateError(request.Id,
            ToError(exception, logger, context, request.Module, request.Type));
    }
}
