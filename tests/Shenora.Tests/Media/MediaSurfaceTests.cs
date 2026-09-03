using System.Text.Json;
using Shenora;
using Shenora.Core.Ipc;
using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// The SECOND surface of the one media-play layer (D58) — the shell drawing the picture under a
/// transparent region the page leaves.
/// <para>
/// 🔴 <b>Every test here supplies a fake and asserts it was USED</b> (D63). An unconsulted surface is
/// indistinguishable from a working one: the picture is simply absent, which is also what a player with no
/// video track looks like, so "the route returned OK" proves nothing at all.
/// </para>
/// </summary>
public class MediaSurfaceTests
{
    /// <summary>
    /// The page's rectangle reaches the shell UNCHANGED — no device-pixel-ratio scaling anywhere on the
    /// path. ⚠ A shell that "helpfully" converts draws the picture several times too big on a phone, and
    /// the numbers here are the only place that is stated as a fact rather than a comment.
    /// </summary>
    [Fact]
    public async Task A_SHOW_hands_the_shell_the_pages_own_rectangle()
    {
        var surface = new FakeSurface();

        var response = await DispatchAsync(surface, MediaPlayerModule.SurfaceShowType,
            new { x = 12.5, y = 40.0, width = 320.0, height = 180.0, onTop = true });

        Assert.True(response.Success, response.Error?.Code);
        var shown = Assert.Single(surface.Shown);
        Assert.Equal(new MediaSurfaceRegion(12.5, 40, 320, 180, OnTop: true), shown);
        Assert.Equal(0, surface.Hidden);
    }

    /// <summary>Behind the webview is the DEFAULT, because that is the order that lets the page paint over
    /// the picture — captions and immersive chrome both need it.</summary>
    [Fact]
    public async Task OnTop_defaults_to_behind_the_page()
    {
        var surface = new FakeSurface();

        await DispatchAsync(surface, MediaPlayerModule.SurfaceShowType,
            new { x = 0.0, y = 0.0, width = 320.0, height = 180.0 });

        Assert.False(Assert.Single(surface.Shown).OnTop);
    }

    /// <summary>
    /// 🔴 A page reports an empty rectangle whenever its stage is unmounted or has a <c>display:none</c>
    /// ancestor, and that must reach the shell as HIDE.
    /// <para>
    /// ⚠ Showing it instead is not a no-op: a 0×0 surface is drawn AT THE ORIGIN, which is a visible
    /// artefact in the top-left corner rather than nothing. The rule lives in the module so that no shell
    /// has to remember it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(320, 0)]
    [InlineData(0, 180)]
    [InlineData(1, 1)]   // below MinimumSide, which is where the boundary actually is
    public async Task A_region_too_small_to_draw_becomes_HIDE(double width, double height)
    {
        var surface = new FakeSurface();

        await DispatchAsync(surface, MediaPlayerModule.SurfaceShowType,
            new { x = 0.0, y = 0.0, width, height });

        Assert.Empty(surface.Shown);
        Assert.Equal(1, surface.Hidden);
    }

    /// <summary>The boundary from the other side, so the test above cannot pass by hiding everything.</summary>
    [Fact]
    public async Task A_region_at_the_minimum_side_still_draws()
    {
        var surface = new FakeSurface();

        await DispatchAsync(surface, MediaPlayerModule.SurfaceShowType, new
        {
            x = 0.0,
            y = 0.0,
            width = MediaSurfaceRegion.MinimumSide,
            height = MediaSurfaceRegion.MinimumSide,
        });

        Assert.Single(surface.Shown);
        Assert.Equal(0, surface.Hidden);
    }

    [Fact]
    public async Task A_HIDE_reaches_the_shell()
    {
        var surface = new FakeSurface();

        var response = await DispatchAsync(surface, MediaPlayerModule.SurfaceHideType, new { });

        Assert.True(response.Success, response.Error?.Code);
        Assert.Equal(1, surface.Hidden);
        Assert.Empty(surface.Shown);
    }

    /// <summary>
    /// 🔴 A shell with no surface REFUSES, and this is the same asymmetry <c>RequirePlayer</c> already has.
    /// A page that positions a picture and is told "fine" draws its whole control layer over nothing, with
    /// no error to branch on — which is the failure the <see cref="ShellCapability.MediaSurface"/>
    /// capability exists to let it avoid in the first place.
    /// </summary>
    [Fact]
    public async Task A_shell_with_no_surface_REFUSES_rather_than_succeeding_silently()
    {
        var response = await DispatchAsync(surface: null, MediaPlayerModule.SurfaceShowType,
            new { x = 0.0, y = 0.0, width = 320.0, height = 180.0 });

        Assert.False(response.Success);
        Assert.Equal("MEDIA_SURFACE_UNAVAILABLE", response.Error?.Code);
    }

    /// <summary>
    /// The platform handle reaches the player's platform half, and DETACHING passes null.
    /// <para>
    /// ⚠ The null leg is the one that matters: a player still holding a destroyed surface draws into a
    /// released buffer, which on some Android devices is a native crash rather than a blank view.
    /// </para>
    /// </summary>
    [Fact]
    public void AttachSurface_reaches_the_platform_half_in_both_directions()
    {
        using var player = new SurfacePlayer();
        var handle = new object();

        player.AttachSurface(handle);
        player.AttachSurface(null);

        Assert.Equal(new object?[] { handle, null }, player.Attached);
    }

    /// <summary>A player that does not draw pictures ignores the seam, so an audio-only shell pays nothing
    /// for having it — the default must not throw.</summary>
    [Fact]
    public void A_player_that_does_not_override_the_seam_accepts_a_surface_and_ignores_it()
    {
        using var player = new AudioOnlyPlayer();

        player.AttachSurface(new object());   // must not throw
    }

    private static async Task<IpcResponse> DispatchAsync(IMediaSurface? surface, string type, object payload)
    {
        var options = new MediaPlayerOptions();
        var dispatcher = new MessageDispatcher();
        dispatcher.MapModule(new MediaPlayerModule(player: null, options, logger: null, surface));
        return await dispatcher.DispatchAsync(new IpcRequest
        {
            Id = "r1",
            Module = options.Access.Module,
            Type = type,
            Payload = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(payload, IpcJson.Options)),
        }, CancellationToken.None);
    }

    /// <summary>A shell surface that records what it was told, so a test can assert it was CONSULTED.</summary>
    private sealed class FakeSurface : IMediaSurface
    {
        public List<MediaSurfaceRegion> Shown { get; } = [];
        public int Hidden { get; private set; }

        public void Show(MediaSurfaceRegion region) => Shown.Add(region);
        public void Hide() => Hidden++;
    }

    /// <summary>A player whose platform half records the handles it is given.</summary>
    private sealed class SurfacePlayer : AudioOnlyPlayer
    {
        public List<object?> Attached { get; } = [];

        protected override void AttachSurfaceCore(object? surface) => Attached.Add(surface);
    }

    /// <summary>The minimum a <see cref="MediaPlayerBase"/> must supply — no picture anywhere in it.</summary>
    private class AudioOnlyPlayer : MediaPlayerBase
    {
        protected override TimeSpan PositionCore => TimeSpan.Zero;
        protected override TimeSpan? DurationCore => null;
        protected override void OpenCore(MediaSource source, Uri uri) { }
        protected override void ApplyStartAt(TimeSpan position) { }
        protected override void PlayCore(double rate) { }
        protected override void PauseCore() { }
        protected override Task SeekCore(TimeSpan position) => Task.CompletedTask;
        protected override void ApplyRateCore(double rate) { }
        protected override void TeardownCore() { }
    }
}
