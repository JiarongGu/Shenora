namespace Shenora.Media;

/// <summary>
/// How <see cref="MediaPlayer"/> decides what a surface should actually load.
/// </summary>
public sealed class MediaPlayerOptions
{
    /// <summary>
    /// Read the source's container and streams. Return <c>null</c> for "I cannot tell", which is treated as
    /// "play it directly and let the surface decide" — the honest fallback, since a surface refusing a file
    /// is a real answer and a probe that guessed would be a worse one.
    /// <para>
    /// Leave it unset to skip probing entirely: every source is then loaded as-is, which is exactly the
    /// behaviour an app had before this type existed.
    /// </para>
    /// </summary>
    public Func<string, CancellationToken, Task<MediaProbeResult?>>? Probe { get; init; }

    /// <summary>
    /// What the surface can play natively. Only consulted when <see cref="Probe"/> returned something.
    /// <para>
    /// ⚠ <b>The kit ships no codec list (D42)</b> — build this from <see cref="IMediaCapability"/>, which
    /// asks the DEVICE, rather than hard-coding a set that is wrong on some phone you do not own.
    /// </para>
    /// </summary>
    public MediaPlaybackPolicy? Policy { get; init; }

    /// <summary>
    /// Turn a source and its plan into the URL the surface loads. Required.
    /// <para>
    /// <b>This is the join the framework was missing, and the reason this type exists.</b> A plan of
    /// <see cref="MediaPlaybackAction.Direct"/> returns a plain file URL; anything else returns the
    /// interceptor's conversion route for that source — so the SAME pipeline a consumer already extends
    /// (<see cref="MediaConversionOptions.Convert"/>, <see cref="IMediaAudioConversion"/>,
    /// <see cref="IMediaContainerWriter"/>) serves the player too. A consumer who wrote their own converter
    /// does not write a second one to use a player.
    /// </para>
    /// <para>
    /// ⚠ The plan is <c>null</c> when nothing was probed or no policy was supplied. Treat that as "play it
    /// directly"; it is not an error.
    /// </para>
    /// </summary>
    public required Func<string, MediaPlaybackPlan?, string> ResolveUri { get; init; }

    /// <summary>Diagnostics. Guarded — a throwing sink never escapes into a callback.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// <b>The kit's media player.</b> Its lifecycle lives in .NET; its display and sound are a page element —
/// the shape the owner named on 2026-08-07: *"the .net one is a proper player but using web as its display
/// and sound"*.
/// <para>
/// <b>It is called <c>MediaPlayer</c> and not <c>WebMediaPlayer</c> deliberately</b> (owner, same day:
/// *"you can just call it MediaPlayer, since the hybrid is our feature"*). A <c>Web</c> prefix would frame
/// rendering-through-the-page as a variant of some purer thing. It is not: **a hybrid framework rendering
/// through the page IS the normal case**, and the native player is the special one, reached for when a
/// platform will not let the page do its job.
/// </para>
/// <para>
/// <b>What .NET owns here is the part React cannot do well</b>, which is the D54 test applied to playback
/// rather than to a format: probing a container, deciding against a DEVICE capability query whether it can
/// be played as-is, driving a conversion when it cannot, and holding the state machine across it all. What
/// the page owns is drawing pixels and making sound, which is the one part it is genuinely better at.
/// </para>
/// <para>
/// <b>⚠ It does NOT replace <c>MobileMediaPlayer</c>; the two answer different questions.</b> A page
/// element cannot play while iOS has the app backgrounded — that is the gap a native player exists for.
/// This one wins everywhere else: it renders into the app's own layout, it costs no native surface, and it
/// reuses the conversion pipeline. **Both are <see cref="IMediaPlayer"/>**, so an app can hold one field
/// and swap which it points at — and <c>ReportTo(session)</c> keeps Now Playing honest for either.
/// </para>
/// <para>
/// Not registered by any shell. An app constructs it with the surface it wants driven, because only the
/// app knows which element in its own page that is.
/// </para>
/// </summary>
public sealed class MediaPlayer : IMediaPlayer, IDisposable
{
    private readonly IMediaRenderTarget _target;
    private readonly MediaPlayerOptions _options;
    private readonly object _gate = new();

    private MediaPlayerStatus _status = new() { State = MediaPlayerState.Empty };
    private TaskCompletionSource? _opening;
    private double _rate = 1.0;
    private bool _disposed;

    /// <param name="target">Where output lands.</param>
    /// <param name="options">How to decide what the target loads.</param>
    public MediaPlayer(IMediaRenderTarget target, MediaPlayerOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ResolveUri);

        _target = target;
        _options = options;
        _target.Reported += OnReported;
    }

    /// <inheritdoc />
    public event Action<MediaPlayerStatus>? StateChanged;

    /// <inheritdoc />
    public MediaPlayerStatus Status { get { lock (_gate) return _status; } }

    /// <inheritdoc />
    public double Rate
    {
        get { lock (_gate) return _rate; }
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "Rate must be greater than zero.");
            lock (_gate) { _rate = value; _status = _status with { Rate = value }; }
            // Fire-and-forget by design: a rate change is advisory and a surface that has nothing loaded
            // simply ignores it. Awaiting here would make a property setter block on IPC.
            _ = Guarded(() => _target.SetRateAsync(value), nameof(Rate));
        }
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
            // A second Open supersedes the first: its waiter is cancelled rather than left hanging, which
            // is what a user rapidly switching tracks actually does.
            _opening?.TrySetCanceled();
            _opening = opening;
            _status = new MediaPlayerStatus { State = MediaPlayerState.Opening, Rate = _rate };
        }
        Raise();

        // PROBE → PLAN → URL. All three are optional in the sense that each degrades to "play it directly",
        // and that chain is the whole point of this class: the app asks for a source, not for a decision.
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
                // A probe that throws is NOT fatal: the surface may well play the file anyway, and refusing
                // here would make the player stricter than the thing it is driving.
                Log(() => $"[Shenora.Media] probe failed, playing directly ({ex.GetType().Name}).");
            }
        }

        var plan = probe is not null && _options.Policy is { } policy
            ? MediaPlaybackPlanner.Plan(probe, policy)
            : null;

        string uri;
        try
        {
            uri = _options.ResolveUri(source.Uri, plan);
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

        await _target.LoadAsync(uri, source.StartAt, cancellationToken).ConfigureAwait(false);

        // The SURFACE decides when it is ready; this waits for its report rather than assuming.
        using var registration = cancellationToken.Register(() => opening.TrySetCanceled(cancellationToken));
        try
        {
            await opening.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ⚠ ONLY tear down if this open is still the current one. A cancellation has two causes and
            // they need opposite handling: the CALLER's token (clean up — nothing else is running) versus
            // being SUPERSEDED by a later OpenAsync (do nothing — the successor already owns the target,
            // and unloading here would rip the source out from under it). Found by a test: without this
            // check, opening a second track while the first was still loading left the surface empty.
            bool superseded;
            lock (_gate) superseded = !ReferenceEquals(_opening, opening);
            if (!superseded)
            {
                await Guarded(() => _target.UnloadAsync(CancellationToken.None), nameof(OpenAsync)).ConfigureAwait(false);
                lock (_gate) _status = new MediaPlayerStatus { State = MediaPlayerState.Empty, Rate = _rate };
                Raise();
            }
            throw;
        }
    }

    /// <inheritdoc />
    public Task PlayAsync(CancellationToken cancellationToken = default)
    {
        RequireLoaded();
        return _target.PlayAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        RequireLoaded();
        return _target.PauseAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        RequireLoaded();
        return _target.SeekAsync(position < TimeSpan.Zero ? TimeSpan.Zero : position, cancellationToken);
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate) { _opening?.TrySetCanceled(); _opening = null; }

        await _target.UnloadAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate) _status = new MediaPlayerStatus { State = MediaPlayerState.Empty, Rate = _rate };
        Raise();
    }

    /// <summary>
    /// The surface reported. ⚠ Position and duration come from HERE and nowhere else — the element is the
    /// thing actually advancing, so anything this class computed itself would be a second, worse clock.
    /// </summary>
    private void OnReported(MediaPlayerStatus reported)
    {
        TaskCompletionSource? opening;
        lock (_gate)
        {
            // The rate stays the player's: a surface that does not carry one reports the default, and
            // taking it verbatim would silently reset a configured 1.5x on the first report.
            _status = reported with { Rate = _rate };
            opening = _opening;

            if (opening is not null && reported.State is not MediaPlayerState.Opening)
            {
                _opening = null;
            }
            else
            {
                opening = null;
            }
        }

        Raise();

        // Settled OUTSIDE the lock: a continuation runs on this thread otherwise, under a lock this class
        // also takes from its own public methods.
        if (opening is null) return;
        if (reported.State == MediaPlayerState.Failed)
            opening.TrySetException(new MediaPlayerException(reported.Error ?? "The media source could not be played."));
        else
            opening.TrySetResult();
    }

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
        Core.AppCallback.Run(() => handler(status),
            ex => Log(() => $"[Shenora.Media] A player state handler threw ({ex.GetType().Name}: {ex.Message})."));
    }

    private async Task Guarded(Func<Task> work, string what)
    {
        try { await work().ConfigureAwait(false); }
        catch (Exception ex) { Log(() => $"[Shenora.Media] MediaPlayer.{what} failed ({ex.GetType().Name}: {ex.Message})."); }
    }

    private void Log(Func<string> message) => Core.AppCallback.Log(_options.Log, message);

    /// <summary>Stop following the surface. Does not unload it — that is <see cref="CloseAsync"/>.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _target.Reported -= OnReported;
        lock (_gate) _opening?.TrySetCanceled();
    }
}
