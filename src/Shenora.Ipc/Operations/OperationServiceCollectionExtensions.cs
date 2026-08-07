using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// <para>
    /// Takes the OPTIONS RECORD directly, not a configure callback (Finding 2, whole-branch review):
    /// every <see cref="OperationRegistryOptions"/> property is <c>{ get; init; }</c>, so a callback
    /// shape (<c>Action&lt;OperationRegistryOptions&gt;? configure</c>) made <c>o => o.ModuleName =
    /// "MY_OPS"</c> a compile error (CS8852) — the callback could only ever read a freshly-defaulted
    /// instance, never configure one. This matches how every other options type in the kit is
    /// consumed (<c>WebViewIpcBridgeOptions</c>, <c>NotificationPumpOptions</c>) and keeps
    /// <c>init</c>-only as the kit's one immutability convention, rather than making this the one
    /// mutable options record.
    /// </para>
    /// </summary>
    public static IServiceCollection AddShenoraOperations(
        this IServiceCollection services, OperationRegistryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        options ??= new OperationRegistryOptions();
        // 🔴 TryAdd throughout, because this is now called BOTH by an app configuring the registry and by
        // `AddMessageDispatcher` defaulting it (D64). With `AddSingleton` the second call registers a
        // SECOND `OperationsFacade`, and two facades claiming one module name is a duplicate the
        // dispatcher rejects — so an app that configured its options would have broken its own routes by
        // doing so. Idempotence is the precondition for defaulting anything, exactly as it was for the
        // engines in `Build()`.
        services.TryAddSingleton(options);

        // A single GetRequiredService<IEventBus>() call, not an enumeration of the provider — this is
        // the ordinary "resolve one dependency" shape every other factory here uses, not the
        // GetServices<IModuleFacade>()-inside-a-singleton-factory pattern that caused the silent
        // StackOverflow in AddMessageDispatcher (see IpcServiceCollectionExtensions).
        services.TryAddSingleton<IOperationRegistry>(sp =>
            new OperationRegistry(sp.GetRequiredService<IEventBus>(), sp.GetRequiredService<OperationRegistryOptions>()));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleFacade, OperationsFacade>());

        return services;
    }
}
