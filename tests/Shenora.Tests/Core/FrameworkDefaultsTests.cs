using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shenora;
using Shenora.Modules.Media;
using Shenora.Engine.Files;
using Shenora.Engine.Missions;
using Shenora.Core.Ipc;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Core;

/// <summary>
/// <b>D64 — the framework is ON BY DEFAULT.</b> An app that calls nothing still gets the engines, because
/// none of them does anything until the frontend asks and gating them bought only the certainty that every
/// app would re-type the same block.
/// <para>
/// 🔴 <b>These exist because the change that introduced the defaults broke NOTHING, which proves only that
/// nothing was looking.</b> Every existing test either called <c>Use…</c> explicitly or never asked for an
/// engine at all, so the whole suite passed identically before and after. A default with no test is
/// indistinguishable from no default — the D63 shape, one layer up.
/// </para>
/// </summary>
public class FrameworkDefaultsTests
{
    private static ShenoraApplication BuildBare(TempDir root) =>
        ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "probe",
            Paths = new ShenoraPathsOptions { ExplicitRoot = root.Root },
        }).Build();

    /// <summary>The claim, stated as bluntly as it can be: call nothing, get the framework.</summary>
    [Fact]
    public void An_app_that_calls_NOTHING_still_gets_every_portable_engine()
    {
        using var root = TempDir.Create();
        using var app = BuildBare(root);

        Assert.NotNull(app.Services.GetService<IMissionScheduler>());
        Assert.NotNull(app.Services.GetService<IFileUpdateQueue>());
        Assert.NotNull(app.Services.GetService<IMediaPlayer>());
    }

    /// <summary>
    /// 🔴 Crash recovery must be reachable from what the framework REGISTERS, which is the interface.
    /// The journal is on by default, and a journal nobody replays is a directory that fills up while
    /// interrupted updates stay un-rolled-back — so the documented startup call has to compile from a
    /// resolved <see cref="IFileUpdateQueue"/>. It did not: `RecoverAsync` lived only on the concrete
    /// type, which is why the guide's own example hand-constructed a queue and side-stepped DI. A
    /// downcast is not a fix either — it fails SILENTLY the moment an app registers its own queue,
    /// which `UseFileSystem` explicitly invites.
    /// </summary>
    [Fact]
    public async Task Crash_recovery_is_callable_through_the_REGISTERED_interface()
    {
        using var root = TempDir.Create();
        using var app = BuildBare(root);

        var queue = app.Services.GetRequiredService<IFileUpdateQueue>();

        // Nothing was interrupted, so the honest answer is zero — the point is that it COMPILES and runs
        // against the interface, with no cast to FileUpdateQueue anywhere.
        Assert.Equal(0, await queue.RecoverAsync());
    }

    /// <summary>
    /// 🔴 The PRECONDITION that makes defaulting safe, and the one worth guarding hardest: registration
    /// must touch no disk. `Paths.DataArea` CREATES the directory it names, so an engine that provisioned
    /// storage merely by being registered would give every app a `journal/` and a `locks/` folder it never
    /// asked for — which is precisely what made "on by default" impossible before.
    /// </summary>
    [Fact]
    public void Building_the_app_provisions_NOTHING_on_disk()
    {
        using var root = TempDir.Create();
        using var app = BuildBare(root);

        var stray = Directory.Exists(root.Root)
            ? Directory.GetDirectories(root.Root, "*", SearchOption.AllDirectories)
            : [];
        Assert.DoesNotContain(stray, d => Path.GetFileName(d) is "journal" or "locks" or "media");
    }

    /// <summary>
    /// The override, which is the other half of D64: <c>Use…</c> CONFIGURES, it does not enable. An
    /// explicit call registers first and the default is <c>TryAdd</c>, so the app's options survive.
    /// ⚠ If this ever fails, the defaults have been moved into the builder's CONSTRUCTOR — which would
    /// register before any app call and silently discard every configuration the app makes.
    /// </summary>
    [Fact]
    public void An_explicit_Use_call_still_WINS_over_the_default()
    {
        using var root = TempDir.Create();
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "probe",
            Paths = new ShenoraPathsOptions { ExplicitRoot = root.Root },
        });

        builder.UseMissions(x => x.GlobalLaneCapacity = 7);
        using var app = builder.Build();

        Assert.Equal(7, app.Services.GetRequiredService<MissionSchedulerOptions>().GlobalLaneCapacity);
    }

    /// <summary>
    /// 🔴 <b>The kit's own media conversion claims its source PATH, so the scheduler the kit registers must
    /// understand path claims.</b> It did not: <c>Scopes</c> defaulted to empty, so every conversion behind
    /// <c>app.UseMediaPlayer()</c> threw at submit and the page was answered 503 forever. Every sample
    /// registered the scope by hand, which is why none of them saw it.
    /// </summary>
    [Fact]
    public async Task The_DEFAULT_scheduler_accepts_a_PATH_claim()
    {
        using var root = TempDir.Create();
        using var app = BuildBare(root);

        var result = await Submit(app, PathClaims.Exclusive(root.Root));

        Assert.Equal(MissionOutcome.Completed, result.Outcome);
    }

    /// <summary>
    /// An app that names its OWN scopes keeps them, and the path scope is added beside them rather than
    /// lost — the media route claims a path whatever else the app registered.
    /// </summary>
    [Fact]
    public async Task An_app_s_own_scopes_keep_the_PATH_scope_beside_them()
    {
        using var root = TempDir.Create();
        var builder = Builder(root);
        builder.UseMissions(x => x.Scopes = [new FlatClaimScope("entity")]);
        using var app = builder.Build();

        var result = await Submit(app, PathClaims.Exclusive(root.Root), MissionClaim.Exclusive("entity", "42"));

        Assert.Equal(MissionOutcome.Completed, result.Outcome);
    }

    /// <summary>An app's own scope NAMED <c>path</c> is the one used — the kit adds its own only when absent.</summary>
    [Fact]
    public void An_app_s_own_scope_named_path_is_KEPT()
    {
        using var root = TempDir.Create();
        var mine = new FlatClaimScope(PathClaims.ScopeName);
        var builder = Builder(root);
        builder.UseMissions(x => x.Scopes = [mine]);
        using var app = builder.Build();

        _ = app.Services.GetRequiredService<IMissionScheduler>();

        Assert.Same(mine, Assert.Single(app.Services.GetRequiredService<MissionSchedulerOptions>().Scopes));
    }

    /// <summary>
    /// The scope is added to the RESOLVED options, so options an app registered itself — without
    /// <c>UseMissions</c> — get it too.
    /// </summary>
    [Fact]
    public async Task Options_registered_DIRECTLY_get_the_PATH_scope_too()
    {
        using var root = TempDir.Create();
        var builder = Builder(root);
        builder.Services.AddSingleton(new MissionSchedulerOptions { GlobalLaneCapacity = 1 });
        using var app = builder.Build();

        var result = await Submit(app, PathClaims.Exclusive(root.Root));

        Assert.Equal(MissionOutcome.Completed, result.Outcome);
    }

    private static ShenoraApplicationBuilder Builder(TempDir root) =>
        ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "probe",
            Paths = new ShenoraPathsOptions { ExplicitRoot = root.Root },
        });

    private static Task<MissionResult> Submit(ShenoraApplication app, params MissionClaim[] claims) =>
        app.Services.GetRequiredService<IMissionScheduler>().SubmitAsync(new MissionDefinition
        {
            Kind = "probe",
            Claims = claims,
            Run = (_, _) => Task.CompletedTask,
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>
    /// The same for the media player, whose options carry the one thing the kit refuses to choose — the
    /// containment boundary. A default registration must never widen it.
    /// </summary>
    [Fact]
    public void The_default_media_player_names_NO_allowed_roots()
    {
        using var root = TempDir.Create();
        using var app = BuildBare(root);

        Assert.Empty(app.Services.GetRequiredService<MediaPlayerOptions>().Access.AllowedRoots);
    }

    /// <summary>
    /// 🔴 <b>THE WHOLE D64+D65 CLAIM, end to end: an app that composes IPC and calls nothing else can
    /// receive the page's player report.</b> Build the app, ask the dispatcher, and the media feature's
    /// own module answers — nobody registered a facade, named a module, or wrote a route.
    /// <para>
    /// ⚠ It asserts the route is HANDLED, not that it succeeds: the point is that the module EXISTS.
    /// <c>NO_HANDLER</c> is what a lost registration looks like, and it is the failure this pins —
    /// the facade's own tests map it by hand, so they would all still pass with the default gone.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_media_module_answers_through_the_DEFAULT_wiring_with_no_app_registration()
    {
        using var root = TempDir.Create();
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "probe",
            Paths = new ShenoraPathsOptions { ExplicitRoot = root.Root },
        });
        builder.Services.UseMessageDispatcher();
        using var app = builder.Build();

        var response = await app.Services.GetRequiredService<IMessageDispatcher>().DispatchAsync(
            new IpcRequest
            {
                Id = "r1",
                Module = app.Services.GetRequiredService<MediaPlayerOptions>().Access.Module,
                Type = MediaPlayerModule.ReportType,
                Payload = JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(new { state = "Paused", position = 0.0 }, IpcJson.Options)),
            },
            CancellationToken.None);

        Assert.True(response.Success, response.Error?.Code);
    }
}
