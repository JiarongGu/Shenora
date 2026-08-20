using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shenora.Core.Ipc;

/// <summary>Inputs for <see cref="ScopedContainerRouter"/>.</summary>
public sealed class ScopedContainerRouterOptions
{
    /// <summary>
    /// Populates a NEW scope's service collection — called once per scope id, on first use. The scope id
    /// is app-defined (<see cref="IpcRequest.Scope"/>: a profile, a workspace, a document…). Validate it
    /// here and throw <see cref="ShenoraException"/> to reject an unknown one. Keep it fast — requests
    /// for the scope wait on it; heavy initialization belongs in <see cref="OnScopeCreated"/>.
    /// <para>
    /// ⚠ EACH SCOPE IS A ROOT PROVIDER, not a DI child scope — so <c>AddScoped</c> registered here
    /// behaves as a SINGLETON for that scope's whole lifetime, and <c>AddTransient</c> disposables it
    /// resolves are held until the scope is disposed.
    /// </para>
    /// </summary>
    public required Action<string, IServiceCollection> ConfigureScope { get; init; }

    /// <summary>
    /// Runs once per scope after its provider is built (schema migrations, plugin loading, crash-resume
    /// sweeps). ⚠ A throw here fails the scope's creation AND the triggering request — isolate anything
    /// that must never block a scope from opening.
    /// </summary>
    public Action<string, IServiceProvider>? OnScopeCreated { get; init; }
}

/// <summary>
/// Routes scope-carrying requests to per-scope service containers: an app-defined scope field plus a
/// scoped-container router, never a domain id. Each scope id lazily gets its own
/// <see cref="ServiceProvider"/> (built from <see cref="ScopedContainerRouterOptions.ConfigureScope"/>),
/// and requests for modules declared via <see cref="MapModule{TFacade}"/> resolve their facade from the
/// request's scope container. Wire it in with
/// <see cref="ScopedContainerRouterExtensions.UseScopedRouter"/>, after the error handler.
/// A scoped module called WITHOUT a scope answers a structured
/// <see cref="IpcErrorCodes.ScopeRequired"/> rather than falling through.
/// </summary>
public sealed class ScopedContainerRouter : IDisposable
{
    private readonly ScopedContainerRouterOptions _options;
    private readonly ILogger<ScopedContainerRouter> _logger;
    private readonly ConcurrentDictionary<string, Lazy<ServiceProvider>> _scopes = new();
    private readonly ConcurrentDictionary<string, Func<IServiceProvider, IIpcModule?>> _facadeResolvers =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>The logger is optional so composition works without <c>AddLogging</c>.</summary>
    public ScopedContainerRouter(ScopedContainerRouterOptions options, ILogger<ScopedContainerRouter>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        // ⚠ `required` only forces the caller to WRITE the initializer, not to write a non-null value —
        // and a null one surfaces as an NRE inside scope creation, which the pipeline's error mapping
        // then reports as UNKNOWN_ERROR: a composition bug disguised as a runtime failure.
        ArgumentNullException.ThrowIfNull(options.ConfigureScope, $"{nameof(options)}.{nameof(options.ConfigureScope)}");
        _logger = logger ?? NullLogger<ScopedContainerRouter>.Instance;
    }

    /// <summary>
    /// Declare <paramref name="module"/> (case-insensitive) scope-routed: its requests resolve a
    /// <typeparamref name="TFacade"/> from the request's scope container. Register the facade
    /// itself in <see cref="ScopedContainerRouterOptions.ConfigureScope"/>.
    /// </summary>
    public ScopedContainerRouter MapModule<TFacade>(string module) where TFacade : class, IIpcModule
    {
        ArgumentException.ThrowIfNullOrEmpty(module);
        _facadeResolvers[module] = services => services.GetService<TFacade>();
        _logger.LogDebug("Scope-routed module mapped: {Module}", module);
        return this;
    }

    /// <summary>True when <paramref name="module"/> was declared via <see cref="MapModule{TFacade}"/>.</summary>
    public bool IsScopedModule(string module) => module is not null && _facadeResolvers.ContainsKey(module);

    /// <summary>
    /// The ids of every scope container currently alive — the seam for app-side sweeps.
    /// </summary>
    public IReadOnlyCollection<string> ActiveScopes => _scopes.Keys.ToArray();

    /// <summary>
    /// Get (or lazily create, single-flight) the scope's service container. Global services use
    /// this to reach into a specific scope without routing a request through it.
    /// </summary>
    public IServiceProvider GetScopeServices(string scopeId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(scopeId);

        // ⚠ ExecutionAndPublication = exactly one build per scope id even when the first requests race.
        // A bare GetOrAdd(factory) can run the factory twice and silently drop one built provider
        // without disposing it.
        var lazy = _scopes.GetOrAdd(scopeId,
            id => new Lazy<ServiceProvider>(() => CreateScope(id), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            // A failed creation must not poison the cache — the next request retries
            // (a Lazy caches its exception forever otherwise).
            _scopes.TryRemove(new KeyValuePair<string, Lazy<ServiceProvider>>(scopeId, lazy));
            throw;
        }
    }

    private ServiceProvider CreateScope(string scopeId)
    {
        var services = new ServiceCollection();
        _options.ConfigureScope(scopeId, services);
        var provider = services.BuildServiceProvider();
        try
        {
            _options.OnScopeCreated?.Invoke(scopeId, provider);
        }
        catch
        {
            provider.Dispose();
            throw;
        }
        _logger.LogDebug("Scope container created: {ScopeId}", scopeId);
        return provider;
    }

    /// <summary>
    /// Drop a scope's container and dispose it — the next request for the id builds a fresh one.
    /// </summary>
    public void InvalidateScope(string scopeId)
    {
        if (_scopes.TryRemove(scopeId, out var lazy))
        {
            DisposeScope(scopeId, lazy);
            _logger.LogDebug("Scope container invalidated: {ScopeId}", scopeId);
        }
    }

    /// <summary>
    /// The routing middleware (signature-compatible with <see cref="MessageMiddleware"/>):
    /// non-scoped modules fall through; scoped modules require a scope and resolve their facade
    /// from the scope container (an unresolved facade falls through). ⚠ Exceptions propagate to the
    /// pipeline's error mapping, so register this after
    /// <see cref="MessageDispatcherExtensions.UseErrorHandler"/>.
    /// </summary>
    public async Task<IpcResponse?> HandleAsync(IpcRequest request, Func<Task<IpcResponse?>> next,
                                                CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        if (!IsScopedModule(request.Module))
            return await next();

        if (string.IsNullOrEmpty(request.Scope))
        {
            _logger.LogWarning("Scoped module {Module} called without a scope", request.Module);
            return IpcResponse.CreateError(request.Id, IpcErrorCodes.ScopeRequired, parameters:
                new Dictionary<string, string> { ["module"] = request.Module });
        }

        IIpcModule? facade;
        try
        {
            facade = _facadeResolvers[request.Module](GetScopeServices(request.Scope));
        }
        catch (ObjectDisposedException) when (!_disposed)
        {
            // The scope was invalidated between fetching its container and resolving from it — a normal
            // race, since InvalidateScope is app-facing and can fire while requests are in flight.
            // GetScopeServices already removed the dead entry, so ONE retry builds a fresh container; a
            // second failure is a real fault and propagates. Guarded on !_disposed so a router shutting
            // down does not spin rebuilding scopes it is tearing down.
            _logger.LogDebug("Scope {Scope} was invalidated mid-request; rebuilding for {Module}/{Type}",
                request.Scope, request.Module, request.Type);
            facade = _facadeResolvers[request.Module](GetScopeServices(request.Scope));
        }

        if (facade is null)
            return await next();

        _logger.LogTrace("Routing {Module}/{Type} to scope {Scope}", request.Module, request.Type, request.Scope);
        return await facade.HandleMessageAsync(request, cancellationToken);
    }

    /// <summary>Dispose every scope container.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Drain until empty: a thread already past the disposed check can GetOrAdd a fresh
        // Lazy behind a single sweep and build an orphan provider.
        while (!_scopes.IsEmpty)
        {
            foreach (var scopeId in _scopes.Keys.ToArray())
            {
                if (_scopes.TryRemove(scopeId, out var lazy))
                    DisposeScope(scopeId, lazy);
            }
        }
    }

    private void DisposeScope(string scopeId, Lazy<ServiceProvider> lazy)
    {
        try
        {
            // ⚠ Value observed unconditionally: an IN-FLIGHT creation (IsValueCreated still false) must
            // be waited for and disposed, or the provider it finishes building leaks untracked. A FAILED
            // creation rethrows here and there is nothing to dispose.
            lazy.Value.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Scope container {ScopeId} had no disposable value", scopeId);
        }
    }
}

/// <summary>Pipeline wiring for <see cref="ScopedContainerRouter"/>.</summary>
public static class ScopedContainerRouterExtensions
{
    /// <summary>
    /// Route scope-carrying requests through <paramref name="router"/>, after the error handler and
    /// before the global facades.
    /// </summary>
    public static IMessageDispatcher UseScopedRouter(this IMessageDispatcher dispatcher, ScopedContainerRouter router)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(router);
        return dispatcher.Use(router.HandleAsync);
    }
}
