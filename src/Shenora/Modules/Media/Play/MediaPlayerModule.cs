using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;
using Shenora.Core.Ipc;

namespace Shenora.Modules.Media;

/// <summary>
/// The page's half of <see cref="IMediaPlayer"/>: the route that carries an element's state back to the
/// host, turning it into <see cref="MediaPlayer.Report"/>.
///
/// <para>
/// 🔴 <b>This exists because without it the loop did not close, and the failure was SILENT.</b> The kit
/// shipped both ends and no joint: <c>useMediaPlayer</c> in <c>@shenora/react</c> posted
/// <see cref="ReportType"/>, nothing on the host answered it, and
/// <see cref="IMediaPlayer.OpenAsync"/> — which completes on the first non-<c>Opening</c> report and on
/// nothing else — simply never returned. No exception, no log line, and an element that was visibly
/// playing, so the symptom read as "my C# await hangs while the video works". Found by the 2026-08-07
/// media review; it is D63's class one layer up, and the reason D64 registers the kit's own modules
/// rather than waiting to be asked.
/// </para>
///
/// <para>
/// <b>⚠ It carries the UNIT CONVERSION, which is the part an app kept getting wrong.</b> The page reports
/// SECONDS as plain numbers because that is what a media element exposes (<c>currentTime</c>,
/// <c>duration</c>); <see cref="MediaPlayerStatus"/> is <see cref="TimeSpan"/>. Deserializing one into the
/// other does not work, so every adopter wrote this mapping — which is exactly the wiring D64 exists to
/// delete.
/// </para>
///
/// <para>
/// <b>Registered by DEFAULT</b> — <c>ShenoraApplicationBuilder.Build()</c> calls <c>UseMediaPlayer()</c>
/// itself (D64), so an app gets these routes without asking — and it costs nothing until the page posts:
/// a module nothing routes to is inert.
/// </para>
/// <para>
/// ⚠ <b>The MECHANISM matters even though the outcome does not change, because it is a rule about
/// layering.</b> This remark named <c>UseMessageDispatcher()</c> until 2026-08-09, and the IPC core
/// registers no feature: hardcoding this module there was tried and reverted, since <b>a core must not
/// know the names of the features built on it</b> (D65) — the attempt beside it
/// (<c>AddShenoraOperations</c>) broke five composition tests, because composing IPC over a bare
/// <c>ServiceCollection</c> is a legitimate shape with no builder behind it. So the FEATURE registers
/// itself and the BUILDER calls it: default-on without a core that knows every feature's name.
/// </para>
/// <para>
/// It answers on
/// <see cref="MediaAccessOptions.Module"/> (<see cref="MediaPlayerOptions.Access"/>) — <c>SHENORA.MEDIA</c> by default, a RESERVED prefix, so an
/// app remains free to own a module called plainly <c>MEDIA</c>.
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

    /// <param name="player">
    /// The host's player, or <c>null</c>.
    /// <para>
    /// ⚠ <b>Nullable because this module is registered by DEFAULT and lives in <c>Shenora.Core.Ipc</c>, which
    /// cannot assume anyone called <c>ShenoraApplicationBuilder.Build()</c>.</b> A bare
    /// <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/> composing IPC alone is a
    /// legitimate shape — the kit's own IPC tests are exactly that — and a default registration that threw
    /// on resolve would turn "the framework is on" into "the framework fell over".
    /// </para>
    /// </param>
    /// <param name="options">Read for <see cref="MediaAccessOptions.Module"/> off <see cref="MediaPlayerOptions.Access"/>, so the emitter and this
    /// listener cannot drift: both sides read the same object.</param>
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
        // ── the page DRIVING the host's player ───────────────────────────────────────────────────────
        // 🔴 THE SAME VERBS AS `MediaPlayerEvents`, AND THE CHANNEL IS THE DIRECTION. An EVENT named
        // PLAYER_PLAY is the host telling the page's element to play; a REQUEST named PLAYER_PLAY is the
        // page telling the HOST's player to play. One vocabulary, two channels — reusing the constants is
        // what stops the two halves drifting into `PLAYER_PLAY` and `PLAY_PLAYER`.
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
                return Drive(p => { p.Rate = rate; return Task.CompletedTask; });
            case StatusType:
                return Task.FromResult<object?>(Wire(RequirePlayer().Status));
        }

        if (!string.Equals(request.Type, ReportType, StringComparison.OrdinalIgnoreCase))
            throw UnknownType(request);

        // ⚠ Only a MediaPlayer takes reports, and this covers BOTH ways that can fail to hold. A shell's
        // NATIVE player (AVPlayer, ExoPlayer) is its own clock and has no Report to call; and there may be
        // no player registered at all, since this facade is a default over a container the kit did not
        // necessarily build. Either way the page is describing an element nothing is driving.
        // Ignored rather than thrown: both are benign mismatches, and failing the page's message would
        // turn a harmless configuration into an error it cannot act on.
        if (_player is not MediaPlayer page) return Done();

        page.Report(new MediaPlayerStatus
        {
            State = ParseState(PayloadHelper.GetRequiredValue<string>(request.Payload, "state")),
            // SECONDS on the wire — see the type's remarks. Guarded against a non-finite number, which
            // `duration` legitimately is for a live stream before metadata lands.
            Position = Seconds(PayloadHelper.GetOptionalValue<double>(request.Payload, "position")) ?? TimeSpan.Zero,
            Duration = Seconds(PayloadHelper.GetOptionalValue<double?>(request.Payload, "duration")),
            Error = PayloadHelper.GetOptionalValue<string>(request.Payload, "error"),
        });
        return Done();
    }

    /// <summary>
    /// The wire's state name. ⚠ An UNKNOWN one becomes <see cref="MediaPlayerState.Failed"/> rather than
    /// throwing: the page is a different codebase on a different release cadence, and a state this host
    /// does not recognise means the two halves disagree — which a player must SAY rather than hang on,
    /// since <c>OpenAsync</c> is waiting for exactly this message.
    /// </summary>
    private static MediaPlayerState ParseState(string state) =>
        Enum.TryParse<MediaPlayerState>(state, ignoreCase: true, out var parsed) ? parsed : MediaPlayerState.Failed;

    private static TimeSpan? Seconds(double? value) =>
        value is { } seconds && double.IsFinite(seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>
    /// Run one drive command and answer with the resulting status, so a page never has to follow a command
    /// with a separate query.
    /// </summary>
    private async Task<object?> Drive(Func<IMediaPlayer, Task> command)
    {
        var player = RequirePlayer();
        await command(player).ConfigureAwait(false);
        return Wire(player.Status);
    }

    /// <summary>
    /// 🔴 <b>A drive request with no player THROWS, where a report with no player is ignored — and the
    /// asymmetry is the point.</b> A <see cref="ReportType"/> for a player nothing is driving is a benign
    /// mismatch: the page is describing its own element and nobody needed to hear it. A drive command is
    /// the opposite — the page is WAITING for the thing to happen, so answering "fine" while doing nothing
    /// leaves it waiting forever with no error to act on. That is the exact failure this module was created
    /// to fix (see the type's remarks), in the other direction.
    /// </summary>
    private IMediaPlayer RequirePlayer() =>
        _player ?? throw new ShenoraException(
            "MEDIA_PLAYER_UNAVAILABLE",
            message: "This shell registers no IMediaPlayer, so the host cannot drive playback.");

    /// <summary>
    /// The source to open. Only <c>uri</c> is required — everything else on <see cref="MediaSource"/> is
    /// optional and the page rarely knows it.
    /// </summary>
    private static MediaSource Source(IpcRequest request) => new()
    {
        Uri = PayloadHelper.GetRequiredValue<string>(request.Payload, "uri"),
    };

    /// <summary>
    /// Status as the page reads it: SECONDS, not <see cref="TimeSpan"/>, matching what a media element
    /// exposes and what <see cref="ReportType"/> already carries in the other direction. The conversion
    /// lives here for the same reason it lives there — every adopter otherwise writes it, and gets it
    /// wrong in the same way (a `TimeSpan` does not deserialize into a `number`).
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
