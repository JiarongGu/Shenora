using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;

using AVFoundation;
using CoreMedia;
using Foundation;
using Shenora;

namespace Shenora.iOS;

/// <summary>
/// iOS's <see cref="IMediaPlayer"/> — <c>AVPlayer</c> over an <c>AVPlayerItem</c>. The state machine is
/// <see cref="MediaPlayerBase"/>'s; this is the platform half. A webview's <c>&lt;video&gt;</c> is PAUSED by
/// the system the moment the app backgrounds; <c>AVPlayer</c> is not, which is the D54 gap it closes.
/// <para>
/// ⚠ <b>Background playback additionally needs two things this class does not do</b>, because both are the
/// APP's policy: an <c>AVAudioSession</c> activated with a playback category, and
/// <c>UIBackgroundModes: [audio]</c> in the app's <c>Info.plist</c>.
/// </para>
/// </summary>
public sealed class IosMediaPlayer : MediaPlayerBase
{
    private AVPlayer? _player;
    private AVPlayerItem? _item;

    /// <summary>Where the picture goes, remembered across opens — a player is built per source, so the
    /// layer has to be re-pointed at each one.</summary>
    private AVPlayerLayer? _layer;
    private NSObject? _endObserver;
    private IDisposable? _statusObserver;
    private IDisposable? _bufferEmptyObserver;
    private IDisposable? _likelyToKeepUpObserver;

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into an AVFoundation callback.</param>
    public IosMediaPlayer(ILogger? log = null)
        : base(log)
    {
    }

    /// <summary>
    /// <inheritdoc />
    /// <para>
    /// ⚠ An indefinite <c>CMTime</c> is not zero and not a number — <c>Seconds</c> yields NaN, which reaches
    /// a page as <c>null</c> after serialization and reads as "no position" rather than a bug.
    /// </para>
    /// </summary>
    protected override TimeSpan PositionCore
    {
        get
        {
            if (_player is not { } player) return TimeSpan.Zero;
            var time = player.CurrentTime;
            if (time.IsInvalid || time.IsIndefinite) return TimeSpan.Zero;
            return double.IsNaN(time.Seconds) ? TimeSpan.Zero : TimeSpan.FromSeconds(time.Seconds);
        }
    }

    /// <summary>
    /// <inheritdoc />
    /// <para>A live stream reports an INDEFINITE duration rather than an error, and the contract says null.</para>
    /// </summary>
    protected override TimeSpan? DurationCore
    {
        get
        {
            if (_item is not { } item) return null;
            var duration = item.Duration;
            if (duration.IsInvalid || duration.IsIndefinite) return null;
            return double.IsNaN(duration.Seconds) ? null : TimeSpan.FromSeconds(duration.Seconds);
        }
    }

    /// <inheritdoc />
    protected override void OpenCore(MediaSource source, Uri uri)
    {
        // The ORIGINAL string for a network URL, not the parsed Uri round-tripped back to text: System.Uri
        // normalizes escaping, and a signed URL whose signature covers its own encoding stops matching.
        var url = uri.IsFile ? NSUrl.FromFilename(uri.LocalPath) : NSUrl.FromString(source.Uri);
        if (url is null) throw new MediaPlayerException("Media source URI is not a file path or an absolute URL.");

        var asset = AVAsset.FromUrl(url);
        var item = new AVPlayerItem(asset);
        _item = item;
        _player = new AVPlayer(item);

        // Null when nothing registered a surface — an audio-only app.
        if (_layer is not null) _layer.Player = _player;

        // AVPlayerItem reports readiness through KVO, and the value can ALREADY be Ready by the time this
        // runs for a local file — hence Initial, so the handler is correct when it fires immediately.
        _statusObserver = item.AddObserver("status",
            NSKeyValueObservingOptions.New | NSKeyValueObservingOptions.Initial, _ => OnStatus(item));

        // ⚠ "Buffer ran dry" and "buffer refilled" are two separate keys; watching only the first gives a
        // player that enters Buffering and never leaves.
        _bufferEmptyObserver = item.AddObserver("playbackBufferEmpty", NSKeyValueObservingOptions.New, _ =>
        {
            if (State == MediaPlayerState.Playing) OnPlatformState(MediaPlayerState.Buffering);
        });
        _likelyToKeepUpObserver = item.AddObserver("playbackLikelyToKeepUp", NSKeyValueObservingOptions.New, _ =>
        {
            if (State == MediaPlayerState.Buffering) OnPlatformState(MediaPlayerState.Playing);
        });

        _endObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            AVPlayerItem.DidPlayToEndTimeNotification, _ => OnEnded(), item);
    }

    private void OnStatus(AVPlayerItem item)
    {
        switch (item.Status)
        {
            case AVPlayerItemStatus.ReadyToPlay:
                OnOpened();
                break;

            case AVPlayerItemStatus.Failed:
                // item.Error carries the platform's own text: LOGGED, never thrown — it can reach a page.
                Log(() => $"MediaPlayer open failed: {item.Error?.LocalizedDescription ?? "unknown"}.");
                OnFailed("The media source could not be played.");
                break;

            case AVPlayerItemStatus.Unknown:
            default:
                break;   // still loading — a later notification decides it
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The completion overload, not <c>Seek(CMTime)</c> — that one is obsolete since iOS 11 and the analyser
    /// fails the build on it (CA1422). Nothing waits on the completion: positioning is best-effort before the
    /// item is handed over, and a caller needing a guaranteed position seeks.
    /// </remarks>
    protected override void ApplyStartAt(TimeSpan position) =>
        _item?.Seek(CMTime.FromSeconds(position.TotalSeconds, TimeScale), _ => { });

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ <c>Rate</c>, not <c>Play()</c> — <c>Play()</c> always resumes at 1.0 and would silently discard a
    /// configured speed. On this platform rate and transport are the SAME control.
    /// </remarks>
    protected override void PlayCore(double rate)
    {
        if (_player is { } player) player.Rate = (float)rate;
    }

    /// <inheritdoc />
    protected override void PauseCore() => _player?.Pause();

    /// <inheritdoc />
    /// <remarks>Genuinely asynchronous here: AVFoundation calls back when the seek LANDS, so the contract's
    /// <c>SeekAsync</c> completes then rather than on the request being accepted.</remarks>
    protected override Task SeekCore(TimeSpan position)
    {
        if (_player is not { } player) return Task.CompletedTask;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.Seek(CMTime.FromSeconds(position.TotalSeconds, TimeScale), _ => completion.TrySetResult());
        return completion.Task;
    }

    /// <inheritdoc />
    protected override void ApplyRateCore(double rate)
    {
        if (_player is { } player) player.Rate = (float)rate;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ The KVO observers and the notification observer are registered against the ITEM; releasing the
    /// player without removing them leaves them firing into a dead handler.
    /// </remarks>
    protected override void TeardownCore()
    {
        _statusObserver?.Dispose();
        _bufferEmptyObserver?.Dispose();
        _likelyToKeepUpObserver?.Dispose();
        if (_endObserver is not null) NSNotificationCenter.DefaultCenter.RemoveObserver(_endObserver);

        _statusObserver = null;
        _bufferEmptyObserver = null;
        _likelyToKeepUpObserver = null;
        _endObserver = null;

        _player?.Pause();
        _player?.ReplaceCurrentItemWithPlayerItem(null);
        // ⚠ The LAYER is kept — it belongs to the view, not to the source — but it must stop pointing at a
        // player that is going away, or it holds the last frame on screen after the source is released.
        if (_layer is not null) _layer.Player = null;
        _player = null;
        _item = null;
    }

    /// <summary>
    /// <inheritdoc />
    /// ⚠ The handle is an <c>AVPlayerLayer</c>. It is the VIEW's, so this only points it at the current
    /// player and never disposes it.
    /// </summary>
    protected override void AttachSurfaceCore(object? surface)
    {
        // Release the outgoing layer first — one left pointing at a player it no longer represents keeps
        // showing that player's last frame.
        if (_layer is not null) _layer.Player = null;
        _layer = surface as AVPlayerLayer;
        if (_layer is not null) _layer.Player = _player;
    }

    /// <summary>The conventional CMTime timescale: it divides evenly by the common frame rates (24/25/30),
    /// so a seek expressed in seconds lands on a frame boundary.</summary>
    private const int TimeScale = 600;
}
