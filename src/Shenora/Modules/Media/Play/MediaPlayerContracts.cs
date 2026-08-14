using Shenora.Modules.Platform;
using Shenora.Core.Shell;

namespace Shenora.Modules.Media;

/// <summary>
/// What the player is doing. Deliberately the same vocabulary as <see cref="PlaybackState"/> —
/// they describe the same thing at two levels (what the engine is doing vs what the OS should say), and
/// two different spellings would guarantee they drift.
/// </summary>
public enum MediaPlayerState
{
    /// <summary>No source. Nothing to play, no position.</summary>
    Empty,

    /// <summary>A source is loading — opening, parsing headers, resolving tracks.</summary>
    Opening,

    /// <summary>Ready and held at a position.</summary>
    Paused,

    /// <summary>Advancing.</summary>
    Playing,

    /// <summary>Ready but waiting on data; the position is not moving.</summary>
    Buffering,

    /// <summary>Reached the end of the source. The position stays at the end so a UI can show it.</summary>
    Ended,

    /// <summary>The source could not be opened or playback failed — see <see cref="MediaPlayerStatus.Error"/>.</summary>
    Failed,
}

/// <summary>
/// A player's state at one instant.
/// <para>
/// <b>⚠ Position is a SNAPSHOT, not a subscription.</b> Reading it is cheap and asking the platform is
/// how you get a true answer; a host that pushes it to the page every frame is paying IPC to tell React
/// something it could have asked for. The kit therefore raises
/// <see cref="IMediaPlayer.StateChanged"/> on real transitions and leaves polling to whoever is drawing
/// a scrubber — at the rate that scrubber actually redraws.
/// </para>
/// </summary>
public sealed record MediaPlayerStatus
{
    /// <summary>What the player is doing.</summary>
    public required MediaPlayerState State { get; init; }

    /// <summary>Where it has got to. <see cref="TimeSpan.Zero"/> when there is no source.</summary>
    public TimeSpan Position { get; init; }

    /// <summary>
    /// How long the source is, when the platform knows. Null for a live stream and — briefly — while
    /// <see cref="MediaPlayerState.Opening"/>.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Playback speed as a multiplier; 1.0 is normal. Reported as the RATE ASKED FOR, which is not always
    /// what the hardware does — see <see cref="IMediaPlayer.Rate"/>.
    /// </summary>
    public double Rate { get; init; } = 1.0;

    /// <summary>
    /// Why it failed, when <see cref="State"/> is <see cref="MediaPlayerState.Failed"/>; null otherwise.
    /// <para>
    /// ⚠ <b>A short, app-safe reason — never the platform's raw exception text.</b> Same rule the IPC
    /// stack applies to every error path: this string can reach a page, and a native error message is a
    /// disclosure surface and unactionable to a user in equal measure.
    /// </para>
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>
/// What to play. A file the host can open, or a URL the platform will stream.
/// <para>
/// <b>⚠ This is deliberately NOT a stream or a byte source</b>, which is the interesting constraint and
/// the reason the type exists at all. A native player wants to own the read: it seeks, it reads ahead, it
/// re-reads on a track switch, and on two of the three platforms it does that on its own threads inside a
/// process-wide media service. Handing it a managed <c>Stream</c> means marshalling every read across that
/// boundary, which is exactly the overhead the player exists to avoid.
/// </para>
/// </summary>
public sealed record MediaSource
{
    /// <summary>
    /// An absolute path or URL. A local file path, or an <c>http(s)</c> URL — including one served by the
    /// app's own in-process host, which is how a source that needs the kit's remux reaches the player.
    /// </summary>
    public required string Uri { get; init; }

    /// <summary>
    /// Start here rather than at zero. Applied as part of opening, so a resumed item does not visibly
    /// start at zero and jump — which is what a caller gets by opening and then seeking.
    /// </summary>
    public TimeSpan StartAt { get; init; }
}

/// <summary>
/// A media player owned by the HOST, driven by the page — the capability D54 says this framework exists
/// to provide, implemented once per shell (D19/D20's law, the same shape as
/// <see cref="IPlaybackSession"/> and <see cref="IUiDispatcher"/>).
/// <para>
/// <b>Why this exists when <c>&lt;video&gt;</c> is right there.</b> Because the element's ceiling is the
/// webview's, and that ceiling is lower than the platform's in ways an app cannot work around from
/// JavaScript:
/// </para>
/// <list type="bullet">
///   <item><b>Background playback.</b> iOS pauses a <c>&lt;video&gt;</c> the moment the app leaves the
///   foreground — the video track cannot render, so the element stops. A native player is not subject to
///   that, and this is the difference that is impossible rather than merely awkward.</item>
///   <item><b>One source of truth for the system surfaces.</b> <see cref="IPlaybackSession"/>
///   publishes Now Playing from whatever the app claims; when the host owns the player, what it publishes
///   is what is actually happening. Today those two can disagree and nothing reconciles them.</item>
///   <item><b>Formats.</b> The player takes what the PLATFORM decodes, which is a superset of what the
///   webview's element accepts.</item>
/// </list>
/// <para>
/// <b>What this does NOT do, on purpose.</b> No queue, no playlist, no gapless, no crossfade, no shuffle —
/// only the app knows what "next" means, the same reasoning that keeps a queue model out of
/// <see cref="IPlaybackSession"/>. And no video SURFACE: rendering into the page's layout is a
/// composition problem per shell, and audio is where the provable gap is (see the remarks on
/// <see cref="OpenAsync"/>).
/// </para>
/// <para>
/// Registered as a SINGLETON by the shell and injected, like <see cref="IPlaybackSession"/>. There is
/// no <c>IDisposable</c> here for the same reason: an app disposing an injected singleton would tear down
/// the shell's player for everyone. Say <see cref="CloseAsync"/>.
/// </para>
/// </summary>
public interface IMediaPlayer
{
    /// <summary>
    /// The player's state right now — cheap, and the honest answer rather than a cached one.
    /// </summary>
    MediaPlayerStatus Status { get; }

    /// <summary>
    /// Playback speed as a multiplier; 1.0 is normal. Setting it while paused is remembered and applied on
    /// the next <see cref="PlayAsync"/>.
    /// <para>
    /// ⚠ <b>A platform may not honour the exact value.</b> Each clamps to its own supported range and some
    /// refuse rates a codec cannot resample; the kit does not pretend otherwise by silently substituting.
    /// <see cref="MediaPlayerStatus.Rate"/> reports what was ASKED FOR, so a UI showing "1.5×" shows what
    /// the user chose.
    /// </para>
    /// </summary>
    double Rate { get; set; }

    /// <summary>
    /// Open a source and get ready to play, without starting. Completes when the platform can report a
    /// duration and accept a seek — which is the point a UI can draw a real scrubber.
    /// <para>
    /// ⚠ <b>Opening does not start playback</b>, deliberately: a caller that wants both says so, and one
    /// that is restoring a session wants a paused player at a saved position, which is the more awkward
    /// thing to express afterwards.
    /// </para>
    /// <para>
    /// <b>AUDIO is what this promises today.</b> Video decodes and its clock advances, but nothing composites
    /// a video surface into the page — see the type's remarks. An app playing video should keep using
    /// <c>&lt;video&gt;</c> in the foreground; this is for the case that element cannot serve.
    /// </para>
    /// </summary>
    /// <param name="source">What to play.</param>
    /// <param name="cancellationToken">Abandons the open. A cancelled open leaves the player
    /// <see cref="MediaPlayerState.Empty"/>, not half-loaded.</param>
    /// <exception cref="MediaPlayerException">The source could not be opened.</exception>
    Task OpenAsync(MediaSource source, CancellationToken cancellationToken = default);

    /// <summary>Start or resume. A no-op if already playing; fails if there is no source.</summary>
    Task PlayAsync(CancellationToken cancellationToken = default);

    /// <summary>Hold at the current position. A no-op if already paused.</summary>
    Task PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Move to an absolute position, clamped to the source. Seeking a player that is playing keeps it
    /// playing; seeking one that is paused leaves it paused.
    /// </summary>
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    /// <summary>
    /// Release the source and return to <see cref="MediaPlayerState.Empty"/>.
    /// <para>
    /// ⚠ <b>Say this when playback is over.</b> An open player holds a decoder, a file handle and — on the
    /// mobile shells — a slice of a process-wide media service. It is the counterpart to
    /// <see cref="IPlaybackSession.Clear"/>, and an app usually wants both.
    /// </para>
    /// </summary>
    Task CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The player's state changed — a transition, not a tick. Raised on open, play, pause, seek, buffering,
    /// end and failure; NOT while a position merely advances.
    /// <para>
    /// ⚠ <b>Raised on whatever thread the platform uses, which is not the UI thread.</b> Marshal with
    /// <see cref="IUiDispatcher"/> before touching UI. A throwing handler is caught and logged rather
    /// than escaping into a platform callback (<see cref="AppCallback"/>) — an exception inside an
    /// AVFoundation or ExoPlayer callback is not catchable by anyone.
    /// </para>
    /// </summary>
    event Action<MediaPlayerStatus>? StateChanged;
}

/// <summary>
/// A player operation failed. Carries an app-safe reason; the platform's own text is logged, never thrown
/// — the same rule every error path in the IPC stack follows.
/// </summary>
public sealed class MediaPlayerException : Exception
{
    /// <summary>A player operation failed.</summary>
    /// <param name="message">App-safe reason.</param>
    public MediaPlayerException(string message) : base(message) { }

    /// <summary>A player operation failed.</summary>
    /// <param name="message">App-safe reason.</param>
    /// <param name="inner">The platform failure. Logged by the shell; not surfaced to a page.</param>
    public MediaPlayerException(string message, Exception inner) : base(message, inner) { }
}
