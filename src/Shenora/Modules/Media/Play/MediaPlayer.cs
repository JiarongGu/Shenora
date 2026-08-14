using Shenora;
using Shenora.Core.Events;
using Shenora.Core.Ipc;

namespace Shenora.Modules.Media;

/// <summary>
/// The event types <see cref="MediaPlayer"/> publishes to the page. The page's element driver subscribes
/// to these and acts on them; it reports back through <see cref="MediaPlayer.Report"/>.
/// <para>
/// Constants rather than an enum, and payloads described here rather than typed, for the same reason
/// <see cref="MediaConversionEvents"/> does it: this is a WIRE contract, and the TypeScript half has to
/// agree with it by string.
/// </para>
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

/// <summary>
/// How <see cref="MediaPlayer"/> decides what the page should actually load.
/// </summary>
public sealed class MediaPlayerOptions
{
    /// <summary>
    /// Read the source's container and streams. Return <c>null</c> for "I cannot tell", which is treated as
    /// "play it directly and let the element decide" — the honest fallback, since an element refusing a
    /// file is a real answer and a probe that guessed would be a worse one.
    /// <para>
    /// Leave it unset to skip probing entirely: every source is then loaded as-is, which is exactly the
    /// behaviour an app had before this type existed.
    /// </para>
    /// </summary>
    public Func<string, CancellationToken, Task<MediaProbeResult?>>? Probe { get; set; }

    /// <summary>
    /// What the page's element can play natively. Only consulted when <see cref="Probe"/> returned something.
    /// <para>
    /// ⚠ <b>The kit ships no codec list (D42)</b> — build this from <see cref="IMediaCapability"/>, which
    /// asks the DEVICE, rather than hard-coding a set that is wrong on some phone you do not own.
    /// </para>
    /// </summary>
    public MediaPlaybackPolicy? Policy { get; set; }

    /// <summary>
    /// How long <see cref="MediaPlayer.OpenAsync"/> waits for the page's first report before failing with
    /// a <see cref="MediaPlayerException"/> that NAMES the likely cause. 30 s by default;
    /// <see cref="TimeSpan.Zero"/> waits forever.
    /// <para>
    /// 🔴 <b>This exists to make one specific silence attributable.</b> <c>OpenAsync</c> completes on the
    /// page's first non-<c>Opening</c> report and on nothing else, so an app whose <c>PLAYER_REPORT</c>
    /// route is missing or mis-named gets an await that NEVER RETURNS — no exception, no log line, and an
    /// element that is visibly playing. "Nobody wired the route" and "the file is slow to open" were the
    /// same silence, and only one of them is a bug. A bounded wait turns the first into a message that
    /// says what to check.
    /// </para>
    /// <para>
    /// ⚠ The timeout is not a correctness boundary and must not be read as one: a genuinely slow source
    /// on a cold network can exceed any default. Raise it for those, or set
    /// <see cref="TimeSpan.Zero"/> and accept the original behaviour — the failure it removes is a WIRING
    /// mistake, which is a build-time fact rather than a runtime one.
    /// </para>
    /// </summary>
    public TimeSpan OpenTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Turn a source and its plan into the URL the element loads. Required.
    /// <para>
    /// <b>This is the join the framework was missing, and the reason this type exists (D58).</b> A plan of
    /// <see cref="MediaPlaybackAction.Direct"/> returns a plain file URL; anything else returns the
    /// interceptor's conversion route for that source — so the SAME pipeline a consumer already extends
    /// (<see cref="MediaConversionOptions.Convert"/>, <see cref="IMediaStreamConversion"/>,
    /// <see cref="IMediaContainerWriter"/>) serves the player too. A consumer who wrote their own converter
    /// does not write a second one to use a player.
    /// </para>
    /// <para>
    /// Return an empty string for "this cannot be played here" — the planner's
    /// <see cref="MediaPlaybackAction.Unsupported"/> reaching its natural conclusion.
    /// </para>
    /// <para>
    /// ⚠ The plan is <c>null</c> when nothing was probed or no policy was supplied. Treat that as "play it
    /// directly"; it is not an error.
    /// </para>
    /// <para>
    /// <b>Leave it unset and the source is passed straight through.</b> That is the right default and not a
    /// placeholder: a file the device can already decode needs no URL rewriting, and an app whose media
    /// plays should not have to say so. Set it when there is a conversion route to point at.
    /// </para>
    /// </summary>
    public Func<string, MediaPlaybackPlan?, string>? ResolveUri { get; set; }

    /// <summary>
    /// Where the conversion route may read from, where its output is cached, and which module its routes and
    /// the player's own commands publish on — the containment boundary shared with every other media
    /// delivery path (<see cref="MediaAccessOptions"/>), stated ONCE instead of a fourth time here.
    /// <para>
    /// ⚠ <b>The kit cannot default <see cref="MediaAccessOptions.AllowedRoots"/> and deliberately does not
    /// try.</b> It is the containment boundary that stops a page-supplied path escaping into the rest of the
    /// disk, so a default would be the kit making a data-access decision on the app's behalf — the same
    /// reasoning that keeps a loopback-gate helper (D10) and a page-diagnostic facade (D60) out of the
    /// surface. **This is why <c>UseMediaPlayer()</c> and <c>UseMediaPlayer(x => …)</c> split where they
    /// do:** the security decision and the ergonomic one are the same decision. Empty means no file-serving
    /// conversion is wired, which is the zero-configuration case.
    /// </para>
    /// <para>
    /// 🔴 <b>DELIBERATELY <c>{ get; set; }</c>, not <c>{ get; init; }</c> like the other two options types
    /// that carry a <see cref="MediaAccessOptions"/> — the one principled exception in this shared type's
    /// rollout.</b> <see cref="MediaConversionOptions"/> and <see cref="SegmentStreamOptions"/>
    /// are always fully specified in ONE object initializer at their call site, so
    /// <c>{ get; init; }</c> costs nothing. This type is configured through
    /// <c>UseMediaPlayer((options, services) => …)</c>'s callback instead — an <c>Action</c> invoked AFTER
    /// construction — and an <c>init</c> accessor cannot be assigned from outside an object initializer or
    /// the declaring type's own constructor, so the callback could never set it at all. Worse, leaving
    /// <see cref="MediaAccessOptions.CacheRoot"/> unset here is not a mistake to reject; it is how an app
    /// asks for the free default — <c>UseMediaPlayer</c> replaces this whole object with one carrying
    /// <c>paths.DataArea("media")</c> the first time <see cref="IMediaPlayer"/> is resolved, and doing that
    /// also needs a settable property. <see cref="MediaAccessOptions"/> ITSELF keeps every member exactly as
    /// specified — this is a looser property on the box it sits in, not a looser box.
    /// </para>
    /// <para>
    /// ⚠ <b><see cref="MediaAccessOptions.Resolve"/> is inert on this particular <see cref="Access"/>.</b>
    /// This type never calls it: <c>UseMediaPlayer(interceptor, services)</c> mounts its OWN
    /// <see cref="MediaConversionOptions"/> with a resolver built from <c>MediaPlayerRoute</c>, and only
    /// borrows this object's <see cref="MediaAccessOptions.AllowedRoots"/>, <c>CacheRoot</c> and
    /// <c>Module</c>. It still has to be supplied, because <see cref="MediaAccessOptions.Resolve"/> is
    /// <c>required</c> on the type every other delivery path DOES call it through — set it to
    /// <c>static _ =&gt; null</c> and move on, exactly as the default below does.
    /// </para>
    /// </summary>
    public MediaAccessOptions Access { get; set; } = new()
    {
        // Never called from here — see the remarks above. A no-op keeps the required member satisfied
        // without implying this type has a resolver of its own.
        Resolve = static _ => null,
        // The empty string is a PLACEHOLDER, not a refusal: `UseMediaPlayer` reads it as "not chosen yet"
        // and substitutes `paths.DataArea("media")` the first time `IMediaPlayer` is resolved, so
        // registration itself still touches no disk (D64).
        CacheRoot = string.Empty,
    };
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
/// <b>What .NET owns here is the part React cannot do well</b> — the D54 test applied to playback rather
/// than to a format: probing a container, deciding against a DEVICE capability query whether it can be
/// played as-is, pointing at a conversion when it cannot, and holding the state machine across it all.
/// What the page owns is drawing pixels and making sound, which is the one part it is genuinely better at.
/// </para>
/// <para>
/// <b>It talks to the page over <see cref="IEventBus"/>, the same channel the conversion route already
/// uses</b>, and the page answers by calling <see cref="Report"/> from its IPC route.
/// 🔴 <b>⚠ That return route is the APP's to write and the kit ships no facade for it</b> — the page
/// posts <c>PLAYER_REPORT</c> on <see cref="MediaAccessOptions.Module"/> (<see cref="MediaPlayerOptions.Access"/>) and something must turn it into
/// a <see cref="Report"/> call. <see cref="OpenAsync"/> completes on the first non-<c>Opening</c> report
/// and on nothing else, so an app that skips the route gets an <see cref="OpenAsync"/> that never
/// returns, with no exception and no log line. `docs/ADOPTION.md` has the four-line route. ⚠ There is
/// deliberately NO surface interface between the two: an earlier draft had one, and it had exactly one
/// production implementation — the page element — which is the seam D52 already refused to build for
/// probing. <see cref="IMediaPlayer"/> is the seam; a second one underneath it was scaffolding.
/// </para>
/// <para>
/// <b>⚠ It does NOT replace <c>IosMediaPlayer</c>/<c>AndroidMediaPlayer</c>; the two answer different questions.</b> A page
/// element cannot play while iOS has the app backgrounded — that is the gap a native player exists for.
/// This one wins everywhere else: it renders into the app's own layout, it costs no native surface, and it
/// reuses the conversion pipeline. **Both are <see cref="IMediaPlayer"/>**, so an app can hold one field
/// and swap which it points at — and <c>ReportTo(session)</c> keeps Now Playing honest for either.
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
    public MediaPlayerStatus Status { get { lock (_gate) return _status; } }

    /// <inheritdoc />
    public double Rate
    {
        get { lock (_gate) return _rate; }
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "Rate must be greater than zero.");
            lock (_gate) { _rate = value; _status = _status with { Rate = value }; }
            Send(MediaPlayerEvents.Rate, new { rate = value });
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

        // PROBE → PLAN → URL. Each step degrades to "play it directly", and that chain is the whole point
        // of this class: the app asks for a source, not for a decision.
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
                // A probe that throws is NOT fatal: the element may well play the file anyway, and refusing
                // here would make the player stricter than the thing it is driving.
                Log(() => $"[Shenora.Modules.Media] probe failed, playing directly ({ex.GetType().Name}).");
            }
        }

        var plan = probe is not null && _options.Policy is { } policy
            ? MediaPlaybackPlanner.Plan(probe, policy)
            : null;

        string uri;
        try
        {
            // Unset means pass the source straight through — the no-gap case, which is most of them.
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

        // The PAGE decides when it is ready; this waits for its report rather than assuming — but BOUNDED,
        // so a missing PLAYER_REPORT route fails with a message instead of hanging forever (OpenTimeout).
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
            // ⚠ ONLY tear down if this open is still the current one. A cancellation has two causes needing
            // opposite handling: the CALLER's token (clean up — nothing else is running) versus being
            // SUPERSEDED by a later OpenAsync (do nothing — the successor already owns the element, and
            // unloading here would rip the source out from under it). Found by a test: without this check,
            // opening a second track while the first was still loading left the element empty.
            bool superseded;
            lock (_gate) superseded = !ReferenceEquals(_opening, opening);
            if (!superseded)
            {
                Send(MediaPlayerEvents.Unload);
                lock (_gate) _status = new MediaPlayerStatus { State = MediaPlayerState.Empty, Rate = _rate };
                Raise();
            }

            // ⚠ The TIMEOUT is a different outcome from the caller cancelling, and it must not be reported
            // as one: a cancel is the app's own decision and needs no explanation, while this is almost
            // always a missing route. Checked in this order because a caller cancelling DURING the timeout
            // window is still a cancel — the caller's intent wins.
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
    /// The page saying what its element is doing — called from the app's IPC route.
    /// <para>
    /// ⚠ <b>Position and duration come from HERE and nowhere else.</b> The element is the thing actually
    /// advancing, so anything this class computed itself would be a second, worse clock.
    /// </para>
    /// <para>
    /// ⚠ <b>Report on TRANSITIONS, not on a timer.</b> A page element fires <c>timeupdate</c> about four
    /// times a second and forwarding every one across IPC costs battery to tell the host something it can
    /// extrapolate. Forward <c>loadedmetadata</c>, <c>canplay</c>, <c>play</c>, <c>pause</c>,
    /// <c>waiting</c>, <c>seeked</c>, <c>ended</c> and <c>error</c>. That is all.
    /// </para>
    /// </summary>
    public void Report(MediaPlayerStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        TaskCompletionSource? opening;
        lock (_gate)
        {
            // The rate stays the player's: a page that does not carry one reports the default, and taking
            // it verbatim would silently reset a configured 1.5x on the first report.
            _status = status with { Rate = _rate };
            opening = _opening;

            if (opening is not null && status.State is not MediaPlayerState.Opening) _opening = null;
            else opening = null;
        }

        Raise();

        // Settled OUTSIDE the lock. RunContinuationsAsynchronously already keeps the awaiter's continuation
        // off this thread, so the reason is the SECOND one: a completion runs this class's own StateChanged
        // subscribers via the awaiting caller, and settling under `_gate` would hold it across app code.
        if (opening is null) return;
        if (status.State == MediaPlayerState.Failed)
            opening.TrySetException(new MediaPlayerException(status.Error ?? "The media source could not be played."));
        else
            opening.TrySetResult();
    }

    /// <summary>
    /// Emit without awaiting. <see cref="IEventBus.Emit(string, string, object?, string?)"/> states the
    /// guarantee that makes this safe: every handler runs inside the bus's own guard, so a subscriber
    /// cannot fault the emit.
    /// </summary>
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
            ex => Log(() => $"[Shenora.Modules.Media] A player state handler threw ({ex.GetType().Name}: {ex.Message})."));
    }

    private void Log(Func<string> message) => AppCallback.Log(_options.Access.Log, message);

    /// <summary>Stop accepting reports. Does not unload the page's element — that is <see cref="CloseAsync"/>.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) _opening?.TrySetCanceled();
    }
}
