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
/// <b>Registered by default</b> with <c>UseMessageDispatcher()</c> (D64) and it costs nothing until the
/// page posts: a module nothing routes to is inert. It answers on
/// <see cref="MediaPlayerOptions.Module"/> — <c>SHENORA.MEDIA</c> by default, a RESERVED prefix, so an
/// app remains free to own a module called plainly <c>MEDIA</c>.
/// </para>
/// </summary>
public sealed class MediaPlayerModule : ModuleBase
{
    /// <summary>Route: the page describing what its element is doing. Payload
    /// <c>{ state, position, duration, error? }</c>, positions in SECONDS.</summary>
    public const string ReportType = "PLAYER_REPORT";

    private readonly IMediaPlayer? _player;
    private readonly MediaPlayerOptions _options;

    /// <param name="player">
    /// The host's player, or <c>null</c>.
    /// <para>
    /// ⚠ <b>Nullable because this facade is registered by DEFAULT and lives in <c>Shenora.Ipc</c>, which
    /// cannot assume anyone called <c>ShenoraApplicationBuilder.Build()</c>.</b> A bare
    /// <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/> composing IPC alone is a
    /// legitimate shape — the kit's own IPC tests are exactly that — and a default registration that threw
    /// on resolve would turn "the framework is on" into "the framework fell over".
    /// </para>
    /// </param>
    /// <param name="options">Read for <see cref="MediaPlayerOptions.Module"/>, so the emitter and this
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
    public override string ModuleName => _options.Module;

    /// <inheritdoc />
    protected override Task<object?> RouteMessageAsync(
        IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
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
}
