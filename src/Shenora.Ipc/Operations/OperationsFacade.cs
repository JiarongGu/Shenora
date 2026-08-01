using Microsoft.Extensions.Logging;

namespace Shenora.Ipc;

/// <summary>
/// The control surface over <see cref="IOperationRegistry"/> — design §4.6. <c>LIST</c> is the
/// client store's snapshot source: a store cannot replay a stream, so a component that mounts
/// while work is already running gets its state from here and only then folds the
/// <see cref="OperationEvents.Updated"/> deltas. <c>CANCEL</c> is the app-level cancel route
/// <c>ipc-contracts</c> already prescribed ("what the client 'cancel this operation' case needs is
/// an app-level CANCEL route carrying the operation id, never a transport concern") — this ships it
/// instead of only describing it. <c>CLEAR_FINISHED</c> drops retained history.
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
/// </summary>
public sealed class OperationsFacade : BaseFacade
{
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
            case "LIST":
                var module = PayloadHelper.GetOptionalValue<string>(request.Payload, "module");
                var scope = PayloadHelper.GetOptionalValue<string>(request.Payload, "scope");
                return Task.FromResult<object?>(_registry.GetAll(module, scope));

            case "CANCEL":
                var operationId = PayloadHelper.GetRequiredValue<string>(request.Payload, "operationId");
                // Always a response, honestly: Cancel() itself now refuses (see OperationRegistry.Cancel)
                // when the operation never opted into cancellation, an unknown id, or one already
                // terminal — this route just forwards that bool rather than assuming success.
                var cancelled = _registry.Cancel(operationId);
                return Task.FromResult<object?>(new { cancelled });

            case "CLEAR_FINISHED":
                _registry.ClearFinished();
                return Done();

            case "RESUME":
                var resumeId = PayloadHelper.GetRequiredValue<string>(request.Payload, "operationId");
                var requested = _registry.RequestResume(resumeId);
                return Task.FromResult<object?>(new { requested });

            default:
                throw UnknownType(request);   // BaseFacade owns the shape (P5.5 H4.5)
        }
    }
}
