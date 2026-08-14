using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora.Engine.Missions;
using Shenora.Core.WebView;
using Shenora.Core.Ipc;

// Extensions live with the type they EXTEND — see MediaPlayerExtensions for the rule.
namespace Shenora;

/// <summary>
/// Wiring the mission scheduler into an application.
/// </summary>
public static class MissionExtensions
{
    /// <summary>
    /// **Configure** the mission scheduler — the execution half of the kit: one engine that serves a
    /// filesystem operation planner (paths conflict by containment) and a job queue (lanes admit N) alike
    /// (D27–D31).
    /// <code>
    /// // nothing at all — the scheduler is already there (D64)
    /// builder.UseMissions(x => x.GlobalLaneCapacity = 4);      // …bounded
    /// </code>
    /// <para>
    /// 🔴 <b>You do not call this to GET a scheduler; you call it to change one.</b> <c>Build()</c>
    /// registers it either way (D64) — the framework is on, the way
    /// <c>WebApplication.CreateBuilder</c> brings Kestrel without anyone calling <c>AddKestrel()</c>.
    /// Calling this registers your options FIRST, and the default registration is <c>TryAdd</c>, so yours
    /// wins. ⚠ It follows that calling it after <c>Build()</c> is meaningless, and that two calls are not
    /// additive: the first wins, like every other <c>TryAdd</c> in the container.
    /// </para>
    /// <para>
    /// <b>It is <c>Use*</c> because it is the APPLICATION'S SETUP for a capability, not a container
    /// registration</b> — D64's rule: <c>Use</c> means the wider configuration including whatever the
    /// capability contributes to a pipeline; <c>Add</c> means the service-collection level only
    /// (<c>AddIpcModule</c> registers a facade and nothing else, so it is an <c>Add</c>).
    /// ⚠ An earlier version of this paragraph justified the prefix by claiming these "behave like
    /// MIDDLEWARE — stages the frontend's requests flow through". **That was the coarse reading, and for
    /// the scheduler it is simply untrue**: no request flows through it. The receiver is what decides —
    /// this is on the BUILDER, which is the app's own setup.
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
    /// 🔴 <b>The guarantee is that YOUR registration wins.</b> An app could always have registered on
    /// <c>builder.Services</c> itself — what it could not do is KNOW that, which took reading the kit's
    /// source to learn these are <c>TryAdd</c>. Owner, 2026-08-08: <i>"the service should be override
    /// inside <c>useXX(s =&gt; {})</c> config instead"</i>.
    /// </para>
    /// <para>
    /// ⚠ <b>Ordering is NOT what makes it work, which was measured rather than assumed.</b> Microsoft DI
    /// resolves the LAST descriptor, so an app wins from either side of the kit's <c>TryAdd</c>. Running
    /// the callback first buys the other half: exactly ONE registration, so nothing enumerating the
    /// service also finds the kit's default sitting shadowed behind yours.
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
            new MissionScheduler(provider.GetRequiredService<MissionSchedulerOptions>()));

        return builder;
    }
}
