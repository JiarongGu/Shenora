using Microsoft.Extensions.Logging;
using Shenora.Modules.Platform;

using Android.Media;
using Android.Media.Session;
using Shenora;
// Both namespaces define `PlaybackState`: ours is the portable enum, Android's the built state object.
using AndroidState = Android.Media.Session.PlaybackState;
using PortableState = Shenora.Modules.Platform.PlaybackState;

namespace Shenora.Android;

/// <summary>
/// Android's <see cref="IPlaybackSession"/> — the platform <c>MediaSession</c> (API 21+, not AndroidX's
/// compat class, which would drag an AndroidX media dependency in). It is what routes headphone and
/// steering-wheel buttons and what the system media controls read.
/// <para>
/// ⚠ <b>A session makes the app CONTROLLABLE; a MediaStyle notification is what makes it VISIBLE.</b>
/// Everything here works without one, but the shade and lock-screen controls attach to a notification,
/// and choosing its icon, channel and importance is the app's decision (D13). Bind one with
/// <see cref="SessionToken"/>.
/// </para>
/// </summary>
public sealed class AndroidPlaybackSession : IPlaybackSession, IDisposable
{
    /// <summary>The tag Android shows in <c>dumpsys</c> and to a car head unit. Not user-facing text.</summary>
    private const string SessionTag = "ShenoraPlayback";

    private readonly MediaSession _session;
    private readonly ILogger? _log;
    private readonly object _gate = new();
    private PlaybackCommands _supported;
    private long _lastPositionMs;
    private TimeSpan _skipInterval = TimeSpan.FromSeconds(15);
    private bool _disposed;

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into a platform callback.</param>
    public AndroidPlaybackSession(ILogger? log = null)
    {
        _log = log;
        _session = new MediaSession(global::Android.App.Application.Context, SessionTag);
        _session.SetCallback(new CommandCallback(this));
        // Active marks this as a CURRENT session — the flag the system reads when choosing which one to
        // surface and to route hardware buttons to.
        // ⚠ Sabotage-measured on Android 12: without it the session is STILL LISTED by
        // `dumpsys media_session`, metadata and state intact, with only `active=false` to show it — so
        // "is it registered?" is not the check that catches this. `Media button session` stayed null in
        // BOTH runs, so this alone does not win the media buttons; audio focus is the app's to own.
        _session.Active = true;
    }

    /// <inheritdoc />
    public PlaybackCommands Supported
    {
        get { lock (_gate) return _supported; }
        set
        {
            lock (_gate) _supported = value;
            // Actions live on PlaybackState, not on the session, so a changed set has to be re-sent.
            Report(new PlaybackProgress
            {
                State = _lastState,
                Position = TimeSpan.FromMilliseconds(_lastPositionMs),
                Rate = _lastRate,
            });
        }
    }

    private PortableState _lastState = PortableState.Stopped;
    private double _lastRate = 1.0;

    /// <inheritdoc />
    /// <remarks>Android takes no preferred interval; this is what a skip request carries back.</remarks>
    public TimeSpan SkipInterval
    {
        get { lock (_gate) return _skipInterval; }
        set { lock (_gate) _skipInterval = value; }
    }

    /// <summary>
    /// The platform session's token — what an app passes to
    /// <c>Notification.MediaStyle.SetMediaSession(token)</c> to bind its own notification to this session.
    /// <para>
    /// Android-only, and not on <see cref="IPlaybackSession"/>: the type is
    /// <c>Android.Media.Session.MediaSession.Token</c> and the portable contract carries no platform types.
    /// Cast the injected <see cref="IPlaybackSession"/>, or register this class itself.
    /// </para>
    /// </summary>
    public MediaSession.Token? SessionToken => _disposed ? null : _session.SessionToken;

    /// <inheritdoc />
    public event Action<PlaybackCommandRequest>? CommandReceived;

    /// <inheritdoc />
    public void Publish(PlaybackInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Try(() =>
        {
            var builder = new MediaMetadata.Builder();
            builder.PutString(MediaMetadata.MetadataKeyTitle, info.Title ?? string.Empty);
            builder.PutString(MediaMetadata.MetadataKeyArtist, info.Subtitle ?? string.Empty);
            builder.PutString(MediaMetadata.MetadataKeyAlbum, info.GroupName ?? string.Empty);
            if (info.Duration is { } duration)
                builder.PutLong(MediaMetadata.MetadataKeyDuration, (long)duration.TotalMilliseconds);

            if (!info.Artwork.IsEmpty)
            {
                // Synchronous — there is no async equivalent, and a lock-screen thumbnail is small. A
                // failed decode returns null rather than throwing, so bad artwork does not lose metadata.
                var bytes = info.Artwork.ToArray();
                var bitmap = global::Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                if (bitmap is not null) builder.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, bitmap);
                else Log(() => "[Shenora.Android] Playback artwork could not be decoded; metadata still published.");
            }

            _session.SetMetadata(builder.Build());
        }, nameof(Publish));
    }

    /// <inheritdoc />
    public void Report(PlaybackProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (_disposed) return;

        _lastState = progress.State;
        _lastRate = progress.Rate;
        _lastPositionMs = (long)progress.Position.TotalMilliseconds;

        Try(() =>
        {
            var builder = new AndroidState.Builder();
            builder.SetActions(ActionsFor(Supported));
            builder.SetState(StateFor(progress.State), _lastPositionMs, (float)RateFor(progress));
            _session.SetPlaybackState(builder.Build());
        }, nameof(Report));
    }

    /// <summary>
    /// The speed to publish — DERIVED from the state, never the caller's rate verbatim.
    /// <para>
    /// ⚠ A controller extrapolates the displayed position as <c>position + elapsed × speed</c>, so a
    /// paused session advertising <c>1.0</c> lets the lock-screen scrubber walk away from audio that is
    /// not moving. Android documents 0 as the paused speed and behaves that way (measured
    /// <c>state=2 … speed=1.0</c> on Android 12 via <c>dumpsys media_session</c>).
    /// </para>
    /// </summary>
    private static double RateFor(PlaybackProgress progress) =>
        progress.State == PortableState.Playing ? progress.Rate : 0.0;

    /// <inheritdoc />
    public void Clear()
    {
        if (_disposed) return;
        Try(() =>
        {
            _session.SetMetadata(null);
            // Not chained: the binding's SetState returns a NULLABLE builder.
            var cleared = new AndroidState.Builder();
            cleared.SetState(PlaybackStateCode.None, 0, 0f);
            _session.SetPlaybackState(cleared.Build());
            // Inactive is what takes the app out of the system's session list; reporting Stopped does not.
            _session.Active = false;
        }, nameof(Clear));
    }

    /// <summary>Map the portable state onto Android's.</summary>
    private static PlaybackStateCode StateFor(PortableState state) => state switch
    {
        PortableState.Playing => PlaybackStateCode.Playing,
        PortableState.Paused => PlaybackStateCode.Paused,
        PortableState.Buffering => PlaybackStateCode.Buffering,
        _ => PlaybackStateCode.Stopped,
    };

    /// <summary>
    /// Map the supported set onto Android's action bits. ⚠ <c>TogglePlayPause</c> lights
    /// <c>ActionPlayPause</c> AND both halves — the platform resolves a media-button toggle into
    /// <c>OnPlay</c>/<c>OnPause</c>, so declaring only the toggle leaves the concrete buttons dark.
    /// </summary>
    private static long ActionsFor(PlaybackCommands commands)
    {
        var toggle = commands.HasFlag(PlaybackCommands.TogglePlayPause);
        long actions = 0;
        if (toggle) actions |= AndroidState.ActionPlayPause;
        if (toggle || commands.HasFlag(PlaybackCommands.Play)) actions |= AndroidState.ActionPlay;
        if (toggle || commands.HasFlag(PlaybackCommands.Pause)) actions |= AndroidState.ActionPause;
        if (commands.HasFlag(PlaybackCommands.Stop)) actions |= AndroidState.ActionStop;
        if (commands.HasFlag(PlaybackCommands.Next)) actions |= AndroidState.ActionSkipToNext;
        if (commands.HasFlag(PlaybackCommands.Previous)) actions |= AndroidState.ActionSkipToPrevious;
        if (commands.HasFlag(PlaybackCommands.Seek)) actions |= AndroidState.ActionSeekTo;
        // Android names these fast-forward/rewind; an app gives them skip-interval semantics.
        if (commands.HasFlag(PlaybackCommands.SkipForward)) actions |= AndroidState.ActionFastForward;
        if (commands.HasFlag(PlaybackCommands.SkipBackward)) actions |= AndroidState.ActionRewind;
        return actions;
    }

    private void Raise(PlaybackCommand command, TimeSpan? position = null, TimeSpan? interval = null)
    {
        var handler = CommandReceived;
        if (handler is null) return;
        var request = new PlaybackCommandRequest { Command = command, Position = position, Interval = interval };
        // 🔴 These run on the session's handler thread, where an escaping exception takes the process.
        AppCallback.Run(() => handler(request),
            ex => Log(() => $"[Shenora.Android] A {command} handler threw.", ex));
    }

    private void Try(Action action, string what)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log(() => $"[Shenora.Android] MediaSession.{what} failed.", ex);
        }
    }

    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_log, message, exception: failure);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Try(() =>
        {
            _session.Active = false;
            // Release, not just inactive: a leaked session stays in the media session list for the
            // life of the process.
            _session.Release();
        }, nameof(Dispose));
        _session.Dispose();
    }

    /// <summary>
    /// Android's side of the two-way contract. There is no <c>OnTogglePlayPause</c> — the platform
    /// resolves a toggle into <see cref="OnPlay"/>/<see cref="OnPause"/> itself.
    /// </summary>
    private sealed class CommandCallback(AndroidPlaybackSession owner) : MediaSession.Callback
    {
        public override void OnPlay() => owner.Raise(PlaybackCommand.Play);

        public override void OnPause() => owner.Raise(PlaybackCommand.Pause);

        public override void OnStop() => owner.Raise(PlaybackCommand.Stop);

        public override void OnSkipToNext() => owner.Raise(PlaybackCommand.Next);

        public override void OnSkipToPrevious() => owner.Raise(PlaybackCommand.Previous);

        public override void OnSeekTo(long pos) =>
            owner.Raise(PlaybackCommand.Seek, TimeSpan.FromMilliseconds(pos));

        // No interval arrives with these; the contract promises one is always set for a skip.
        public override void OnFastForward() =>
            owner.Raise(PlaybackCommand.SkipForward, interval: owner.SkipInterval);

        public override void OnRewind() =>
            owner.Raise(PlaybackCommand.SkipBackward, interval: owner.SkipInterval);
    }
}
