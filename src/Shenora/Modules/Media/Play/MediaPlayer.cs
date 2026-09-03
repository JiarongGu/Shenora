using Shenora.Core.Events;

namespace Shenora.Modules.Media;

/// <summary>
/// The event types <see cref="MediaPlayer"/> publishes to the page; the page's element driver reports back
/// through <see cref="MediaPlayer.Report"/>. A WIRE contract — the TypeScript half matches these by string,
/// so the payloads are described here rather than typed.
/// </summary>
public static class MediaPlayerEvents
{
    /// <summary>Point the element at a URL and prepare it, without playing: <c>{ uri, startAt }</c> (seconds).</summary>
    public const string Load = "PLAYER_LOAD";

    /// <summary>Start or resume. No payload.</summary>
    public const string Play = "PLAYER_PLAY";

    /// <summary>Hold at the current position. No payload.</summary>
    public const string Pause = "PLAYER_PAUSE";

    /// <summary>Move to an absolute position: <c>{ position }</c> (seconds).</summary>
    public const string Seek = "PLAYER_SEEK";

    /// <summary>Set the speed multiplier: <c>{ rate }</c>.</summary>
    public const string Rate = "PLAYER_RATE";

    /// <summary>Release the source — clear <c>src</c> and call <c>load()</c>, which is what frees the buffer. No payload.</summary>
    public const string Unload = "PLAYER_UNLOAD";
}

/// <summary>How <see cref="MediaPlayer"/> decides what the page should actually load.</summary>
public sealed class MediaPlayerOptions
{
    /// <summary>Read the source's container and streams. <c>null</c> means "I cannot tell" and is treated
    /// as "play it directly". Unset skips probing entirely.</summary>
    public Func<string, CancellationToken, Task<MediaProbeResult?>>? Probe { get; set; }

    /// <summary>
    /// What the page's element can play natively. Only consulted when <see cref="Probe"/> returned something.
    /// ⚠ The kit ships no codec list (D42) — build this from <see cref="IMediaCapability"/>, which asks the DEVICE.
    /// </summary>
    public MediaPlaybackPolicy? Policy { get; set; }

    /// <summary>
    /// How long <see cref="MediaPlayer.OpenAsync"/> waits for the page's first report before failing with a
    /// <see cref="MediaPlayerException"/> naming the likely cause — the missing <c>PLAYER_REPORT</c> route
    /// described on <see cref="MediaPlayer"/>. 30 s by default. ⚠ <see cref="TimeSpan.Zero"/> waits forever,
    /// an await that never returns when nothing answers.
    /// </summary>
    public TimeSpan OpenTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Turn a source and its plan into the URL the element loads: a <see cref="MediaPlaybackAction.Direct"/>
    /// plan returns a plain file URL, anything else the interceptor's conversion route. An empty string
    /// means "this cannot be played here"; unset passes the source straight through. ⚠ A <c>null</c> plan
    /// means nothing was probed or no policy was supplied — "play it directly", not an error.
    /// </summary>
    public Func<string, MediaPlaybackPlan?, string>? ResolveUri { get; set; }

    /// <summary>
    /// Where the conversion route may read from, where its output is cached, and which module its routes and
    /// the player's own commands publish on — the containment boundary shared with every other media delivery
    /// path (<see cref="MediaAccessOptions"/>). <see cref="MediaAccessOptions.AllowedRoots"/> is never
    /// defaulted, being what stops a page-supplied path escaping into the rest of the disk; empty means no
    /// file-serving conversion is wired, the zero-configuration case.
    /// <para>
    /// ⚠ <b><see cref="MediaAccessOptions.Resolve"/> is inert on this particular <see cref="Access"/>.</b>
    /// <c>UseMediaPlayer</c> mounts its OWN <see cref="MediaConversionOptions"/> with a resolver built from
    /// <c>MediaPlayerRoute</c>, and borrows only <see cref="MediaAccessOptions.AllowedRoots"/>,
    /// <c>CacheRoot</c> and <c>Module</c>. It is <c>required</c> on the type, so set it to
    /// <c>static _ =&gt; null</c>.
    /// </para>
    /// </summary>
    public MediaAccessOptions Access { get; set; } = new()
    {
        Resolve = static _ => null,
        // "" is a PLACEHOLDER, not a refusal: `UseMediaPlayer` substitutes `paths.DataArea("media")` the
        // first time `IMediaPlayer` is resolved, so registration itself touches no disk (D64).
        CacheRoot = string.Empty,
    };
}

/// <summary>
/// <b>The kit's media player.</b> Its lifecycle lives in .NET — probe, plan, resolve, and the state machine
/// across all three shells; its display and sound are a page element. It talks to the page over
/// <see cref="IEventBus"/>, and the page answers by calling <see cref="Report"/> from its IPC route.
/// <para>
/// 🔴 <b>That return route is the APP's to write and the kit ships no facade for it</b> — the page posts
/// <c>PLAYER_REPORT</c> on <see cref="MediaAccessOptions.Module"/> and something must turn it into a
/// <see cref="Report"/> call. <see cref="OpenAsync"/> completes on the first non-<c>Opening</c> report and
/// on nothing else, so skipping the route makes every open fail after
/// <see cref="MediaPlayerOptions.OpenTimeout"/>. <c>docs/ADOPTION.md</c> has the four-line route.
/// </para>
/// <para>
/// It does NOT replace <c>IosMediaPlayer</c>/<c>AndroidMediaPlayer</c>: a page element cannot play while iOS
/// has the app backgrounded. Both are <see cref="IMediaPlayer"/>, so an app can swap which it holds.
/// </para>
/// </summary>
public sealed class MediaPlayer : IMediaPlayer, IDisposable
{
    private readonly IEventBus _events;
    private readonly MediaPlayerOptions _options;
    private readonly object _gate = new();

    private MediaPlayerStatus _status = new() { State = MediaPlayerState.Empty };
    private TaskCompletionSource? _opening;
    private double _rate = 1.0;
    private bool _disposed;

    /// <param name="events">The bus the page listens on.</param>
    /// <param name="options">How to decide what the page loads.</param>
    public MediaPlayer(IEventBus events, MediaPlayerOptions options)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Access);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Access.Module);

        _events = events;
        _options = options;
    }

    /// <inheritdoc />
    public event Action<MediaPlayerStatus>? StateChanged;

    /// <inheritdoc />
    /// <summary>
    /// <inheritdoc />
    /// <para>
    /// ⚠ <see cref="MediaPlayerStatus.Engine"/> is stamped HERE rather than at each of the six places
    /// <c>_status</c> is built — one of those is the page's own report, and the page must not be able to
    /// name the engine any more than it can name the rate.
    /// </para>
    /// </summary>
    public MediaPlayerStatus Status { get { lock (_gate) return _status with { Engine = EngineName }; } }

    /// <summary>What <see cref="MediaPlayerStatus.Engine"/> reports. The type name, as
    /// <see cref="MediaPlayerBase.EngineName"/> does — this class is not that one's subclass, and the two
    /// answer the same question the same way on purpose.</summary>
    private string EngineName => GetType().Name;

    /// <inheritdoc />
    public Task SetRateAsync(double rate, CancellationToken cancellationToken = default)
    {
        if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate), "Rate must be greater than zero.");
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { _rate = rate; _status = _status with { Rate = rate }; }
        Send(MediaPlayerEvents.Rate, new { rate });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task OpenAsync(MediaSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(source.Uri)) throw new MediaPlayerException("Media source URI is empty.");

        TaskCompletionSource opening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            // A second Open supersedes the first: its waiter is cancelled rather than left hanging.
            _opening?.TrySetCanceled();
            _opening = opening;
            _status = new MediaPlayerStatus { State = MediaPlayerState.Opening, Rate = _rate };
        }
        Raise();

        // PROBE → PLAN → URL. Each step degrades to "play it directly".
        MediaProbeResult? probe = null;
        if (_options.Probe is { } read)
        {
            try
            {
                probe = await read(source.Uri, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A probe that throws is NOT fatal — the element may well play the file anyway.
                Log(() => "[Shenora.Modules.Media] probe failed, playing directly.", ex);
            }
        }

        var plan = probe is not null && _options.Policy is { } policy
            ? MediaPlaybackPlanner.Plan(probe, policy)
            : null;

        string uri;
        try
        {
            uri = _options.ResolveUri is { } resolve ? resolve(source.Uri, plan) : source.Uri;
        }
        catch (Exception ex)
        {
            Fail("The media source could not be resolved.");
            throw new MediaPlayerException("The media source could not be resolved.", ex);
        }

        if (string.IsNullOrWhiteSpace(uri))
        {
            Fail("The media source cannot be played on this device.");
            throw new MediaPlayerException("The media source cannot be played on this device.");
        }

        Send(MediaPlayerEvents.Load, new { uri, startAt = source.StartAt.TotalSeconds });

        // The PAGE decides when it is ready. Bounded, so a missing PLAYER_REPORT route fails with a message
        // instead of hanging forever.
        using var expiry = _options.OpenTimeout > TimeSpan.Zero
            ? new CancellationTokenSource(_options.OpenTimeout)
            : null;
        using var linked = expiry is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        var waitToken = linked?.Token ?? cancellationToken;

        using var registration = waitToken.Register(() => opening.TrySetCanceled(waitToken));
        try
        {
            await opening.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ⚠ ONLY tear down if this open is still the current one: being SUPERSEDED by a later OpenAsync
            // means do nothing, because the successor already owns the element.
            bool superseded;
            lock (_gate) superseded = !ReferenceEquals(_opening, opening);
            if (!superseded)
            {
                Send(MediaPlayerEvents.Unload);
                lock (_gate) _status = new MediaPlayerStatus { State = MediaPlayerState.Empty, Rate = _rate };
                Raise();
            }

            // A timeout is a different outcome from the caller cancelling — and a cancel DURING the timeout
            // window is still a cancel, hence this order.
            if (expiry is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
            {
                Fail("The page never reported on the media source.");
                throw new MediaPlayerException(
                    $"The page did not report on the media source within {_options.OpenTimeout.TotalSeconds:0.#}s. " +
                    $"The usual cause is that nothing routes the page's '{MediaPlayerModule.ReportType}' message on " +
                    $"module '{_options.Access.Module}' to MediaPlayer.Report — see docs/ADOPTION.md. " +
                    "Raise MediaPlayerOptions.OpenTimeout if the source is genuinely this slow.");
            }
            throw;
        }
    }

    /// <inheritdoc />
    public Task PlayAsync(CancellationToken cancellationToken = default)
    {
        RequireLoaded();
        Send(MediaPlayerEvents.Play);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        RequireLoaded();
        Send(MediaPlayerEvents.Pause);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        RequireLoaded();
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        Send(MediaPlayerEvents.Seek, new { position = position.TotalSeconds });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate) { _opening?.TrySetCanceled(); _opening = null; }

        Send(MediaPlayerEvents.Unload);
        lock (_gate) _status = new MediaPlayerStatus { State = MediaPlayerState.Empty, Rate = _rate };
        Raise();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The page saying what its element is doing — called from the app's IPC route. Position and duration
    /// come from HERE and nowhere else; nothing else has a clock.
    /// <para>
    /// ⚠ <b>Report on TRANSITIONS, not on a timer</b> — <c>loadedmetadata</c>, <c>canplay</c>, <c>play</c>,
    /// <c>pause</c>, <c>waiting</c>, <c>seeked</c>, <c>ended</c>, <c>error</c>, and no more.
    /// </para>
    /// </summary>
    public void Report(MediaPlayerStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        TaskCompletionSource? opening;
        lock (_gate)
        {
            // ⚠ The rate stays the player's: taking the page's verbatim silently resets a configured 1.5x
            // on the first report from a page that does not carry one.
            _status = status with { Rate = _rate };
            opening = _opening;

            if (opening is not null && status.State is not MediaPlayerState.Opening) _opening = null;
            else opening = null;
        }

        Raise();

        // Settled OUTSIDE the lock: a completion runs subscribers via the awaiting caller, and settling
        // under `_gate` would hold it across app code.
        if (opening is null) return;
        if (status.State == MediaPlayerState.Failed)
            opening.TrySetException(new MediaPlayerException(status.Error ?? "The media source could not be played."));
        else
            opening.TrySetResult();
    }

    /// <summary>Emit without awaiting — <see cref="IEventBus.Emit(string, string, object?, string?)"/> runs
    /// every handler inside the bus's own guard, so a subscriber cannot fault the emit.</summary>
    private void Send(string type, object? payload = null) => _events.Emit(_options.Access.Module, type, payload);

    private void RequireLoaded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_status.State is MediaPlayerState.Empty) throw new MediaPlayerException("No media source is open.");
        }
    }

    private void Fail(string reason)
    {
        lock (_gate) _status = new MediaPlayerStatus { State = MediaPlayerState.Failed, Error = reason, Rate = _rate };
        Raise();
    }

    private void Raise()
    {
        var handler = StateChanged;
        if (handler is null) return;
        var status = Status;
        AppCallback.Run(() => handler(status),
            ex => Log(() => "[Shenora.Modules.Media] A player state handler threw.", ex));
    }

    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_options.Access.Log, message, exception: failure);

    /// <summary>Stop accepting reports. Does not unload the page's element — that is <see cref="CloseAsync"/>.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) _opening?.TrySetCanceled();
    }
}
