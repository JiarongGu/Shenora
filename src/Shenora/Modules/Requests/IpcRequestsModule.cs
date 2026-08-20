using Microsoft.Extensions.Logging;
using Shenora.Core.Ipc;

namespace Shenora.Modules.Requests;

/// <summary>
/// The page's control surface over requests that outlived their own send — <c>LIST</c>, <c>CANCEL</c>,
/// <c>CLEAR_FINISHED</c>. <c>LIST</c> is the client store's snapshot source: a component that mounts
/// while work is already running gets its state from here, then folds
/// <see cref="IpcRequestEvents.Updated"/> deltas.
/// <para>
/// 🔴 <b><c>CANCEL</c> carries a <c>requestId</c>, and that is the whole of D66 on the wire</b> — the id
/// the page already has when it sends a request is the id it uses to abort it.
/// </para>
/// <para>
/// ⚠ <c>LIST</c> and <see cref="IpcRequestEvents.Updated"/> read the SAME optional
/// <c>module</c>/<c>scope</c> keys and must agree, or a scoped store loads every scope once and never
/// sheds the rest.
/// </para>
/// </summary>
public sealed class IpcRequestsModule : ModuleBase
{
    /// <summary>Route: snapshot of known requests — in flight first, then retained history.</summary>
    public const string ListType = "LIST";

    /// <summary>Route: abort a request in flight by id — <c>XMLHttpRequest.abort()</c>.</summary>
    public const string CancelType = "CANCEL";

    /// <summary>Route: drop retained finished history.</summary>
    public const string ClearFinishedType = "CLEAR_FINISHED";

    private readonly IIpcRequestTracker _tracker;
    private readonly string _moduleName;

    /// <param name="tracker">The live view this module exposes.</param>
    /// <param name="options">Supplies the module name, so the routes and the published events agree.</param>
    /// <param name="logger">Diagnostics.</param>
    public IpcRequestsModule(IIpcRequestTracker tracker, IpcRequestTrackerOptions options,
                             ILogger<IpcRequestsModule>? logger = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _moduleName = options.ModuleName;
    }

    /// <inheritdoc />
    public override string ModuleName => _moduleName;

    /// <inheritdoc />
    protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context,
                                                       CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        switch (request.Type.ToUpperInvariant())
        {
            case ListType:
                var listModule = PayloadHelper.GetOptionalValue<string>(request.Payload, "module");
                var listScope = PayloadHelper.GetOptionalValue<string>(request.Payload, "scope");
                return Task.FromResult<object?>(_tracker.GetAll(listModule, listScope));

            case CancelType:
                var cancelId = PayloadHelper.GetRequiredValue<string>(request.Payload, "requestId");
                // Cancel() refuses an unknown or already-finished id; the bool forwards that refusal.
                return Task.FromResult<object?>(new { cancelled = _tracker.Cancel(cancelId) });

            case ClearFinishedType:
                var clearModule = PayloadHelper.GetOptionalValue<string>(request.Payload, "module");
                var clearScope = PayloadHelper.GetOptionalValue<string>(request.Payload, "scope");
                _tracker.ClearFinished(clearModule, clearScope);
                return Done();

            default:
                throw UnknownType(request);   // ModuleBase owns the shape
        }
    }
}
