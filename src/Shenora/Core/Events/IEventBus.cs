using Shenora.Core.WebView;

namespace Shenora.Core.Events;

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
    /// <summary>
    /// Subscribe to one event type in one scope.
    /// </summary>
    /// <returns>
    /// Dispose to unsubscribe. Disposing twice is safe and does nothing the second time.
    /// <para>
    /// ⚠ <b>An <see cref="IDisposable"/>, not an id string, and the whole kit answers this way</b> —
    /// <c>IWebViewInterceptor.Use</c> and <c>WebViewResourcePipeline.Use</c> already did. The id version
    /// this replaced could not be scoped with <c>using</c>, could not be released by a compiler-enforced
    /// path, and ignored an id it did not recognise — so a typo or a double-release was a silent no-op
    /// rather than a failure. One library should have ONE answer to "how do I undo a registration".
    /// </para>
    /// </returns>
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
    /// This is deliberately not "just" <c>_ = EmitAsync(…)</c> at the call site, even though that is
    /// what it does. Discarding a task is normally a hazard — an unobserved exception dies in it — and
    /// whether it is safe here depends on an internal guarantee: every handler runs inside this bus's
    /// own guard, so the returned task cannot fault because of a subscriber. A caller could only learn
    /// that by reading the implementation, which was the actual finding (P6.4): an adopter bridging a
    /// synchronous emit callback had to either read kit source or write the discard nervously. The
    /// guarantee is the API's to state, so it states it.
    /// </para>
    /// ARGUMENT errors still throw synchronously — those are caller bugs, not event failures.
    /// </summary>
    void Emit(EventMessage message);

    /// <summary>Build an <see cref="EventMessage"/> and emit it without awaiting. See <see cref="Emit(EventMessage)"/>.</summary>
    void Emit(string module, string type, object? payload = null, string? scope = null);
}
