using Shenora.Core.Ipc;

namespace Shenora.Modules.Media;

/// <summary>
/// The <see cref="IMediaPlayer"/> STATE MACHINE, with the platform left abstract — so a shell writes only
/// the part that is genuinely its own.
/// <para>
/// <b>Extracted from two shipping implementations, not designed ahead of them</b> (owner, 2026-08-08).
/// <c>IosMediaPlayer</c> (AVPlayer) and <c>WindowsMediaPlayer</c> (Media Foundation) were written
/// independently and converged on the same ~150 lines of bookkeeping: a lock around five fields, a status
/// snapshot, a deferred rate, an open that completes on a platform callback, and guarded event raising.
/// What differs between them is about forty lines each. This holds the common half, and it paid for itself
/// immediately: <c>AndroidMediaPlayer</c> landed the same day and inherited all of it rather than
/// rediscovering it.
/// </para>
/// <para>
/// 🔴 <b>Four invariants live here because BOTH implementations had to learn them separately, and each is
/// invisible when wrong.</b> They are the reason this class is worth its keep:
/// </para>
/// <list type="number">
///   <item><b>A terminal state is never overwritten by a platform transition.</b> Every platform drives its
///   session to "paused" immediately after it ends or fails, so a mapping that trusts the platform erases
///   <see cref="MediaPlayerState.Ended"/> and <see cref="MediaPlayerState.Failed"/> microseconds after
///   raising them — a UI sees "finished" flicker to "paused at the end", and a failed open reports as a
///   healthy paused player carrying an error string nothing displays.</item>
///   <item><b>A rate set while paused is remembered, not applied.</b> On AVFoundation rate and transport
///   are the SAME control, so pushing a remembered 1.5× would silently start a paused player. Deferring on
///   every platform makes all shells observably identical, which is what the contract promises.</item>
///   <item><b>A cancelled open leaves the player <see cref="MediaPlayerState.Empty"/>, not half-loaded</b>,
///   so a caller that retries does not inherit the previous attempt's source.</item>
///   <item><b>An abandoned open completes EXCEPTIONALLY rather than hanging.</b> Re-opening or closing
///   while an open is in flight used to leave its caller awaiting forever — no exception, no log line. That
///   is the exact shape of the defect that made <c>MediaPlayer.OpenAsync</c> wait for a
///   <c>PLAYER_REPORT</c> nothing sent.</item>
/// </list>
/// <para>
/// <b>What a shell supplies:</b> the platform handle, four transport verbs, position/duration, and the
/// callbacks that tell this class what the platform decided —
/// <see cref="OnOpened"/> / <see cref="OnEnded"/> / <see cref="OnFailed"/> / <see cref="OnPlatformState"/>.
/// Everything else is inherited.
/// </para>
/// <para>
/// ⚠ <b>This is not the page-backed <see cref="MediaPlayer"/>'s base.</b> That one's "platform" is a
/// webview element reporting over IPC, and its lifecycle is driven from the far side of a wire rather than
/// by a handle this process owns — a different shape, deliberately left alone.
/// </para>
/// </summary>
public abstract class MediaPlayerBase : IMediaPlayer, IDisposable
{
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    private TaskCompletionSource? _opening;
    private TimeSpan _startAt;
    private MediaPlayerState _state = MediaPlayerState.Empty;
    private double _rate = 1.0;
    private string? _error;
    private bool _hasSource;
    private bool _disposed;

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into a platform callback.</param>
    protected MediaPlayerBase(Action<string>? log = null) => _log = log;

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
                    // Not asked of the platform when there is no source: a released handle answers with
                    // whatever it last held, which reads as a position that survived CloseAsync.
                    Position = _hasSource ? Try(() => PositionCore, TimeSpan.Zero, nameof(PositionCore)) : TimeSpan.Zero,
                    Duration = _hasSource ? Try(() => DurationCore, null, nameof(DurationCore)) : null,
                    Rate = _rate,
                    Error = _error,
                };
            }
        }
    }

    /// <summary>
    /// What this player currently believes it is doing — without asking the platform for a position or a
    /// duration, which <see cref="Status"/> does.
    /// <para>
    /// For a platform callback that is only meaningful in one state: AVFoundation reports "the buffer ran
    /// dry" whether or not anything was playing, so a handler that forwarded it unconditionally would put a
    /// PAUSED player into <see cref="MediaPlayerState.Buffering"/> and leave it there.
    /// </para>
    /// </summary>
    protected MediaPlayerState State { get { lock (_gate) return _state; } }

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

            // Invariant 2 — see the type's remarks. Only pushed while PLAYING.
            if (playing) Try(() => ApplyRateCore(value), nameof(Rate));
        }
    }

    /// <inheritdoc />
    public Task OpenAsync(MediaSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(source.Uri)) throw new MediaPlayerException("Media source URI is empty.");

        var uri = ParseUri(source.Uri)
            ?? throw new MediaPlayerException("Media source URI is not a file path or an absolute URL.");

        Teardown();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _error = null;
            _state = MediaPlayerState.Opening;
            _opening = completion;
            _startAt = source.StartAt;
            _hasSource = true;
        }
        Raise();

        try
        {
            OpenCore(source, uri);
        }
        catch (Exception ex)
        {
            Fail("Could not open the media source.", ex);
            completion.TrySetException(new MediaPlayerException("Could not open the media source.", ex));
            throw new MediaPlayerException("Could not open the media source.", ex);
        }

        return WaitForOpen(completion, cancellationToken);
    }

    /// <summary>Invariant 3 — a cancelled open tears down rather than leaving a half-loaded player.</summary>
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

    /// <inheritdoc />
    public Task PlayAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        double rate;
        lock (_gate)
        {
            if (!_hasSource) throw new MediaPlayerException("No media source is open.");
            if (_state == MediaPlayerState.Playing) return Task.CompletedTask;
            rate = _rate;
            _state = MediaPlayerState.Playing;
        }

        // The remembered rate is handed to the platform WITH the start, so a player configured at 1.5× while
        // paused starts at 1.5× rather than starting at 1.0 and visibly stepping up.
        Try(() => PlayCore(rate), nameof(PlayAsync));
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
            if (!_hasSource) throw new MediaPlayerException("No media source is open.");
            if (_state is MediaPlayerState.Paused or MediaPlayerState.Empty) return Task.CompletedTask;
            _state = MediaPlayerState.Paused;
        }

        Try(PauseCore, nameof(PauseAsync));
        Raise();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;

        lock (_gate)
        {
            if (!_hasSource) throw new MediaPlayerException("No media source is open.");
            // Seeking out of Ended makes it resumable again; leaving it Ended makes a UI that seeks
            // backwards from the end still show "finished".
            if (_state == MediaPlayerState.Ended) _state = MediaPlayerState.Paused;
        }

        Raise();

        try
        {
            await SeekCore(position).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed seek is not a failed PLAYER: the source is still open at its old position, and a
            // caller can seek again. Logged rather than thrown, like every other transport verb here.
            Log(() => $"MediaPlayer.SeekAsync failed ({ex.GetType().Name}: {ex.Message}).");
        }
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

    // ---- What the platform tells US.

    /// <summary>
    /// The platform has the source open and can report a duration and accept a seek. Applies
    /// <see cref="MediaSource.StartAt"/> and completes the pending <see cref="OpenAsync"/>.
    /// </summary>
    protected void OnOpened()
    {
        TaskCompletionSource? completion;
        TimeSpan startAt;
        lock (_gate)
        {
            completion = _opening;
            _opening = null;
            startAt = _startAt;
            _state = MediaPlayerState.Paused;
        }

        // Positioned as part of OPENING rather than by a seek afterwards — the difference a caller sees is
        // whether a resumed item visibly starts at zero and jumps.
        if (startAt > TimeSpan.Zero) Try(() => ApplyStartAt(startAt), nameof(ApplyStartAt));

        Raise();
        completion?.TrySetResult();
    }

    /// <summary>The source played to its end. The position stays at the end so a UI can show it.</summary>
    protected void OnEnded()
    {
        lock (_gate) _state = MediaPlayerState.Ended;
        Raise();
    }

    /// <summary>
    /// The platform failed. <paramref name="reason"/> must be APP-SAFE — never the platform's own text,
    /// which can reach a page; log that separately.
    /// </summary>
    protected void OnFailed(string reason)
    {
        TaskCompletionSource? completion;
        lock (_gate) { completion = _opening; _opening = null; }

        Fail(reason, inner: null);
        completion?.TrySetException(new MediaPlayerException(reason));
    }

    /// <summary>
    /// The platform changed transport state on its own — buffering, resuming, stalling.
    /// <para>
    /// 🔴 Invariant 1: a TERMINAL state wins. See the type's remarks for why this guard is the difference
    /// between a correct player and one that erases its own outcome.
    /// </para>
    /// <para>
    /// A transition that matches what we already believe raises NOTHING: the contract says
    /// <see cref="StateChanged"/> is a transition and not a tick.
    /// </para>
    /// </summary>
    /// <param name="state">What the platform now reports, already mapped to the kit's vocabulary.</param>
    protected void OnPlatformState(MediaPlayerState state)
    {
        lock (_gate)
        {
            if (_state is MediaPlayerState.Ended or MediaPlayerState.Failed or MediaPlayerState.Empty) return;
            if (state == _state) return;
            _state = state;
        }
        Raise();
    }

    // ---- What a platform must provide.

    /// <summary>Where the platform has got to. Only asked while a source is open.</summary>
    protected abstract TimeSpan PositionCore { get; }

    /// <summary>
    /// How long the source is, or null when the platform does not know — a live stream, or a source still
    /// resolving. Only asked while a source is open.
    /// </summary>
    protected abstract TimeSpan? DurationCore { get; }

    /// <summary>
    /// Begin opening. Returns as soon as the platform has accepted the source; readiness is signalled later
    /// through <see cref="OnOpened"/> or <see cref="OnFailed"/>.
    /// </summary>
    /// <param name="source">The caller's source, for anything beyond the URI.</param>
    /// <param name="uri">The validated absolute URI — a file path has already become a <c>file:</c> URI.</param>
    protected abstract void OpenCore(MediaSource source, Uri uri);

    /// <summary>Position the freshly-opened source. Called from <see cref="OnOpened"/> only.</summary>
    protected abstract void ApplyStartAt(TimeSpan position);

    /// <summary>Start or resume at <paramref name="rate"/>.</summary>
    protected abstract void PlayCore(double rate);

    /// <summary>Hold at the current position.</summary>
    protected abstract void PauseCore();

    /// <summary>
    /// Move to <paramref name="position"/>. The returned task completes when the platform has finished
    /// seeking, where the platform says so — <see cref="Task.CompletedTask"/> where it is synchronous.
    /// </summary>
    protected abstract Task SeekCore(TimeSpan position);

    /// <summary>Change the speed of a PLAYING player.</summary>
    protected abstract void ApplyRateCore(double rate);

    /// <summary>
    /// Release the source. Called by <see cref="CloseAsync"/>, before every re-open, and by
    /// <see cref="Dispose"/> — so it must be safe with no source open.
    /// </summary>
    protected abstract void TeardownCore();

    /// <summary>
    /// Detach platform callbacks, before teardown. Override where the platform raises during disposal — a
    /// handler running then touches a half-disposed object from a thread nobody owns.
    /// </summary>
    protected virtual void DetachCore() { }

    /// <summary>Release the platform handle itself, after teardown.</summary>
    protected virtual void DisposeCore() { }

    // ---- Shared plumbing.

    /// <summary>
    /// A file path or an absolute URL, and nothing else. A relative string is REJECTED rather than resolved
    /// against the process's working directory, which is not where an app's media lives — on a phone that
    /// directory is not even writable.
    /// </summary>
    private static Uri? ParseUri(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && !parsed.IsFile) return parsed;
        return Path.IsPathRooted(uri) ? new Uri(uri) : null;
    }

    /// <summary>Move to <see cref="MediaPlayerState.Failed"/> with an app-safe reason.</summary>
    private void Fail(string reason, Exception? inner)
    {
        if (inner is not null) Log(() => $"MediaPlayer: {inner.GetType().Name}: {inner.Message}.");
        lock (_gate) { _state = MediaPlayerState.Failed; _error = reason; }
        Raise();
    }

    /// <summary>Invariant 4 — an open abandoned by a close or a re-open fails its caller rather than hanging.</summary>
    private void Teardown()
    {
        TaskCompletionSource? opening;
        lock (_gate)
        {
            opening = _opening;
            _opening = null;
            _hasSource = false;
        }

        opening?.TrySetException(new MediaPlayerException("The open was abandoned before it completed."));
        Try(TeardownCore, nameof(TeardownCore));
    }

    /// <summary>
    /// Raise <see cref="StateChanged"/> with a fresh snapshot.
    /// <para>
    /// ⚠ Guarded: a throwing handler is caught and logged rather than escaping into a platform callback,
    /// where an exception is not catchable by anyone.
    /// </para>
    /// </summary>
    private void Raise()
    {
        var handler = StateChanged;
        if (handler is null) return;
        var status = Status;
        AppCallback.Run(() => handler(status),
            ex => Log(() => $"A MediaPlayer state handler threw ({ex.GetType().Name}: {ex.Message})."));
    }

    /// <summary>
    /// Run a platform call, logging rather than throwing — the rule every transport verb follows.
    /// <para>
    /// PRIVATE deliberately: this class already wraps every <c>*Core</c> call, so a shell never needs it,
    /// and a protected helper named <c>Try</c> would be permanent public surface with a name that says
    /// nothing (the vocabulary rule in <c>generic-library</c>).
    /// </para>
    /// </summary>
    private void Try(Action action, string what)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log(() => $"MediaPlayer.{what} failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <summary>Read a platform value, falling back rather than throwing out of a property getter.</summary>
    private T Try<T>(Func<T> read, T fallback, string what)
    {
        try { return read(); }
        catch (Exception ex)
        {
            Log(() => $"MediaPlayer.{what} failed ({ex.GetType().Name}: {ex.Message}).");
            return fallback;
        }
    }

    /// <summary>Diagnostics, guarded, and only evaluated when a sink is attached.</summary>
    protected void Log(Func<string> message) => AppCallback.Log(_log, message);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Handlers off FIRST, then the source, then the handle — each step assumes the previous one ran.
        Try(DetachCore, nameof(DetachCore));
        Teardown();
        Try(DisposeCore, nameof(DisposeCore));
    }
}
