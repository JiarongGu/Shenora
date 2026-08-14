using Microsoft.Extensions.DependencyInjection;
using Shenora;
using Shenora.Core.Ipc;
using Shenora.Core.WebView;
using Shenora.Modules.Media;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Media;

/// <summary>
/// <c>UseMediaPlayer</c>'s two phases — REGISTER at builder time, MOUNT once the webview exists.
/// <para>
/// 🔴 <b>Written because a coverage run said so, not because reading found a bug.</b>
/// <see cref="MediaPlayerExtensions"/> sat at 53 % line coverage — the lowest in the whole portable
/// framework — and the uncovered half was the whole of the mount phase plus the cache-root defaulting.
/// <see cref="MediaPlayerReportingTests"/> already pins <c>ReportTo</c> thoroughly, so what was missing
/// was exactly the wiring an ADOPTER writes. That is the same shape as the <c>UseFiles</c> middleware and
/// <c>IpcRequestsModule</c> gaps found the same way: the PIECES were tested and the composition was not.
/// </para>
/// <para>
/// ⚠ All of these passed on their first run. They are here so a future edit cannot quietly break them —
/// each failure below is invisible at the call site, which is the argument for pinning rather than
/// evidence anything was wrong.
/// </para>
/// </summary>
public class MediaPlayerCompositionTests
{
    private static ShenoraApplicationBuilder Builder(TempDir root) =>
        ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "probe",
            Paths = new ShenoraPathsOptions { ExplicitRoot = root.Root },
        });

    /// <summary>A plan carrying only the field the URL convention reads.</summary>
    private static MediaPlaybackPlan Plan(MediaPlaybackAction action) =>
        new(action, [], ContainerOpens: true, Reason: "test");

    private static MediaAccessOptions Access(TempDir root, string cacheRoot = "") => new()
    {
        Resolve = static _ => null,
        AllowedRoots = [root.Root],
        CacheRoot = cacheRoot,
    };

    /// <summary>
    /// 🔴 <b>The documented reason <c>TryAddEnumerable</c> names its implementation type.</b> Registering
    /// the same <see cref="IIpcModule"/> twice is a DUPLICATE the dispatcher rejects — so a second
    /// <c>UseMediaPlayer</c> call (two windows, a re-entrant composition root, a library that also calls
    /// it) would fail the app at startup rather than no-op.
    /// <para>
    /// ⚠ The failure is at BUILD time and names a type, not a call site, so nothing points at the second
    /// call. That is what makes it worth a test rather than a comment.
    /// </para>
    /// </summary>
    [Fact]
    public void Calling_UseMediaPlayer_TWICE_still_registers_exactly_ONE_module()
    {
        using var root = TempDir.Create();
        var builder = Builder(root);

        builder.UseMediaPlayer();
        builder.UseMediaPlayer();
        using var app = builder.Build();

        Assert.Single(app.Services.GetServices<IIpcModule>().OfType<MediaPlayerModule>());
    }

    /// <summary>
    /// The mount is <b>a no-op returning null when no roots were allowed</b>, which is what makes
    /// <c>app.UseMediaPlayer()</c> safe to call unconditionally — the zero-configuration case D64 is built
    /// on. Inverted, every app that never asked for conversion would mount a route that answers for URLs
    /// it was never given a containment boundary for.
    /// </summary>
    [Fact]
    public void The_MOUNT_declines_when_the_app_named_no_allowed_roots()
    {
        using var root = TempDir.Create();
        var builder = Builder(root);
        builder.UseMediaPlayer();
        using var app = builder.Build();

        Assert.Null(new FakeInterceptor().UseMediaPlayer(app.Services));
    }

    /// <summary>
    /// 🔴 <b>THE DOCUMENTED ADOPTION SNIPPET, EXECUTED — and it used to THROW.</b> This is
    /// <c>docs/guides/media.md</c>'s copy-pasteable block verbatim: name the roots, leave
    /// <see cref="MediaAccessOptions.CacheRoot"/> blank because the guide says <c>""</c> means "let
    /// <c>UseMediaPlayer</c> default it", build, mount.
    /// <para>
    /// Until 2026-08-14 the default was applied ONLY inside the <see cref="IMediaPlayer"/> factory, while
    /// the mount reads <see cref="MediaPlayerOptions"/> directly and hands the blank cache root to
    /// <see cref="MediaConversionExtensions.UseMediaConversion"/>, which rejects it — so an adopter
    /// following the guide got <c>ArgumentException: options.Access.CacheRoot</c> at startup, naming the
    /// option they had deliberately left blank.
    /// </para>
    /// <para>
    /// ⚠ <b>Why nothing caught it, which is the part worth keeping:</b> the desktop sample never names
    /// <c>AllowedRoots</c>, so its <c>app.UseMediaPlayer()</c> returns null before reaching any of this;
    /// the MAUI sample mounts <c>UseMediaConversion</c>/<c>UseComputedRemux</c> directly with an explicit
    /// cache root. <b>No sample, no test and no device run had ever executed the configured path.</b> It
    /// was found by a coverage run, not by reading.
    /// </para>
    /// <para>
    /// ⚠ Order-dependence measured, because "it works for me" was the likeliest objection: resolving
    /// <see cref="IMediaPlayer"/> first HIDES it, and composing IPC does NOT (the dispatcher resolves its
    /// modules lazily). So it fired for exactly the apps that followed the guide and nothing else.
    /// </para>
    /// </summary>
    [Fact]
    public void The_DOCUMENTED_snippet_mounts_a_route_with_no_cache_root_of_its_own()
    {
        using var root = TempDir.Create();
        var builder = Builder(root);
        builder.UseMediaPlayer(x => x.Access = Access(root));
        using var app = builder.Build();

        using var mounted = new FakeInterceptor().UseMediaPlayer(app.Services);

        Assert.NotNull(mounted);
        // …and the mount is what filled it in, since nothing here ever resolved IMediaPlayer.
        Assert.NotEqual(string.Empty, app.Services.GetRequiredService<MediaPlayerOptions>().Access.CacheRoot);
    }

    /// <summary>
    /// The cache root is defaulted under <c>Paths.DataArea</c> — but only for an app that named roots, and
    /// only when it left <see cref="MediaAccessOptions.CacheRoot"/> blank.
    /// <para>
    /// ⚠ <b>Defaulted at RESOLVE time, not registration time (D64).</b> <c>Paths.DataArea</c> CREATES the
    /// directory it names, so doing it during <c>UseMediaPlayer</c> would give every app a <c>media/</c>
    /// folder it never asked for — the precondition <see cref="Core.FrameworkDefaultsTests"/> guards from
    /// the other side.
    /// </para>
    /// </summary>
    [Fact]
    public void A_blank_cache_root_is_defaulted_when_the_player_is_RESOLVED()
    {
        using var root = TempDir.Create();
        var builder = Builder(root);
        builder.UseMediaPlayer(x => x.Access = Access(root));
        using var app = builder.Build();

        Assert.Equal(string.Empty, app.Services.GetRequiredService<MediaPlayerOptions>().Access.CacheRoot);

        _ = app.Services.GetRequiredService<IMediaPlayer>();

        var cacheRoot = app.Services.GetRequiredService<MediaPlayerOptions>().Access.CacheRoot;
        Assert.NotEqual(string.Empty, cacheRoot);
        Assert.StartsWith(root.Root, cacheRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An app that CHOSE a cache root keeps it — the default must never overwrite a decision.</summary>
    [Fact]
    public void An_app_supplied_cache_root_survives_resolution()
    {
        using var root = TempDir.Create();
        var chosen = root.Combine("my-cache");
        var builder = Builder(root);
        builder.UseMediaPlayer(x => x.Access = Access(root, chosen));
        using var app = builder.Build();

        _ = app.Services.GetRequiredService<IMediaPlayer>();

        Assert.Equal(chosen, app.Services.GetRequiredService<MediaPlayerOptions>().Access.CacheRoot);
    }

    /// <summary>
    /// 🔴 <b>The trap the factory's own comment names: default onto the options resolved FROM DI, never
    /// onto the captured instance.</b> <c>TryAddSingleton</c> no-ops when the app registered its own
    /// <see cref="MediaPlayerOptions"/>, so a factory closing over the locally-constructed object would
    /// write the cache root onto something nothing ever reads — and the app's own options would keep the
    /// blank placeholder, silently, with the route mounting against an empty path.
    /// </summary>
    [Fact]
    public void The_default_lands_on_the_APP_s_own_options_object_when_it_registered_one()
    {
        using var root = TempDir.Create();
        var mine = new MediaPlayerOptions { Access = Access(root) };
        var builder = Builder(root);
        builder.UseMediaPlayer((_, services) => services.AddSingleton(mine));
        using var app = builder.Build();

        Assert.Same(mine, app.Services.GetRequiredService<MediaPlayerOptions>());

        _ = app.Services.GetRequiredService<IMediaPlayer>();

        Assert.NotEqual(string.Empty, mine.Access.CacheRoot);
    }

    /// <summary>
    /// <see cref="MediaPlayerOptions.ResolveUri"/> is assigned with <c>??=</c>, so an app that supplied its
    /// own URL convention keeps it. Overwriting it would point the PLAYER at URLs the mounted route does
    /// not answer — a silent 404 for every converted source, on an app that configured everything right.
    /// </summary>
    [Fact]
    public void An_app_supplied_ResolveUri_is_NOT_replaced_by_the_kit_s_convention()
    {
        using var root = TempDir.Create();
        var builder = Builder(root);
        static string Mine(string source, MediaPlaybackPlan? plan) => "mine://" + source;
        builder.UseMediaPlayer(x =>
        {
            x.Access = Access(root);
            x.ResolveUri = Mine;
        });
        using var app = builder.Build();

        using var _ = new FakeInterceptor().UseMediaPlayer(app.Services);

        Assert.Equal("mine://clip.mkv",
            app.Services.GetRequiredService<MediaPlayerOptions>().ResolveUri!("clip.mkv", null));
    }

    /// <summary>
    /// The kit's own convention, when the app supplied none: a <see cref="MediaPlaybackAction.Direct"/>
    /// plan (and no plan at all) passes the source straight through, and anything else is rewritten onto
    /// the route both halves agree on.
    /// <para>
    /// ⚠ Pinned through <c>UseMediaPlayer</c> rather than by calling <see cref="MediaPlayerRoute"/>
    /// directly, because the thing that can break is the ASSIGNMENT — a route mounted without it leaves
    /// <see cref="MediaPlayerOptions.ResolveUri"/> null and the player emits raw paths that the route
    /// never matches.
    /// </para>
    /// </summary>
    [Fact]
    public void The_MOUNT_installs_the_kit_s_URL_convention_when_the_app_supplied_none()
    {
        using var root = TempDir.Create();
        var builder = Builder(root);
        builder.UseMediaPlayer(x => x.Access = Access(root));
        using var app = builder.Build();

        using var _ = new FakeInterceptor().UseMediaPlayer(app.Services);
        var resolve = app.Services.GetRequiredService<MediaPlayerOptions>().ResolveUri;

        Assert.NotNull(resolve);
        Assert.Equal("clip.mkv", resolve("clip.mkv", null));
        Assert.Equal("clip.mkv", resolve("clip.mkv", Plan(MediaPlaybackAction.Direct)));

        var rewritten = resolve("clip.mkv", Plan(MediaPlaybackAction.Transcode));
        Assert.NotEqual("clip.mkv", rewritten);
        Assert.Equal("clip.mkv", MediaPlayerRoute.SourceOf(new Uri(new Uri("https://x.local"), rewritten).PathAndQuery));
    }
}
