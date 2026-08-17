using Microsoft.Extensions.Logging;
using Shenora.Core.Ipc;

namespace Shenora.Modules.Media;

/// <summary>
/// The page's half of <see cref="IMediaPlayer"/>: the route that carries an element's state back to the
/// host, turning it into <see cref="MediaPlayer.Report"/> — the only thing
/// <see cref="IMediaPlayer.OpenAsync"/> completes on.
/// <para>
/// <b>⚠ It carries the UNIT CONVERSION.</b> The page reports SECONDS as plain numbers because that is what
/// a media element exposes (<c>currentTime</c>, <c>duration</c>); <see cref="MediaPlayerStatus"/> is
/// <see cref="TimeSpan"/>, and one does not deserialize into the other.
/// </para>
/// <para>
/// <b>Registered by DEFAULT</b> — <c>ShenoraApplicationBuilder.Build()</c> calls <c>UseMediaPlayer()</c>
/// itself (D64), and it costs nothing until the page posts. It answers on
/// <see cref="MediaAccessOptions.Module"/> (<see cref="MediaPlayerOptions.Access"/>) — <c>SHENORA.MEDIA</c>
/// by default, a RESERVED prefix, so an app remains free to own a module called plainly <c>MEDIA</c>.
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

    private readonly IMediaPlayer? _player;
    private readonly MediaPlayerOptions _options;

    /// <param name="player">The host's player, or <c>null</c>: this module is registered by DEFAULT and
    /// cannot assume anyone called <c>ShenoraApplicationBuilder.Build()</c> — a bare
    /// <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/> composing IPC alone is a
    /// legitimate shape.</param>
    /// <param name="options">Read for <see cref="MediaAccessOptions.Module"/> off
    /// <see cref="MediaPlayerOptions.Access"/> — the same object the emitter reads, so the two cannot drift.</param>
    /// <param name="logger">Optional.</param>
    public MediaPlayerModule(IMediaPlayer? player, MediaPlayerOptions options, ILogger<MediaPlayerModule>? logger = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _player = player;
        _options = options;
    }

    /// <inheritdoc />
    public override string ModuleName => _options.Access.Module;

    /// <inheritdoc />
    protected override Task<object?> RouteMessageAsync(
        IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        // The page DRIVING the host's player, on the same verbs as `MediaPlayerEvents` — the CHANNEL is the
        // direction. An EVENT named PLAYER_PLAY drives the page's element; a REQUEST drives the HOST's.
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
        }

        if (!string.Equals(request.Type, ReportType, StringComparison.OrdinalIgnoreCase))
            throw UnknownType(request);

        // ⚠ Only a MediaPlayer takes reports: a shell's NATIVE player (AVPlayer, ExoPlayer) is its own
        // clock and has no Report to call, and there may be no player registered at all. Ignored rather
        // than thrown — the page is describing an element nothing is driving, which is benign.
        if (_player is not MediaPlayer page) return Done();

        page.Report(new MediaPlayerStatus
        {
            State = ParseState(PayloadHelper.GetRequiredValue<string>(request.Payload, "state")),
            // SECONDS on the wire. Guarded against a non-finite number, which `duration` legitimately is
            // for a live stream before metadata lands.
            Position = Seconds(PayloadHelper.GetOptionalValue<double>(request.Payload, "position")) ?? TimeSpan.Zero,
            Duration = Seconds(PayloadHelper.GetOptionalValue<double?>(request.Payload, "duration")),
            Error = PayloadHelper.GetOptionalValue<string>(request.Payload, "error"),
        });
        return Done();
    }

    /// <summary>
    /// The wire's state name. ⚠ An UNKNOWN one becomes <see cref="MediaPlayerState.Failed"/> rather than
    /// throwing, because <c>OpenAsync</c> is waiting for exactly this message and would otherwise hang.
    /// </summary>
    private static MediaPlayerState ParseState(string state) =>
        Enum.TryParse<MediaPlayerState>(state, ignoreCase: true, out var parsed) ? parsed : MediaPlayerState.Failed;

    private static TimeSpan? Seconds(double? value) =>
        value is { } seconds && double.IsFinite(seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>Run one drive command and answer with the resulting status, so a page never has to follow
    /// a command with a separate query.</summary>
    private async Task<object?> Drive(Func<IMediaPlayer, Task> command)
    {
        var player = RequirePlayer();
        await command(player).ConfigureAwait(false);
        return Wire(player.Status);
    }

    /// <summary>
    /// ⚠ A drive request with no player THROWS, where a <see cref="ReportType"/> with no player is ignored:
    /// the page is WAITING for a drive command to happen, so answering "fine" while doing nothing leaves it
    /// waiting forever with no error to act on.
    /// </summary>
    private IMediaPlayer RequirePlayer() =>
        _player ?? throw new ShenoraException(
            "MEDIA_PLAYER_UNAVAILABLE",
            message: "This shell registers no IMediaPlayer, so the host cannot drive playback.");

    /// <summary>The source to open. Only <c>uri</c> is required; the page rarely knows the rest.</summary>
    private static MediaSource Source(IpcRequest request) => new()
    {
        Uri = PayloadHelper.GetRequiredValue<string>(request.Payload, "uri"),
    };

    /// <summary>
    /// Status as the page reads it: SECONDS, not <see cref="TimeSpan"/>, matching what a media element
    /// exposes and what <see cref="ReportType"/> already carries in the other direction.
    /// </summary>
    private static object Wire(MediaPlayerStatus status) => new
    {
        state = status.State.ToString(),
        position = status.Position.TotalSeconds,
        duration = status.Duration?.TotalSeconds,
        rate = status.Rate,
        error = status.Error,
    };
}
