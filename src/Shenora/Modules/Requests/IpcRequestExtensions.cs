using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora.Core.Events;
using Shenora.Core.Ipc;
using Shenora.Modules.Requests;

namespace Shenora;

/// <summary>
/// Request tracking as APPLICATION SETUP: the tracker (built from the app's <see cref="IEventBus"/>) plus
/// <see cref="IpcRequestsModule"/>, mapped like any other module.
/// <para>
/// 🔴 <b>This is a CORE module — the framework does not work without it, so an app CONFIGURES it rather
/// than adding it</b> (D64). The app-facing surface is
/// <see cref="UseRequests(ShenoraApplicationBuilder, Action{IpcRequestTrackerOptions})"/> on the BUILDER;
/// the <see cref="IServiceCollection"/> registration below is internal, because <c>Build()</c> calls it
/// for every application regardless.
/// </para>
/// </summary>
public static class IpcRequestExtensions
{
    /// <summary>
    /// Configure request tracking — grace period, progress throttle, retained history, module name.
    /// <para>
    /// ⚠ <b>It does not ENABLE anything.</b> Tracking is on for every application (D64); this only
    /// configures it, and calling it is optional.
    /// </para>
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="configure">Optional. Receives the options before anything is registered.</param>
    public static ShenoraApplicationBuilder UseRequests(
        this ShenoraApplicationBuilder builder,
        Action<IpcRequestTrackerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseRequests((options, _) => configure?.Invoke(options));
    }

    /// <summary>
    /// Configure request tracking AND substitute its collaborators — most usefully the
    /// <see cref="IIpcRequestTracker"/> itself, for a host that wants to record requests its own way.
    /// <para>
    /// 🔴 <b>The guarantee is that YOUR registration wins:</b> <paramref name="configure"/> runs before
    /// the kit registers anything, and the kit registers with <c>TryAdd</c>.
    /// </para>
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="configure">Receives the options and the container, before the kit registers anything.</param>
    public static ShenoraApplicationBuilder UseRequests(
        this ShenoraApplicationBuilder builder,
        Action<IpcRequestTrackerOptions, IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new IpcRequestTrackerOptions();
        configure(options, builder.Services);
        builder.Services.AddShenoraRequests(options);
        return builder;
    }

    /// <summary>
    /// Register the request tracker and its control module. ON BY DEFAULT (D64).
    /// <para>
    /// 🔴 <b><c>TryAdd</c> throughout, because this is called BOTH by an app configuring options and by
    /// <c>UseMessageDispatcher</c> defaulting it.</b> With <c>AddSingleton</c> the second call registers a
    /// SECOND module, and two modules claiming one name is a duplicate the dispatcher rejects — so an app
    /// that configured its options would break its own routes by doing so.
    /// </para>
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="options">Optional. The mutable options record, taken directly.</param>
    internal static IServiceCollection AddShenoraRequests(
        this IServiceCollection services, IpcRequestTrackerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(options ?? new IpcRequestTrackerOptions());

        services.TryAddSingleton<IIpcRequestTracker>(sp =>
            new IpcRequestTracker(sp.GetRequiredService<IEventBus>(),
                                  sp.GetRequiredService<IpcRequestTrackerOptions>()));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcModule, IpcRequestsModule>());

        return services;
    }
}
