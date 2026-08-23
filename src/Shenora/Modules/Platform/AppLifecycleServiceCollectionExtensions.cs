using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shenora.Core.Events;
using Shenora.Modules.Platform;

namespace Shenora;

/// <summary>
/// Wires <see cref="AppLifecycle"/> into DI, so the page learns how long the app was backgrounded.
/// </summary>
public static class AppLifecycleServiceCollectionExtensions
{
    /// <summary>
    /// Register the lifecycle reporter.
    /// <para>
    /// ⚠ <b>It registers NO routes</b>, unlike the kit's other page-facing clusters: the page only ever
    /// LISTENS here, so there is nothing for it to call and adding a route to ask "am I foreground?"
    /// would be answering a question only a running page can ask — the answer is always yes.
    /// </para>
    /// <para>
    /// 🔴 <b>Something still has to REPORT the transitions</b>, which is the shell's half:
    /// <c>MobileAppLifecycle</c> attaches to a MAUI <c>Window</c>. Registered alone this publishes
    /// nothing at all, and a page waiting on <c>RESUMED</c> waits forever — the same D63 shape as the
    /// back gesture. The two calls are a PAIR.
    /// </para>
    /// </summary>
    /// <param name="services">The container.</param>
    public static IServiceCollection AddShenoraAppLifecycle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(provider => new AppLifecycle(
            provider.GetRequiredService<IEventBus>(),
            provider.GetService<ILoggerFactory>()?.CreateLogger<AppLifecycle>()));
        return services;
    }
}
