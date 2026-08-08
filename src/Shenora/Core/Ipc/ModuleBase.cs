using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shenora;
using Shenora.Core.Events;

namespace Shenora.Core.Ipc;

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
public abstract class ModuleBase : IIpcModule
{
    private readonly ILogger _logger;
    private readonly IEventBus? _events;
    private readonly IIpcRequestTracker? _requests;

    /// <summary>
    /// The logger is optional so composition works without <c>AddLogging</c>; the bus and the request
    /// tracker are optional so a facade that never publishes (and every unit test that constructs one
    /// bare) still works. A facade that publishes WITHOUT a bus fails loudly at the call site; a missing
    /// tracker is a silent no-op, and <see cref="ModuleContext.Report"/> explains why the two differ.
    /// Neither is exposed as protected surface: the sanctioned accessor for a route is the
    /// <c>context</c> parameter of <see cref="RouteMessageAsync"/>.
    /// </summary>
    protected ModuleBase(ILogger? logger = null, IEventBus? events = null, IIpcRequestTracker? requests = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _events = events;
        _requests = requests;
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
            // 🔴 TRACKED FROM HERE, with nothing declared (D66). Begin publishes NOTHING; the page hears
            // about this request only if it outlives the grace period. So the fast path — nearly every
            // request — costs one dictionary insert and one removal and never reaches the wire.
            //
            // The scope's token is what CANCEL targets, so it is the one the route must observe: the
            // caller's own lifetime is linked into it, not replaced by it.
            using var scope = _requests?.Begin(request, cancellationToken);
            var token = scope?.CancellationToken ?? cancellationToken;
            var context = new ModuleContext(ModuleName, request.Id, _logger, _events, scope);

            // NO ConfigureAwait(false) — removed in P5.5 H6. It was the only one in the dispatch path and
            // it CONTRADICTED the documented model: the pipeline preserves the synchronization context on
            // purpose, because a facade routing a WINDOW command touches WinForms and must resume on the
            // UI thread.
            var data = await RouteMessageAsync(request, context, token);
            return IpcResponse.CreateSuccess(request.Id, data);
            // ⚠ The response is returned BEFORE the scope disposes, which is the ordering D66 insists on:
            // the grace period suppresses notifications and must never delay the answer.
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
    /// The terminator for an unrecognized request type: a structured <see cref="IpcErrorCodes.NoRoute"/>
    /// carrying the module and type. Every module ended its switch with a hand-written copy of this,
    /// so every consumer had to know the exact error shape to stay consistent with the framework.
    /// <para>
    /// 🔴 <b>It answers <see cref="IpcErrorCodes.NoRoute"/>, NOT <see cref="IpcErrorCodes.NoHandler"/>,
    /// and the difference is an adopter's debugging time.</b> Reaching here proves the module IS
    /// registered and mapped — the dispatcher found it and handed the request over. `NO_HANDLER` means
    /// the opposite: nothing claimed the module name at all. Those need opposite fixes (correct a route
    /// name, versus wire the module up), and until 2026-08-08 both answered `NO_HANDLER` with identical
    /// parameters, so the wire could not tell them apart. Found by a test that tried to USE the
    /// distinction as its probe and discovered there was none.
    /// </para>
    /// </summary>
    protected OperationException UnknownType(IpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new OperationException(IpcErrorCodes.NoRoute,
            new Dictionary<string, string> { ["module"] = ModuleName, ["type"] = request.Type });
    }

    /// <summary>
    /// Route the request to the module's handler and return the response data (null when the
    /// operation returns nothing). Throw <see cref="OperationException"/> for every expected
    /// failure.
    /// <para>
    /// <paramref name="context"/> is how a route EMITS (<see cref="IModuleContext.Publish"/>) — the
    /// event path is the desktop default and the request path the special case, so it is in the
    /// signature rather than behind a base-class member a route author may never find.
    /// </para>
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
    protected abstract Task<object?> RouteMessageAsync(
        IpcRequest request, IModuleContext context, CancellationToken cancellationToken);
}
