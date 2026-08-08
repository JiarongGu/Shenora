using Microsoft.Extensions.Logging;
using Shenora.Core.Ipc;

namespace Shenora.Modules.Requests;

/// <summary>
/// The page's control surface over requests that outlived their own send — <c>LIST</c>, <c>CANCEL</c>,
/// <c>CLEAR_FINISHED</c>.
/// <para>
/// <b>Exactly what <c>XMLHttpRequest</c> offers and nothing more:</b> see what is in flight, abort one,
/// forget the finished ones. There is no <c>RESUME</c>, <c>WAIT</c> or <c>DISMISS</c> — those were the
/// waiting band's, and a request is in flight or done (D66).
/// </para>
/// <para>
/// <c>LIST</c> is the client store's snapshot source: a store cannot replay a stream, so a component that
/// mounts while work is already running gets its state from here and only then folds
/// <see cref="IpcRequestEvents.Updated"/> deltas. Both halves read the SAME optional
/// <c>module</c>/<c>scope</c> payload keys, so a scoped store's snapshot is filtered the way its deltas are
/// — they must agree, or a scoped store loads every scope once and never sheds the rest.
/// </para>
/// <para>
/// 🔴 <b><c>CANCEL</c> carries a <c>requestId</c>, and that is the whole of D66 on the wire.</b> It used to
/// carry an <c>operationId</c> — a GUID minted host-side with no relationship to the request that caused
/// it, which the page had to correlate itself. The id the page already has when it sends a request is now
/// the id it uses to abort it.
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
    /// <param name="options">Supplies the module name, so the routes and the events it publishes agree.</param>
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
                // An honest bool, not an assumed success: Cancel() refuses an unknown or already-finished
                // id, and this route forwards that rather than claiming it worked.
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
