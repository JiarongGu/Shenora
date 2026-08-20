namespace Shenora.Core.Events;

/// <summary>
/// In-process pub/sub between modules, services, and transport bridges. Subscriptions match on
/// module/type/scope with <c>"*"</c> wildcards: an UNSCOPED subscription receives events of every scope,
/// and a SCOPED subscription also receives global (scope-less) events, so a global broadcast reaches
/// everyone. Handler failures are logged and isolated — one throwing subscriber never breaks the others
/// or the emitter.
/// <para>
/// Registered automatically by <see cref="ShenoraApplicationBuilder.Build"/> (as <see cref="EventBus"/>;
/// <c>TryAdd</c>, so an app can substitute its own).
/// </para>
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Subscribe to one event type in one scope.
    /// </summary>
    /// <returns>Dispose to unsubscribe. Disposing twice is safe and does nothing the second time.</returns>
    IDisposable Subscribe(string module, string type, string scope, Func<EventMessage, Task> handler);

    /// <summary>Subscribe to one event type across all scopes. Dispose to unsubscribe.</summary>
    IDisposable Subscribe(string module, string type, Func<EventMessage, Task> handler);

    /// <summary>Subscribe to all of a module's events in one scope. Dispose to unsubscribe.</summary>
    IDisposable SubscribeToModule(string module, string scope, Func<EventMessage, Task> handler);

    /// <summary>Subscribe to all of a module's events across all scopes. Dispose to unsubscribe.</summary>
    IDisposable SubscribeToModule(string module, Func<EventMessage, Task> handler);

    /// <summary>Subscribe to every event. Dispose to unsubscribe.</summary>
    IDisposable SubscribeToAll(Func<EventMessage, Task> handler);

    /// <summary>Emit to all matching subscribers; completes when every handler has run.</summary>
    Task EmitAsync(EventMessage message);

    /// <summary>Build an <see cref="EventMessage"/> and emit it.</summary>
    Task EmitAsync(string module, string type, object? payload = null, string? scope = null);

    /// <summary>
    /// Emit WITHOUT awaiting the handlers — for a caller that has no <c>await</c> to offer, such as a
    /// synchronous <c>Action</c>-shaped callback, a timer tick, or a UI event handler.
    /// <para>
    /// Safe to discard: every handler runs inside the bus's own guard, so nothing a subscriber does
    /// becomes an unobserved exception. ARGUMENT errors still throw synchronously.
    /// </para>
    /// </summary>
    void Emit(EventMessage message);

    /// <summary>Build an <see cref="EventMessage"/> and emit it without awaiting. See <see cref="Emit(EventMessage)"/>.</summary>
    void Emit(string module, string type, object? payload = null, string? scope = null);
}
