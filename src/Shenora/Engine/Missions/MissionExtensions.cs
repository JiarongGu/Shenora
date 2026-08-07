using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora;
using Shenora.Core.WebView;
using Shenora.Core.Ipc;

namespace Shenora.Engine.Missions;

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
    /// <b>It stays <c>Use*</c>, and that is deliberate (D64).</b> These capabilities behave like
    /// MIDDLEWARE — stages the frontend's requests flow through, not inert singletons — and <c>Use*</c> is
    /// already this kit's vocabulary for a pipeline stage (<c>IMessageDispatcher.UseModule</c>/
    /// <c>UseRoute</c>/<c>UseLogging</c>, <see cref="IWebViewInterceptor.Use"/>). What the
    /// ASP.NET comparison actually corrects is not the PREFIX but the OBJECT: a pipeline is configured on
    /// the built app, which is why mounting a route is <c>app.Use…</c> and not
    /// <c>interceptor.Use…(services)</c>.
    /// </para>
    /// <para>
    /// <b>Everything defaults, and there is no security-shaped choice here</b> — unlike
    /// <c>AddShenoraMediaPlayer</c>, whose <c>AllowedRoots</c> the kit refuses to pick (D61's test: *does
    /// this change what the app is EXPOSED to?*). A scheduler with no options is priority-then-FIFO, unbounded,
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
        builder.Services.TryAddSingleton<IMissionScheduler>(provider =>
            new MissionScheduler(provider.GetRequiredService<MissionSchedulerOptions>()));

        return builder;
    }
}
