using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shenora.Core;

/// <summary>
/// The <see cref="IEventBus"/> implementation (see the interface for the matching semantics).
/// Ported from the primary desktop sibling: lock-free concurrent registries plus a
/// per-subscription match cache — pattern evaluation is memoized per distinct event key
/// (<c>module.type[.scope]</c>), so hot repeated events skip re-matching every subscription.
/// Matching handlers run concurrently (<c>Task.WhenAll</c>); a failing handler is logged and
/// isolated. All registration state is per-instance — static mutable registries were one of the
/// source gaps this extraction fixes.
///
/// The match cache holds one entry per subscription × distinct event key for the subscription's
/// lifetime, so keep the KEY SPACE bounded: scopes and types should be drawn from small sets
/// (profiles, windows, feature areas — the family usage), not per-entity or per-request ids.
/// </summary>
public sealed class EventBus : IEventBus
{
    private readonly ILogger<EventBus> _logger;
    private readonly ConcurrentDictionary<string, Func<EventMessage, Task>> _handlers = new();
    private readonly ConcurrentDictionary<string, (string Module, string Type, string? Scope)> _patterns = new();
    // subscription id -> (event key -> matched?)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _matchCache = new();

    /// <summary>The logger is optional so composition works without <c>AddLogging</c>.</summary>
    public EventBus(ILogger<EventBus>? logger = null)
    {
        _logger = logger ?? NullLogger<EventBus>.Instance;
    }

    /// <inheritdoc />
    public string Subscribe(string module, string type, string scope, Func<EventMessage, Task> handler) =>
        SubscribeCore(module, type, scope, handler);

    /// <inheritdoc />
    public string Subscribe(string module, string type, Func<EventMessage, Task> handler) =>
        SubscribeCore(module, type, null, handler);

    /// <inheritdoc />
    public string SubscribeToModule(string module, string scope, Func<EventMessage, Task> handler) =>
        SubscribeCore(module, "*", scope, handler);

    /// <inheritdoc />
    public string SubscribeToModule(string module, Func<EventMessage, Task> handler) =>
        SubscribeCore(module, "*", null, handler);

    /// <inheritdoc />
    public string SubscribeToAll(Func<EventMessage, Task> handler) =>
        SubscribeCore("*", "*", null, handler);

    private string SubscribeCore(string modulePattern, string typePattern, string? scopePattern,
        Func<EventMessage, Task> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(modulePattern);
        ArgumentException.ThrowIfNullOrEmpty(typePattern);
        ArgumentNullException.ThrowIfNull(handler);

        // Human-readable prefix + guid: the id doubles as a diagnostic label in logs.
        var subscriptionId = $"{modulePattern}.{typePattern}.{scopePattern ?? "*"}_{Guid.NewGuid()}";
        _handlers[subscriptionId] = handler;
        _patterns[subscriptionId] = (modulePattern, typePattern, scopePattern);
        _matchCache[subscriptionId] = new ConcurrentDictionary<string, bool>();
        return subscriptionId;
    }

    /// <inheritdoc />
    public void Unsubscribe(string subscriptionId)
    {
        _handlers.TryRemove(subscriptionId, out _);
        _patterns.TryRemove(subscriptionId, out _);
        _matchCache.TryRemove(subscriptionId, out _);
    }

    /// <inheritdoc />
    public async Task EmitAsync(EventMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // '\0'-joined so arbitrary app strings can't collide (module/type/scope are all
        // app-defined; a '.'-joined key would make ("APP","TASK","s1") and ("APP","TASK.s1")
        // the same cache entry — and the cache is permanent per subscription).
        var eventKey = $"{message.Module}\0{message.Type}\0{message.Scope}";

        var matched = new List<Func<EventMessage, Task>>();
        foreach (var (subscriptionId, pattern) in _patterns)
        {
            if (!_matchCache.TryGetValue(subscriptionId, out var cache))
                continue; // concurrently unsubscribed

            if (!cache.TryGetValue(eventKey, out var matches))
            {
                var moduleMatch = pattern.Module == "*" || pattern.Module == message.Module;
                var typeMatch = pattern.Type == "*" || pattern.Type == message.Type;
                // Scope semantics kept from the source: an unscoped subscription sees every
                // scope, and a scope-less (global) event reaches scoped subscriptions too.
                var scopeMatch = string.IsNullOrEmpty(pattern.Scope) || pattern.Scope == "*"
                    || string.IsNullOrEmpty(message.Scope) || pattern.Scope == message.Scope;
                matches = moduleMatch && typeMatch && scopeMatch;
                cache[eventKey] = matches;
            }

            if (matches && _handlers.TryGetValue(subscriptionId, out var handler))
                matched.Add(handler);
        }

        _logger.LogTrace("Emitting {EventKey} to {HandlerCount} handler(s)", eventKey, matched.Count);

        await Task.WhenAll(matched.Select(handler => InvokeSafely(handler, message))).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task EmitAsync(string module, string type, object? payload = null, string? scope = null) =>
        EmitAsync(new EventMessage { Module = module, Type = type, Payload = payload, Scope = scope });

    private async Task InvokeSafely(Func<EventMessage, Task> handler, EventMessage message)
    {
        try
        {
            await handler(message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // One subscriber's failure must not break the other subscribers or the emitter.
            _logger.LogError(ex, "Event handler failed for {Module}.{Type}", message.Module, message.Type);
        }
    }

    /// <summary>Active subscription count (diagnostics).</summary>
    public int GetHandlerCount() => _handlers.Count;
}
