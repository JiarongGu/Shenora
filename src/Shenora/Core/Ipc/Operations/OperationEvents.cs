using Shenora.Core.Events;

namespace Shenora.Core.Ipc;

/// <summary>Event and request type names for the operations module. Constants, so an app matches by
/// symbol rather than by a literal that a rename cannot follow.</summary>
public static class OperationEvents
{
    /// <summary>A full <see cref="OperationInfo"/> snapshot — every transition uses this one type,
    /// so folding is last-write-wins by id with no cross-type ordering hazard.</summary>
    public const string Updated = "OPERATION_UPDATED";



    /// <summary>
    /// One or more operation ids left the registry with NO corresponding <see cref="Updated"/>
    /// snapshot — <c>MaxHistory</c> eviction and <see cref="IOperationRegistry.ClearFinished"/>
    /// (generic-library audit finding 4: the host bounds its own history, but the client — the side
    /// actually rendering — never heard about a removal, so a long-lived store's mirror of a bounded
    /// list was unbounded). Payload is <c>{ operationIds: string[] }</c> — a BATCH, since eviction and
    /// clearing can remove several ids at once; a client folds it by deleting those ids from its own
    /// state.
    /// Emitted with no <c>scope</c> (global): a removal can span several scopes in one batch, and
    /// deleting an id a subscriber does not have is a harmless no-op, so every store hears it
    /// regardless of its own scope filter — the same rule an unscoped event already follows for a
    /// scoped subscriber (<see cref="Shenora.Core.Events.IEventBus"/>).
    /// </summary>
    public const string Removed = "OPERATION_REMOVED";
}
