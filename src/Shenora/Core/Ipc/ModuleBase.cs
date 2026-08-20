using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shenora.Core.Events;

namespace Shenora.Core.Ipc;

/// <summary>
/// Base class for module facades: routes each request to the implementation and standardizes the error
/// boundary through <see cref="IpcErrorMapping"/>, so raw exception text never crosses the wire.
/// A facade owns its whole module namespace — every request for the module gets a response from it, so
/// an unknown type should throw (see <see cref="UnknownType"/>) rather than fall through.
/// </summary>
public abstract class ModuleBase : IIpcModule
{
    private readonly ILogger _logger;
    private readonly IEventBus? _events;

    /// <summary>
    /// Both are optional so composition works without <c>AddLogging</c> and a facade that never
    /// publishes still works; publishing WITHOUT a bus fails loudly at the call site. Neither is exposed
    /// as protected surface — a route's sanctioned accessor is the <c>context</c> parameter of
    /// <see cref="RouteMessageAsync"/>.
    /// <para>
    /// ⚠ <b>Request tracking is deliberately NOT a parameter here</b> — it belongs to the dispatch
    /// boundary (<see cref="MessageDispatcher.DispatchAsync"/>), which no module can forget to opt into
    /// (D63).
    /// </para>
    /// </summary>
    protected ModuleBase(ILogger? logger = null, IEventBus? events = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _events = events;
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

            // The request's tracking scope, if it was dispatched (D66) — picked up here so
            // IModuleContext.Report needs no id and no wiring. The DISPATCHER owns its lifetime.
            // ⚠ Matched by id: HandleMessageAsync is public and callable outside dispatch, so an
            // unmatched ambient must resolve to "not tracked" rather than to somebody else's request.
            // Captured ONCE rather than read per Report(), so work this route hands off to the background
            // keeps reporting against the request that started it.
            var context = new ModuleContext(ModuleName, request.Id, _logger, _events,
                                            IpcRequestScopeAccessor.For(request.Id));

            // No ConfigureAwait(false): the dispatch path preserves the synchronization context, because
            // a facade routing a WINDOW command must resume on the UI thread.
            var data = await RouteMessageAsync(request, context, cancellationToken);
            return IpcResponse.CreateSuccess(request.Id, data);
        }
        catch (Exception ex)
        {
            return IpcErrorMapping.ToErrorResponse(request, ex, _logger, $"{ModuleName} handling");
        }
    }

    /// <summary>A route that returns nothing.</summary>
    protected static Task<object?> Done() => Task.FromResult<object?>(null);

    /// <summary>
    /// The terminator for an unrecognized request type: a structured <see cref="IpcErrorCodes.NoRoute"/>
    /// carrying the module and type.
    /// <para>
    /// 🔴 <b>It answers <see cref="IpcErrorCodes.NoRoute"/>, NOT
    /// <see cref="IpcErrorCodes.NoHandler"/>.</b> Reaching here proves the module IS registered and
    /// mapped; <c>NO_HANDLER</c> means nothing claimed the module name at all. Those need opposite fixes,
    /// so answering both the same way leaves the wire unable to tell them apart.
    /// </para>
    /// </summary>
    protected ShenoraException UnknownType(IpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ShenoraException(IpcErrorCodes.NoRoute,
            new Dictionary<string, string> { ["module"] = ModuleName, ["type"] = request.Type });
    }

    /// <summary>
    /// Route the request to the module's handler and return the response data (null when the
    /// operation returns nothing). Throw <see cref="ShenoraException"/> for every expected
    /// failure.
    /// <para>
    /// <paramref name="context"/> is how a route EMITS (<see cref="IModuleContext.Publish"/>) and
    /// reports progress. <paramref name="cancellationToken"/> is the CALLER's lifetime, not a
    /// per-request cancel — see <see cref="IMessageDispatcher.DispatchAsync"/>; an
    /// <see cref="OperationCanceledException"/> out of here becomes
    /// <see cref="IpcErrorCodes.OperationCancelled"/> rather than a fault.
    /// </para>
    /// <para>
    /// ⚠ Work this route hands OFF to the background outlives the request, so give it its own token —
    /// capturing this one kills a long operation the moment the page navigates.
    /// </para>
    /// </summary>
    protected abstract Task<object?> RouteMessageAsync(
        IpcRequest request, IModuleContext context, CancellationToken cancellationToken);
}
