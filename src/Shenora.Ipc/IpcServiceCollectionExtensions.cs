using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shenora.Media;

namespace Shenora.Ipc;

/// <summary>
/// The standard IPC composition, formalizing the pattern the sample app proved: modules
/// contribute facades through DI (<see cref="AddModuleFacade{TFacade}"/> from their
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
    public static IServiceCollection AddModuleFacade<TFacade>(this IServiceCollection services)
        where TFacade : class, IModuleFacade
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IModuleFacade, TFacade>();
        return services;
    }

    /// <summary>
    /// Map every DI-registered <see cref="IModuleFacade"/> onto the dispatcher, in registration order.
    /// Resolves the facades NOW — safe from application code that already holds a built provider, but
    /// see <see cref="MapRegisteredModulesLazily"/> for the version <see cref="AddMessageDispatcher"/>
    /// must use.
    /// </summary>
    public static IMessageDispatcher MapRegisteredModules(this IMessageDispatcher dispatcher, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(services);
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var facade in services.GetServices<IModuleFacade>())
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
        var facades = new Lazy<IReadOnlyDictionary<string, IModuleFacade>>(() =>
        {
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var map = new Dictionary<string, IModuleFacade>(StringComparer.OrdinalIgnoreCase);
            foreach (var facade in services.GetServices<IModuleFacade>())
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
    private static void GuardDuplicateModule(Dictionary<string, string> seen, IModuleFacade facade)
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

        // 🔴 THE KIT'S OWN MODULES COME WITH THE DISPATCHER (D64). If you are composing IPC at all, the
        // framework's routes are part of what you are composing — the same reason
        // `WebApplication.CreateBuilder` brings Kestrel. They are inert until the page posts to them, so
        // there is nothing to opt out of, and they answer on RESERVED `SHENORA.` module names that cannot
        // collide with an app's own.
        //
        // ⚠ `MediaPlayerFacade` is the one that was MISSING rather than merely opt-in, and its absence was
        // silent: the kit shipped `useMediaPlayer` on the page and no host route for the reports it posts,
        // so `IMediaPlayer.OpenAsync` waited forever on a message nothing answered. TryAddEnumerable
        // rather than AddModuleFacade so registering it twice cannot map the module twice — which the
        // dispatcher rejects outright as a duplicate.
        //
        // ⚠ Both dependencies are resolved OPTIONALLY. This lives in `Shenora.Ipc`, which cannot assume
        // anyone called `ShenoraApplicationBuilder.Build()` — composing IPC over a bare ServiceCollection
        // is a legitimate shape and the kit's own IPC tests are exactly that. A default registration that
        // threw on resolve would turn "the framework is on" into "the framework fell over".
        // ⚠ The two-type overload, NOT `Singleton<IModuleFacade>(factory)`: `TryAddEnumerable` refuses a
        // descriptor whose implementation type is the SERVICE type, because it has nothing to compare for
        // duplicates ("indistinguishable from other services registered for IModuleFacade"). Naming
        // MediaPlayerFacade as the implementation is what makes the try-add idempotent at all.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleFacade, MediaPlayerFacade>(sp =>
            new MediaPlayerFacade(
                sp.GetService<IMediaPlayer>(),
                sp.GetService<MediaPlayerOptions>() ?? new MediaPlayerOptions(),
                sp.GetService<ILogger<MediaPlayerFacade>>())));

        // ⚠ NOTHING ELSE IS REGISTERED HERE, and the attempt to is worth recording. Defaulting
        // `AddShenoraOperations()` from this method broke five composition tests with UNKNOWN_ERROR:
        // `OperationRegistry` needs `IEventBus`, which `Shenora.Ipc` cannot assume exists because
        // composing IPC over a bare ServiceCollection is legitimate. The lesson is not "resolve it
        // optionally too" — it is that **a CORE must not know the names of the features built on it**
        // (D65). The dispatcher composes whatever facades are REGISTERED; deciding which ones exist
        // belongs to each feature. `MediaPlayerFacade` above is the last hold-out of the wrong shape and
        // moves out with the D65 restructure.

        services.AddSingleton<IMessageDispatcher>(sp =>
        {
            IMessageDispatcher dispatcher = new MessageDispatcher(sp.GetService<ILogger<MessageDispatcher>>());
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
