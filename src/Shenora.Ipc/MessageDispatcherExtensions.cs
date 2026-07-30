using Microsoft.Extensions.Logging;

namespace Shenora.Ipc;

/// <summary>
/// The composition surface of <see cref="IMessageDispatcher"/> — every mapping and middleware helper,
/// built on the single <see cref="IMessageDispatcher.Use"/> primitive.
/// <para>
/// These were instance methods on <see cref="MessageDispatcher"/>, which meant they were unreachable
/// through the interface — so a composition that maps a facade AFTER the container is built (the
/// documented pattern for anything needing the live window) had to DOWNCAST. The reference composition
/// did, and its <c>if (dispatcher is MessageDispatcher concrete)</c> had no <c>else</c>: a different
/// <see cref="IMessageDispatcher"/> registration, or any decorator, silently dropped three whole
/// modules — the frameless title bar just stopped working with no error anywhere, in the exact code
/// adopters copy (P5.5 H6).
/// </para>
/// <para>
/// Extension methods rather than interface members on purpose: the interface stays at the FOUR things a
/// dispatcher genuinely is (dispatch, two sends, and compose), so a custom implementation or a decorator
/// has four members to write instead of ten, and every helper below is automatically available on it.
/// </para>
/// </summary>
public static class MessageDispatcherExtensions
{
    /// <summary>
    /// Give every request for <paramref name="module"/> (case-insensitive) to
    /// <paramref name="handler"/>; a null result falls through to the rest of the pipeline.
    /// </summary>
    public static IMessageDispatcher UseModule(this IMessageDispatcher dispatcher, string module,
                                               Func<IpcRequest, CancellationToken, Task<IpcResponse?>> handler)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrEmpty(module);
        ArgumentNullException.ThrowIfNull(handler);
        return dispatcher.Use(async (request, next, ct) =>
        {
            if (string.Equals(request.Module, module, StringComparison.OrdinalIgnoreCase))
            {
                var response = await handler(request, ct);
                if (response is not null)
                    return response;
            }
            return await next();
        });
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
        // Fall back to the dispatcher's OWN logger, not NullLogger: these used to be instance methods
        // that closed over it, so defaulting to "silent" would quietly stop reporting errors that a
        // pipeline composed the old way has always logged.
        var log = logger
            ?? (dispatcher as MessageDispatcher)?.Logger
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
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
    /// FIRST so it wraps everything after it. <see cref="OperationException"/> crosses as its structured
    /// error; cancellation crosses as <see cref="IpcErrorCodes.OperationCancelled"/>; anything else is
    /// logged host-side and crosses only as <see cref="IpcErrorCodes.UnknownError"/> plus the exception
    /// type name. <see cref="MessageDispatcher.DispatchAsync"/> keeps a last-resort copy of this mapping
    /// for pipelines composed without it.
    /// </summary>
    public static IMessageDispatcher UseErrorHandler(this IMessageDispatcher dispatcher, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        // Fall back to the dispatcher's OWN logger, not NullLogger: these used to be instance methods
        // that closed over it, so defaulting to "silent" would quietly stop reporting errors that a
        // pipeline composed the old way has always logged.
        var log = logger
            ?? (dispatcher as MessageDispatcher)?.Logger
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
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
    /// Route a whole module to a facade. This replaces the source app's static mutable service
    /// registry: facades live in DI (registered as <see cref="IModuleFacade"/>) and are mapped here.
    /// <para>
    /// THROWS if the module is already mapped, matching the eager DI path
    /// (<c>MapRegisteredModules</c>) which has always guarded this. A facade answers every request
    /// for its module, so a second mapping is dead code — it used to be accepted silently and simply
    /// never run. Use <see cref="TryMapModule"/> when a name being taken is a normal outcome rather
    /// than a composition bug.
    /// </para>
    /// </summary>
    public static IMessageDispatcher MapModule(this IMessageDispatcher dispatcher, IModuleFacade facade)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(facade);
        if (dispatcher is IModuleRegistry registry)
        {
            if (registry.IsModuleMapped(facade.ModuleName))
            {
                throw new InvalidOperationException(
                    $"Module '{facade.ModuleName}' is already mapped. A facade answers every request for its "
                    + $"module, so this mapping would never run. Use {nameof(TryMapModule)} if a taken name is "
                    + "an expected outcome (dynamically composed modules).");
            }
            registry.TrackMappedModule(facade.ModuleName);
        }
        return dispatcher.UseModule(facade.ModuleName, async (request, ct) => await facade.HandleMessageAsync(request, ct));
    }

    /// <summary>
    /// Map <paramref name="facade"/> unless its module name is already taken; returns false if it is.
    /// <para>
    /// The primitive for a DYNAMICALLY composed IPC surface — modules an app does not write itself
    /// and cannot check at compile time: plug-ins, optional features behind a licence or flag,
    /// per-tenant modules, lazily loaded areas. For anything arriving from outside the app this is a
    /// boundary, not tidiness: without it a late mapping could quietly take a name an earlier module
    /// owns. Map the app's own modules FIRST, then offer the rest through this.
    /// </para>
    /// <para>
    /// Throws <see cref="NotSupportedException"/> when the dispatcher cannot answer the question —
    /// never "false", and never a silent map. A dispatcher that does not know what it routes must not
    /// be able to report a name as free; a permissive wrong answer is the dangerous one. Custom
    /// dispatchers and decorators opt in by implementing <see cref="IModuleRegistry"/>.
    /// </para>
    /// <para>
    /// KNOWN LIMIT, stated rather than worked around: a mapped module cannot be RELEASED — the
    /// pipeline only grows, so disabling a dynamic module needs a restart. No consumer has yet needed
    /// runtime removal (the surveyed app applies plug-in enable/disable at startup), so the kit does
    /// not guess at that surface; it is a real capability gap, recorded in <c>TASKS.md</c>.
    /// </para>
    /// </summary>
    /// <returns>True if the module was mapped; false if the name was already claimed.</returns>
    public static bool TryMapModule(this IMessageDispatcher dispatcher, IModuleFacade facade)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(facade);
        if (dispatcher is not IModuleRegistry registry)
        {
            throw new NotSupportedException(
                $"This {nameof(IMessageDispatcher)} does not implement {nameof(IModuleRegistry)}, so it cannot "
                + "say whether a module name is already taken. Implement it (a decorator must forward all three "
                + "members) rather than assuming the name is free.");
        }
        if (registry.IsModuleMapped(facade.ModuleName)) return false;
        registry.TrackMappedModule(facade.ModuleName);
        dispatcher.UseModule(facade.ModuleName, async (request, ct) => await facade.HandleMessageAsync(request, ct));
        return true;
    }
}
