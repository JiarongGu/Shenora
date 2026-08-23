using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shenora.Core.Events;
using Shenora.Modules.Platform;

namespace Shenora;

/// <summary>
/// Wires <see cref="BackNavigation"/> and its routes into DI, so a page can handle the system back
/// gesture instead of the platform finishing the activity under it.
/// </summary>
public static class BackNavigationServiceCollectionExtensions
{
    /// <summary>
    /// Register the back-gesture coordinator and its route module.
    /// <para>
    /// ⚠ <b>Registering it does not intercept anything.</b> Until a page asks
    /// (<see cref="BackNavigation.InterceptType"/>) every press takes the platform default with no round
    /// trip, so this is safe to call on a shell whose page may or may not want it — and safe to call on
    /// a shell that has no back gesture at all, where nothing will ever raise one.
    /// </para>
    /// <para>
    /// 🔴 <b>Something still has to RAISE the press, and it is NOT this call.</b> Construct
    /// <c>MobileBackNavigation</c> from the page, the way <c>MobileSafeArea</c> is constructed — it needs
    /// a live activity, which does not exist while the builder runs, so no <c>Use…</c> can do it for you.
    /// Registered alone this is a coordinator nobody calls, and the page's <c>INTERCEPT</c> would be
    /// accepted while no press ever arrived: exactly the D63 shape where ABSENT is indistinguishable
    /// from working. The two calls are a PAIR.
    /// </para>
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="options">How long a press waits for the page. Null takes the defaults.</param>
    /// <param name="log">
    /// Where a press that nobody answered is reported. ⚠ <b>Pass one.</b> The fallback is an
    /// <see cref="ILoggerFactory"/> from the container, and an app that registered none gets a silent
    /// coordinator — which costs exactly the diagnostic that matters here, since "the page did not
    /// answer within the timeout" is the only signal that back is quietly taking the platform default.
    /// </param>
    public static IServiceCollection AddShenoraBackNavigation(
        this IServiceCollection services, BackNavigationOptions? options = null, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(provider => new BackNavigation(
            provider.GetRequiredService<IEventBus>(),
            options,
            log ?? provider.GetService<ILoggerFactory>()?.CreateLogger<BackNavigation>()));

        // TryAddEnumerable for the same reason the dialogs use it: the SHELL calls this as well as the
        // app, and two facades claiming one module name is a duplicate the dispatcher rejects.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Core.Ipc.IIpcModule, BackNavigationModule>());
        return services;
    }
}
