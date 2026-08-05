namespace Shenora.Core;

/// <summary>
/// What the OS should say is happening — the state every system transport surface renders.
/// </summary>
public enum PlaybackState
{
    /// <summary>Nothing is playing. Distinct from <see cref="Paused"/>: a stopped session has no position.</summary>
    Stopped,

    /// <summary>Advancing.</summary>
    Playing,

    /// <summary>Held at a position the user can resume from.</summary>
    Paused,

    /// <summary>
    /// Waiting on data. Reported separately from <see cref="Playing"/> because two of the three platforms
    /// have a distinct state for it and will render a spinner rather than a stale elapsed time — and
    /// because collapsing it into <c>Playing</c> makes the OS extrapolate a position that is not moving.
    /// </summary>
    Buffering,
}

/// <summary>
/// Which transport controls the OS should OFFER. A flags set rather than "all of them", because a
/// surface that shows a next-track button for something with no next track is worse than one that does
/// not: the user presses it and nothing happens, and there is no way to tell that apart from a bug.
/// </summary>
[Flags]
public enum PlaybackCommands
{
    /// <summary>Nothing — the OS shows metadata only.</summary>
    None = 0,

    /// <summary>Resume from a paused or stopped position.</summary>
    Play = 1 << 0,

    /// <summary>Hold at the current position.</summary>
    Pause = 1 << 1,

    /// <summary>
    /// One button that means "the other thing". Separate from <see cref="Play"/>/<see cref="Pause"/>
    /// because hardware sends it as its own event — a headphone pinch and a car stereo's centre button are
    /// a toggle, not a play — and an app that only handles Play/Pause silently ignores both.
    /// </summary>
    TogglePlayPause = 1 << 2,

    /// <summary>End playback and discard the position.</summary>
    Stop = 1 << 3,

    /// <summary>Advance to the next item — only offer it when there IS one.</summary>
    Next = 1 << 4,

    /// <summary>Go back to the previous item.</summary>
    Previous = 1 << 5,

    /// <summary>Scrubbing to an absolute position — the OS renders a draggable timeline.</summary>
    Seek = 1 << 6,

    /// <summary>
    /// Jump forward by <see cref="IPlaybackSession.SkipInterval"/> — the ±15 s button.
    /// <para>
    /// Distinct from <see cref="Next"/> and from <see cref="Seek"/>, and both distinctions are the point.
    /// For LONG-FORM audio — an audiobook, a podcast, a lecture — "next" is the wrong granularity when a
    /// track is fifty minutes long, and a scrubber is a drag rather than a button. Added because the first
    /// adopter had this working and gave it up to adopt the kit, which is the trade the kit must not force.
    /// </para>
    /// </summary>
    SkipForward = 1 << 7,

    /// <summary>Jump back by <see cref="IPlaybackSession.SkipInterval"/>.</summary>
    SkipBackward = 1 << 8,
}

/// <summary>One transport control. A single value, never a combination — see <see cref="PlaybackCommands"/>.</summary>
public enum PlaybackCommand
{
    /// <summary>Resume.</summary>
    Play,

    /// <summary>Hold at the current position.</summary>
    Pause,

    /// <summary>Whichever of play/pause is not current — hardware sends this as its own event.</summary>
    TogglePlayPause,

    /// <summary>End playback.</summary>
    Stop,

    /// <summary>Advance to the next item.</summary>
    Next,

    /// <summary>Go back to the previous item.</summary>
    Previous,

    /// <summary>Scrub to <see cref="PlaybackCommandRequest.Position"/>.</summary>
    Seek,

    /// <summary>Jump forward by <see cref="PlaybackCommandRequest.Interval"/>.</summary>
    SkipForward,

    /// <summary>Jump back by <see cref="PlaybackCommandRequest.Interval"/>.</summary>
    SkipBackward,
}

/// <summary>
/// A transport control the OS is asking the app to perform.
/// <para>
/// A record rather than a bare enum because <see cref="PlaybackCommand.Seek"/> carries a destination, and
/// an event that cannot express it would force a second event for one command.
/// </para>
/// </summary>
public sealed record PlaybackCommandRequest
{
    /// <summary>What was asked for.</summary>
    public required PlaybackCommand Command { get; init; }

    /// <summary>
    /// Where to seek to — set only for <see cref="PlaybackCommand.Seek"/>, null for every other command.
    /// </summary>
    public TimeSpan? Position { get; init; }

    /// <summary>
    /// How far to jump — set for <see cref="PlaybackCommand.SkipForward"/> and
    /// <see cref="PlaybackCommand.SkipBackward"/>, null otherwise.
    /// <para>
    /// It is carried on the request even though the app already declared
    /// <see cref="IPlaybackSession.SkipInterval"/>, because one platform sends the interval WITH the event
    /// (iOS's <c>MPSkipIntervalCommandEvent</c>) and honouring what arrived is more correct than assuming
    /// what was asked for. Where a platform sends nothing, this is the configured interval, so a handler
    /// can always just use it.
    /// </para>
    /// </summary>
    public TimeSpan? Interval { get; init; }
}

/// <summary>
/// What is playing, in the fields every system transport surface can render.
/// <para>
/// <b>The names are deliberately generic rather than <c>Artist</c>/<c>Album</c>.</b> The same three fields
/// carry a podcast's show and episode, an audiobook's book and chapter, a lecture's course, a slideshow's
/// deck — and this contract lives in <c>Shenora.Core</c>, which every package references, so music
/// vocabulary here would put containers-and-codecs words on the surface of an app that has none (the
/// reasoning D40/D45 used to keep <c>Shenora.Media</c> separate and optional).
/// </para>
/// <para>
/// Every field is optional, and that is not laziness: a platform renders what it has, and a host that
/// throws on a missing subtitle is a host that cannot report a file with no tags.
/// </para>
/// </summary>
public sealed record PlaybackInfo
{
    /// <summary>The primary line. A track, an episode, a chapter.</summary>
    public string? Title { get; init; }

    /// <summary>The secondary line — an artist, a show, an author, a presenter.</summary>
    public string? Subtitle { get; init; }

    /// <summary>What it belongs to — an album, a series, a book, a course.</summary>
    public string? GroupName { get; init; }

    /// <summary>
    /// Cover image bytes (PNG or JPEG). Empty means "no artwork", which every platform handles.
    /// <para>
    /// ⚠ BYTES, not a path or a URL, and deliberately: two of the three platforms need an in-memory image
    /// object, all three need the host process to have read it anyway, and a path would make this contract
    /// carry file-access rules that <see cref="WebViewFiles"/> already owns. Keep it small — this is a
    /// thumbnail on a lock screen, and the artwork is copied on every publish.
    /// </para>
    /// </summary>
    public ReadOnlyMemory<byte> Artwork { get; init; }

    /// <summary>
    /// How long the item is, when known. A property of the ITEM, which is why it is here and not on
    /// <see cref="PlaybackProgress"/> — it does not change as the position moves, and re-sending it with
    /// every position update is what makes a timeline flicker on one of the platforms.
    /// </summary>
    public TimeSpan? Duration { get; init; }
}

/// <summary>
/// Where playback has got to. Report this when the position JUMPS or the state changes — not on a timer.
/// <para>
/// <b>⚠ The OS extrapolates, so this is a snapshot AS OF NOW.</b> All three platforms take a position plus a
/// rate and advance the displayed time themselves; a host that pushes the current position every 250 ms is
/// paying for battery and IPC to tell the OS something it already worked out, and on one platform it makes
/// the timeline stutter. Report on seek, pause, resume, rate change and track change. That is all.
/// </para>
/// <para>
/// It follows that a DELAYED report is worse than none: the platform treats the position as current, so a
/// value queued behind other work lands as a jump backwards. Do not batch these.
/// </para>
/// </summary>
public sealed record PlaybackProgress
{
    /// <summary>What the OS should show.</summary>
    public required PlaybackState State { get; init; }

    /// <summary>The position as of now.</summary>
    public TimeSpan Position { get; init; }

    /// <summary>
    /// How fast, as a multiplier — 1.0 normal, 0.0 not advancing. The OS uses this to extrapolate the
    /// displayed position as <c>position + elapsed × rate</c>.
    /// <para>
    /// <b>You do not have to zero it when pausing.</b> A rate is only meaningful while
    /// <see cref="State"/> is <see cref="PlaybackState.Playing"/>, so every shell derives the published
    /// speed from the state and ignores this value otherwise — set it to the app's real playback speed and
    /// let the state say whether anything is moving. (Android forwarded it verbatim until 2026-08-05, which
    /// made a paused session advertise <c>speed=1.0</c> and its scrubber drift; the fix went in the shell
    /// rather than in a note telling apps to compensate.)
    /// </para>
    /// <para>
    /// ⚠ <b>Not every shell can carry a rate.</b> Windows' <c>SystemMediaTransportControls</c> has no speed
    /// field at all — its timeline is a position and an end, and "is it advancing" is the playback status —
    /// so a 1.5× audiobook reads as normal speed there. Stated because it is not discoverable from the
    /// types: the two mobile shells honour the multiplier, the desktop conveys only moving/not-moving.
    /// </para>
    /// </summary>
    public double Rate { get; init; } = 1.0;
}

/// <summary>
/// The app's handle on the OS's media transport surface — the lock screen, the Dynamic Island's Now
/// Playing, Android's media notification, Windows' <c>SystemMediaTransportControls</c> — implemented once
/// per shell (D19/D20's law, the same shape as <see cref="IUiDispatcher"/> and
/// <see cref="IWebViewInterceptor"/>).
/// <para>
/// <b>It is TWO-WAY, and the return direction is the interesting one.</b> Metadata and position travel
/// app → OS; transport commands travel OS → app, from a lock screen, a headphone gesture, a car stereo or a
/// keyboard media key. So this is an event source as much as a publisher, and the kit deliberately ships no
/// queue model behind it: only the app knows what "next" means.
/// </para>
/// <para>
/// Registered as a SINGLETON by the shell and injected. There is no <c>IDisposable</c> here on purpose —
/// an app that disposed an injected singleton would tear down the shell's session for everyone; say
/// <see cref="Clear"/> instead, which is what "nothing is playing any more" actually means. The concrete
/// implementations own their native resources and are disposed by the container.
/// </para>
/// </summary>
public interface IPlaybackSession
{
    /// <summary>
    /// Which controls the OS should offer. Settable because it changes with context — the last track in a
    /// queue has no <see cref="PlaybackCommands.Next"/>, and a live stream has no
    /// <see cref="PlaybackCommands.Seek"/>.
    /// </summary>
    PlaybackCommands Supported { get; set; }

    /// <summary>
    /// How far <see cref="PlaybackCommands.SkipForward"/>/<see cref="PlaybackCommands.SkipBackward"/> jump.
    /// Default 15 seconds.
    /// <para>
    /// Stated ONCE rather than per press, because that is what the platforms take — a *preferred* interval
    /// they render onto the button itself. On iOS it is literally what makes the control draw "15" instead of
    /// a bare arrow, so leaving it unset reads to a user as a different feature.
    /// </para>
    /// <para>
    /// ⚠ Keep it to a value the platform UI is designed around. 15 s is the near-universal default and 10/30
    /// are common; an arbitrary interval is not obviously better and renders less well. Set it before
    /// <see cref="Supported"/>, since that is when the controls are configured.
    /// </para>
    /// </summary>
    TimeSpan SkipInterval { get; set; }

    /// <summary>Say what is playing. Call on a track change, or when metadata arrives late (a tag read, artwork decoded).</summary>
    void Publish(PlaybackInfo info);

    /// <summary>Say where it has got to — see <see cref="PlaybackProgress"/> for when NOT to call this.</summary>
    void Report(PlaybackProgress progress);

    /// <summary>
    /// Nothing is playing any more: take the app off the lock screen and out of the media controls.
    /// <para>
    /// Distinct from reporting <see cref="PlaybackState.Stopped"/>, which leaves the app present with a
    /// stopped item — the state a user can resume from. This removes the surface.
    /// </para>
    /// </summary>
    void Clear();

    /// <summary>
    /// The OS asking for a transport control.
    /// <para>
    /// ⚠ <b>Raised on whatever thread the platform uses, which is NOT the UI thread on at least one of
    /// them</b> (Windows delivers its button events on a pool thread). Marshal with
    /// <see cref="IUiDispatcher"/> before touching UI or player state that expects it. A throwing handler is
    /// caught and logged rather than escaping into a platform callback, per the kit's
    /// <see cref="AppCallback"/> rule — an exception there is not catchable by anyone.
    /// </para>
    /// </summary>
    event Action<PlaybackCommandRequest>? CommandReceived;
}
