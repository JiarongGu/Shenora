using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;

using AVFoundation;
using CoreMedia;
using Foundation;
using Shenora;
using UIKit;

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
/// <para>
/// 🔴 <b>Started while the app is in the background, it makes that session MIXABLE until it is playing</b>, then
/// puts the app's session back: a backgrounded app may not START non-mixable audio, and on an iPhone the player was
/// paused at once, silently, with the app suspended — which is when <c>BackgroundPlaybackTransfer</c> starts it. A
/// session that is not <c>Playback</c> is logged as a warning instead. The simulator enforces none of this.
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

    /// <summary>The app's own audio session, put back once a start in the background is playing.</summary>
    private PendingRestore? _sessionRestore;
    private readonly ILogger? _log;

    /// <summary>The app's own session options, put back once a start in the background plays — or when that player
    /// goes first. Made before the observer exists, because its first callback can come from inside
    /// <c>AddObserver</c> or from the main thread before this one has stored anything.</summary>
    private sealed class PendingRestore(AVAudioSessionMode mode, AVAudioSessionCategoryOptions options)
    {
        public readonly AVAudioSessionMode Mode = mode;
        public readonly AVAudioSessionCategoryOptions Options = options;
        public IDisposable? Observer;
        public int Done;
    }

    // Whether the app is in the background, kept from UIKit's notifications — `ApplicationState` may only be read on
    // the main thread, and PlayCore is called from wherever the caller's await resumed — and seeded from that state,
    // because a player first built while the app is already away (resolved in a Stopped handler, a background
    // launch) would otherwise start as if in the foreground and be paused at once.
    private static volatile bool s_inBackground;
    private static readonly NSObject s_backgroundObserver =
        UIApplication.Notifications.ObserveDidEnterBackground((_, _) => s_inBackground = true);
    private static readonly NSObject s_foregroundObserver =
        UIApplication.Notifications.ObserveWillEnterForeground((_, _) => s_inBackground = false);

    static IosMediaPlayer()
    {
        void Seed() => s_inBackground = UIApplication.SharedApplication.ApplicationState == UIApplicationState.Background;
        if (NSThread.IsMain) Seed();
        else NSRunLoop.Main.BeginInvokeOnMainThread(Seed);
    }

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into an AVFoundation callback.</param>
    public IosMediaPlayer(ILogger? log = null)
        : base(log)
    {
        _log = log;
        GC.KeepAlive(s_backgroundObserver);
        GC.KeepAlive(s_foregroundObserver);
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
        if (_player is not { } player) return;
        if (s_inBackground) StartInBackground(player, (float)rate);
        else player.Rate = (float)rate;
    }

    /// <summary>
    /// 🔴 <b>A backgrounded app may not START non-mixable audio</b> — measured on an iPhone: the player's rate drops
    /// to 0 at once, nothing reports it, and the app is suspended — and <c>BackgroundPlaybackTransfer</c> starts this
    /// player exactly then, at <c>Window.Stopped</c>. So a start in the background runs under the app's own
    /// <c>Playback</c> session made mixable for the moment, and the session is put back as soon as the player is
    /// actually playing; measured to keep playing. The simulator enforces none of this.
    /// </summary>
    private void StartInBackground(AVPlayer player, float rate)
    {
        var session = AVAudioSession.SharedInstance();
        if (session.Category != AVAudioSessionCategory.Playback.GetConstant()?.ToString())
        {
            AppCallback.Log(_log, () => "[Shenora.iOS] the media player was asked to play in the background, which "
                + $"needs a Playback audio session (and UIBackgroundModes: audio); this app's is {session.Category}.",
                LogLevel.Warning);
            player.Rate = rate;
            return;
        }

        var options = session.CategoryOptions;
        if (options.HasFlag(AVAudioSessionCategoryOptions.MixWithOthers))
        {
            player.Rate = rate;
            return;
        }

        PendingRestore pending;
        try
        {
            pending = new PendingRestore(AVAudioSessionModeExtensions.GetValue(new NSString(session.Mode)), options);
            var mixable = session.SetCategory(AVAudioSessionCategory.Playback, pending.Mode,
                options | AVAudioSessionCategoryOptions.MixWithOthers) ?? session.SetActive(true);
            if (mixable is not null)
            {
                AppCallback.Log(_log, () => "[Shenora.iOS] background start: the session could not be made mixable "
                    + $"({mixable.LocalizedDescription}).", LogLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            // An audio-session mode this binding does not know: start anyway rather than leave a silent `Playing`.
            AppCallback.Log(_log, () => "[Shenora.iOS] background start: the session could not be made mixable.",
                LogLevel.Warning, ex);
            player.Rate = rate;
            return;
        }

        // An earlier one still pending is retired, not run: this one restores the same options, and running it now would
        // make the session non-mixable again just before the start that needs it mixable.
        if (Interlocked.Exchange(ref _sessionRestore, pending) is { } earlier)
        {
            Interlocked.Exchange(ref earlier.Done, 1);
            earlier.Observer?.Dispose();
        }
        player.Rate = rate;
        pending.Observer = player.AddObserver("timeControlStatus",
            NSKeyValueObservingOptions.Initial | NSKeyValueObservingOptions.New, _ =>
            {
                if (player.TimeControlStatus == AVPlayerTimeControlStatus.Playing) RestoreSession(pending);
            });
        // Restored from inside AddObserver (Initial) or by the main thread before the line above stored the observer.
        if (Volatile.Read(ref pending.Done) == 1) pending.Observer.Dispose();
    }

    /// <summary>Put the app's own session back — non-mixable again, which keeps a playing player playing and lets the
    /// app be the one the lock screen shows. Once per pending restore, whichever of playing or teardown comes first.</summary>
    private void RestoreSession(PendingRestore? pending)
    {
        if (pending is null || Interlocked.Exchange(ref pending.Done, 1) == 1) return;
        Interlocked.CompareExchange(ref _sessionRestore, null, pending);
        pending.Observer?.Dispose();
        var session = AVAudioSession.SharedInstance();
        var error = session.SetCategory(AVAudioSessionCategory.Playback, pending.Mode, pending.Options) ?? session.SetActive(true);
        AppCallback.Log(_log, () => error is null
            ? "[Shenora.iOS] background start: the app's audio session is back"
            : $"[Shenora.iOS] background start: the app's audio session could not be put back ({error.LocalizedDescription}).",
            error is null ? LogLevel.Debug : LogLevel.Warning);
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
        // A background start torn down before it played: the app's session goes back now, or nothing ever restores it
        // — a later start finds MixWithOthers and takes it for the app's own choice.
        RestoreSession(Interlocked.Exchange(ref _sessionRestore, null));
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
