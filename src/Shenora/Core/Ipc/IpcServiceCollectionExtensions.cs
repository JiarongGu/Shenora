using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shenora.Core.Ipc;

namespace Shenora;

/// <summary>
/// The standard IPC composition: a feature contributes its facade through DI
/// (<see cref="AddIpcModule{TFacade}"/>, from wherever it registers its services), and the dispatcher
/// is registered once with the §5 pipeline order encoded — error handler → app middleware →
/// registered facades.
/// </summary>
public static class IpcServiceCollectionExtensions
{
    /// <summary>
    /// Register a module facade for dispatch. Facades registered this way are mapped
    /// automatically by <see cref="UseMessageDispatcher"/> (or explicitly via
    /// <see cref="MapRegisteredModules"/>).
    /// </summary>
    public static IServiceCollection AddIpcModule<TFacade>(this IServiceCollection services)
        where TFacade : class, IIpcModule
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IIpcModule, TFacade>();
        return services;
    }

    /// <summary>
    /// Map an IPC module onto the built app's dispatcher — the <c>app.MapControllers()</c> of this kit
    /// (D64). <typeparamref name="TModule"/> is resolved from the app's provider.
    /// <para>
    /// ⚠ <b>For a module that could not be registered before the app was built</b> — typically one
    /// needing the live window. A module with no such constraint is better registered at build time with
    /// <see cref="AddIpcModule{TFacade}"/>, where the duplicate-module guard runs at composition instead
    /// of on first dispatch.
    /// </para>
    /// </summary>
    /// <returns>The app, so calls chain.</returns>
    public static ShenoraApplication MapModule<TModule>(this ShenoraApplication app)
        where TModule : notnull, IIpcModule
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Services.GetRequiredService<IMessageDispatcher>()
            .MapModule(app.Services.GetRequiredService<TModule>());
        return app;
    }

    /// <summary>
    /// Map every DI-registered <see cref="IIpcModule"/> onto the dispatcher, in registration order.
    /// Resolves the facades NOW, so call it only from code that already holds a built provider — see
    /// <see cref="MapRegisteredModulesLazily"/> for the version <see cref="UseMessageDispatcher"/> uses.
    /// </summary>
    public static IMessageDispatcher MapRegisteredModules(this IMessageDispatcher dispatcher, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(services);
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var facade in services.GetServices<IIpcModule>())
        {
            GuardDuplicateModule(seen, facade);
            dispatcher.MapModule(facade);
        }
        return dispatcher;
    }

    /// <summary>
    /// Map the DI-registered facades through ONE terminal middleware that resolves them on the FIRST
    /// dispatch instead of at composition time.
    /// <para>
    /// 🔴 <b>Lazy because eager is a <see cref="StackOverflowException"/> with no diagnostic.</b>
    /// Resolving facades inside the <see cref="IMessageDispatcher"/> singleton factory re-enters that
    /// factory for any facade whose graph reaches <see cref="IMessageDispatcher"/> — the documented
    /// cross-module <c>SendAsync</c> seam. Microsoft DI's cycle detection cannot see a factory delegate
    /// re-entering the provider, so it is process death with no exception and no log line.
    /// </para>
    /// </summary>
    internal static IMessageDispatcher MapRegisteredModulesLazily(this IMessageDispatcher dispatcher, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(services);

        // ⚠ PublicationOnly, never the default mode, which CACHES a thrown exception for the life of the
        // Lazy: one composition mistake would then answer EVERY later request to every DI-registered
        // module with UNKNOWN_ERROR for the process life. Racing first dispatches may each build the
        // map — one wins, and the build has no side effects beyond resolving singletons DI caches.
        var facades = new Lazy<IReadOnlyDictionary<string, IIpcModule>>(() =>
        {
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var map = new Dictionary<string, IIpcModule>(StringComparer.OrdinalIgnoreCase);
            foreach (var facade in services.GetServices<IIpcModule>())
            {
                GuardDuplicateModule(seen, facade);
                map[facade.ModuleName] = facade;
            }
            return map;
        }, LazyThreadSafetyMode.PublicationOnly);

        return dispatcher.Use(async (request, next, ct) =>
        {
            if (facades.Value.TryGetValue(request.Module, out var facade))
            {
                var response = await facade.HandleMessageAsync(request, ct);
                if (response is not null) return response;
            }
            return await next();
        });
    }

    /// <summary>
    /// Reject two facades claiming the same module name: mapping is first-match-wins, so the second
    /// facade's ENTIRE route table would be silently unreachable.
    /// <para>
    /// ⚠ Where it surfaces depends on the path. <see cref="MapRegisteredModules"/> throws at
    /// COMPOSITION; <see cref="MapRegisteredModulesLazily"/> cannot detect it until the first dispatch,
    /// where the never-throws contract turns it into a logged
    /// <see cref="IpcErrorCodes.UnknownError"/> response — diagnosable, not fail-at-startup.
    /// </para>
    /// </summary>
    private static void GuardDuplicateModule(Dictionary<string, string> seen, IIpcModule facade)
    {
        var name = facade.ModuleName;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException($"{facade.GetType().Name} has an empty ModuleName.");
        if (seen.TryGetValue(name, out var existing))
        {
            throw new InvalidOperationException(
                $"Two module facades both claim module '{name}': {existing} and {facade.GetType().Name}. " +
                "Dispatch is first-match-wins, so the second facade's routes would be unreachable.");
        }
        seen[name] = facade.GetType().Name;
    }

    /// <summary>
    /// Register the app's <see cref="IMessageDispatcher"/> singleton composed in the proven
    /// order: <see cref="MessageDispatcherExtensions.UseErrorHandler"/> FIRST (so it wraps everything),
    /// then <paramref name="configure"/> (logging, app middleware, a scoped router, ad-hoc
    /// routes), then every DI-registered facade. Transports resolve <see cref="IMessageDispatcher"/>
    /// and feed requests in.
    /// </summary>
    public static IServiceCollection UseMessageDispatcher(
        this IServiceCollection services, Action<IServiceProvider, IMessageDispatcher>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 🔴 THIS METHOD NAMES NO FEATURE (D65): a CORE must not know the names of the features built on
        // it. The dispatcher composes whatever `IIpcModule`s are REGISTERED; which ones exist is each
        // feature's own business. ⚠ Hardcoding one here fails without looking like a layering mistake —
        // composing IPC over a bare `ServiceCollection` is a legitimate shape, so a feature whose
        // dependencies are not all optional turns every such composition into UNKNOWN_ERROR.
        //
        // TryAdd, so this is IDEMPOTENT: `Build()` calls it for every app (D64), and an app calling it
        // explicitly to pass `configure` must WIN rather than register a second dispatcher — which needs
        // the explicit call FIRST, and `Build()` defaults last.
        services.TryAddSingleton<IMessageDispatcher>(sp =>
        {
            // GetService, not GetRequiredService: composing IPC over a bare ServiceCollection with no
            // IEventBus behind it is a legitimate shape, and the tracker needs a bus. Without one,
            // requests dispatch untracked.
            IMessageDispatcher dispatcher = new MessageDispatcher(sp.GetService<ILogger<MessageDispatcher>>(),
                                                                  sp.GetService<IIpcRequestTracker>());
            dispatcher.UseErrorHandler();
            configure?.Invoke(sp, dispatcher);
            // LAZILY — see MapRegisteredModulesLazily; eager resolution here is a silent StackOverflow.
            return dispatcher.MapRegisteredModulesLazily(sp);
        });
        return services;
    }
}
