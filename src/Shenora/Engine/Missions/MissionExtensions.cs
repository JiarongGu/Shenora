using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora.Engine.Files;
using Shenora.Engine.Missions;

namespace Shenora;

/// <summary>
/// Wiring the mission scheduler into an application.
/// </summary>
public static class MissionExtensions
{
    /// <summary>
    /// Configure the mission scheduler (D27–D31).
    /// <code>
    /// // nothing at all — the scheduler is already there (D64)
    /// builder.UseMissions(x => x.GlobalLaneCapacity = 4);      // …bounded
    /// </code>
    /// <para>
    /// 🔴 <b>You do not call this to GET a scheduler; you call it to change one.</b> <c>Build()</c>
    /// registers it either way (D64); this registers your options FIRST and the kit's is <c>TryAdd</c>,
    /// so yours wins. ⚠ After <c>Build()</c> it does nothing, and two calls are not additive.
    /// </para>
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="configure">Optional. No configuration = priority-then-FIFO, in-memory.</param>
    public static ShenoraApplicationBuilder UseMissions(
        this ShenoraApplicationBuilder builder,
        Action<MissionSchedulerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseMissions((options, _) => configure?.Invoke(options));
    }

    /// <summary>
    /// Configure the scheduler AND substitute any of its collaborators, in one place.
    /// <code>
    /// builder.UseMissions((x, services) =>
    /// {
    ///     x.GlobalLaneCapacity = 4;
    ///     services.AddSingleton&lt;IMissionQueueStore, MyStore&gt;();   // wins over the kit's default
    /// });
    /// </code>
    /// <para>
    /// 🔴 <b>The guarantee is that YOUR registration wins</b> — the kit's own registrations are
    /// <c>TryAdd</c> and run AFTER this callback, so exactly one ever exists (D64).
    /// </para>
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="configure">Receives the options and the container, before the kit registers anything.</param>
    public static ShenoraApplicationBuilder UseMissions(
        this ShenoraApplicationBuilder builder,
        Action<MissionSchedulerOptions, IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new MissionSchedulerOptions();
        configure(options, builder.Services);

        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddSingleton<IMissionScheduler>(provider =>
        {
            // ⚠ On the RESOLVED options, never the captured ones: an app may have registered its own.
            var resolved = provider.GetRequiredService<MissionSchedulerOptions>();
            // The kit's own media conversion claims its source path, so this scheduler must understand path
            // claims whatever else the app registered. An app scope already named "path" is kept as it is.
            if (!resolved.Scopes.Any(scope => scope.Name == PathClaims.ScopeName))
                resolved.Scopes = [.. resolved.Scopes, PathClaims.Scope];
            return new MissionScheduler(resolved);
        });

        return builder;
    }
}
