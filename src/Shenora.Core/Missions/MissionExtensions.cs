using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora.Core;

namespace Shenora.Core.Missions;

/// <summary>
/// Wiring the mission scheduler into an application.
/// </summary>
public static class MissionExtensions
{
    /// <summary>
    /// Register the mission scheduler — the execution half of the kit: one engine that serves a filesystem
    /// operation planner (paths conflict by containment) and a job queue (lanes admit N) alike (D27–D31).
    /// <code>
    /// builder.UseMissions();                                   // priority-then-FIFO, no persistence
    /// builder.UseMissions(x => x.GlobalLaneCapacity = 4);      // …bounded
    /// </code>
    /// <para>
    /// <b>It exists to complete a pattern, and the gap it fills was found by naming the subsystems.</b>
    /// Media and the file system each got a one-call registration; missions — the third portable engine —
    /// still made an adopter write <c>new MissionScheduler(options)</c> and register it themselves. Three
    /// engines, three ways in, was the inconsistency.
    /// </para>
    /// <para>
    /// <b>Everything defaults, and there is no security-shaped choice here</b> — unlike
    /// <c>UseMediaPlayer</c>, whose <c>AllowedRoots</c> the kit refuses to pick (D61's test: *does this
    /// change what the app is EXPOSED to?*). A scheduler with no options is priority-then-FIFO, unbounded,
    /// in-memory: the behaviour the design started from, and a legitimate production choice for an app that
    /// wants ordering without persistence.
    /// </para>
    /// <para>
    /// ⚠ <b>What it deliberately does NOT do is start anything.</b> The scheduler is event-driven and owns
    /// no timer (D57) — a policy that defers on an external condition needs
    /// <see cref="IMissionScheduler.Reevaluate"/>, because only the app knows what it is waiting for.
    /// </para>
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="configure">Optional. A scheduler with no configuration is priority-then-FIFO.</param>
    public static ShenoraApplicationBuilder UseMissions(
        this ShenoraApplicationBuilder builder,
        Action<MissionSchedulerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new MissionSchedulerOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddSingleton<IMissionScheduler>(services =>
            new MissionScheduler(services.GetRequiredService<MissionSchedulerOptions>()));

        return builder;
    }
}
