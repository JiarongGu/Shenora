namespace Shenora.Core;

/// <summary>
/// In-process pub/sub between modules, services, and transport bridges — ported from the primary
/// desktop sibling. Subscriptions match on module/type/scope with <c>"*"</c> wildcards. Scope
/// matching keeps the source's proven semantics: an UNSCOPED subscription receives events of
/// every scope, and a SCOPED subscription also receives global (scope-less) events — global
/// broadcasts reach everyone. Handler failures are logged and isolated: one throwing subscriber
/// never breaks the others or the emitter.
///
/// Registered automatically by <see cref="ShenoraApplicationBuilder.Build"/> (as
/// <see cref="EventBus"/>; <c>TryAdd</c>, so an app can substitute its own).
/// </summary>
public interface IEventBus
{
    /// <summary>Subscribe to one event type in one scope. Returns the subscription id.</summary>
    string Subscribe(string module, string type, string scope, Func<EventMessage, Task> handler);

    /// <summary>Subscribe to one event type across all scopes. Returns the subscription id.</summary>
    string Subscribe(string module, string type, Func<EventMessage, Task> handler);

    /// <summary>Subscribe to all of a module's events in one scope. Returns the subscription id.</summary>
    string SubscribeToModule(string module, string scope, Func<EventMessage, Task> handler);

    /// <summary>Subscribe to all of a module's events across all scopes. Returns the subscription id.</summary>
    string SubscribeToModule(string module, Func<EventMessage, Task> handler);

    /// <summary>Subscribe to every event. Returns the subscription id.</summary>
    string SubscribeToAll(Func<EventMessage, Task> handler);

    /// <summary>Remove a subscription by the id its Subscribe call returned. Unknown ids are ignored.</summary>
    void Unsubscribe(string subscriptionId);

    /// <summary>Emit to all matching subscribers; completes when every handler has run.</summary>
    Task EmitAsync(EventMessage message);

    /// <summary>Build an <see cref="EventMessage"/> and emit it.</summary>
    Task EmitAsync(string module, string type, object? payload = null, string? scope = null);
}
