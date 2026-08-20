using Shenora.Core.WebView;
using Shenora.Core.Shell;

namespace Shenora.Modules.Platform;

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
    /// Waiting on data. ⚠ Distinct from <see cref="Playing"/>: two of the three platforms render a spinner
    /// for it, and collapsing it into <c>Playing</c> makes the OS extrapolate a position that is not moving.
    /// </summary>
    Buffering,
}

/// <summary>
/// Which transport controls the OS should OFFER — only the ones that will do something, since a button
/// the user presses to no effect is indistinguishable from a bug.
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
    /// One button that means "the other thing". ⚠ Hardware sends this as its own event — a headphone pinch
    /// and a car stereo's centre button — so an app handling only <see cref="Play"/>/<see cref="Pause"/>
    /// silently ignores both.
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
    /// Jump forward by <see cref="IPlaybackSession.SkipInterval"/> — the ±15 s button long-form audio
    /// wants, where <see cref="Next"/> is the wrong granularity and <see cref="Seek"/> is a drag.
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

/// <summary>A transport control the OS is asking the app to perform.</summary>
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
    /// iOS sends the interval WITH the event (<c>MPSkipIntervalCommandEvent</c>) and that value is honoured;
    /// where a platform sends none this is the configured <see cref="IPlaybackSession.SkipInterval"/>, so a
    /// handler can always just use it.
    /// </para>
    /// </summary>
    public TimeSpan? Interval { get; init; }
}

/// <summary>
/// What is playing, in the fields every system transport surface can render. The names are generic rather
/// than <c>Artist</c>/<c>Album</c>: the same three fields carry a podcast's show and episode, an
/// audiobook's book and chapter, a lecture's course. Every field is optional — a platform renders what it
/// has.
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
    /// ⚠ Bytes, not a path — a path would put file-access rules here that <see cref="WebViewFiles"/> owns.
    /// Keep it small: this is a lock-screen thumbnail, and the artwork is copied on every publish.
    /// </para>
    /// </summary>
    public ReadOnlyMemory<byte> Artwork { get; init; }

    /// <summary>
    /// How long the item is, when known. ⚠ It lives here and not on <see cref="PlaybackProgress"/> because
    /// re-sending it with every position update makes the timeline flicker on one of the platforms.
    /// </summary>
    public TimeSpan? Duration { get; init; }
}

/// <summary>
/// Where playback has got to. Report this when the position JUMPS or the state changes — not on a timer.
/// <para>
/// <b>⚠ The OS extrapolates, so this is a snapshot AS OF NOW.</b> All three platforms take a position plus a
/// rate and advance the displayed time themselves. Report on seek, pause, resume, rate change and track
/// change, and do not batch: the platform treats a report as current, so a delayed one lands as a jump
/// backwards.
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
    /// displayed position as <c>position + elapsed × rate</c>. Set the app's real playback speed and leave
    /// it there: a rate only counts while <see cref="State"/> is <see cref="PlaybackState.Playing"/>, and
    /// every shell derives the published speed from the state.
    /// <para>
    /// ⚠ <b>Not every shell can carry a rate.</b> Windows' <c>SystemMediaTransportControls</c> has no speed
    /// field, so a 1.5× audiobook reads as normal speed there; the two mobile shells honour the multiplier.
    /// </para>
    /// </summary>
    public double Rate { get; init; } = 1.0;
}

/// <summary>
/// The app's handle on the OS's media transport surface — the lock screen, the Dynamic Island's Now
/// Playing, Android's media notification, Windows' <c>SystemMediaTransportControls</c> — implemented once
/// per shell (D19/D20).
/// <para>
/// <b>It is TWO-WAY.</b> Metadata and position travel app → OS; transport commands travel OS → app, from a
/// lock screen, a headphone gesture, a car stereo or a keyboard media key. There is no queue model behind
/// it: only the app knows what "next" means.
/// </para>
/// <para>
/// Registered as a SINGLETON by the shell and injected — say <see cref="Clear"/> rather than disposing it,
/// which is what "nothing is playing any more" actually means.
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
    /// Default 15 seconds. The platforms render it onto the button itself — on iOS it is what draws "15"
    /// instead of a bare arrow — so keep to a value their UI is designed around (15, 10, 30).
    /// <para>
    /// ⚠ Set it BEFORE <see cref="Supported"/>: that is when the controls are configured.
    /// </para>
    /// </summary>
    TimeSpan SkipInterval { get; set; }

    /// <summary>Say what is playing. Call on a track change, or when metadata arrives late (a tag read, artwork decoded).</summary>
    void Publish(PlaybackInfo info);

    /// <summary>Say where it has got to — see <see cref="PlaybackProgress"/> for when NOT to call this.</summary>
    void Report(PlaybackProgress progress);

    /// <summary>
    /// Nothing is playing any more: take the app off the lock screen and out of the media controls. Distinct
    /// from reporting <see cref="PlaybackState.Stopped"/>, which leaves the app present with a stopped item.
    /// </summary>
    void Clear();

    /// <summary>
    /// The OS asking for a transport control.
    /// <para>
    /// ⚠ <b>Raised on whatever thread the platform uses, which is NOT the UI thread on at least one of
    /// them</b> (Windows delivers its button events on a pool thread). Marshal with
    /// <see cref="IUiDispatcher"/> before touching UI or player state that expects it. A throwing handler is
    /// caught and logged rather than escaping into a platform callback (<see cref="AppCallback"/>).
    /// </para>
    /// </summary>
    event Action<PlaybackCommandRequest>? CommandReceived;
}
