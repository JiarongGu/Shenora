using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shenora.Core.Ipc;

/// <summary>Inputs for <see cref="ScopedContainerRouter"/>.</summary>
public sealed class ScopedContainerRouterOptions
{
    /// <summary>
    /// Populates a NEW scope's service collection — called once per scope id, on first use.
    /// The scope id is app-defined (<see cref="IpcRequest.Scope"/>: a profile, a workspace, a
    /// document…). Validate the id here and throw <see cref="ShenoraException"/> (e.g. a
    /// SCOPE_NOT_FOUND code) to reject unknown scopes — the pipeline's error mapping turns it
    /// into the structured wire error. Keep it fast: requests for the scope wait on it (the
    /// source app blocked here deliberately, for message ordering); heavy initialization
    /// belongs in <see cref="OnScopeCreated"/>, fire-and-forget where possible.
    /// <para>
    /// EACH SCOPE IS A ROOT PROVIDER, not a DI child scope — so <c>AddScoped</c> registered here
    /// behaves as a SINGLETON for that scope's whole lifetime, and <c>AddTransient</c> disposables it
    /// resolves are held until the scope is disposed. That is usually what an app wants (the scope IS
    /// the lifetime boundary), but it is the opposite of what <c>AddScoped</c> means everywhere else in
    /// Microsoft DI, so it is worth stating rather than discovering.
    /// </para>
    /// </summary>
    public required Action<string, IServiceCollection> ConfigureScope { get; init; }

    /// <summary>
    /// Runs once per scope after its provider is built — the generalization of everything the
    /// source app hardcoded post-build (schema migrations, plugin loading, crash-resume sweeps).
    /// A throw here fails the scope's creation (and the triggering request) — isolate anything
    /// that must never block a scope from opening, as the source did.
    /// </summary>
    public Action<string, IServiceProvider>? OnScopeCreated { get; init; }
}

/// <summary>
/// Routes scope-carrying requests to per-scope service containers — the generalization of the
/// primary desktop sibling's per-profile service router (generic-library: an app-defined scope
/// field + a scoped-container router, never a domain id). Each scope id lazily gets its own
/// child <see cref="ServiceProvider"/> (built from <see cref="ScopedContainerRouterOptions.ConfigureScope"/>);
/// requests for modules declared via <see cref="MapModule{TFacade}"/> resolve their facade from
/// the request's scope container. Wire into the pipeline with
/// <see cref="ScopedContainerRouterExtensions.UseScopedRouter"/> (after the error handler).
///
/// DEVIATIONS from the source, all deliberate: a scoped module called WITHOUT a scope answers a
/// structured <see cref="IpcErrorCodes.ScopeRequired"/> error instead of falling through (the
/// source's equivalent check was unreachable through its own wiring — which is why its client
/// grew a hand-rolled guard); exceptions flow to the pipeline's error mapping instead of a local
/// catch (the source leaked <c>ex.Message</c> here); and scope creation is single-flight (the
/// source's bare <c>GetOrAdd</c> could build two providers under a first-request race and drop
/// one undisposed).
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
        // `required` only forces the caller to WRITE the initializer, not to write a non-null value —
        // and an explicit `ConfigureScope = null!` (or a field that happened to be null) surfaced as an
        // NRE from inside scope creation, which the pipeline's error mapping then reported to the client
        // as UNKNOWN_ERROR: a composition bug disguised as a runtime failure (P5.5 H3).
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
    /// The ids of every scope container currently alive — the seam for app-side sweeps (the
    /// source hardcoded a close-all-secondary-windows walk here; apps enumerate and act on
    /// their own services instead).
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

        // Lazy with ExecutionAndPublication = exactly one build per scope id even when the first
        // requests race; a bare GetOrAdd(factory) can run the factory twice and silently drop
        // one fully built provider without disposing it.
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
    /// Drop a scope's container (e.g. the scoped entity was deleted or closed) and dispose it —
    /// the next request for the id builds a fresh one.
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
    /// from the scope container (an unresolved facade falls through, keeping composition open).
    /// Exceptions (incl. <see cref="ScopedContainerRouterOptions.ConfigureScope"/> validation)
    /// propagate to the pipeline's error mapping — register the router after
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
            // The scope was invalidated (or disposed) BETWEEN us fetching its container and resolving
            // from it — a normal race, because InvalidateScope is a documented app-facing call that can
            // fire while requests are in flight (P5.5 H6). It used to surface as UNKNOWN_ERROR, telling
            // the client something broke when the correct answer is simply to use the rebuilt scope.
            // GetScopeServices already removed the dead entry, so ONE retry builds a fresh container; a
            // second failure is a real fault and propagates. Guarded on !_disposed so a router shutting
            // down does not spin rebuilding scopes it is trying to tear down.
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
        // Lazy behind a single sweep and build an orphan provider (found in review).
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
            // Observe Value unconditionally: an IN-FLIGHT creation (IsValueCreated still false)
            // must be waited for and disposed, or the provider it finishes building leaks
            // untracked — two live containers over single-writer resources (found in review).
            // A FAILED creation rethrows here and there is nothing to dispose.
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
    /// Route scope-carrying requests through <paramref name="router"/>. Family order: error
    /// handler → logging → app middleware → THIS → global facades.
    /// </summary>
    public static IMessageDispatcher UseScopedRouter(this IMessageDispatcher dispatcher, ScopedContainerRouter router)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(router);
        return dispatcher.Use(router.HandleAsync);
    }
}
