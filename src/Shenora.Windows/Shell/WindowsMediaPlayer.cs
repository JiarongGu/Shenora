using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;

#if WINDOWS10_0_17763_0_OR_GREATER
using Shenora;

// ⚠ `global::` on every WinRT namespace, for the reason WindowsPlaybackSession documents: inside
// `namespace Shenora.Windows`, a bare `using Windows.Media.Playback` binds to
// `Shenora.Windows.Windows.Media.Playback` and fails with a CS0234 that blames `Shenora.Windows`.
using MediaPlaybackState = global::Windows.Media.Playback.MediaPlaybackState;
using WinRtMediaPlayer = global::Windows.Media.Playback.MediaPlayer;
using WinRtMediaSource = global::Windows.Media.Core.MediaSource;

namespace Shenora.Windows;

/// <summary>
/// Windows' <see cref="IMediaPlayer"/> — Media Foundation, reached through
/// <c>Windows.Media.Playback.MediaPlayer</c>. The state machine is
/// <see cref="MediaPlayerBase"/>'s; this is the platform half.
/// <para>
/// <b>Why the WinRT player and not Media Foundation directly.</b> <c>MediaPlayer</c> IS Media Foundation:
/// it owns an <c>IMFMediaEngine</c>, the same pipeline, the same codecs, the same hardware offload. What it
/// adds is the part that is tedious and easy to get wrong by hand — source resolution for
/// <c>file:</c>/<c>http(s):</c>, buffering state, rate control, and a managed lifetime. Writing the COM
/// interop instead would be several hundred lines of <c>IMFMediaEngineNotify</c> plumbing to arrive at the
/// same behaviour, and this shell already takes this dependency for <see cref="WindowsPlaybackSession"/>.
/// </para>
/// <para>
/// <b>What this closes.</b> D54's question is "can React already do this?", and for the desktop the honest
/// answer about <c>&lt;audio&gt;</c> is *mostly yes* — the gap here is narrower than iOS's, where the system
/// pauses a backgrounded <c>&lt;video&gt;</c> outright. What Windows gives that the element cannot: playback
/// that survives the webview being torn down or navigated, the platform's whole codec set rather than the
/// webview's subset (see <see cref="WindowsMediaCapability"/>), and a player the HOST owns — so what
/// <see cref="Shenora.Modules.Platform.IPlaybackSession"/> publishes to the taskbar is what is actually
/// happening rather than what the page claimed. Use <c>ReportTo</c> to wire those two together.
/// </para>
/// <para>
/// ⚠ <b>AUDIO. There is no video surface here</b>, the same limit the contract states for every shell:
/// compositing a decoded frame into the page's layout is a per-shell problem, and on Windows it would mean
/// an airspace-punching child window over the WebView2 render surface. An app playing video in the
/// foreground should keep using <c>&lt;video&gt;</c>.
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

        // 🔴 LOAD-BEARING, and it is the same line WindowsPlaybackSession relies on for the mirror-image
        // reason. A MediaPlayer publishes its OWN SystemMediaTransportControls unless the command manager
        // is off — so with this left enabled an app using both types would register the taskbar/Now Playing
        // surface TWICE, and the OS would show whichever won the race. IPlaybackSession is the one owner of
        // that surface in this kit; this player just plays.
        _player.CommandManager.IsEnabled = false;

        // Tells the OS this is media rather than a notification blip, which is what makes ducking, the
        // volume mixer's grouping and mute-on-call behave the way a user expects of a player. Set BEFORE any
        // source: the category is read when the audio graph is built.
        //
        // Caught rather than thrown, because it is a NICETY: a player that mixes wrongly still plays, and
        // failing construction over it would take the whole capability down for a behavioural detail.
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
    /// live stream and while a source is still resolving — there is no separate indefinite value the way
    /// <c>CMTime</c> has one — so a nullable duration is the honest mapping and a UI gets "no scrubber"
    /// rather than a scrubber pinned at the end.
    /// </para>
    /// </summary>
    protected override TimeSpan? DurationCore =>
        _player.PlaybackSession.NaturalDuration is { Ticks: > 0 } duration ? duration : null;

    /// <inheritdoc />
    protected override void OpenCore(MediaSource source, Uri uri)
    {
        // CreateFromUri covers file:, http: and https: — including the app's own in-process host, which is
        // how a source needing the kit's remux reaches the player. The MediaSource is OURS to dispose;
        // handing it to Source does not transfer that.
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
            // A live stream, not a bug. Saying so once beats a silent no-op that reads as a stuck UI.
            Log(() => "MediaPlayer.SeekAsync ignored: the source cannot seek.");
            return Task.CompletedTask;
        }

        // Clamped to the source, as the contract promises. NaturalDuration is Zero while unknown, in which
        // case there is nothing to clamp TO and the platform's own clamp is the better answer.
        var duration = _player.PlaybackSession.NaturalDuration;
        _player.PlaybackSession.Position = duration > TimeSpan.Zero && position > duration ? duration : position;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override void ApplyRateCore(double rate) => _player.PlaybackSession.PlaybackRate = rate;

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ <b>The MediaSource is disposed HERE and not left to the player.</b> Assigning <c>Source</c> does not
    /// transfer ownership: the previous source keeps its handle on the file — which on Windows means the file
    /// stays LOCKED, and the next thing the app does with it (delete, replace, hand to the converter) fails
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
        // The platform's own text is LOGGED and never thrown: this string can reach a page, and the same
        // rule the IPC stack applies to every error path applies here. `Error` is the coarse enum
        // (Aborted/NetworkError/DecodingError/SourceNotSupported) and ExtendedErrorCode the HRESULT — both
        // are what makes a support report actionable, and neither belongs on the wire.
        Log(() => $"MediaPlayer open failed: {args.Error} "
            + $"(0x{args.ExtendedErrorCode?.HResult ?? 0:X8}) {args.ErrorMessage}");
        OnFailed("The media source could not be played.");
    }

    /// <summary>
    /// WinRT's transport state, in the kit's vocabulary. The guard that keeps this from erasing
    /// <c>Ended</c> and <c>Failed</c> — which WinRT follows with <c>Paused</c> every time — lives in
    /// <see cref="MediaPlayerBase.OnPlatformState"/>, because every platform does the same thing.
    /// </summary>
    private void OnPlaybackStateChanged(global::Windows.Media.Playback.MediaPlaybackSession sender, object args)
    {
        var mapped = sender.PlaybackState switch
        {
            MediaPlaybackState.Opening => MediaPlayerState.Opening,
            MediaPlaybackState.Buffering => MediaPlayerState.Buffering,
            MediaPlaybackState.Playing => MediaPlayerState.Playing,
            MediaPlaybackState.Paused => MediaPlayerState.Paused,
            // None means "no source", which Empty already covers and CloseAsync already set. Reaching it
            // here would mean the platform dropped the source underneath us; report what we know rather
            // than inventing a transition.
            _ => (MediaPlayerState?)null,
        };

        if (mapped is { } state) OnPlatformState(state);
    }
}
#endif
