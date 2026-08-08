using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora.Core.Events;
using Shenora.Core.Ipc;
using Shenora.Modules.Requests;

// Extensions live with the type they EXTEND — see MediaPlayerExtensions for the rule.
namespace Shenora;

/// <summary>
/// Request tracking as APPLICATION SETUP: the tracker (built from the app's <see cref="IEventBus"/>) plus
/// <see cref="IpcRequestsModule"/>, mapped like any other module.
/// <para>
/// 🔴 <b>This is a CORE module — the framework does not work without it, so an app configures it rather
/// than adding it.</b> Owner, 2026-08-08: <i>"think about this is more like a webapp config as .net so you
/// can have a setup for the application itself, because this entire framework cannot work without those
/// core modules."</i> That is the <c>WebApplication.CreateBuilder</c> model exactly: Kestrel is THERE, and
/// you never call <c>AddKestrel()</c> — you configure it as part of setting the application up.
/// </para>
/// <para>
/// So the app-facing surface is <see cref="UseRequests(ShenoraApplicationBuilder, Action{IpcRequestTrackerOptions})"/>
/// on the BUILDER, beside <c>UseMissions</c>/<c>UseFileSystem</c>/<c>UseMediaPlayer</c>. The
/// <see cref="IServiceCollection"/> registration below is INTERNAL: exposing it would offer a choice that
/// does not exist, since <c>Build()</c> calls it for every application regardless.
/// </para>
/// </summary>
public static class IpcRequestExtensions
{
    /// <summary>
    /// Configure request tracking — grace period, progress throttle, retained history, module name.
    /// <code>
    /// builder.UseRequests(x => x.GracePeriod = TimeSpan.FromMilliseconds(80));
    /// </code>
    /// <para>
    /// ⚠ <b>It does not ENABLE anything.</b> Tracking is on for every application (D64); this only
    /// configures it, and calling it is optional. That is why there is no way to turn it off: a request
    /// that finishes inside the grace period already costs no event, no history entry and no wire traffic,
    /// so the thing an "off" switch would save does not exist.
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
    /// 🔴 <b>The guarantee is that YOUR registration wins</b> — see <c>UseMissions</c>'s overload for why
    /// ordering is not the mechanism, and what running the callback first actually buys.
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
    /// Register the request tracker and its control module.
    /// <para>
    /// ON BY DEFAULT (D64), and it costs a request that finishes quickly one dictionary insert and one
    /// removal — no event, no history entry, no wire traffic. That is the grace period doing its job, and
    /// it is why this can be defaulted at all: the old registry had to be opt-in because every tracked
    /// thing published immediately.
    /// </para>
    /// <para>
    /// 🔴 <b><c>TryAdd</c> throughout, because this is called BOTH by an app configuring options and by
    /// <c>UseMessageDispatcher</c> defaulting it.</b> With <c>AddSingleton</c> the second call would
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
    internal static IServiceCollection AddShenoraRequests(
        this IServiceCollection services, IpcRequestTrackerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(options ?? new IpcRequestTrackerOptions());

        // A single GetRequiredService<IEventBus>(), not an enumeration of the provider — the ordinary
        // "resolve one dependency" shape, not the GetServices<IIpcModule>()-inside-a-singleton-factory
        // pattern that once caused a silent StackOverflow in UseMessageDispatcher.
        services.TryAddSingleton<IIpcRequestTracker>(sp =>
            new IpcRequestTracker(sp.GetRequiredService<IEventBus>(),
                                  sp.GetRequiredService<IpcRequestTrackerOptions>()));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcModule, IpcRequestsModule>());

        return services;
    }
}
