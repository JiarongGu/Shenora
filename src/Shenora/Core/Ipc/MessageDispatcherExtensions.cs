using Microsoft.Extensions.Logging;

namespace Shenora.Core.Ipc;

/// <summary>
/// Every mapping and middleware helper for <see cref="IMessageDispatcher"/>, built on its single
/// <see cref="IMessageDispatcher.Use"/> primitive — so they work on any implementation or decorator.
/// </summary>
public static class MessageDispatcherExtensions
{
    /// <summary>Give every request for <paramref name="module"/> (case-insensitive) to
    /// <paramref name="handler"/>; a null result falls through to the rest of the pipeline.</summary>
    public static IMessageDispatcher UseModule(this IMessageDispatcher dispatcher, string module,
                                               Func<IpcRequest, CancellationToken, Task<IpcResponse?>> handler)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return dispatcher.Use(ModuleMiddleware(module, handler));
    }

    /// <summary>
    /// The module-matching rule, in ONE place. <see cref="MessageDispatcher.TryClaimModule"/> installs
    /// it directly and keeps the reference, which is what makes release possible.
    /// </summary>
    internal static MessageMiddleware ModuleMiddleware(string module,
                                                       Func<IpcRequest, CancellationToken, Task<IpcResponse?>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(module);
        ArgumentNullException.ThrowIfNull(handler);
        return async (request, next, ct) =>
        {
            if (string.Equals(request.Module, module, StringComparison.OrdinalIgnoreCase))
            {
                var response = await handler(request, ct);
                if (response is not null)
                    return response;
            }
            return await next();
        };
    }

    /// <summary>Give requests matching module + type (both case-insensitive) to <paramref name="handler"/>.</summary>
    public static IMessageDispatcher UseRoute(this IMessageDispatcher dispatcher, string module, string type,
                                             Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrEmpty(module);
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(handler);
        return dispatcher.Use(async (request, next, ct) =>
        {
            if (string.Equals(request.Module, module, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(request.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                return await handler(request, ct);
            }
            return await next();
        });
    }

    /// <summary>Middleware that logs every request and its outcome.</summary>
    public static IMessageDispatcher UseLogging(this IMessageDispatcher dispatcher, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var log = ResolveLogger(dispatcher, logger, nameof(UseLogging));
        return dispatcher.Use(async (request, next, ct) =>
        {
            log.LogDebug("Processing {Module}/{Type}", request.Module, request.Type);
            var response = await next();
            if (response is { Success: true })
                log.LogDebug("Success {Module}/{Type}", request.Module, request.Type);
            else if (response is { Success: false })
                log.LogWarning("Failed {Module}/{Type}: [{Code}]", request.Module, request.Type, response.Error?.Code);
            return response;
        });
    }

    /// <summary>
    /// Middleware that converts downstream exceptions into structured error responses — register it
    /// FIRST so it wraps everything after it. <see cref="ShenoraException"/> crosses as its structured
    /// error; cancellation crosses as <see cref="IpcErrorCodes.OperationCancelled"/>; anything else is
    /// logged host-side and crosses only as <see cref="IpcErrorCodes.UnknownError"/> plus the exception
    /// type name — raw exception text never reaches the wire.
    /// </summary>
    public static IMessageDispatcher UseErrorHandler(this IMessageDispatcher dispatcher, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var log = ResolveLogger(dispatcher, logger, nameof(UseErrorHandler));
        return dispatcher.Use(async (request, next, ct) =>
        {
            try
            {
                return await next();
            }
            catch (Exception ex)
            {
                return IpcErrorMapping.ToErrorResponse(request, ex, log, "in");
            }
        });
    }

    /// <summary>Map one route to a simple handler; the result is wrapped in a success response.</summary>
    public static IMessageDispatcher MapRoute(this IMessageDispatcher dispatcher, string module, string type,
                                             Func<IpcRequest, object?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return dispatcher.UseRoute(module, type,
            (request, _) => Task.FromResult(IpcResponse.CreateSuccess(request.Id, handler(request))));
    }

    /// <summary>Map a route table for one module (see <see cref="ModuleRouteBuilder"/>).</summary>
    public static IMessageDispatcher MapModule(this IMessageDispatcher dispatcher, string module,
                                              Action<ModuleRouteBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrEmpty(module);
        ArgumentNullException.ThrowIfNull(configure);
        configure(new ModuleRouteBuilder(dispatcher, module));
        return dispatcher;
    }

    /// <summary>
    /// Route a whole module to a facade (registered in DI as <see cref="IIpcModule"/>). THROWS if the
    /// module is already mapped — a facade answers every request for its module, so a second mapping
    /// would never run. Use <see cref="TryMapModule"/> when a taken name is a normal outcome.
    /// </summary>
    public static IMessageDispatcher MapModule(this IMessageDispatcher dispatcher, IIpcModule facade)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(facade);
        if (dispatcher is IModuleRegistry registry)
        {
            if (!registry.TryClaimModule(facade))
            {
                throw new InvalidOperationException(
                    $"Module '{facade.ModuleName}' is already mapped. A facade answers every request for its "
                    + $"module, so this mapping would never run. Use {nameof(TryMapModule)} if a taken name is "
                    + "an expected outcome (dynamically composed modules).");
            }
            return dispatcher;
        }
        // A dispatcher that does not track modules gets the route with NO duplicate guard: a second
        // facade for the same module maps silently and never runs. Hence TryMapModule's refusal.
        return dispatcher.UseModule(facade.ModuleName, async (request, ct) => await facade.HandleMessageAsync(request, ct));
    }

    /// <summary>
    /// Map <paramref name="facade"/> unless its module name is already taken; returns false if it is.
    /// The claim is ATOMIC — check and install under one lock — so two threads offering the same
    /// plug-in name cannot both win. The primitive for a dynamically composed IPC surface (plug-ins,
    /// licence-gated features, per-tenant or lazily loaded modules): map the app's own modules FIRST,
    /// then offer the rest through this. Pair it with <see cref="TryReleaseModule"/>.
    /// <para>
    /// Throws <see cref="NotSupportedException"/> when the dispatcher cannot answer the question —
    /// never "false", and never a silent map. Custom dispatchers and decorators opt in by
    /// implementing <see cref="IModuleRegistry"/>.
    /// </para>
    /// <para>
    /// ⚠ <b>KNOWN LIMIT: this does not see facades registered through DI.</b>
    /// <see cref="IpcServiceCollectionExtensions.UseMessageDispatcher"/> maps those through one lazy
    /// terminal middleware, not through <see cref="IModuleRegistry.TryClaimModule"/> — so
    /// <see cref="IModuleRegistry.IsModuleMapped"/> reports <c>false</c> for such a module, this method
    /// answers <c>true</c> for its name, and the plug-in then NEVER RUNS, silently, because that
    /// middleware sits EARLIER in the pipeline. Map anything a plug-in must be able to collide with
    /// through <see cref="MapModule(IMessageDispatcher, IIpcModule)"/> or this method explicitly.
    /// </para>
    /// </summary>
    /// <returns>True if the module was mapped; false if the name was already claimed.</returns>
    public static bool TryMapModule(this IMessageDispatcher dispatcher, IIpcModule facade)
    {
        ArgumentNullException.ThrowIfNull(facade);
        return Registry(dispatcher, nameof(TryMapModule)).TryClaimModule(facade);
    }

    /// <summary>
    /// Release a mapped module: it stops answering and its name becomes free to claim again — the
    /// other half of a dynamic IPC surface (disabling a plug-in, dropping a per-tenant module).
    /// <para>
    /// It removes the ROUTE and nothing else. Requests already executing inside the facade run to
    /// completion, and the facade is NOT disposed: its lifetime belongs to whoever created it (usually
    /// the DI container). Dispose it yourself if you own it, after releasing.
    /// </para>
    /// <para>
    /// Only modules mapped from a FACADE can be released — those are the ones the registry claimed.
    /// Routes added with <see cref="MapRoute"/>/<see cref="UseRoute"/>/<see cref="UseModule"/> or the
    /// <see cref="ModuleRouteBuilder"/> form are plain middleware and were never tracked, so they are
    /// not releasable; <see cref="IModuleRegistry.MappedModules"/> tells you what is.
    /// </para>
    /// <para>Throws <see cref="NotSupportedException"/> when the dispatcher cannot answer.</para>
    /// </summary>
    /// <returns>True if the module was mapped and is now released; false if it was not mapped.</returns>
    public static bool TryReleaseModule(this IMessageDispatcher dispatcher, string module)
    {
        ArgumentException.ThrowIfNullOrEmpty(module);
        return Registry(dispatcher, nameof(TryReleaseModule)).TryReleaseModule(module);
    }

    /// <summary>
    /// The logger a middleware reports through: the caller's, else the dispatcher's OWN.
    /// <para>
    /// 🔴 <b>REFUSES rather than answering silently</b>, for the same reason <see cref="Registry"/> below
    /// does: a pipeline that cannot report an error must not pretend it can. The dispatcher's own logger
    /// is reachable only on the kit's concrete type, so behind a DECORATOR — which
    /// <see cref="IModuleRegistry"/>'s own docs positively encourage — the lookup finds nothing and every
    /// unhandled exception would be mapped to <c>UNKNOWN_ERROR</c> for the client and logged NOWHERE.
    /// That is the one place the kit promises the detail stays host-side.
    /// </para>
    /// <para>
    /// It throws at COMPOSITION, never per request, which is the convention the shells already use for a
    /// half-configured pipeline. Want silence deliberately? Pass <c>NullLogger.Instance</c> — that is a
    /// decision, and it should have to be typed.
    /// </para>
    /// </summary>
    private static ILogger ResolveLogger(IMessageDispatcher dispatcher, ILogger? logger, string operation)
    {
        if (logger is not null) return logger;
        // Non-null by construction — a dispatcher built without one holds NullLogger — so the concrete
        // path never reaches the throw below.
        if (dispatcher is MessageDispatcher concrete) return concrete.Logger;
        throw new InvalidOperationException(
            $"{operation} could not find a logger: this dispatcher is a " +
            $"'{dispatcher.GetType().Name}', not the kit's {nameof(MessageDispatcher)}, so its own logger " +
            "cannot be reached — and errors mapped here are logged host-side, so they would be lost " +
            $"entirely. Pass one explicitly: {operation}(logger). For deliberate silence, pass " +
            "NullLogger.Instance.");
    }

    /// <summary>
    /// The registry seam, or a refusal — never a permissive wrong answer: a dispatcher that does not
    /// know what it routes must not report a name as free, nor claim a release succeeded.
    /// </summary>
    private static IModuleRegistry Registry(IMessageDispatcher dispatcher, string operation)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (dispatcher is IModuleRegistry registry) return registry;
        throw new NotSupportedException(
            $"This {nameof(IMessageDispatcher)} does not implement {nameof(IModuleRegistry)}, so {operation} "
            + "cannot know what it routes. Implement it (a decorator must forward every member) rather than "
            + "assuming the name is free.");
    }
}
