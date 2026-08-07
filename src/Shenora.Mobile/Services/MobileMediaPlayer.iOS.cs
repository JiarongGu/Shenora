#if IOS || MACCATALYST
using AVFoundation;
using CoreMedia;
using Foundation;
using Shenora;
using Shenora.Media;

namespace Shenora.Mobile;

/// <summary>
/// iOS's <see cref="IMediaPlayer"/> — <c>AVPlayer</c> over an <c>AVPlayerItem</c>.
/// <para>
/// <b>This is the shell capability D54 says the framework exists to provide</b>, and iOS is where the gap
/// it closes is provable rather than argued: a <c>&lt;video&gt;</c> in a webview is PAUSED by the system the
/// moment the app backgrounds, because the video track cannot render. <c>AVPlayer</c> is not, so an app can
/// keep playing with the screen off — which React cannot do at any price.
/// </para>
/// <para>
/// ⚠ <b>Playing in the background additionally needs two things this class deliberately does not do</b>,
/// because both are the APP's policy and the same division <see cref="MobilePlaybackSession"/> already
/// draws for the audio session: an <c>AVAudioSession</c> activated with a playback category, and
/// <c>UIBackgroundModes: [audio]</c> in the app's <c>Info.plist</c>. The kit plays; the app decides whether
/// it is allowed to mix, duck, or interrupt someone else's audio.
/// </para>
/// </summary>
public sealed class MobileMediaPlayer : IMediaPlayer, IDisposable
{
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    private AVPlayer? _player;
    private AVPlayerItem? _item;
    private NSObject? _endObserver;
    private IDisposable? _statusObserver;
    private IDisposable? _bufferEmptyObserver;
    private IDisposable? _likelyToKeepUpObserver;

    private MediaPlayerState _state = MediaPlayerState.Empty;
    private double _rate = 1.0;
    private string? _error;
    private bool _disposed;

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into an AVFoundation callback.</param>
    public MobileMediaPlayer(Action<string>? log = null) => _log = log;

    /// <inheritdoc />
    public event Action<MediaPlayerStatus>? StateChanged;

    /// <inheritdoc />
    public MediaPlayerStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new MediaPlayerStatus
                {
                    State = _state,
                    Position = CurrentPosition(),
                    Duration = CurrentDuration(),
                    Rate = _rate,
                    Error = _error,
                };
            }
        }
    }

    /// <inheritdoc />
    public double Rate
    {
        get { lock (_gate) return _rate; }
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "Rate must be greater than zero.");
            lock (_gate)
            {
                _rate = value;
                // ⚠ Only push it while PLAYING. Setting AVPlayer.Rate to a non-zero value is what STARTS
                // playback on this platform — rate and transport are the same control — so applying a
                // remembered 1.5x to a paused player would silently start it. The contract says a rate set
                // while paused is applied on the next PlayAsync, and this is why.
                if (_state == MediaPlayerState.Playing && _player is { } p) Try(() => p.Rate = (float)value, nameof(Rate));
            }
        }
    }

    /// <inheritdoc />
    public Task OpenAsync(MediaSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(source.Uri)) throw new MediaPlayerException("Media source URI is empty.");

        Teardown();

        var url = ToUrl(source.Uri)
            ?? throw new MediaPlayerException("Media source URI is not a file path or an absolute URL.");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _error = null;
            _state = MediaPlayerState.Opening;
        }
        Raise();

        try
        {
            var asset = AVAsset.FromUrl(url);
            var item = new AVPlayerItem(asset);
            var player = new AVPlayer(item);

            lock (_gate) { _item = item; _player = player; }

            // AVPlayerItem reports readiness through KVO rather than a callback, and the value can ALREADY
            // be Ready by the time this runs for a local file — so the handler must also be correct when it
            // fires immediately, and the initial value is checked below rather than assumed pending.
            _statusObserver = item.AddObserver("status", NSKeyValueObservingOptions.New | NSKeyValueObservingOptions.Initial, _ =>
                OnStatus(item, source.StartAt, completion));

            // "Buffer ran dry" and "buffer refilled" are two separate keys; watching only the first gives a
            // player that enters Buffering and never leaves.
            _bufferEmptyObserver = item.AddObserver("playbackBufferEmpty", NSKeyValueObservingOptions.New, _ =>
            {
                lock (_gate) { if (_state == MediaPlayerState.Playing) _state = MediaPlayerState.Buffering; else return; }
                Raise();
            });
            _likelyToKeepUpObserver = item.AddObserver("playbackLikelyToKeepUp", NSKeyValueObservingOptions.New, _ =>
            {
                lock (_gate) { if (_state == MediaPlayerState.Buffering) _state = MediaPlayerState.Playing; else return; }
                Raise();
            });

            _endObserver = NSNotificationCenter.DefaultCenter.AddObserver(
                AVPlayerItem.DidPlayToEndTimeNotification,
                _ => { lock (_gate) _state = MediaPlayerState.Ended; Raise(); },
                item);
        }
        catch (Exception ex)
        {
            Fail("Could not open the media source.", ex);
            throw new MediaPlayerException("Could not open the media source.", ex);
        }

        return WaitForOpen(completion, cancellationToken);
    }

    /// <summary>
    /// Await readiness, mapping cancellation onto a torn-down player rather than a half-loaded one — the
    /// contract promises Empty after a cancelled open, and a caller that retries must not inherit the
    /// previous attempt's item.
    /// </summary>
    private async Task WaitForOpen(TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Teardown();
            lock (_gate) _state = MediaPlayerState.Empty;
            Raise();
            throw;
        }
    }

    private void OnStatus(AVPlayerItem item, TimeSpan startAt, TaskCompletionSource completion)
    {
        switch (item.Status)
        {
            case AVPlayerItemStatus.ReadyToPlay:
                // The completion overload, not Seek(CMTime) — that one is obsolete since iOS 11 and the
                // analyser fails the build on it (CA1422), the same trap MobilePlaybackSession hit with
                // MPMediaItemArtwork. Nothing waits on this completion: StartAt is best-effort positioning
                // before the item is handed over, and a caller that needs a guaranteed position seeks.
                if (startAt > TimeSpan.Zero)
                    Try(() => item.Seek(CMTime.FromSeconds(startAt.TotalSeconds, 600), _ => { }), "StartAt");
                lock (_gate) _state = MediaPlayerState.Paused;
                Raise();
                completion.TrySetResult();
                break;

            case AVPlayerItemStatus.Failed:
                // item.Error carries the platform's own text. It is LOGGED and never thrown: this string can
                // reach a page, and the same rule the IPC stack applies to every error path applies here.
                Log(() => $"[Shenora.Mobile] MediaPlayer open failed: {item.Error?.LocalizedDescription ?? "unknown"}.");
                Fail("The media source could not be played.", inner: null);
                completion.TrySetException(new MediaPlayerException("The media source could not be played."));
                break;

            case AVPlayerItemStatus.Unknown:
            default:
                break;   // still loading — a later notification decides it
        }
    }

    /// <inheritdoc />
    public Task PlayAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        AVPlayer player;
        double rate;
        lock (_gate)
        {
            if (_player is null) throw new MediaPlayerException("No media source is open.");
            if (_state == MediaPlayerState.Playing) return Task.CompletedTask;
            player = _player;
            rate = _rate;
            _state = MediaPlayerState.Playing;
        }

        // Rate, not Play() — Play() always resumes at 1.0 and would silently discard a configured speed.
        Try(() => player.Rate = (float)rate, nameof(PlayAsync));
        Raise();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        AVPlayer player;
        lock (_gate)
        {
            if (_player is null) throw new MediaPlayerException("No media source is open.");
            if (_state is MediaPlayerState.Paused or MediaPlayerState.Empty) return Task.CompletedTask;
            player = _player;
            _state = MediaPlayerState.Paused;
        }

        Try(player.Pause, nameof(PauseAsync));
        Raise();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;

        AVPlayer player;
        lock (_gate)
        {
            if (_player is null) throw new MediaPlayerException("No media source is open.");
            player = _player;
            // Seeking out of Ended is a resumable state again; leaving it as Ended makes a UI that seeks
            // backwards from the end still show "finished".
            if (_state == MediaPlayerState.Ended) _state = MediaPlayerState.Paused;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 600 is the conventional timescale here: it divides evenly by the common frame rates (24/25/30),
        // so a seek expressed in seconds lands on a frame boundary rather than between two.
        Try(() => player.Seek(CMTime.FromSeconds(position.TotalSeconds, 600), _ => completion.TrySetResult()),
            nameof(SeekAsync));
        Raise();
        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        Teardown();
        lock (_gate) { _state = MediaPlayerState.Empty; _error = null; }
        Raise();
        return Task.CompletedTask;
    }

    private TimeSpan CurrentPosition()
    {
        if (_player is not { } player) return TimeSpan.Zero;
        var time = player.CurrentTime;
        // An indefinite CMTime is not zero and not a number — asking Seconds for one yields NaN, which
        // reaches a page as `null` after serialization and reads as "no position" rather than a bug.
        if (time.IsInvalid || time.IsIndefinite) return TimeSpan.Zero;
        var seconds = time.Seconds;
        return double.IsNaN(seconds) ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
    }

    private TimeSpan? CurrentDuration()
    {
        if (_item is not { } item) return null;
        var duration = item.Duration;
        // A live stream reports an INDEFINITE duration rather than an error, and the contract says null.
        if (duration.IsInvalid || duration.IsIndefinite) return null;
        var seconds = duration.Seconds;
        return double.IsNaN(seconds) ? null : TimeSpan.FromSeconds(seconds);
    }

    private void Fail(string reason, Exception? inner)
    {
        if (inner is not null) Log(() => $"[Shenora.Mobile] MediaPlayer: {inner.GetType().Name}: {inner.Message}.");
        lock (_gate) { _state = MediaPlayerState.Failed; _error = reason; }
        Raise();
    }

    /// <summary>
    /// Drop every observer and the player itself. ⚠ The KVO observers and the notification observer are
    /// registered against the ITEM; releasing the player without removing them leaves them firing into a
    /// dead handler, which is the same leak <see cref="MobilePlaybackSession"/> documents for command targets.
    /// </summary>
    private void Teardown()
    {
        AVPlayer? player;
        lock (_gate)
        {
            player = _player;
            _player = null;
            _item = null;
        }

        Try(() =>
        {
            _statusObserver?.Dispose();
            _bufferEmptyObserver?.Dispose();
            _likelyToKeepUpObserver?.Dispose();
            if (_endObserver is not null) NSNotificationCenter.DefaultCenter.RemoveObserver(_endObserver);
            player?.Pause();
            player?.ReplaceCurrentItemWithPlayerItem(null);
        }, nameof(Teardown));

        _statusObserver = null;
        _bufferEmptyObserver = null;
        _likelyToKeepUpObserver = null;
        _endObserver = null;
    }

    /// <summary>
    /// A file path or an absolute URL, and nothing else. A relative string is rejected rather than resolved
    /// against the process's working directory, which on a phone is not a place an app's media lives.
    /// </summary>
    private static NSUrl? ToUrl(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && !parsed.IsFile)
            return NSUrl.FromString(uri);
        return Path.IsPathRooted(uri) ? NSUrl.FromFilename(uri) : null;
    }

    private void Raise()
    {
        var handler = StateChanged;
        if (handler is null) return;
        var status = Status;
        AppCallback.Run(() => handler(status),
            ex => Log(() => $"[Shenora.Mobile] A MediaPlayer state handler threw ({ex.GetType().Name}: {ex.Message})."));
    }

    private void Try(Action action, string what)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log(() => $"[Shenora.Mobile] MediaPlayer.{what} failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    private void Log(Func<string> message) => AppCallback.Log(_log, message);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Teardown();
    }
}
#endif
