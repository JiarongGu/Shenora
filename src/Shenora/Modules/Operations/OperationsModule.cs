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
/// The route types are also public constants (<see cref="ListType"/>/<see cref="CancelType"/>/
/// <see cref="ClearFinishedType"/>), matching <see cref="OperationEvents"/>'s own const shape — an app
/// or test matches by symbol, and <c>WireMirrorTests</c> pins them against the client's route literals
/// so a host rename cannot silently leave the client deaf.
/// </para>
/// <para>
/// 🔴 <b>THREE routes, and the three that went are the point (D66, 2026-08-08).</b> <c>RESUME</c>,
/// <c>WAIT</c> and <c>DISMISS</c> were the WAITING band's control surface, and the band described
/// host-initiated work rather than a request — the only code that ever drove them wrapped a queued
/// MISSION. What is left is what an <c>XMLHttpRequest</c> actually offers: see what is in flight
/// (<c>LIST</c>), abort one (<c>CANCEL</c>), and forget the finished ones (<c>CLEAR_FINISHED</c>).
/// A request is in flight or done; there is no parked state to ask about.
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

    // NO `RESUME`/`WAIT`/`DISMISS`. They were the WAITING band's routes and went with it (D66,
    // 2026-08-08): the only code that ever drove them wrapped a queued MISSION, which is host-initiated
    // work, not a request. What is left is what a request actually has — list it, cancel it, forget it.

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

            default:
                throw UnknownType(request);   // ModuleBase owns the shape (P5.5 H4.5)
        }
    }
}
