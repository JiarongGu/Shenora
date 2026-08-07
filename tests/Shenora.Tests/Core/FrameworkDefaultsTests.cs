using Microsoft.Extensions.DependencyInjection;
using Shenora;
using Shenora.IO;
using Shenora.Media;
using Shenora.Missions;

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
    private static ShenoraApplication BuildBare(TempRoot root) =>
        ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "probe",
            Paths = new ShenoraPathsOptions { ExplicitRoot = root.Path },
        }).Build();

    /// <summary>The claim, stated as bluntly as it can be: call nothing, get the framework.</summary>
    [Fact]
    public void An_app_that_calls_NOTHING_still_gets_every_portable_engine()
    {
        using var root = new TempRoot();
        using var app = BuildBare(root);

        Assert.NotNull(app.Services.GetService<IMissionScheduler>());
        Assert.NotNull(app.Services.GetService<IFileUpdateQueue>());
        Assert.NotNull(app.Services.GetService<IMediaPlayer>());
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
        using var root = new TempRoot();
        using var app = BuildBare(root);

        var stray = Directory.Exists(root.Path)
            ? Directory.GetDirectories(root.Path, "*", SearchOption.AllDirectories)
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
        using var root = new TempRoot();
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "probe",
            Paths = new ShenoraPathsOptions { ExplicitRoot = root.Path },
        });

        builder.UseMissions(x => x.GlobalLaneCapacity = 7);
        using var app = builder.Build();

        Assert.Equal(7, app.Services.GetRequiredService<MissionSchedulerOptions>().GlobalLaneCapacity);
    }

    /// <summary>
    /// The same for the media player, whose options carry the one thing the kit refuses to choose — the
    /// containment boundary. A default registration must never widen it.
    /// </summary>
    [Fact]
    public void The_default_media_player_names_NO_allowed_roots()
    {
        using var root = new TempRoot();
        using var app = BuildBare(root);

        Assert.Empty(app.Services.GetRequiredService<MediaPlayerOptions>().AllowedRoots);
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"shenora-d64-{Guid.NewGuid():N}");

        public TempRoot() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
