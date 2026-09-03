using Microsoft.Extensions.Logging;
using Shenora.Core.Ipc;

namespace Shenora.Modules.Media;

/// <summary>
/// The page's half of <see cref="IMediaPlayer"/>: the route that carries an element's state back to the
/// host, turning it into <see cref="MediaPlayer.Report"/> — the only thing
/// <see cref="IMediaPlayer.OpenAsync"/> completes on.
/// <para>
/// <b>⚠ It carries the UNIT CONVERSION.</b> The page reports SECONDS as plain numbers, as a media element
/// exposes them (<c>currentTime</c>, <c>duration</c>); <see cref="MediaPlayerStatus"/> is
/// <see cref="TimeSpan"/>, and one does not deserialize into the other.
/// </para>
/// <para>
/// <b>Registered by DEFAULT</b> — <c>ShenoraApplicationBuilder.Build()</c> calls <c>UseMediaPlayer()</c>
/// itself (D64), and it costs nothing until the page posts. It answers on
/// <see cref="MediaAccessOptions.Module"/> — <c>SHENORA.MEDIA</c> by default, a RESERVED prefix, so an app
/// remains free to own a module called plainly <c>MEDIA</c>.
/// </para>
/// </summary>
public sealed class MediaPlayerModule : ModuleBase
{
    /// <summary>Route: the page describing what its element is doing. Payload
    /// <c>{ state, position, duration, error? }</c>, positions in SECONDS.</summary>
    public const string ReportType = "PLAYER_REPORT";

    /// <summary>Route: what is the host's player doing right now? No payload; answers like a drive
    /// command does.</summary>
    public const string StatusType = "PLAYER_STATUS";

    /// <summary>Route: put the shell's picture here. Payload <c>{ x, y, width, height, onTop? }</c> in CSS
    /// pixels — see <see cref="MediaSurfaceRegion"/>. Answers nothing; a page repositions per scroll frame.</summary>
    public const string SurfaceShowType = "SURFACE_SHOW";

    /// <summary>Route: take the shell's picture off screen. No payload. Does not stop playback.</summary>
    public const string SurfaceHideType = "SURFACE_HIDE";

    private readonly IMediaPlayer? _player;
    private readonly IMediaSurface? _surface;
    private readonly MediaPlayerOptions _options;

    /// <param name="player">The host's player, or <c>null</c>: this module is registered by DEFAULT, and a
    /// bare <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/> composing IPC alone
    /// registers no player.</param>
    /// <param name="options">Read for <see cref="MediaAccessOptions.Module"/> — the same object the emitter
    /// reads, so the two cannot drift.</param>
    /// <param name="logger">Optional.</param>
    /// <param name="surface">The shell's picture surface, or <c>null</c> on a shell that has none — the
    /// desktop, where the page's own element is already the picture. Absent makes the two surface routes
    /// answer <c>MEDIA_SURFACE_UNAVAILABLE</c> rather than succeeding silently.</param>
    public MediaPlayerModule(IMediaPlayer? player, MediaPlayerOptions options,
        ILogger<MediaPlayerModule>? logger = null, IMediaSurface? surface = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _player = player;
        _options = options;
        _surface = surface;
    }

    /// <inheritdoc />
    public override string ModuleName => _options.Access.Module;

    /// <inheritdoc />
    protected override Task<object?> RouteMessageAsync(
        IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        // The CHANNEL is the direction: an EVENT named PLAYER_PLAY drives the page's element, a REQUEST on
        // the same verb drives the HOST's player.
        switch (request.Type.ToUpperInvariant())
        {
            case MediaPlayerEvents.Load: return Drive(p => p.OpenAsync(Source(request), cancellationToken));
            case MediaPlayerEvents.Play: return Drive(p => p.PlayAsync(cancellationToken));
            case MediaPlayerEvents.Pause: return Drive(p => p.PauseAsync(cancellationToken));
            case MediaPlayerEvents.Unload: return Drive(p => p.CloseAsync(cancellationToken));
            case MediaPlayerEvents.Seek:
                var position = Seconds(PayloadHelper.GetRequiredValue<double>(request.Payload, "position"))
                               ?? TimeSpan.Zero;
                return Drive(p => p.SeekAsync(position, cancellationToken));
            case MediaPlayerEvents.Rate:
                var rate = PayloadHelper.GetRequiredValue<double>(request.Payload, "rate");
                return Drive(p => p.SetRateAsync(rate, cancellationToken));
            case StatusType:
                return Task.FromResult<object?>(Wire(RequirePlayer().Status));

            // 🔴 The zero-area rule lives HERE, not in each shell: a page reports an empty rectangle
            // whenever its stage is unmounted or hidden, and every shell would otherwise have to remember
            // that a 0×0 surface at the origin is a visible artefact rather than nothing.
            case SurfaceShowType:
            {
                var region = Region(request);
                if (region.IsDrawable) RequireSurface().Show(region);
                else RequireSurface().Hide();
                return Done();
            }

            case SurfaceHideType:
                RequireSurface().Hide();
                return Done();
        }

        if (!string.Equals(request.Type, ReportType, StringComparison.OrdinalIgnoreCase))
            throw UnknownType(request);

        // ⚠ Only a MediaPlayer takes reports: a shell's NATIVE player is its own clock and has none.
        // Ignored rather than thrown — the page is describing an element nothing is driving.
        if (_player is not MediaPlayer page) return Done();

        page.Report(new MediaPlayerStatus
        {
            State = ParseState(PayloadHelper.GetRequiredValue<string>(request.Payload, "state")),
            // SECONDS on the wire, guarded against a non-finite number — which `duration` legitimately is
            // for a live stream before metadata lands.
            Position = Seconds(PayloadHelper.GetOptionalValue<double>(request.Payload, "position")) ?? TimeSpan.Zero,
            Duration = Seconds(PayloadHelper.GetOptionalValue<double?>(request.Payload, "duration")),
            Error = PayloadHelper.GetOptionalValue<string>(request.Payload, "error"),
        });
        return Done();
    }

    /// <summary>The wire's state name. ⚠ An UNKNOWN one becomes <see cref="MediaPlayerState.Failed"/> rather
    /// than throwing, because <c>OpenAsync</c> is waiting for exactly this message and would hang.</summary>
    private static MediaPlayerState ParseState(string state) =>
        Enum.TryParse<MediaPlayerState>(state, ignoreCase: true, out var parsed) ? parsed : MediaPlayerState.Failed;

    private static TimeSpan? Seconds(double? value) =>
        value is { } seconds && double.IsFinite(seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>Run one drive command and answer with the resulting status, so a page never follows a
    /// command with a separate query.</summary>
    private async Task<object?> Drive(Func<IMediaPlayer, Task> command)
    {
        var player = RequirePlayer();
        await command(player).ConfigureAwait(false);
        return Wire(player.Status);
    }

    /// <summary>⚠ A drive request with no player THROWS, where a <see cref="ReportType"/> with no player is
    /// ignored: the page is WAITING for the command, so answering "fine" while doing nothing leaves it
    /// waiting forever with no error to act on.</summary>
    private IMediaPlayer RequirePlayer() =>
        _player ?? throw new ShenoraException(
            "MEDIA_PLAYER_UNAVAILABLE",
            message: "This shell registers no IMediaPlayer, so the host cannot drive playback.");

    /// <summary>⚠ Same reasoning as <see cref="RequirePlayer"/>: a page that positions a picture and is
    /// answered "fine" by a shell with no surface draws its controls over nothing, with no error to act on.</summary>
    private IMediaSurface RequireSurface() =>
        _surface ?? throw new ShenoraException(
            "MEDIA_SURFACE_UNAVAILABLE",
            message: "This shell registers no IMediaSurface, so the host cannot draw a picture.");

    /// <summary>Where the page wants the picture. Every side defaults to 0, which
    /// <see cref="MediaSurfaceRegion.IsDrawable"/> then reads as "nothing to show".</summary>
    private static MediaSurfaceRegion Region(IpcRequest request) => new(
        PayloadHelper.GetOptionalValue<double?>(request.Payload, "x") ?? 0,
        PayloadHelper.GetOptionalValue<double?>(request.Payload, "y") ?? 0,
        PayloadHelper.GetOptionalValue<double?>(request.Payload, "width") ?? 0,
        PayloadHelper.GetOptionalValue<double?>(request.Payload, "height") ?? 0,
        PayloadHelper.GetOptionalValue<bool?>(request.Payload, "onTop") ?? false);

    /// <summary>The source to open. Only <c>uri</c> is required; the page rarely knows the rest.</summary>
    private static MediaSource Source(IpcRequest request) => new()
    {
        Uri = PayloadHelper.GetRequiredValue<string>(request.Payload, "uri"),
    };

    /// <summary>Status as the page reads it: SECONDS, not <see cref="TimeSpan"/>, matching what
    /// <see cref="ReportType"/> carries in the other direction.</summary>
    private static object Wire(MediaPlayerStatus status) => new
    {
        state = status.State.ToString(),
        position = status.Position.TotalSeconds,
        duration = status.Duration?.TotalSeconds,
        rate = status.Rate,
        error = status.Error,
    };
}
