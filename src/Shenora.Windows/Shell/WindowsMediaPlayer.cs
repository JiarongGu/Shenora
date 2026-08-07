using Shenora.Modules.Media;
using Shenora.Core.Ipc;

#if WINDOWS10_0_17763_0_OR_GREATER
using Shenora;

// ⚠ `global::` on every WinRT namespace, for the reason WindowsPlaybackSession documents: inside
// `namespace Shenora.Windows`, a bare `using Windows.Media.Playback` binds to
// `Shenora.Windows.Windows.Media.Playback` and fails with a CS0234 that blames `Shenora.Windows`.
using MediaPlaybackState = global::Windows.Media.Playback.MediaPlaybackState;
using WinRtMediaPlayer = global::Windows.Media.Playback.MediaPlayer;

namespace Shenora.Windows;

/// <summary>
/// Windows' <see cref="IMediaPlayer"/> — Media Foundation, reached through
/// <c>Windows.Media.Playback.MediaPlayer</c>.
/// <para>
/// <b>Why the WinRT player and not Media Foundation directly.</b> <c>MediaPlayer</c> IS Media Foundation:
/// it owns an <c>IMFMediaEngine</c>, the same pipeline, the same codecs, the same hardware offload. What it
/// adds is the part that is tedious and easy to get wrong by hand — source resolution for
/// <c>file:</c>/<c>http(s):</c>, buffering state, rate control, and a managed lifetime. Writing the COM
/// interop instead would be several hundred lines of <c>IMFMediaEngineNotify</c> plumbing to arrive at the
/// same behaviour, and this shell already takes this dependency for
/// <see cref="WindowsPlaybackSession"/>.
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
public sealed class WindowsMediaPlayer : IMediaPlayer, IDisposable
{
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly WinRtMediaPlayer _player;

    private global::Windows.Media.Core.MediaSource? _source;
    private TaskCompletionSource? _opening;
    /// <summary>Where the pending open should land, applied once the platform says the source is ready.</summary>
    private TimeSpan _startAt;

    private MediaPlayerState _state = MediaPlayerState.Empty;
    private double _rate = 1.0;
    private string? _error;
    private bool _disposed;

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into a WinRT callback.</param>
    public WindowsMediaPlayer(Action<string>? log = null)
    {
        _log = log;
        _player = new WinRtMediaPlayer
        {
            // 🔴 LOAD-BEARING, and it is the same line WindowsPlaybackSession relies on for the mirror-image
            // reason. A MediaPlayer publishes its OWN SystemMediaTransportControls unless the command manager
            // is off — so with this left enabled an app using both types would register the taskbar/Now
            // Playing surface TWICE, and the OS would show whichever won the race. IPlaybackSession is the
            // one owner of that surface in this kit; this player just plays.
            AutoPlay = false,
        };
        _player.CommandManager.IsEnabled = false;

        // Tells the OS this is media rather than a notification blip, which is what makes ducking, the
        // volume mixer's grouping and mute-on-call behave the way a user expects of a player. Set BEFORE any
        // source: the category is read when the audio graph is built.
        Try(() => _player.AudioCategory = global::Windows.Media.Playback.MediaPlayerAudioCategory.Media,
            nameof(WinRtMediaPlayer.AudioCategory));

        _player.MediaOpened += OnMediaOpened;
        _player.MediaFailed += OnMediaFailed;
        _player.MediaEnded += OnMediaEnded;
        _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
    }

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
            bool playing;
            lock (_gate)
            {
                _rate = value;
                playing = _state == MediaPlayerState.Playing;
            }

            // Applied only while PLAYING, which on THIS platform is a contract decision rather than a
            // platform constraint: unlike AVPlayer — where rate and transport are the same control, so a
            // remembered 1.5x would start a paused player — WinRT's PlaybackRate is independent of Play().
            // Deferring anyway keeps all three shells observably identical, and the contract already
            // promises "remembered and applied on the next PlayAsync".
            if (playing) Try(() => _player.PlaybackSession.PlaybackRate = value, nameof(Rate));
        }
    }

    /// <inheritdoc />
    public Task OpenAsync(MediaSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(source.Uri)) throw new MediaPlayerException("Media source URI is empty.");

        var uri = ToUri(source.Uri)
            ?? throw new MediaPlayerException("Media source URI is not a file path or an absolute URL.");

        Teardown();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _error = null;
            _state = MediaPlayerState.Opening;
            _opening = completion;
            _startAt = source.StartAt;
        }
        Raise();

        try
        {
            // CreateFromUri covers file:, http: and https: — including the app's own in-process host, which
            // is how a source needing the kit's remux reaches the player. The MediaSource is OURS to dispose;
            // handing it to Source does not transfer that.
            var media = global::Windows.Media.Core.MediaSource.CreateFromUri(uri);
            lock (_gate) _source = media;
            _player.Source = media;
        }
        catch (Exception ex)
        {
            Fail("Could not open the media source.", ex);
            completion.TrySetException(new MediaPlayerException("Could not open the media source.", ex));
            throw new MediaPlayerException("Could not open the media source.", ex);
        }

        return WaitForOpen(completion, cancellationToken);
    }

    /// <summary>
    /// Await readiness, mapping cancellation onto a torn-down player rather than a half-loaded one — the
    /// contract promises <see cref="MediaPlayerState.Empty"/> after a cancelled open, so a caller that
    /// retries does not inherit the previous attempt's source.
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

    private void OnMediaOpened(WinRtMediaPlayer sender, object args)
    {
        TaskCompletionSource? completion;
        TimeSpan startAt;
        lock (_gate)
        {
            completion = _opening;
            startAt = _startAt;
            _state = MediaPlayerState.Paused;
        }

        // Positioned as part of opening so a resumed item does not visibly start at zero and jump. Guarded by
        // CanSeek: a live stream reports the position as read-only, and assigning anyway throws from a WinRT
        // callback where nobody can catch it.
        if (startAt > TimeSpan.Zero)
        {
            Try(() =>
            {
                if (_player.PlaybackSession.CanSeek) _player.PlaybackSession.Position = startAt;
            }, "StartAt");
        }

        Raise();
        completion?.TrySetResult();
    }

    private void OnMediaFailed(WinRtMediaPlayer sender, global::Windows.Media.Playback.MediaPlayerFailedEventArgs args)
    {
        // The platform's own text is LOGGED and never thrown: this string can reach a page, and the same
        // rule the IPC stack applies to every error path applies here. `Error` is the coarse enum
        // (Aborted/NetworkError/DecodingError/SourceNotSupported) and ExtendedErrorCode the HRESULT — both
        // are what makes a support report actionable, and neither belongs on the wire.
        Log(() => $"[Shenora.Windows] MediaPlayer open failed: {args.Error} "
            + $"(0x{args.ExtendedErrorCode?.HResult ?? 0:X8}) {args.ErrorMessage}");

        TaskCompletionSource? completion;
        lock (_gate) completion = _opening;

        Fail("The media source could not be played.", inner: null);
        completion?.TrySetException(new MediaPlayerException("The media source could not be played."));
    }

    private void OnMediaEnded(WinRtMediaPlayer sender, object args)
    {
        lock (_gate) _state = MediaPlayerState.Ended;
        Raise();
    }

    /// <summary>
    /// The platform's transport state, mapped onto ours.
    /// <para>
    /// 🔴 <b>Ended and Failed are NOT overwritten here, and that guard is the whole subtlety.</b> WinRT
    /// drives its session to <see cref="MediaPlaybackState.Paused"/> immediately after
    /// <c>MediaEnded</c> and after a failure — so a mapping that trusted this event would erase the state
    /// the caller actually needs, microseconds after raising it. A UI would see "finished" flicker to
    /// "paused at the end", and a failed open would report as a healthy paused player with an error string
    /// nothing was showing.
    /// </para>
    /// </summary>
    private void OnPlaybackStateChanged(global::Windows.Media.Playback.MediaPlaybackSession sender, object args)
    {
        var platform = AppCallback.RunOrDefault(() => sender.PlaybackState, MediaPlaybackState.None);

        lock (_gate)
        {
            if (_state is MediaPlayerState.Ended or MediaPlayerState.Failed or MediaPlayerState.Empty) return;

            var mapped = platform switch
            {
                MediaPlaybackState.Opening => MediaPlayerState.Opening,
                MediaPlaybackState.Buffering => MediaPlayerState.Buffering,
                MediaPlaybackState.Playing => MediaPlayerState.Playing,
                MediaPlaybackState.Paused => MediaPlayerState.Paused,
                // None means "no source", which our Empty already covers and CloseAsync already set. Reaching
                // it here would mean the platform dropped the source underneath us; report what we know
                // rather than inventing a transition.
                _ => _state,
            };

            if (mapped == _state) return;   // a tick, not a transition — the contract says nothing is raised
            _state = mapped;
        }

        Raise();
    }

    /// <inheritdoc />
    public Task PlayAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        double rate;
        lock (_gate)
        {
            if (_source is null) throw new MediaPlayerException("No media source is open.");
            if (_state == MediaPlayerState.Playing) return Task.CompletedTask;
            rate = _rate;
            _state = MediaPlayerState.Playing;
        }

        // The remembered rate goes on BEFORE Play(), so a player configured at 1.5x while paused starts at
        // 1.5x rather than starting at 1.0 and visibly stepping up.
        Try(() => _player.PlaybackSession.PlaybackRate = rate, nameof(PlayAsync));
        Try(_player.Play, nameof(PlayAsync));
        Raise();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_source is null) throw new MediaPlayerException("No media source is open.");
            if (_state is MediaPlayerState.Paused or MediaPlayerState.Empty) return Task.CompletedTask;
            _state = MediaPlayerState.Paused;
        }

        Try(_player.Pause, nameof(PauseAsync));
        Raise();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;

        lock (_gate)
        {
            if (_source is null) throw new MediaPlayerException("No media source is open.");
            // Seeking out of Ended makes it a resumable state again; leaving it Ended makes a UI that seeks
            // backwards from the end still show "finished".
            if (_state == MediaPlayerState.Ended) _state = MediaPlayerState.Paused;
        }

        Try(() =>
        {
            if (!_player.PlaybackSession.CanSeek)
            {
                // A live stream, not a bug. Saying so once beats a silent no-op that reads as a stuck UI.
                Log(() => "[Shenora.Windows] MediaPlayer.SeekAsync ignored: the source cannot seek.");
                return;
            }
            // Clamped to the source, as the contract promises. NaturalDuration is Zero while unknown, in
            // which case there is nothing to clamp TO and the platform's own clamp is the better answer.
            var duration = _player.PlaybackSession.NaturalDuration;
            _player.PlaybackSession.Position = duration > TimeSpan.Zero && position > duration ? duration : position;
        }, nameof(SeekAsync));

        Raise();
        return Task.CompletedTask;
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

    private TimeSpan CurrentPosition() =>
        _source is null ? TimeSpan.Zero : AppCallback.RunOrDefault(() => _player.PlaybackSession.Position, TimeSpan.Zero);

    /// <summary>
    /// How long the source is, or null when the platform does not know.
    /// <para>
    /// ⚠ <b>Zero means "not known yet", not "zero long".</b> WinRT reports <see cref="TimeSpan.Zero"/> for a
    /// live stream and while a source is still resolving — there is no separate indefinite value the way
    /// <c>CMTime</c> has one — so a nullable duration is the honest mapping and a UI gets "no scrubber"
    /// rather than a scrubber pinned at the end.
    /// </para>
    /// </summary>
    private TimeSpan? CurrentDuration()
    {
        if (_source is null) return null;
        var duration = AppCallback.RunOrDefault(() => _player.PlaybackSession.NaturalDuration, TimeSpan.Zero);
        return duration > TimeSpan.Zero ? duration : null;
    }

    private void Fail(string reason, Exception? inner)
    {
        if (inner is not null) Log(() => $"[Shenora.Windows] MediaPlayer: {inner.GetType().Name}: {inner.Message}.");
        lock (_gate) { _state = MediaPlayerState.Failed; _error = reason; }
        Raise();
    }

    /// <summary>
    /// Drop the source and settle any open that is still waiting.
    /// <para>
    /// ⚠ <b>The MediaSource is disposed HERE and not left to the player.</b> Assigning
    /// <c>Source</c> does not transfer ownership: the previous source keeps its handle on the file — which
    /// on Windows means the file stays locked, and the next thing the app does with it (delete, replace,
    /// hand to the converter) fails for a reason that points nowhere near this class.
    /// </para>
    /// </summary>
    private void Teardown()
    {
        global::Windows.Media.Core.MediaSource? source;
        TaskCompletionSource? opening;
        lock (_gate)
        {
            source = _source;
            opening = _opening;
            _source = null;
            _opening = null;
        }

        // An open abandoned by a Close or a re-Open must not leave its caller awaiting forever — the exact
        // failure that made MediaPlayer.OpenAsync hang before MediaPlayerModule existed.
        opening?.TrySetException(new MediaPlayerException("The open was abandoned before it completed."));

        Try(() =>
        {
            _player.Pause();
            _player.Source = null;
        }, nameof(Teardown));
        Try(() => source?.Dispose(), nameof(Teardown));
    }

    /// <summary>
    /// A file path or an absolute URL, and nothing else. A relative string is rejected rather than resolved
    /// against the process's working directory, which is not where an app's media lives.
    /// </summary>
    private static Uri? ToUri(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && !parsed.IsFile) return parsed;
        return Path.IsPathRooted(uri) ? new Uri(uri) : null;
    }

    private void Raise()
    {
        var handler = StateChanged;
        if (handler is null) return;
        var status = Status;
        AppCallback.Run(() => handler(status),
            ex => Log(() => $"[Shenora.Windows] A MediaPlayer state handler threw ({ex.GetType().Name}: {ex.Message})."));
    }

    private void Try(Action action, string what)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log(() => $"[Shenora.Windows] MediaPlayer.{what} failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    private void Log(Func<string> message) => AppCallback.Log(_log, message);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Handlers off BEFORE the player goes: WinRT raises a final state change during disposal, and a
        // handler that ran then would touch a half-disposed session from a thread we do not own.
        Try(() =>
        {
            _player.MediaOpened -= OnMediaOpened;
            _player.MediaFailed -= OnMediaFailed;
            _player.MediaEnded -= OnMediaEnded;
            _player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
        }, nameof(Dispose));

        Teardown();
        Try(_player.Dispose, nameof(Dispose));
    }
}
#endif
