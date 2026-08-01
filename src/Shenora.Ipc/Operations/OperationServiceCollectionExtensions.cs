using Microsoft.Extensions.DependencyInjection;
using Shenora.Core;

namespace Shenora.Ipc;

/// <summary>
/// Wires the operations control surface into DI: the registry (built from the app's
/// <see cref="IEventBus"/> — already registered by <c>ShenoraApplicationBuilder.Build</c>, or by the
/// app/test directly) plus <see cref="OperationsFacade"/>, mapped like any other module through the
/// existing <see cref="IpcServiceCollectionExtensions.AddModuleFacade{TFacade}"/>.
/// </summary>
public static class OperationServiceCollectionExtensions
{
    /// <summary>
    /// Register the operation registry + its facade. OPT-IN: an app with no long-running work should
    /// pay nothing for it, and D21 says the kit ships the primitive, never the product.
    /// </summary>
    public static IServiceCollection AddShenoraOperations(
        this IServiceCollection services, Action<OperationRegistryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new OperationRegistryOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // A single GetRequiredService<IEventBus>() call, not an enumeration of the provider — this is
        // the ordinary "resolve one dependency" shape every other factory here uses, not the
        // GetServices<IModuleFacade>()-inside-a-singleton-factory pattern that caused the silent
        // StackOverflow in AddMessageDispatcher (see IpcServiceCollectionExtensions).
        services.AddSingleton<IOperationRegistry>(sp =>
            new OperationRegistry(sp.GetRequiredService<IEventBus>(), sp.GetRequiredService<OperationRegistryOptions>()));

        services.AddModuleFacade<OperationsFacade>();

        return services;
    }
}
