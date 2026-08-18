using Microsoft.Extensions.Logging;

namespace Shenora.Modules.Media;

/// <summary>
/// The <see cref="IMediaPlayer"/> STATE MACHINE, with the platform left abstract.
/// <para>🔴 <b>Four invariants live here, and each is invisible when broken:</b></para>
/// <list type="number">
///   <item><b>A terminal state is never overwritten by a platform transition.</b> Every platform drives
///   its session to "paused" once it ends or fails, erasing <see cref="MediaPlayerState.Ended"/> and
///   <see cref="MediaPlayerState.Failed"/> microseconds after they are raised — a failed open then reports
///   as a healthy paused player carrying an error string nothing displays.</item>
///   <item><b>A rate set while paused is remembered, not applied.</b> On AVFoundation rate and transport
///   are the SAME control, so pushing a remembered 1.5× would silently start a paused player.</item>
///   <item><b>A cancelled open leaves the player <see cref="MediaPlayerState.Empty"/>, not half-loaded</b>,
///   so a retry does not inherit the previous attempt's source.</item>
///   <item><b>An abandoned open completes EXCEPTIONALLY.</b> Re-opening or closing while an open is in
///   flight otherwise leaves its caller awaiting forever — no exception, no log.</item>
/// </list>
/// <para>
/// <b>A shell supplies</b> the platform handle, four transport verbs, position/duration, and the callbacks
/// <see cref="OnOpened"/> / <see cref="OnEnded"/> / <see cref="OnFailed"/> / <see cref="OnPlatformState"/>.
/// </para>
/// <para>
/// ⚠ Not the page-backed <see cref="MediaPlayer"/>'s base — that one's "platform" is a webview element
/// reporting over IPC.
/// </para>
/// </summary>
public abstract class MediaPlayerBase : IMediaPlayer, IDisposable
{
    private readonly ILogger? _log;
    private readonly object _gate = new();

    private TaskCompletionSource? _opening;
    private TimeSpan _startAt;
    private MediaPlayerState _state = MediaPlayerState.Empty;
    private double _rate = 1.0;
    private string? _error;
    private bool _hasSource;
    private bool _disposed;

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into a platform callback.</param>
    protected MediaPlayerBase(ILogger? log = null) => _log = log;

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
                    // A released handle answers with whatever it last held — a position outliving CloseAsync.
                    Position = _hasSource ? Try(() => PositionCore, TimeSpan.Zero, nameof(PositionCore)) : TimeSpan.Zero,
                    Duration = _hasSource ? Try(() => DurationCore, null, nameof(DurationCore)) : null,
                    Rate = _rate,
                    Error = _error,
                };
            }
        }
    }

    /// <summary>
    /// What this player believes it is doing, without asking the platform for a position or duration. For a
    /// callback meaningful in only one state: AVFoundation reports "the buffer ran dry" whether or not
    /// anything was playing, so forwarding it blindly leaves a PAUSED player
    /// <see cref="MediaPlayerState.Buffering"/> for good.
    /// </summary>
    protected MediaPlayerState State { get { lock (_gate) return _state; } }

    /// <inheritdoc />
    public Task SetRateAsync(double rate, CancellationToken cancellationToken = default)
    {
        if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate), "Rate must be greater than zero.");
        cancellationToken.ThrowIfCancellationRequested();
        bool playing;
        lock (_gate)
        {
            _rate = rate;
            playing = _state == MediaPlayerState.Playing;
        }

        // Invariant 2: only pushed while PLAYING.
        if (playing) Try(() => ApplyRateCore(rate), nameof(SetRateAsync));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OpenAsync(MediaSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(source.Uri)) throw new MediaPlayerException("Media source URI is empty.");

        // Names what is actually wrong: only a RELATIVE string reaches here now, and the fix is to root
        // it. The old wording ("not a file path or an absolute URL") was the message a `file:` URL got
        // while being both of those things.
        // ⚠ The offending value is NOT interpolated. This type documents its message as an "app-safe
        // reason", and an adopter reporting `ex.Message` onward is the exact copy-paste `IpcErrorMapping`
        // exists to prevent — a caller's path must not become the thing that leaks through it.
        var uri = ParseUri(source.Uri)
            ?? throw new MediaPlayerException(
                "Media source URI is relative — pass a rooted path or an absolute URL.");

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

        // The remembered rate goes WITH the start, so a player set to 1.5× while paused starts there.
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
            // Left Ended, a UI that seeks back from the end still shows "finished".
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
            // A failed seek is not a failed PLAYER — the source is still open at its old position.
            Log(() => "MediaPlayer.SeekAsync failed.", ex);
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

        // Part of OPENING, not a seek afterwards: a resumed item otherwise starts at zero and jumps.
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
    /// <para>🔴 Invariant 1: a TERMINAL state wins.</para>
    /// <para>A transition matching what we already believe raises NOTHING: it is not a tick.</para>
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
    /// How long the source is, or null when the platform does not know (a live stream, a source still
    /// resolving). Only asked while a source is open.
    /// </summary>
    protected abstract TimeSpan? DurationCore { get; }

    /// <summary>
    /// Begin opening. Returns once the platform has accepted the source; readiness is signalled later
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
    /// A file path or an absolute URL, and nothing else. A relative string is REJECTED, never resolved
    /// against the process's working directory — that is not where an app's media lives.
    /// <para>
    /// ⚠ <b>A <c>file:</c> URL is accepted, and used not to be.</b> The guard read <c>!parsed.IsFile</c>,
    /// so <c>new Uri(path).AbsoluteUri</c> — the obvious thing for a .NET caller to hand over — matched
    /// neither branch and was refused as "not a file path or an absolute URL", naming both of the things
    /// it IS. Found by the first adopter, one failed open each. It costs nothing to accept: the rooted
    /// branch below already returns a <c>file:</c> URI, so every consumer downstream was handling one
    /// anyway (<c>IosMediaPlayer</c> branches on <c>IsFile</c> and takes <c>LocalPath</c>).
    /// </para>
    /// </summary>
    private static Uri? ParseUri(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return parsed;
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
    /// Raise <see cref="StateChanged"/> with a fresh snapshot. ⚠ A throwing handler is caught and logged
    /// rather than escaping into a platform callback, where nobody can catch it.
    /// </summary>
    private void Raise()
    {
        var handler = StateChanged;
        if (handler is null) return;
        var status = Status;
        AppCallback.Run(() => handler(status),
            ex => Log(() => "A MediaPlayer state handler threw.", ex));
    }

    /// <summary>Run a platform call, logging rather than throwing — the rule every transport verb follows.</summary>
    private void Try(Action action, string what)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log(() => $"MediaPlayer.{what} failed.", ex);
        }
    }

    /// <summary>Read a platform value, falling back rather than throwing out of a property getter.</summary>
    private T Try<T>(Func<T> read, T fallback, string what)
    {
        try { return read(); }
        catch (Exception ex)
        {
            Log(() => $"MediaPlayer.{what} failed.", ex);
            return fallback;
        }
    }

    /// <summary>Diagnostics, guarded, and only evaluated when a sink is attached.</summary>
    protected void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_log, message, exception: failure);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Order matters: handlers off, then the source, then the handle.
        Try(DetachCore, nameof(DetachCore));
        Teardown();
        Try(DisposeCore, nameof(DisposeCore));
    }
}
