using Shenora.Modules.Platform;
using Shenora.Core.Shell;

namespace Shenora.Modules.Media;

/// <summary>What the player is doing. The same vocabulary as <see cref="PlaybackState"/>, which describes
/// the same thing one level up — what the OS should say.</summary>
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
/// A player's state at one instant. ⚠ <b>Position is a SNAPSHOT, not a subscription</b> —
/// <see cref="IMediaPlayer.StateChanged"/> is raised on real transitions only, so whoever draws a scrubber
/// polls at the rate that scrubber redraws.
/// </summary>
public sealed record MediaPlayerStatus
{
    /// <summary>What the player is doing.</summary>
    public required MediaPlayerState State { get; init; }

    /// <summary>Where it has got to. <see cref="TimeSpan.Zero"/> when there is no source.</summary>
    public TimeSpan Position { get; init; }

    /// <summary>How long the source is, when the platform knows. Null for a live stream and — briefly —
    /// while <see cref="MediaPlayerState.Opening"/>.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Playback speed as a multiplier; 1.0 is normal. The RATE ASKED FOR, which is not always what
    /// the hardware does — see <see cref="IMediaPlayer.SetRateAsync"/>.</summary>
    public double Rate { get; init; } = 1.0;

    /// <summary>Why it failed, when <see cref="State"/> is <see cref="MediaPlayerState.Failed"/>; null
    /// otherwise. ⚠ <b>A short, app-safe reason — never the platform's raw exception text</b>, which can
    /// reach a page.</summary>
    public string? Error { get; init; }
}

/// <summary>What to play: a file the host can open, or a URL the platform will stream. Never a
/// <c>Stream</c> — a native player owns the read, on its own threads.</summary>
public sealed record MediaSource
{
    /// <summary>An absolute local file path, or an <c>http(s)</c> URL — including one served by the app's
    /// own in-process host, which is how a source needing the kit's remux reaches the player.</summary>
    public required string Uri { get; init; }

    /// <summary>Start here rather than at zero. Applied as part of opening, so a resumed item does not
    /// visibly start at zero and jump the way open-then-seek does.</summary>
    public TimeSpan StartAt { get; init; }
}

/// <summary>
/// A media player owned by the HOST, driven by the page (D54), implemented once per shell (D19/D20). It
/// plays what the PLATFORM decodes, which a <c>&lt;video&gt;</c> element does not.
/// <para>
/// <b>What this does NOT do.</b> No queue, no playlist, no gapless, no crossfade, no shuffle.
/// </para>
/// <para>
/// <b>The PICTURE needs two halves, and either one missing is sound with a blank frame.</b> A shell that
/// registers an <see cref="IMediaSurface"/> — which is where the page says the picture goes — and a player
/// that takes the platform handle it offers (<see cref="MediaPlayerBase.AttachSurface"/>). Without both,
/// video still decodes and its clock still advances; nothing composites it into the page's layout.
/// </para>
/// <para>
/// Registered as a SINGLETON by the shell and injected. ⚠ There is no <c>IDisposable</c> — disposing an
/// injected singleton would tear down the shell's player for everyone. Say <see cref="CloseAsync"/>.
/// </para>
/// </summary>
public interface IMediaPlayer
{
    /// <summary>The player's state right now — cheap, and not a cached answer.</summary>
    MediaPlayerStatus Status { get; }

    /// <summary>
    /// Set playback speed as a multiplier; 1.0 is normal. Setting it while paused is remembered and
    /// applied on the next <see cref="PlayAsync"/>. Read the current value from
    /// <see cref="MediaPlayerStatus.Rate"/> via <see cref="Status"/>.
    /// <para>
    /// ⚠ <b>A platform may not honour the exact value</b> — each clamps to its own range and some refuse
    /// rates a codec cannot resample, so <see cref="MediaPlayerStatus.Rate"/> reports what was ASKED FOR.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rate"/> is not greater than zero.</exception>
    Task SetRateAsync(double rate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Open a source and get ready to play, without starting. Completes when the platform can report a
    /// duration and accept a seek — the point a UI can draw a real scrubber. ⚠ <b>Opening does not start
    /// playback</b>: a caller restoring a session gets a paused player at a saved position.
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

    /// <summary>Move to an absolute position, clamped to the source. A player that is playing keeps
    /// playing; one that is paused stays paused.</summary>
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    /// <summary>Release the source and return to <see cref="MediaPlayerState.Empty"/>. ⚠ <b>Say this when
    /// playback is over</b> — an open player holds a decoder, a file handle and, on the mobile shells, a
    /// slice of a process-wide media service.</summary>
    Task CloseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The player's state changed — a transition, not a tick. Raised on open, play, pause, seek, buffering,
    /// end and failure; NOT while a position merely advances.
    /// <para>
    /// ⚠ <b>Raised on whatever thread the platform uses, which is not the UI thread</b> — marshal with
    /// <see cref="IUiDispatcher"/> before touching UI. A throwing handler is caught and logged
    /// (<see cref="AppCallback"/>).
    /// </para>
    /// </summary>
    event Action<MediaPlayerStatus>? StateChanged;
}

/// <summary>A player operation failed. Carries an app-safe reason; the platform's own text is logged,
/// never thrown.</summary>
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
