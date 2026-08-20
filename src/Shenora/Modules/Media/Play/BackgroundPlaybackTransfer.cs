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

    /// <summary>Playback FINISHED while the app was away, so the page was parked at the end rather than
    /// restarted.</summary>
    Finished,

    /// <summary>The app supplied no source the native player could open. Nothing moved.</summary>
    Unresolved,

    /// <summary>The transfer threw. <see cref="BackgroundPlaybackResult.Detail"/> names the type.</summary>
    Failed,
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

    /// <summary>How close to the end counts as FINISHED. A player can stop a few milliseconds short of its
    /// duration, and handing that position back restarts the film.</summary>
    public TimeSpan EndTolerance { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Diagnostics. The host's own sink, never the page's.</summary>
    public ILogger? Log { get; init; }
}

/// <summary>
/// <b>Keep playing when the app goes away</b> — move the playhead from the PAGE's player to the platform's
/// own and back.
/// <para>
/// <b>Prerequisites are the app's:</b> iOS needs <c>UIBackgroundModes: [audio]</c> and an active
/// <c>AVAudioSession</c>, which the shell's native player takes when it opens. A backgrounded page player
/// is suspended within seconds on both platforms and cannot START audio at all (<c>NotAllowedError</c> —
/// pressing HOME is not a user gesture). Measurements: <c>docs/design/mobile-shells.md</c>.
/// </para>
/// <para>⚠ <b>Four traps:</b></para>
/// <list type="number">
/// <item><b>The playhead comes from the PLAYER, not the element</b> — <c>visibilitychange</c> fires before
/// any host lifecycle hook, so by then the element reports "not playing" about something that was.</item>
/// <item><b>ONE owner per transition.</b> The page hands off, the HOST hands back; both driving the element
/// destroys the state the other needs.</item>
/// <item><b>The native player needs a source it can OPEN</b>, which is not the page's URL — hence
/// <see cref="BackgroundPlaybackOptions.ResolveNativeSource"/>.</item>
/// <item>🔴 <b>Playback may FINISH while you are away, and handing that position back RESTARTS the film</b>
/// — seeking a 60 s element to 60.00 rewinds it. A finished playback parks the page at the end.</item>
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

    /// <summary>The app is going away: take the playhead off the page and give it to the platform. Call from
    /// the host's own "stopped" lifecycle hook — <c>Window.Stopped</c> under MAUI, i.e. <c>onStop</c> /
    /// <c>didEnterBackground</c>.</summary>
    public async Task<BackgroundPlaybackResult> ToBackgroundAsync(CancellationToken cancellationToken = default)
    {
        var status = _page.Status;
        // ⚠ Playing OR paused-with-a-position — the platform may already have paused the element (trap 1).
        if (status.State is MediaPlayerState.Empty or MediaPlayerState.Failed)
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

        try
        {
            await _native.OpenAsync(new MediaSource { Uri = source }, cancellationToken).ConfigureAwait(false);
            await _native.SeekAsync(status.Position, cancellationToken).ConfigureAwait(false);
            await _native.PlayAsync(cancellationToken).ConfigureAwait(false);
            Report(() => $"[Shenora] background handoff: {status.Position.TotalSeconds:F2}s -> native, state={_native.Status.State}");
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.TookOver, status.Position);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The TYPE only — a source path must never travel into a log line.
            Report(() => "[Shenora] background handoff failed", ex);
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Failed, status.Position, ex.GetType().Name);
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
        var status = _native.Status;
        if (status.State is MediaPlayerState.Empty)
        {
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Nothing, default, "nothing to give back");
        }

        var at = status.Position;
        // Both tests are needed: a player may report Ended, or stop a few milliseconds short of its duration.
        var finished = status.State is MediaPlayerState.Ended
            || (status.Duration is { } duration && duration > TimeSpan.Zero && at >= duration - _options.EndTolerance);

        try
        {
            await _native.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A native player that will not close must not cost the page its playhead.
            Report(() => "[Shenora] background handback: native close threw — continuing", ex);
        }

        try
        {
            if (finished)
            {
                // Park AT the end: seeking to the duration rewinds the element and play() restarts the film.
                await _page.PauseAsync(cancellationToken).ConfigureAwait(false);
                Report(() => $"[Shenora] background handback: FINISHED at {at.TotalSeconds:F2}s — the page is parked, not restarted");
                return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Finished, at);
            }

            await _page.SeekAsync(at, cancellationToken).ConfigureAwait(false);
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
