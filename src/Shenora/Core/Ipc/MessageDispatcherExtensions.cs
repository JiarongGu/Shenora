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
    /// FIRST so it wraps everything after it. Mapping is <see cref="IpcErrorMapping"/>'s: raw exception
    /// text never reaches the wire.
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
        // ⚠ A dispatcher that does not track modules gets the route with NO duplicate guard: a second
        // facade for the same module maps silently and never runs.
        return dispatcher.UseModule(facade.ModuleName, async (request, ct) => await facade.HandleMessageAsync(request, ct));
    }

    /// <summary>
    /// Map <paramref name="facade"/> unless its module name is already taken; returns false if it is.
    /// The claim is ATOMIC, so two threads offering the same plug-in name cannot both win. Pair it with
    /// <see cref="TryReleaseModule"/>; throws <see cref="NotSupportedException"/> when the dispatcher
    /// does not implement <see cref="IModuleRegistry"/> and so cannot answer the question.
    /// <para>
    /// ⚠ <b>KNOWN LIMIT: this does not see facades registered through DI.</b>
    /// <see cref="IpcServiceCollectionExtensions.UseMessageDispatcher"/> maps those through one lazy
    /// terminal middleware, so this answers <c>true</c> for a name a DI facade already owns and the
    /// plug-in then NEVER RUNS, silently, because that middleware sits EARLIER in the pipeline. Map
    /// anything a plug-in must be able to collide with explicitly.
    /// </para>
    /// </summary>
    /// <returns>True if the module was mapped; false if the name was already claimed.</returns>
    public static bool TryMapModule(this IMessageDispatcher dispatcher, IIpcModule facade)
    {
        ArgumentNullException.ThrowIfNull(facade);
        return Registry(dispatcher, nameof(TryMapModule)).TryClaimModule(facade);
    }

    /// <summary>
    /// Release a mapped module: it stops answering and its name becomes free to claim again. It removes
    /// the ROUTE and nothing else — requests already inside the facade run to completion, and the facade
    /// is NOT disposed. Throws <see cref="NotSupportedException"/> when the dispatcher cannot answer.
    /// <para>
    /// Only modules mapped from a FACADE are releasable. Routes added with
    /// <see cref="MapRoute"/>/<see cref="UseRoute"/>/<see cref="UseModule"/> or the
    /// <see cref="ModuleRouteBuilder"/> form are plain middleware and were never tracked;
    /// <see cref="IModuleRegistry.MappedModules"/> tells you what is.
    /// </para>
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
    /// 🔴 <b>REFUSES rather than answering silently</b> (at COMPOSITION, never per request): behind a
    /// decorator the concrete dispatcher's logger is unreachable, and every unhandled exception would
    /// then cross as <c>UNKNOWN_ERROR</c> and be logged NOWHERE — the one place the kit promises the
    /// detail stays host-side. Pass <c>NullLogger.Instance</c> for deliberate silence.
    /// </para>
    /// </summary>
    private static ILogger ResolveLogger(IMessageDispatcher dispatcher, ILogger? logger, string operation)
    {
        if (logger is not null) return logger;
        // Non-null by construction — a dispatcher built without one holds NullLogger.
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
    /// know what it routes must not report a name as free.
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
