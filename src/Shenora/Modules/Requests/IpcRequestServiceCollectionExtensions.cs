using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora.Core.Events;
using Shenora.Core.Ipc;

namespace Shenora.Modules.Requests;

/// <summary>
/// Wires request tracking into DI: the tracker (built from the app's <see cref="IEventBus"/>, already
/// registered by <c>ShenoraApplicationBuilder.Build</c>) plus <see cref="IpcRequestsModule"/>, mapped like
/// any other module.
/// </summary>
public static class IpcRequestServiceCollectionExtensions
{
    /// <summary>
    /// Register the request tracker and its control module.
    /// <para>
    /// ON BY DEFAULT (D64), and it costs a request that finishes quickly one dictionary insert and one
    /// removal — no event, no history entry, no wire traffic. That is the grace period doing its job, and
    /// it is why this can be defaulted at all: the old registry had to be opt-in because every tracked
    /// thing published immediately.
    /// </para>
    /// <para>
    /// 🔴 <b><c>TryAdd</c> throughout, because this is called BOTH by an app configuring options and by
    /// <c>AddMessageDispatcher</c> defaulting it.</b> With <c>AddSingleton</c> the second call would
    /// register a SECOND module, and two modules claiming one name is a duplicate the dispatcher rejects —
    /// so an app that configured its options would have broken its own routes by doing so. Idempotence is
    /// the precondition for defaulting anything.
    /// </para>
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="options">
    /// Optional. The mutable options record, taken DIRECTLY rather than through a configure callback: the
    /// kit's other options types are consumed the same way, and an <c>init</c>-only record behind a
    /// callback makes <c>o => o.ModuleName = "…"</c> a compile error.
    /// </param>
    public static IServiceCollection AddShenoraRequests(
        this IServiceCollection services, IpcRequestTrackerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(options ?? new IpcRequestTrackerOptions());

        // A single GetRequiredService<IEventBus>(), not an enumeration of the provider — the ordinary
        // "resolve one dependency" shape, not the GetServices<IIpcModule>()-inside-a-singleton-factory
        // pattern that once caused a silent StackOverflow in AddMessageDispatcher.
        services.TryAddSingleton<IIpcRequestTracker>(sp =>
            new IpcRequestTracker(sp.GetRequiredService<IEventBus>(),
                                  sp.GetRequiredService<IpcRequestTrackerOptions>()));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcModule, IpcRequestsModule>());

        return services;
    }
}
