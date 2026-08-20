using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;

#if WINDOWS10_0_17763_0_OR_GREATER
using Shenora;

// ⚠ `global::` on every WinRT namespace — see WindowsPlaybackSession.cs.
using MediaPlaybackState = global::Windows.Media.Playback.MediaPlaybackState;
using WinRtMediaPlayer = global::Windows.Media.Playback.MediaPlayer;
using WinRtMediaSource = global::Windows.Media.Core.MediaSource;

namespace Shenora.Windows;

/// <summary>
/// Windows' <see cref="IMediaPlayer"/> — Media Foundation, reached through
/// <c>Windows.Media.Playback.MediaPlayer</c> (which owns an <c>IMFMediaEngine</c>, so it is the same
/// pipeline with source resolution, buffering and lifetime already handled). The state machine is
/// <see cref="MediaPlayerBase"/>'s; this is the platform half. What it gives that <c>&lt;audio&gt;</c>
/// cannot: playback that survives the webview, the platform's whole codec set (see
/// <see cref="WindowsMediaCapability"/>), and a player the HOST owns — wire it to
/// <see cref="Shenora.Modules.Platform.IPlaybackSession"/> with <c>ReportTo</c>.
/// <para>
/// ⚠ <b>AUDIO. There is no video surface here</b>, the same limit the contract states for every shell —
/// an app playing video in the foreground should keep using <c>&lt;video&gt;</c>.
/// </para>
/// </summary>
public sealed class WindowsMediaPlayer : MediaPlayerBase
{
    private readonly WinRtMediaPlayer _player;
    private WinRtMediaSource? _source;

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into a WinRT callback.</param>
    public WindowsMediaPlayer(ILogger? log = null)
        : base(log)
    {
        _player = new WinRtMediaPlayer { AutoPlay = false };

        // 🔴 LOAD-BEARING. A MediaPlayer publishes its OWN SystemMediaTransportControls unless the command
        // manager is off, so leaving it enabled would register the Now Playing surface TWICE for an app
        // using both types, and the OS would show whichever won the race.
        _player.CommandManager.IsEnabled = false;

        // Media rather than a notification blip — what makes ducking, mixer grouping and mute-on-call
        // behave. Set BEFORE any source: the category is read when the audio graph is built. Caught rather
        // than thrown; a player that mixes wrongly still plays.
        try
        {
            _player.AudioCategory = global::Windows.Media.Playback.MediaPlayerAudioCategory.Media;
        }
        catch (Exception ex)
        {
            Log(() => "MediaPlayer.AudioCategory failed.", ex);
        }

        _player.MediaOpened += OnMediaOpened;
        _player.MediaFailed += OnMediaFailed;
        _player.MediaEnded += OnMediaEnded;
        _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
    }

    /// <inheritdoc />
    protected override TimeSpan PositionCore => _player.PlaybackSession.Position;

    /// <summary>
    /// <inheritdoc />
    /// <para>
    /// ⚠ <b>Zero means "not known yet", not "zero long".</b> WinRT reports <see cref="TimeSpan.Zero"/> for a
    /// live stream and while a source is still resolving, so a nullable duration is the honest mapping and a
    /// UI gets "no scrubber" rather than a scrubber pinned at the end.
    /// </para>
    /// </summary>
    protected override TimeSpan? DurationCore =>
        _player.PlaybackSession.NaturalDuration is { Ticks: > 0 } duration ? duration : null;

    /// <inheritdoc />
    protected override void OpenCore(MediaSource source, Uri uri)
    {
        // CreateFromUri covers file:, http: and https:, including the app's own in-process host. The
        // MediaSource is OURS to dispose; handing it to Source does not transfer that.
        _source = WinRtMediaSource.CreateFromUri(uri);
        _player.Source = _source;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Guarded by <c>CanSeek</c>: a live stream reports its position as read-only, and assigning anyway
    /// throws from inside a WinRT callback where nobody can catch it.
    /// </remarks>
    protected override void ApplyStartAt(TimeSpan position)
    {
        if (_player.PlaybackSession.CanSeek) _player.PlaybackSession.Position = position;
    }

    /// <inheritdoc />
    protected override void PlayCore(double rate)
    {
        _player.PlaybackSession.PlaybackRate = rate;
        _player.Play();
    }

    /// <inheritdoc />
    protected override void PauseCore() => _player.Pause();

    /// <inheritdoc />
    /// <remarks>Synchronous on this platform — WinRT applies the position on assignment.</remarks>
    protected override Task SeekCore(TimeSpan position)
    {
        if (!_player.PlaybackSession.CanSeek)
        {
            // A live stream, not a bug — but logged, or the no-op reads as a stuck UI.
            Log(() => "MediaPlayer.SeekAsync ignored: the source cannot seek.");
            return Task.CompletedTask;
        }

        // Clamped to the source, as the contract promises. NaturalDuration is Zero while unknown, in which
        // case there is nothing to clamp TO.
        var duration = _player.PlaybackSession.NaturalDuration;
        _player.PlaybackSession.Position = duration > TimeSpan.Zero && position > duration ? duration : position;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override void ApplyRateCore(double rate) => _player.PlaybackSession.PlaybackRate = rate;

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ <b>The MediaSource is disposed HERE and not left to the player.</b> Assigning <c>Source</c> does not
    /// transfer ownership, so the previous source keeps the file LOCKED and the next delete or replace fails
    /// for a reason that points nowhere near this class.
    /// </remarks>
    protected override void TeardownCore()
    {
        _player.Pause();
        _player.Source = null;
        _source?.Dispose();
        _source = null;
    }

    /// <inheritdoc />
    protected override void DetachCore()
    {
        _player.MediaOpened -= OnMediaOpened;
        _player.MediaFailed -= OnMediaFailed;
        _player.MediaEnded -= OnMediaEnded;
        _player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
    }

    /// <inheritdoc />
    protected override void DisposeCore() => _player.Dispose();

    private void OnMediaOpened(WinRtMediaPlayer sender, object args) => OnOpened();

    private void OnMediaEnded(WinRtMediaPlayer sender, object args) => OnEnded();

    private void OnMediaFailed(WinRtMediaPlayer sender, global::Windows.Media.Playback.MediaPlayerFailedEventArgs args)
    {
        // ⚠ The platform's own text is LOGGED and never thrown — it can reach a page, and no raw platform
        // error text goes on the wire. The enum and the HRESULT are what makes a support report actionable.
        Log(() => $"MediaPlayer open failed: {args.Error} "
            + $"(0x{args.ExtendedErrorCode?.HResult ?? 0:X8}) {args.ErrorMessage}");
        OnFailed("The media source could not be played.");
    }

    /// <summary>
    /// WinRT's transport state, in the kit's vocabulary. The guard that keeps a trailing <c>Paused</c> from
    /// erasing <c>Ended</c>/<c>Failed</c> lives in <see cref="MediaPlayerBase.OnPlatformState"/>.
    /// </summary>
    private void OnPlaybackStateChanged(global::Windows.Media.Playback.MediaPlaybackSession sender, object args)
    {
        var mapped = sender.PlaybackState switch
        {
            MediaPlaybackState.Opening => MediaPlayerState.Opening,
            MediaPlaybackState.Buffering => MediaPlayerState.Buffering,
            MediaPlaybackState.Playing => MediaPlayerState.Playing,
            MediaPlaybackState.Paused => MediaPlayerState.Paused,
            // None means "no source", which Empty already covers and CloseAsync already set.
            _ => (MediaPlayerState?)null,
        };

        if (mapped is { } state) OnPlatformState(state);
    }
}
#endif
