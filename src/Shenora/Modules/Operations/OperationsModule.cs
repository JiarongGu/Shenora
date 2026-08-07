using Microsoft.Extensions.Logging;
using Shenora.Core.Ipc;

namespace Shenora.Modules.Operations;

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
/// Precedent: <c>WindowCommandModule</c>/<c>DropZoneModule</c> (Shenora.Windows, not referenceable
/// from here — downward-only dependencies) — an ordinary <see cref="ModuleBase"/>, no special wiring.
/// Registered opt-in via <see cref="OperationServiceCollectionExtensions.AddShenoraOperations"/>.
/// </para>
/// <para>
/// <c>RESUME</c> forwards <c>{ operationId }</c> straight to
/// <see cref="IOperationRegistry.RequestResume"/> and answers <c>{ requested: bool }</c> — the same
/// honest-bool shape as <c>WAIT</c>, and its exact mirror: the request always succeeds, and the bool
/// says whether the operation actually was waiting and eligible to be asked. Asking is not acting —
/// the owning module's own <see cref="IOperation.Resume"/> is what restarts the work.
/// </para>
/// <para>
/// The route types are also public constants (<see cref="ListType"/>/<see cref="CancelType"/>/
/// <see cref="ClearFinishedType"/>/<see cref="ResumeType"/>/<see cref="DismissType"/>/
/// <see cref="WaitType"/>), matching <see cref="OperationEvents"/>'s own const shape — an app or
/// test matches by symbol, and <c>WireMirrorTests</c> pins them against the client's route literals
/// so a host rename cannot silently leave the client deaf.
/// </para>
/// <para>
/// <c>WAIT</c> (generic-library audit finding 3, renamed from <c>PAUSE</c>) forwards
/// <c>{ operationId }</c> to <see cref="IOperationRegistry.RequestWait"/> and answers
/// <c>{ requested: bool }</c> — the same honest-bool shape as <c>RESUME</c>: the request always
/// succeeds, and the bool says whether the operation actually was running and eligible to be asked.
/// §5A.3 (D23) reasoned "pausing is the HOST's own knowledge" from ONE app's wait semantics — a host
/// discovering its own blocker (expired credentials, DNS not yet propagated). That does not hold for
/// the equally-common shape the kit itself already names as a consumer (a download-manager-style
/// activity panel, "a download service starting an installer fetch"): a human clicking Pause on
/// visible work. <c>WAIT</c> asks; it does not act — <see cref="IOperationRegistry.RequestWait"/>
/// leaves the entry untouched, and the owning module's OWN <see cref="IOperation.Wait"/> is what
/// actually stops the work and publishes the transition, same split as <c>RESUME</c> vs
/// <see cref="IOperation.Resume"/>.
/// </para>
/// </summary>
public sealed class OperationsModule : ModuleBase
{
    /// <summary>Route: snapshot of currently-known operations — see the class doc's table.</summary>
    public const string ListType = "LIST";

    /// <summary>Route: cancel a running (or waiting) operation by id.</summary>
    public const string CancelType = "CANCEL";

    /// <summary>Route: drop retained finished history.</summary>
    public const string ClearFinishedType = "CLEAR_FINISHED";

    /// <summary>Route: ask the owning module to resume a waiting operation — the mirror of <see cref="WaitType"/>.</summary>
    public const string ResumeType = "RESUME";

    /// <summary>
    /// Route: ask the owning module to wait a running operation by id — <see cref="IOperationRegistry.RequestWait"/>.
    /// Mirrors <see cref="ResumeType"/>'s shape (<c>{ operationId }</c> → <c>{ requested }</c>): the
    /// request always succeeds, and the bool says whether the operation was actually running and
    /// eligible to be asked.
    /// </summary>
    public const string WaitType = "WAIT";

    /// <summary>
    /// Route: decline a pending Waiting offer by id — <see cref="IOperationRegistry.Dismiss"/>.
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
    public OperationsModule(IOperationRegistry registry, OperationRegistryOptions? options = null,
        ILogger<OperationsModule>? logger = null)
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

            case WaitType:
                var waitId = PayloadHelper.GetRequiredValue<string>(request.Payload, "operationId");
                // Same honest-bool shape as RESUME: RequestWait() itself refuses (and changes
                // nothing) for anything not Running — this route just forwards that bool.
                var waitRequested = _registry.RequestWait(waitId);
                return Task.FromResult<object?>(new { requested = waitRequested });

            default:
                throw UnknownType(request);   // ModuleBase owns the shape (P5.5 H4.5)
        }
    }
}
