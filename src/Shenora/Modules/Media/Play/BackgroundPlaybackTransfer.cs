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

    /// <summary>
    /// Playback FINISHED while the app was away, so the page was parked at the end rather than restarted.
    /// </summary>
    Finished,

    /// <summary>The app supplied no source the native player could open. Nothing moved.</summary>
    Unresolved,

    /// <summary>The transfer was attempted and threw. <see cref="BackgroundPlaybackResult.Detail"/> names the type.</summary>
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
    /// should carry on.
    ///
    /// <para>
    /// 🔴 <b>The one thing only the app can answer, which is why it is required.</b> The page plays a URL the
    /// app's own routes serve (an interceptor scheme, a converted-cache path); a native player cannot fetch
    /// any of that, so something has to map "what the page is playing" to "a file this device can open".
    /// The app owns both ends of that mapping already — it is the same knowledge
    /// <see cref="MediaAccessOptions.Resolve"/> encodes (via <c>MediaConversionOptions.Access</c>).
    /// </para>
    /// <para>
    /// ⚠ It is asked at BACKGROUND time, on the app's own thread, and must not block.
    /// </para>
    /// </summary>
    public required Func<string?> ResolveNativeSource { get; init; }

    /// <summary>
    /// How close to the end counts as FINISHED. A player can report a position a few milliseconds short of
    /// its duration at the end, and handing that back restarts the film — see
    /// <see cref="BackgroundPlaybackTransfer"/>.
    /// </summary>
    public TimeSpan EndTolerance { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Diagnostics. The host's own sink, never the page's.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// <b>Keep playing when the app goes away</b> — by moving the playhead from the PAGE's player to the
/// platform's own and back.
///
/// <para>
/// 🔴 <b>This is the one media job a page provably cannot do for itself, which is what makes it the kit's
/// rather than an app's.</b> Measured on Android (API 36) and iOS (iPhone 17 Pro simulator):
/// </para>
/// <list type="bullet">
/// <item>A page <c>&lt;audio&gt;</c> already playing is SUSPENDED after ~15.3 s in the background; the
/// native player ran 45 s and counting, with no foreground service.</item>
/// <item>On iOS the system pauses a backgrounded <c>&lt;video&gt;</c> outright, while the native player
/// carried 43 s hidden and a whole 60 s clip in a longer run.</item>
/// <item>The page cannot even START audio at background time —
/// <c>NotAllowedError: play() can only be initiated by a user gesture</c> — because user activation is
/// transient and pressing HOME is not a gesture. There is no earlier hook to win that race with.</item>
/// </list>
/// <para>
/// So the asymmetry is the feature: .NET can do this and React cannot (D54).
/// </para>
///
/// <para>
/// <b>Prerequisites are the app's, and the kit cannot supply them:</b> iOS needs
/// <c>UIBackgroundModes: [audio]</c> in the app's Info.plist and an active <c>AVAudioSession</c> — which
/// the shell's native player takes when it opens.
/// </para>
///
/// <para>
/// ⚠ <b>Four things this gets right that a first draft does not</b>, each of them measured rather than
/// reasoned, and each one a defect that shipped in the sample before it was found:
/// </para>
/// <list type="number">
/// <item><b>The playhead comes from the PLAYER, not the element.</b> The page's element is already paused by
/// the platform by the time a host lifecycle hook runs — <c>visibilitychange</c> fires first — so asking it
/// reports "not playing" about something that was playing a second ago.</item>
/// <item><b>ONE owner per transition.</b> The page hands off, the HOST hands back. Both driving the element
/// destroys the state the other needs.</item>
/// <item><b>The native player needs a source it can OPEN</b>, which is not the page's URL — hence
/// <see cref="BackgroundPlaybackOptions.ResolveNativeSource"/>.</item>
/// <item>🔴 <b>Playback may FINISH while you are away, and handing that position back RESTARTS the film.</b>
/// Seeking a 60 s element to 60.00 rewinds it and the follow-up play() runs the opening titles. Coming back
/// to the credits is a worse bug than losing the audio, so a finished playback parks the page at the end and
/// says so.</item>
/// </list>
///
/// <para>
/// ⚠ <b>Opening the native player PAUSES the page by itself</b> on both mobile platforms, because it takes
/// the audio session. So this does NOT pause the page — doing both would hide which one works.
/// </para>
/// </summary>
/// <param name="page">The page-backed player — what <c>UseMediaPlayer</c> registers as <see cref="IMediaPlayer"/>.</param>
/// <param name="native">
/// The shell's own player, resolved BY ITS TYPE (<c>AndroidMediaPlayer</c>, <c>IosMediaPlayer</c>).
/// </param>
/// <param name="options">The app's half.</param>
public sealed class BackgroundPlaybackTransfer(IMediaPlayer page, IMediaPlayer native, BackgroundPlaybackOptions options)
{
    private readonly IMediaPlayer _page = page ?? throw new ArgumentNullException(nameof(page));
    private readonly IMediaPlayer _native = native ?? throw new ArgumentNullException(nameof(native));
    private readonly BackgroundPlaybackOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// The app is going away: take the playhead off the page and give it to the platform.
    /// <para>
    /// Call from the host's own "stopped" lifecycle hook — <c>Window.Stopped</c> under MAUI, which is
    /// <c>onStop</c> on Android and <c>didEnterBackground</c> on iOS.
    /// </para>
    /// </summary>
    public async Task<BackgroundPlaybackResult> ToBackgroundAsync(CancellationToken cancellationToken = default)
    {
        var status = _page.Status;
        // ⚠ Playing OR paused-with-a-position: the platform may already have paused the element before this
        // runs, which is exactly the ordering trap, so a paused player with a real position still carries.
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
            Report($"[Shenora] background handoff: {status.Position.TotalSeconds:F2}s -> native, state={_native.Status.State}");
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.TookOver, status.Position);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The TYPE only. A source path is exactly what must not travel, and the caller knows what it asked.
            Report($"[Shenora] background handoff failed: {ex.GetType().Name}");
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Failed, status.Position, ex.GetType().Name);
        }
    }

    /// <summary>
    /// The app is back: take the playhead off the platform and give it to the page.
    /// <para>
    /// Call from the host's "resumed" hook — <c>Window.Resumed</c>, i.e. <c>onResume</c> /
    /// <c>willEnterForeground</c>.
    /// </para>
    /// <para>
    /// ⚠ <b>The page resumes with NO fresh user gesture, and that is measured rather than assumed</b>: an
    /// element already played by a real gesture keeps its activation, so returning to the foreground does not
    /// need to be a gesture. Confirmed on both mobile shells.
    /// </para>
    /// </summary>
    public async Task<BackgroundPlaybackResult> ToForegroundAsync(CancellationToken cancellationToken = default)
    {
        var status = _native.Status;
        if (status.State is MediaPlayerState.Empty)
        {
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Nothing, default, "nothing to give back");
        }

        var at = status.Position;
        // 🔴 FINISHED IS A FIRST-CLASS OUTCOME, not an edge case — see the type's remarks. Both tests are
        // needed: a player may report Ended, or stop a few milliseconds short of its own duration.
        var finished = status.State is MediaPlayerState.Ended
            || (status.Duration is { } duration && duration > TimeSpan.Zero && at >= duration - _options.EndTolerance);

        try
        {
            await _native.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A native player that will not close must not cost the page its playhead.
            Report($"[Shenora] background handback: native close threw {ex.GetType().Name} — continuing");
        }

        try
        {
            if (finished)
            {
                // Park the page AT the end rather than seeking to it and playing: seeking to the duration
                // rewinds the element, and the follow-up play() restarts the film.
                await _page.PauseAsync(cancellationToken).ConfigureAwait(false);
                Report($"[Shenora] background handback: FINISHED at {at.TotalSeconds:F2}s — the page is parked, not restarted");
                return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Finished, at);
            }

            await _page.SeekAsync(at, cancellationToken).ConfigureAwait(false);
            await _page.PlayAsync(cancellationToken).ConfigureAwait(false);
            Report($"[Shenora] background handback: native -> page at {at.TotalSeconds:F2}s");
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Resumed, at);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Report($"[Shenora] background handback failed: {ex.GetType().Name}");
            return new BackgroundPlaybackResult(BackgroundPlaybackOutcome.Failed, at, ex.GetType().Name);
        }
    }

    /// <summary>An app-supplied resolver must not become this feature's failure.</summary>
    private string? SafeResolve()
    {
        try { return _options.ResolveNativeSource(); }
        catch (Exception ex)
        {
            Report($"[Shenora] background handoff: the app's source resolver threw {ex.GetType().Name}");
            return null;
        }
    }

    private void Report(string message)
    {
        if (_options.Log is null) return;
        try { _options.Log(message); } catch (Exception) { /* a diagnostic must never break what it reports on */ }
    }
}
