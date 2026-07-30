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

        // ORDER MATTERS: _patterns is what EmitAsync ENUMERATES, so it must be published LAST (P5.5 H6).
        // Published first, a concurrent emit could see the pattern and then miss the handler or the match
        // cache that had not been written yet — and it would `continue`, whose comment claims that can
        // only mean "concurrently unsubscribed". Registering in reverse makes the comment true: by the
        // time a subscription is visible to an emit, everything it needs is already there.
        _handlers[subscriptionId] = handler;
        _matchCache[subscriptionId] = new ConcurrentDictionary<string, bool>();
        _patterns[subscriptionId] = (modulePattern, typePattern, scopePattern);
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
    public Task EmitAsync(string module, string type, object? payload = null, string? scope = null)
    {
        // Guard HERE too (P5.5 H6). The envelope overload validates via `required` + the checks in
        // SubscribeCore's mirror, but this convenience overload accepted an empty module or type and
        // built a message that could never match any subscription — a silently undeliverable event,
        // which is exactly the class of failure this bus is supposed to make impossible.
        ArgumentException.ThrowIfNullOrEmpty(module);
        ArgumentException.ThrowIfNullOrEmpty(type);
        return EmitAsync(new EventMessage { Module = module, Type = type, Payload = payload, Scope = scope });
    }

    /// <inheritdoc />
    public void Emit(EventMessage message) => Forget(EmitAsync(message));

    /// <inheritdoc />
    public void Emit(string module, string type, object? payload = null, string? scope = null)
        => Forget(EmitAsync(module, type, payload, scope));

    /// <summary>
    /// The whole body of the fire-and-forget contract, in one place so it cannot be half-kept.
    /// <see cref="InvokeSafely"/> already contains every handler failure, so the task cannot fault
    /// because of a subscriber — but "cannot fault" is a claim about TODAY's implementation, and the
    /// interface now promises it to callers. So observe the task anyway: if this bus ever grows a
    /// path that faults, it surfaces as a log line instead of an unobserved-task crash on the
    /// finalizer thread. Argument validation is NOT swallowed — it throws synchronously out of
    /// <c>Emit</c>, before there is a task at all, because an empty module is a caller bug.
    /// </summary>
    private void Forget(Task emitting)
    {
        if (emitting.IsCompletedSuccessfully) return;
        _ = Observe(emitting);

        async Task Observe(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fire-and-forget emit failed.");
            }
        }
    }

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
