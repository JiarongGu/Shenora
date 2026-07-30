using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    /// <summary>Map every DI-registered <see cref="IModuleFacade"/> onto the dispatcher, in registration order.</summary>
    public static MessageDispatcher MapRegisteredModules(this MessageDispatcher dispatcher, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(services);
        foreach (var facade in services.GetServices<IModuleFacade>())
            dispatcher.MapModule(facade);
        return dispatcher;
    }

    /// <summary>
    /// Register the app's <see cref="IMessageDispatcher"/> singleton composed in the proven
    /// order: <see cref="MessageDispatcher.UseErrorHandler"/> FIRST (so it wraps everything),
    /// then <paramref name="configure"/> (logging, app middleware, a scoped router, ad-hoc
    /// routes), then every DI-registered facade. Transports resolve <see cref="IMessageDispatcher"/>
    /// and feed requests in.
    /// </summary>
    public static IServiceCollection AddMessageDispatcher(
        this IServiceCollection services, Action<IServiceProvider, MessageDispatcher>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IMessageDispatcher>(sp =>
        {
            var dispatcher = new MessageDispatcher(sp.GetService<ILogger<MessageDispatcher>>())
                .UseErrorHandler();
            configure?.Invoke(sp, dispatcher);
            return dispatcher.MapRegisteredModules(sp);
        });
        return services;
    }
}
