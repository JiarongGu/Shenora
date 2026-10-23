using Microsoft.Extensions.Logging;

namespace Shenora.Modules.Media;

/// <summary>What a handoff did, so an app can log or surface it rather than guess.</summary>
public enum BackgroundPlaybackOutcome
{
    /// <summary>Nothing was playing, so nothing moved. The ordinary case, and not a failure.</summary>
    Nothing,

    /// <summary>The native player took the playhead over and is playing while the app is away.</summary>
    TookOver,

    /// <summary>The page took the playhead back and is playing again.</summary>
    Resumed,

    /// <summary>Playback FINISHED while the app was away, so the page was parked just short of its end
    /// (<see cref="BackgroundPlaybackOptions.EndTolerance"/> before it), paused, rather than restarted.</summary>
    Finished,

    /// <summary>The app supplied no source the native player could open. Nothing moved.</summary>
    Unresolved,

    /// <summary>The transfer threw. <see cref="BackgroundPlaybackResult.Detail"/> names the type.</summary>
    Failed,

    /// <summary>The page took the playhead back and stays PAUSED: the native player the handoff started was paused
    /// while the app was away — by whatever the app wired to it, the lock screen's controls for one — so playing on
    /// would undo that. The kit's native players never pause by themselves.</summary>
    Paused,
}

/// <summary>The outcome of one transfer, with the position it happened at.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Position">The playhead at the moment of the transfer.</param>
/// <param name="Detail">A short, non-localised note for the host LOG — never for the wire.</param>
public sealed record BackgroundPlaybackResult(
    BackgroundPlaybackOutcome Outcome,
    TimeSpan Position = default,
    string? Detail = null);

/// <summary>Inputs for <see cref="BackgroundPlaybackTransfer"/>.</summary>
public sealed class BackgroundPlaybackOptions
{
    /// <summary>
    /// What the NATIVE player should open to continue what the page is playing — <c>null</c> when nothing
    /// should carry on. A native player cannot fetch the URLs the app's own routes serve, so only the app
    /// can map one to a file this device can open. ⚠ Asked at BACKGROUND time, on the app's own thread: it
    /// must not block.
    /// </summary>
    public required Func<string?> ResolveNativeSource { get; init; }

    /// <summary>How close to the end counts as FINISHED, and how far short of the end a finished page is parked. A
    /// player can stop a few milliseconds short of its duration, and handing that position back restarts the
    /// film.</summary>
    public TimeSpan EndTolerance { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Diagnostics. The host's own sink, never the page's.</summary>
    public ILogger? Log { get; init; }
}

/// <summary>
/// <b>Keep playing when the app goes away</b> — move the playhead from the PAGE's player to the platform's
/// own and back.
/// <para>
/// <b>Prerequisites are the app's:</b> iOS needs <c>UIBackgroundModes: [audio]</c> and a <c>Playback</c>
/// <c>AVAudioSession</c> that is MIXABLE (<c>MixWithOthers</c>) when the handoff plays — neither this class nor the
/// shell's native player configures it. Measured on an iPhone: a backgrounded app may not START non-mixable audio, so
/// under a non-mixable session the native player was paused at once (rate 0) and the app suspended; under a mixable
/// one it played on, and switching to non-mixable once it was playing kept it playing. The simulator enforces none of
/// this. A backgrounded page player is suspended within seconds on both platforms and cannot START audio at all
/// (<c>NotAllowedError</c> — pressing HOME is not a user gesture). Measurements: <c>docs/design/mobile-shells.md</c>.
/// </para>
/// <para>⚠ <b>Four traps:</b></para>
/// <list type="number">
/// <item><b>The playhead comes from the PLAYER, not the element</b> — <c>visibilitychange</c> fires before
/// any host lifecycle hook, and the platform then pauses the hidden element itself, so by then the element
/// reports "not playing" about something that was. Only a page reported <c>Playing</c> or <c>Buffering</c> is
/// handed off, so a page must not report that platform pause as its own: <c>useMediaPlayer</c> drops a pause
/// nobody asked for that comes as the page goes hidden, and a page reporting by hand must do the same. The page's
/// hidden-time report can land after <c>Window.Stopped</c>, so the position handed over is the page player's
/// estimate (<see cref="MediaPlayer.Status"/>), or a report that lands while the native player opens. ⚠ iOS pauses the
/// element about a second BEFORE the page hides, so that pause reads as the user's: a playing film is handed off
/// there because those reports reach the host after <c>Window.Stopped</c> (measured on the simulator).</item>
/// <item><b>ONE owner per transition.</b> The page hands off, the HOST hands back; both driving the element
/// destroys the state the other needs.</item>
/// <item><b>The native player needs a source it can OPEN</b>, which is not the page's URL — hence
/// <see cref="BackgroundPlaybackOptions.ResolveNativeSource"/>.</item>
/// <item>🔴 <b>Playback may FINISH while you are away, and handing that position back RESTARTS the film</b>
/// — seeking a 60 s element to 60.00 rewinds it. A finished playback parks the page just short of the end.</item>
/// </list>
/// <para>⚠ <b>Opening the native player PAUSES the page by itself</b> on both mobile platforms, because it
/// takes the audio session — so this does not pause the page.</para>
/// </summary>
/// <param name="page">The page-backed player — what <c>UseMediaPlayer</c> registers as <see cref="IMediaPlayer"/>.</param>
/// <param name="native">The shell's own player, resolved BY ITS TYPE (<c>AndroidMediaPlayer</c>, <c>IosMediaPlayer</c>).</param>
/// <param name="options">The app's half.</param>
public sealed class BackgroundPlaybackTransfer(IMediaPlayer page, IMediaPlayer native, BackgroundPlaybackOptions options)
{
    private readonly IMediaPlayer _page = page ?? throw new ArgumentNullException(nameof(page));
    private readonly IMediaPlayer _native = native ?? throw new ArgumentNullException(nameof(native));
    private readonly BackgroundPlaybackOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    // The last handoff got as far as PLAYING the native player. Until it has, the player reads Paused because it
    // has only just opened, not because anyone paused it.
    private volatile bool _nativeStarted;

    // The handoff still in flight, which a return cancels.
    private CancellationTokenSource? _handoff;

    /// <summary>The app is going away: take the playhead off the page and give it to the platform. Call from
    /// the host's own "stopped" lifecycle hook — <c>Window.Stopped</c> under MAUI, i.e. <c>onStop</c> /
    /// <c>didEnterBackground</c>.</summary>
    public async Task<BackgroundPlaybackResult> ToBackgroundAsync(CancellationToken cancellationToken = default)
    {
        _nativeStarted = false;
        var status = _page.Status;
        // Only a page that was PLAYING: a film the user paused stays paused. This relies on the page not
        // reporting the platform's own pause as it goes hidden, which `useMediaPlayer` drops (trap 1).
        if (status.State is not (MediaPlayerState.Playing or MediaPlayerState.Buffering))
        {
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Nothing, status.Position,
                $"the page player is {status.State}");
        }

        var source = SafeResolve();
        if (string.IsNullOrWhiteSpace(source))
        {
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Unresolved, status.Position,
                "the app resolved no native source");
        }

        // The position is the page player's ESTIMATE when its hidden-time report has not landed yet — it can lose
        // the race with `Window.Stopped` — so a report that lands while the native player opens is the page's own
        // reading and wins: its POSITION, not its state, the decision to hand off having been made above. A Paused
        // one counts — iOS pauses the element before the page hides, and that report says exactly where it stopped
        // (measured on the simulator: the estimate alone ran 0.22–0.30 s past it). Nothing waits for one.
        var position = status.Position;
        MediaPlayerStatus? landed = null;
        void OnPageReport(MediaPlayerStatus report)
        {
            if (report.State is MediaPlayerState.Playing or MediaPlayerState.Buffering or MediaPlayerState.Paused)
                Volatile.Write(ref landed, report);
        }

        // The app can come back while the native player is still opening — a swipe up and straight back, 148 ms on an
        // iPhone — and `ToForegroundAsync` then cancels this through `_handoff` rather than handing back a native
        // position that never played.
        using var handoff = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancelQuietly(Interlocked.Exchange(ref _handoff, handoff));
        var token = handoff.Token;
        try
        {
            _page.StateChanged += OnPageReport;
            try
            {
                await _native.OpenAsync(new MediaSource { Uri = source }, token).ConfigureAwait(false);
            }
            finally
            {
                _page.StateChanged -= OnPageReport;
            }

            if (Volatile.Read(ref landed) is { } report)
            {
                Report(() => $"[Shenora] background handoff: the page reported {report.Position.TotalSeconds:F2}s "
                           + $"while the native player opened (estimated {status.Position.TotalSeconds:F2}s)");
                position = report.Position;
            }

            await _native.SeekAsync(position, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await _native.PlayAsync(token).ConfigureAwait(false);
            _nativeStarted = true;
            Report(() => $"[Shenora] background handoff: {position.TotalSeconds:F2}s -> native, state={_native.Status.State}");
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.TookOver, position);
        }
        catch (Exception ex) when (token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The app came back first. Whatever the native player threw on its way down — a cancellation, or the
            // open failing because the return closed it — nothing moved.
            Report(() => "[Shenora] background handoff: the app came back before the native player played");
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Nothing, position,
                $"the app came back before the native player played ({ex.GetType().Name})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The TYPE only — a source path must never travel into a log line.
            Report(() => "[Shenora] background handoff failed", ex);
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Failed, position, ex.GetType().Name);
        }
        finally
        {
            Interlocked.CompareExchange(ref _handoff, null, handoff);
        }
    }

    /// <summary>
    /// The app is back: take the playhead off the platform and give it to the page. Call from the host's
    /// "resumed" hook — <c>Window.Resumed</c>, i.e. <c>onResume</c> / <c>willEnterForeground</c>.
    /// ⚠ <b>The page resumes with NO fresh user gesture</b>: an element already played by a real gesture
    /// keeps its activation across backgrounding.
    /// </summary>
    public async Task<BackgroundPlaybackResult> ToForegroundAsync(CancellationToken cancellationToken = default)
    {
        var inFlight = Interlocked.Exchange(ref _handoff, null);
        var started = _nativeStarted;
        _nativeStarted = false;
        if (inFlight is not null && !started)
        {
            // Back before the handoff played the native player — a swipe up and straight back. The page never stopped
            // being the player of record, so it carries on from where IT stopped: the native player's position is a
            // half-opened one (0.00 on an iPhone, where handing it back restarted the film).
            CancelQuietly(inFlight);
            await CloseNativeQuietlyAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _page.PlayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Report(() => "[Shenora] background handback failed", ex);
                return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Failed, _page.Status.Position, ex.GetType().Name);
            }
            Report(() => "[Shenora] background handback: back before the native player played — the page carries on");
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Resumed, _page.Status.Position,
                "back before the native player played");
        }

        var status = _native.Status;
        if (status.State is MediaPlayerState.Empty)
        {
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Nothing, default, "nothing to give back");
        }

        var at = status.Position;
        // Both tests are needed: a player may report Ended, or stop a few milliseconds short of its duration.
        var finished = status.State is MediaPlayerState.Ended
            || (status.Duration is { } duration && duration > TimeSpan.Zero && at >= duration - _options.EndTolerance);

        await CloseNativeQuietlyAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (finished)
            {
                // Park JUST SHORT of the end: seeking to the duration rewinds the element and play() restarts the
                // film, and leaving it where the page stopped replays what was heard while away. With no duration,
                // or no tolerance to stop short by, there is no safe place to seek to, so it is only paused.
                if (status.Duration is { } length && _options.EndTolerance > TimeSpan.Zero && length > _options.EndTolerance)
                    await _page.SeekAsync(length - _options.EndTolerance, cancellationToken).ConfigureAwait(false);
                await _page.PauseAsync(cancellationToken).ConfigureAwait(false);
                Report(() => $"[Shenora] background handback: FINISHED at {at.TotalSeconds:F2}s — the page is parked, not restarted");
                return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Finished, at);
            }

            await _page.SeekAsync(at, cancellationToken).ConfigureAwait(false);
            if (status.State is MediaPlayerState.Paused && started)
            {
                // SAID, though the element is already paused: Chromium resumes a video it paused for a hidden page as
                // soon as the page is shown, and `useMediaPlayer` undoes that only when the host's last word was pause.
                await _page.PauseAsync(cancellationToken).ConfigureAwait(false);
                Report(() => $"[Shenora] background handback: native was PAUSED at {at.TotalSeconds:F2}s — the page stays paused");
                return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Paused, at);
            }
            await _page.PlayAsync(cancellationToken).ConfigureAwait(false);
            Report(() => $"[Shenora] background handback: native -> page at {at.TotalSeconds:F2}s");
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Resumed, at);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Report(() => "[Shenora] background handback failed", ex);
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Failed, at, ex.GetType().Name);
        }
    }

    /// <summary>A native player that will not close must not cost the page its playhead.</summary>
    private async Task CloseNativeQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _native.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(() => "[Shenora] background handback: native close threw — continuing", ex);
        }
    }

    /// <summary>The handoff may have finished and disposed its source between being read and being cancelled.</summary>
    private static void CancelQuietly(CancellationTokenSource? source)
    {
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>An app-supplied resolver must not become this feature's failure.</summary>
    private string? SafeResolve()
    {
        try { return _options.ResolveNativeSource(); }
        catch (Exception ex)
        {
            Report(() => "[Shenora] background handoff: the app's source resolver threw", ex);
            return null;
        }
    }

    /// <summary>Guarded and lazy — every call site sits inside a <c>catch</c> whose job is to keep a handoff
    /// failure from costing the page its playhead.</summary>
    private void Report(Func<string> message, Exception? failure = null) =>
        AppCallback.Log(_options.Log, message,
                        failure is null ? LogLevel.Debug : LogLevel.Warning, failure);
}
