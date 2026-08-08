using Microsoft.Extensions.DependencyInjection;
using Shenora;
using Shenora.Core.Ipc;
using Shenora.Engine.Files;
using Shenora.Engine.Missions;
using Shenora.Modules.Media;
using Shenora.Modules.Requests;

namespace Shenora.Tests.Core;

/// <summary>
/// The <c>Use…((options, services) => …)</c> overload: one place to configure a capability AND substitute
/// its collaborators.
/// <para>
/// Owner, 2026-08-08: <i>"the service should be override inside <c>useXX(s =&gt; {})</c> config instead"</i>.
/// An app could always have registered on <c>builder.Services</c> itself — what it could not do is KNOW
/// that, which took reading the kit's source to learn these are <c>TryAdd</c> and that registering
/// therefore wins. These pin the GUARANTEE, so it stops being a property of DI semantics nobody wrote down.
/// </para>
/// </summary>
public class CapabilityOverrideTests
{
    private static ShenoraApplicationBuilder Builder() =>
        ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "Shenora.Tests.App",
            BaseDirectory = @"C:\MyApp",
            GetEnvironmentVariable = _ => null,
        });

    /// <summary>
    /// 🔴 The headline, in the D63 shape: a FAKE is supplied through the callback and the resolved service
    /// IS that fake. The kit's default would be <see cref="MediaPlayer"/>, so identity is what proves the
    /// override actually beat it rather than merely sitting in the container beside it.
    /// </summary>
    [Fact]
    public void A_player_substituted_in_the_callback_is_the_one_that_resolves()
    {
        var fake = new FakePlayer();
        var builder = Builder();
        builder.UseMediaPlayer((options, services) =>
        {
            options.AllowedRoots = [@"C:\MyApp\media"];
            services.AddSingleton<IMediaPlayer>(fake);
        });

        using var app = builder.Build();

        Assert.Same(fake, app.Services.GetRequiredService<IMediaPlayer>());

        // 🔴 And EXACTLY ONE registration — this is the half that ordering actually buys, and the only
        // assertion here that is order-sensitive. `Assert.Same` above passes either way, because
        // Microsoft DI resolves the LAST descriptor: register after the kit and you still win the resolve
        // while leaving the kit's default SHADOWED but present, which anything enumerating still sees.
        // Measured by sabotage — moving the callback to run last left this test the only one that failed.
        Assert.Same(fake, Assert.Single(app.Services.GetServices<IMediaPlayer>()));
    }

    /// <summary>
    /// The other half, and the one that would break silently: options set in the SAME callback still
    /// arrive. A substitution that quietly discarded the configuration beside it would be worse than no
    /// overload at all, because the call site would look right.
    /// </summary>
    [Fact]
    public void Options_set_beside_a_substitution_still_reach_the_container()
    {
        var builder = Builder();
        builder.UseMediaPlayer((options, services) =>
        {
            // ⚠ A SETTABLE option on purpose: `Module` is `{ get; init; }`, so `o => o.Module = …` is
            // CS8852 — the same trap the kit already documented for its other options records.
            options.CacheRoot = @"C:\MyApp\cache";
            services.AddSingleton<IMediaPlayer>(new FakePlayer());
        });

        using var app = builder.Build();

        Assert.Equal(@"C:\MyApp\cache", app.Services.GetRequiredService<MediaPlayerOptions>().CacheRoot);
    }

    /// <summary>
    /// The same guarantee on an ENGINE, because the rule has to be uniform or it is not a rule — an
    /// adopter should not have to discover per capability whether substitution works. Substituted with a
    /// real <see cref="MissionScheduler"/> instance rather than a fake: the interface is wide, and
    /// identity proves the point without a hand-written stand-in that could drift from it.
    /// </summary>
    [Fact]
    public void The_same_override_works_for_an_engine()
    {
        var mine = new MissionScheduler(new MissionSchedulerOptions());
        var builder = Builder();
        builder.UseMissions((options, services) =>
        {
            options.GlobalLaneCapacity = 7;
            services.AddSingleton<IMissionScheduler>(mine);
        });

        using var app = builder.Build();

        Assert.Same(mine, app.Services.GetRequiredService<IMissionScheduler>());
        Assert.Equal(7, app.Services.GetRequiredService<MissionSchedulerOptions>().GlobalLaneCapacity);
    }

    /// <summary>
    /// The callback receives the app's LIVE container, not a copy — so anything registered there is
    /// resolvable afterwards. Proven on <c>UseFileSystem</c> with a marker, because its own service has a
    /// factory that needs paths; what is being pinned is the container's identity, which is the part every
    /// capability shares.
    /// </summary>
    [Fact]
    public void The_callback_receives_the_apps_own_container()
    {
        var marker = new Marker();
        var builder = Builder();
        builder.UseFileSystem((options, services) =>
        {
            options.Log = _ => { };
            services.AddSingleton(marker);
        });

        using var app = builder.Build();

        Assert.Same(marker, app.Services.GetRequiredService<Marker>());
    }

    /// <summary>
    /// The plain one-argument overload still behaves exactly as before — it delegates to the two-argument
    /// one, and a delegation that dropped the callback would be invisible until an adopter wondered why
    /// their option did nothing.
    /// </summary>
    [Fact]
    public void The_options_only_overload_still_configures()
    {
        var builder = Builder();
        builder.UseMissions(x => x.GlobalLaneCapacity = 3);

        using var app = builder.Build();

        Assert.Equal(3, app.Services.GetRequiredService<MissionSchedulerOptions>().GlobalLaneCapacity);
    }

    /// <summary>
    /// 🔴 Request tracking is a CORE module — the framework does not work without it — so the app-facing
    /// surface CONFIGURES it rather than adding it, the way `WebApplication.CreateBuilder` gives you
    /// Kestrel and you configure it instead of calling `AddKestrel()`. Owner, 2026-08-08:
    /// <i>"more like a webapp config as .net … this entire framework cannot work without those core modules."</i>
    /// <para>
    /// The point of the test is that the app NEVER asks for tracking and still gets it, configured.
    /// </para>
    /// </summary>
    [Fact]
    public void A_core_module_is_configured_by_the_app_setup_never_added_by_it()
    {
        var builder = Builder();
        builder.UseRequests(x => x.GracePeriod = TimeSpan.FromMilliseconds(80));

        using var app = builder.Build();

        Assert.Equal(TimeSpan.FromMilliseconds(80),
                     app.Services.GetRequiredService<IpcRequestTrackerOptions>().GracePeriod);
        // Still present and still wired, because it is not optional.
        Assert.NotNull(app.Services.GetRequiredService<IIpcRequestTracker>());
    }

    /// <summary>
    /// And an app that configures NOTHING still gets the core module — the half that would make the
    /// "configure, don't add" framing a lie if it broke.
    /// </summary>
    [Fact]
    public void A_core_module_is_present_even_when_the_app_says_nothing()
    {
        using var app = Builder().Build();

        Assert.NotNull(app.Services.GetRequiredService<IIpcRequestTracker>());
        Assert.Equal(TimeSpan.FromMilliseconds(50),
                     app.Services.GetRequiredService<IpcRequestTrackerOptions>().GracePeriod);
    }

    private sealed class Marker;

    /// <summary>A sink. Mirrors the shape <c>MediaPlayerReportingTests</c> already uses.</summary>
    private sealed class FakePlayer : IMediaPlayer
    {
        public MediaPlayerStatus Status { get; } = new() { State = MediaPlayerState.Empty };
        public double Rate { get; set; } = 1.0;
        public event Action<MediaPlayerStatus>? StateChanged;

        public Task OpenAsync(MediaSource source, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PlayAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        // Never raised — referenced so the compiler does not warn the event is unused.
        internal void Unused() => StateChanged?.Invoke(Status);
    }
}
