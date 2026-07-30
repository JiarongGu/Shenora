using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shenora.Ipc;

/// <summary>
/// Base class for module facades, ported from the primary desktop sibling: routes each request
/// to the implementation and standardizes the error boundary — an
/// <see cref="OperationException"/> crosses as its structured error, anything else is logged
/// host-side and crosses only as <see cref="IpcErrorCodes.UnknownError"/> plus the exception
/// type name (the source leaked raw exception messages here; design contract §5 forbids that).
/// A facade owns its whole module namespace: every request for the module gets a response from
/// it, so unknown types should throw an <see cref="OperationException"/> rather than fall
/// through.
/// </summary>
public abstract class BaseFacade : IModuleFacade
{
    private readonly ILogger _logger;

    /// <summary>The logger is optional so composition works without <c>AddLogging</c>.</summary>
    protected BaseFacade(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public abstract string ModuleName { get; }

    /// <inheritdoc />
    public async Task<IpcResponse> HandleMessageAsync(IpcRequest request,
                                                      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            _logger.LogDebug("{Module} handling {Type}", ModuleName, request.Type);
            // NO ConfigureAwait(false) — removed in P5.5 H6. It was the only one in the dispatch path and
            // it CONTRADICTED the documented model: the pipeline preserves the synchronization context on
            // purpose, because a facade routing a WINDOW command touches WinForms and must resume on the
            // UI thread. Under the WebView2 bridge the request already arrives there, so this discarded
            // the very context the design guarantees — it survived only because every in-repo facade
            // happens to marshal internally anyway. See .claude/knowledge/ipc-contracts.md.
            var data = await RouteMessageAsync(request, cancellationToken);
            return IpcResponse.CreateSuccess(request.Id, data);
        }
        catch (Exception ex)
        {
            return IpcErrorMapping.ToErrorResponse(request, ex, _logger, $"{ModuleName} handling");
        }
    }

    /// <summary>
    /// A route that returns nothing. Absorbed from the facades that each declared their own private
    /// copy — including the SAMPLE app's, which is the tell that it was consumer-facing boilerplate
    /// rather than an implementation detail (P5.5 H4.5).
    /// </summary>
    protected static Task<object?> Done() => Task.FromResult<object?>(null);

    /// <summary>
    /// The terminator for an unrecognized request type: a structured <see cref="IpcErrorCodes.NoHandler"/>
    /// carrying the module and type. Every facade ended its switch with a hand-written copy of this,
    /// so every consumer had to know the exact error shape to stay consistent with the framework.
    /// </summary>
    protected OperationException UnknownType(IpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new OperationException(IpcErrorCodes.NoHandler,
            new Dictionary<string, string> { ["module"] = ModuleName, ["type"] = request.Type });
    }

    /// <summary>
    /// Route the request to the module's handler and return the response data (null when the
    /// operation returns nothing). Throw <see cref="OperationException"/> for every expected
    /// failure.
    /// <para>
    /// <paramref name="cancellationToken"/> is the CALLER's lifetime, not a per-request cancel —
    /// see <see cref="IMessageDispatcher.DispatchAsync"/>. Ignore it for quick synchronous work
    /// (most window commands); observe it for anything that awaits, and an
    /// <see cref="OperationCanceledException"/> out of here becomes
    /// <see cref="IpcErrorCodes.OperationCancelled"/> rather than a fault. Work this route hands OFF
    /// to run in the background outlives the request, so give that its own token — do not capture
    /// this one and then wonder why a long operation dies when the page navigates.
    /// </para>
    /// </summary>
    protected abstract Task<object?> RouteMessageAsync(IpcRequest request, CancellationToken cancellationToken);
}
