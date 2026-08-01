using Microsoft.Extensions.Logging;

namespace Shenora.Ipc;

/// <summary>
/// The control surface over <see cref="IOperationRegistry"/> — design §4.6. <c>LIST</c> is the
/// client store's snapshot source: a store cannot replay a stream, so a component that mounts
/// while work is already running gets its state from here and only then folds the
/// <see cref="OperationEvents.Updated"/> deltas. <c>CANCEL</c> is the app-level cancel route
/// <c>ipc-contracts</c> already prescribed ("what the client 'cancel this operation' case needs is
/// an app-level CANCEL route carrying the operation id, never a transport concern") — this ships it
/// instead of only describing it. <c>CLEAR_FINISHED</c> drops retained history, filtered by the SAME
/// optional <c>module</c>/<c>scope</c> payload keys <c>LIST</c> reads (generic-library audit finding
/// 1) — unfiltered, so a scoped store's "clear completed" cannot wipe another scope's history.
/// <para>
/// <see cref="ModuleName"/> comes from the injected <see cref="OperationRegistryOptions.ModuleName"/>,
/// so the request module and the event module the registry publishes under are one renameable
/// string — the duplicate-module guard in
/// <see cref="IpcServiceCollectionExtensions.MapRegisteredModules"/> catches a collision with an
/// app's own module at composition.
/// </para>
/// <para>
/// Precedent: <c>WindowCommandFacade</c>/<c>DropZoneFacade</c> (Shenora.WebView2, not referenceable
/// from here — downward-only dependencies) — an ordinary <see cref="BaseFacade"/>, no special wiring.
/// Registered opt-in via <see cref="OperationServiceCollectionExtensions.AddShenoraOperations"/>.
/// </para>
/// <para>
/// <c>RESUME</c> forwards <c>{ operationId }</c> straight to
/// <see cref="IOperationRegistry.RequestResume"/> and answers <c>{ requested: bool }</c> — the same
/// honest-bool shape as <c>CANCEL</c>: the request always succeeds, and the bool says whether the
/// operation actually was a pending, resumable, interrupted offer. This is single-app-provenance
/// mechanism (design §4.2) — a state, an opaque token, an event; the app owns the checkpoint and
/// what "resume" actually does with it.
/// </para>
/// <para>
/// The route types are also public constants (<see cref="ListType"/>/<see cref="CancelType"/>/
/// <see cref="ClearFinishedType"/>/<see cref="ResumeType"/>/<see cref="DismissType"/>/
/// <see cref="PauseType"/>), matching <see cref="OperationEvents"/>'s own const shape — an app or
/// test matches by symbol, and <c>WireMirrorTests</c> pins them against the client's route literals
/// so a host rename cannot silently leave the client deaf.
/// </para>
/// <para>
/// <c>PAUSE</c> (generic-library audit finding 3) forwards <c>{ operationId }</c> to
/// <see cref="IOperationRegistry.RequestPause"/> and answers <c>{ requested: bool }</c> — the same
/// honest-bool shape as <c>RESUME</c>: the request always succeeds, and the bool says whether the
/// operation actually was running and eligible to be asked. §5A.3 (D23) reasoned "pausing is the
/// HOST's own knowledge" from ONE app's pause semantics — a host discovering its own blocker
/// (expired credentials, DNS not yet propagated). That does not hold for the equally-common shape
/// the kit itself already names as a consumer (a download-manager-style activity panel, "a download
/// service starting an installer fetch"): a human clicking Pause on visible work. <c>PAUSE</c> asks;
/// it does not act — <see cref="IOperationRegistry.RequestPause"/> leaves the entry untouched, and
/// the owning module's OWN <see cref="IOperation.Pause"/> is what actually stops the work and
/// publishes the transition, same split as <c>RESUME</c> vs <see cref="IOperation.Resume"/>.
/// </para>
/// </summary>
public sealed class OperationsFacade : BaseFacade
{
    /// <summary>Route: snapshot of currently-known operations — see the class doc's table.</summary>
    public const string ListType = "LIST";

    /// <summary>Route: cancel a running (or paused) operation by id.</summary>
    public const string CancelType = "CANCEL";

    /// <summary>Route: drop retained finished history.</summary>
    public const string ClearFinishedType = "CLEAR_FINISHED";

    /// <summary>Route: continue a paused or interrupted, resumable operation.</summary>
    public const string ResumeType = "RESUME";

    /// <summary>
    /// Route: ask the owning module to pause a running operation by id — <see cref="IOperationRegistry.RequestPause"/>.
    /// Mirrors <see cref="ResumeType"/>'s shape (<c>{ operationId }</c> → <c>{ requested }</c>): the
    /// request always succeeds, and the bool says whether the operation was actually running and
    /// eligible to be asked.
    /// </summary>
    public const string PauseType = "PAUSE";

    /// <summary>
    /// Route: decline a pending Paused/Interrupted offer by id — <see cref="IOperationRegistry.Dismiss"/>.
    /// Mirrors <see cref="CancelType"/>'s shape (<c>{ operationId }</c> → <c>{ dismissed }</c>), a
    /// separate route rather than <see cref="CancelType"/> accepting more states, for the same reason
    /// <see cref="IOperationRegistry.Dismiss"/> is a separate member from <see cref="IOperationRegistry.Cancel(string)"/>.
    /// </summary>
    public const string DismissType = "DISMISS";

    private readonly IOperationRegistry _registry;
    private readonly string _moduleName;

    /// <summary>
    /// The control surface for <paramref name="registry"/>. <paramref name="options"/> supplies only
    /// <see cref="OperationRegistryOptions.ModuleName"/> (defaults to <c>"OPERATIONS"</c>) — pass the
    /// SAME options instance the registry itself was built with so the request module and the event
    /// module never drift apart.
    /// </summary>
    public OperationsFacade(IOperationRegistry registry, OperationRegistryOptions? options = null,
        ILogger<OperationsFacade>? logger = null)
        : base(logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _moduleName = (options ?? new OperationRegistryOptions()).ModuleName;
    }

    /// <inheritdoc />
    public override string ModuleName => _moduleName;

    /// <inheritdoc />
    protected override Task<object?> RouteMessageAsync(
        IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        switch (request.Type.ToUpperInvariant())
        {
            case ListType:
                var module = PayloadHelper.GetOptionalValue<string>(request.Payload, "module");
                var scope = PayloadHelper.GetOptionalValue<string>(request.Payload, "scope");
                return Task.FromResult<object?>(_registry.GetAll(module, scope));

            case CancelType:
                var operationId = PayloadHelper.GetRequiredValue<string>(request.Payload, "operationId");
                // Always a response, honestly: Cancel() itself now refuses (see OperationRegistry.Cancel)
                // when the operation never opted into cancellation, an unknown id, or one already
                // terminal — this route just forwards that bool rather than assuming success.
                var cancelled = _registry.Cancel(operationId);
                return Task.FromResult<object?>(new { cancelled });

            case ClearFinishedType:
                // Reads the SAME two payload keys LIST reads (Finding 1, generic-library audit): the
                // route used to read no payload at all, so a scoped client's CLEAR_FINISHED silently
                // cleared every OTHER scope's finished history too.
                var clearModule = PayloadHelper.GetOptionalValue<string>(request.Payload, "module");
                var clearScope = PayloadHelper.GetOptionalValue<string>(request.Payload, "scope");
                _registry.ClearFinished(clearModule, clearScope);
                return Done();

            case ResumeType:
                var resumeId = PayloadHelper.GetRequiredValue<string>(request.Payload, "operationId");
                var requested = _registry.RequestResume(resumeId);
                return Task.FromResult<object?>(new { requested });

            case DismissType:
                var dismissId = PayloadHelper.GetRequiredValue<string>(request.Payload, "operationId");
                // Always a response, honestly, same shape as CANCEL: Dismiss() itself refuses (and
                // changes nothing) for Running or an unknown/already-terminal id — this route just
                // forwards that bool rather than assuming success.
                var dismissed = _registry.Dismiss(dismissId);
                return Task.FromResult<object?>(new { dismissed });

            case PauseType:
                var pauseId = PayloadHelper.GetRequiredValue<string>(request.Payload, "operationId");
                // Same honest-bool shape as RESUME: RequestPause() itself refuses (and changes
                // nothing) for anything not Running — this route just forwards that bool.
                var pauseRequested = _registry.RequestPause(pauseId);
                return Task.FromResult<object?>(new { requested = pauseRequested });

            default:
                throw UnknownType(request);   // BaseFacade owns the shape (P5.5 H4.5)
        }
    }
}
