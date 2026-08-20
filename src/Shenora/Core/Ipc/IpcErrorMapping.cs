using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shenora.Core.Ipc;

/// <summary>
/// The ONE implementation of the kit's most load-bearing invariant: <b>an exception becomes a
/// structured wire error, and its text never crosses the bridge</b>.
/// <para>
/// Only two things ever reach the client: an <see cref="ShenoraException"/>'s own structured error (its
/// code is the client's i18n key), or <see cref="IpcErrorCodes.UnknownError"/> plus the exception's TYPE
/// NAME. The message, the stack and any inner exception stay host-side in the log.
/// </para>
/// <para>
/// PUBLIC so an app whose own IPC surface reports failures as EVENTS — where there is no response to
/// attach an error to — can reach the same policy instead of retyping it. A facade gets it free through
/// <see cref="ModuleBase"/>.
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
    /// <param name="context">Names the boundary for the log line (e.g. "dispatching"). Only ever logged.</param>
    /// <param name="module">Logged for correlation; optional.</param>
    /// <param name="type">Logged for correlation; optional.</param>
    public static IpcError ToError(Exception exception, ILogger? logger = null,
                                   string context = "handling", string? module = null, string? type = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var log = logger ?? NullLogger.Instance;
        var where = module ?? "-";
        var what = type ?? "-";

        if (exception is ShenoraException operation)
        {
            // Expected failure the app described itself — code, parameters AND MESSAGE pass through
            // verbatim. 🔴 So never build an ShenoraException from an arbitrary `ex.Message`: that turns
            // the one sanctioned channel into a bypass of the whole boundary.
            log.LogWarning(operation, "Operation error {Context} {Module}/{Type}: [{Code}]",
                context, where, what, operation.Code);
            return operation.ToError();
        }

        // Cancellation is a NORMAL outcome, not an unknown failure — it is the one failure a UI should
        // stay silent about. Checked AFTER ShenoraException, so an app that models cancellation with its
        // own code keeps its own words.
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
    /// full detail host-side.
    /// </summary>
    public static IpcResponse ToErrorResponse(IpcRequest request, Exception exception,
                                              ILogger? logger = null, string context = "handling")
    {
        ArgumentNullException.ThrowIfNull(request);
        return IpcResponse.CreateError(request.Id,
            ToError(exception, logger, context, request.Module, request.Type));
    }
}
