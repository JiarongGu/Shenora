using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;
using Shenora.Core.Events;

namespace Shenora.Core.Ipc;

/// <summary>
/// The standard IPC composition, formalizing the pattern the sample app proved: modules
/// contribute facades through DI (<see cref="AddIpcModule{TFacade}"/> from their
/// <c>IShenoraModule.ConfigureServices</c>), and the dispatcher is registered once with the
/// family's §5 pipeline order encoded — error handler → app middleware → registered facades.
/// This replaces the source app's static mutable service registry with plain DI enumeration.
/// </summary>
public static class IpcServiceCollectionExtensions
{
    /// <summary>
    /// Register a module facade for dispatch. Facades registered this way are mapped
    /// automatically by <see cref="AddMessageDispatcher"/> (or explicitly via
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
    /// Map every DI-registered <see cref="IIpcModule"/> onto the dispatcher, in registration order.
    /// Resolves the facades NOW — safe from application code that already holds a built provider, but
    /// see <see cref="MapRegisteredModulesLazily"/> for the version <see cref="AddMessageDispatcher"/>
    /// must use.
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
    /// This is not a micro-optimization, it is a deadlock fix (P5.5 H2). Resolving facades inside the
    /// <see cref="IMessageDispatcher"/> singleton factory means calling back into the provider WHILE
    /// that singleton is still being constructed. Any facade whose dependency graph reaches
    /// <see cref="IMessageDispatcher"/> — the documented seam for cross-module <c>SendAsync</c>, so a
    /// perfectly ordinary thing to inject — re-enters the same factory. Microsoft DI's cycle detection
    /// is call-site based and cannot see a factory delegate re-entering the provider, and the
    /// singleton is not in the resolved-services cache yet, so the factory simply runs again:
    /// unbounded recursion, <see cref="StackOverflowException"/>, process death with no exception and
    /// no log line. By the first dispatch the singleton is cached, so the same graph resolves fine.
    /// </para>
    /// </summary>
    public static IMessageDispatcher MapRegisteredModulesLazily(this IMessageDispatcher dispatcher, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(services);

        // Lazy<T> is thread-safe by default: concurrent first dispatches resolve the facade set once.
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
        });

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
    /// Reject two facades claiming the same module name. Mapping is first-match-wins, so the second
    /// facade's ENTIRE route table was silently unreachable and nothing logged it (P5.5 H2) — a
    /// library module and an app module both called "APP" would have looked like a routing mystery.
    /// <para>
    /// Where this surfaces depends on the path, and the difference is worth knowing:
    /// <see cref="MapRegisteredModules"/> throws at COMPOSITION, while
    /// <see cref="MapRegisteredModulesLazily"/> cannot detect it until the first dispatch — and since
    /// <see cref="MessageDispatcher.DispatchAsync"/> never throws by contract, it arrives there as a
    /// logged <see cref="IpcErrorCodes.UnknownError"/> response with the detail kept host-side. So on
    /// the lazy path the guarantee is "diagnosable", not "fails at startup".
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
    /// <para>
    /// <paramref name="configure"/> receives the INTERFACE, not the concrete dispatcher (P5.5 H6): every
    /// mapping helper now composes on <see cref="IMessageDispatcher"/>, so nothing needs the concrete
    /// type — and taking it here would have kept propagating the very downcast this change removes.
    /// </para>
    /// </summary>
    public static IServiceCollection AddMessageDispatcher(
        this IServiceCollection services, Action<IServiceProvider, IMessageDispatcher>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 🔴 THIS METHOD NAMES NO FEATURE, and that is the D65 rule it exists to demonstrate: **a CORE
        // must not know the names of the features built on it.** The dispatcher composes whatever
        // `IIpcModule`s are REGISTERED; which ones exist is each feature's own business, registered
        // from the feature itself (`UseMediaPlayer`) or from the shell that can satisfy it
        // (`AddShenoraFileDialogs`, called by the shells because only a platform knows whether it has
        // native dialogs).
        //
        // ⚠ It briefly did the opposite, and BOTH attempts are worth not repeating. Hardcoding
        // `MediaPlayerModule` here worked only because its dependencies could be resolved optionally;
        // adding `AddShenoraOperations()` beside it broke five composition tests with UNKNOWN_ERROR,
        // because `OperationRegistry` needs `IEventBus` and composing IPC over a bare
        // `ServiceCollection` is a legitimate shape with no builder behind it. The fix was never
        // "resolve that optionally too" — it was to stop a core reaching downward at all.
        // TryAdd, so this is IDEMPOTENT: `Build()` calls it for every app (D64 — IPC is a core, and a
        // framework that needs to be asked for its own wire is not on by default), and an app calling it
        // explicitly to pass `configure` must WIN rather than register a second dispatcher.
        // ⚠ The explicit call has to come FIRST for that, which it does: `Build()` defaults last.
        services.TryAddSingleton<IMessageDispatcher>(sp =>
        {
            // GetService, not GetRequiredService: composing IPC over a bare ServiceCollection with no
            // IEventBus behind it is a legitimate shape (five composition tests do it), and the tracker
            // needs a bus. An app that registered one — AddShenoraRequests, which Build() calls for every
            // app — gets tracking; one that did not, dispatches untracked exactly as before.
            IMessageDispatcher dispatcher = new MessageDispatcher(sp.GetService<ILogger<MessageDispatcher>>(),
                                                                  sp.GetService<IIpcRequestTracker>());
            dispatcher.UseErrorHandler();
            configure?.Invoke(sp, dispatcher);
            // LAZILY — resolving facades here would re-enter this very factory for any facade whose
            // graph reaches IMessageDispatcher, which is a StackOverflow with no diagnostic. See
            // MapRegisteredModulesLazily.
            return dispatcher.MapRegisteredModulesLazily(sp);
        });
        return services;
    }
}
