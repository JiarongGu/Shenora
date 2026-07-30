using Microsoft.Extensions.Logging;

namespace Shenora.Ipc;

/// <summary>
/// The ONE implementation of the kit's most load-bearing invariant: <b>an exception becomes a
/// structured wire error, and its text never crosses the bridge</b>.
/// <para>
/// This existed as four byte-identical <c>catch (OperationException) / catch (Exception)</c> pairs —
/// two in <see cref="MessageDispatcher"/> (its transport entry point and
/// <see cref="MessageDispatcher.UseErrorHandler"/>), one in <see cref="BaseFacade"/>, and a partial
/// one in the WebView2 bridge. Four copies of the rule that must never be broken is how it
/// eventually gets broken: a fifth error path gets written by copy-paste, and the one that forgets
/// <c>ex.GetType().Name</c> and passes <c>ex.Message</c> instead leaks a filesystem path or a
/// connection string to the page. Collapsed in P5.5 H4.5.
/// </para>
/// <para>
/// Only two things ever reach the client: an <see cref="OperationException"/>'s own structured error
/// (the app chose those words deliberately, and the code is the client's i18n key), or
/// <see cref="IpcErrorCodes.UnknownError"/> plus the exception's TYPE NAME. The message, the stack
/// and any inner exception stay host-side in the log.
/// </para>
/// </summary>
internal static class IpcErrorMapping
{
    /// <summary>
    /// Map <paramref name="exception"/> to the response for <paramref name="request"/>, logging the
    /// full detail host-side. <paramref name="context"/> names the boundary for the log line
    /// (e.g. "dispatching", "APP handling").
    /// </summary>
    internal static IpcResponse ToErrorResponse(IpcRequest request, Exception exception,
                                                ILogger logger, string context)
    {
        if (exception is OperationException operation)
        {
            // Expected failure the app described itself — pass its code/parameters through verbatim.
            logger.LogWarning(operation, "Operation error {Context} {Module}/{Type}: [{Code}]",
                context, request.Module, request.Type, operation.Code);
            return IpcResponse.CreateError(request.Id, operation.ToError());
        }

        // Unexpected: the client learns only THAT it failed and the exception's type name.
        logger.LogError(exception, "Unhandled error {Context} {Module}/{Type}",
            context, request.Module, request.Type);
        return IpcResponse.CreateError(request.Id, IpcErrorCodes.UnknownError, parameters:
            new Dictionary<string, string> { ["exceptionType"] = exception.GetType().Name });
    }
}
