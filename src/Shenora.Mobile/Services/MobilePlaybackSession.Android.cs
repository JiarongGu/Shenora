#if ANDROID
using Android.Media;
using Android.Media.Session;
using Shenora.Core;
// BOTH namespaces define `PlaybackState` and they mean different things — ours is the portable enum,
// Android's is the builder-built state object. Aliased rather than partially qualified so no line in this
// file is ambiguous to a reader either.
using AndroidState = Android.Media.Session.PlaybackState;
using PortableState = Shenora.Core.PlaybackState;

namespace Shenora.Mobile;

/// <summary>
/// Android's <see cref="IPlaybackSession"/> — a platform <c>MediaSession</c>, which is what routes
/// headphone and steering-wheel buttons and what the system media controls read.
/// <para>
/// The PLATFORM <c>MediaSession</c> (API 21+) rather than AndroidX <c>MediaSessionCompat</c>, deliberately:
/// the compat class would drag an AndroidX media dependency into a package whose whole selling point is
/// that it adds almost nothing, and every API this uses has been present since the minimum the shell
/// supports.
/// </para>
/// <para>
/// ⚠ <b>A session makes the app CONTROLLABLE; a MediaStyle notification is what makes it VISIBLE.</b>
/// Everything here — metadata, state, actions, button routing — works without one, and
/// <c>adb shell dumpsys media_session</c> shows it. But the media controls in the shade and on the lock
/// screen are attached to a notification, and posting one means choosing an icon, a channel name and a
/// channel importance, which are the app's design decisions and not the kit's (D13). So the kit owns the
/// session and the app owns the notification; see the class remarks in <see cref="IPlaybackSession"/> for
/// where that line sits.
/// </para>
/// </summary>
public sealed class MobilePlaybackSession : IPlaybackSession, IDisposable
{
    /// <summary>
    /// The tag Android shows for this session — in <c>dumpsys</c>, in bug reports, and to a car head unit
    /// that lists sessions by tag. Not user-facing text, so it is not localised.
    /// </summary>
    private const string SessionTag = "ShenoraPlayback";

    private readonly MediaSession _session;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private PlaybackCommands _supported;
    private long _lastPositionMs;
    private bool _disposed;

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into a platform callback.</param>
    public MobilePlaybackSession(Action<string>? log = null)
    {
        _log = log;
        _session = new MediaSession(Android.App.Application.Context, SessionTag);
        _session.SetCallback(new CommandCallback(this));
        // Active BEFORE anything is published: an inactive session is invisible to dumpsys and to media
        // button routing, so a host that set this last would look like it had done nothing at all.
        _session.Active = true;
    }

    /// <inheritdoc />
    public PlaybackCommands Supported
    {
        get { lock (_gate) return _supported; }
        set
        {
            lock (_gate) _supported = value;
            // Re-publishing the state is how Android learns the action set — actions live on
            // PlaybackState, not on the session — so changing what is supported has to re-send it.
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
                // Decoded here rather than on a pool thread, unlike the desktop: this is a synchronous
                // call with no async equivalent, and a lock-screen thumbnail is small. A FAILED decode
                // returns null rather than throwing, so a malformed image quietly means "no artwork"
                // instead of taking the metadata with it.
                var bytes = info.Artwork.ToArray();
                var bitmap = Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                if (bitmap is not null) builder.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, bitmap);
                else Log(() => "[Shenora.Mobile] Playback artwork could not be decoded; metadata still published.");
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
            builder.SetState(StateFor(progress.State), _lastPositionMs, (float)progress.Rate);
            _session.SetPlaybackState(builder.Build());
        }, nameof(Report));
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (_disposed) return;
        Try(() =>
        {
            _session.SetMetadata(null);
            // Not chained off SetState: the binding returns a NULLABLE builder, so a fluent chain is a
            // possible-null dereference the analyser is right about.
            var cleared = new AndroidState.Builder();
            cleared.SetState(PlaybackStateCode.None, 0, 0f);
            _session.SetPlaybackState(cleared.Build());
            // Inactive is what takes the app out of the system's session list — the counterpart of the
            // constructor's Active = true, and the difference between Clear() and reporting Stopped.
            _session.Active = false;
        }, nameof(Clear));
    }

    /// <summary>
    /// Map the portable state onto Android's. <c>Buffering</c> is a real state here, which is half the
    /// reason the portable enum has one.
    /// </summary>
    private static PlaybackStateCode StateFor(PortableState state) => state switch
    {
        PortableState.Playing => PlaybackStateCode.Playing,
        PortableState.Paused => PlaybackStateCode.Paused,
        PortableState.Buffering => PlaybackStateCode.Buffering,
        _ => PlaybackStateCode.Stopped,
    };

    /// <summary>
    /// Map the supported set onto Android's action bits.
    /// <para>
    /// ⚠ <c>TogglePlayPause</c> lights <c>ActionPlayPause</c> AND both halves, for the same reason the
    /// desktop does: Android's default callback dispatch turns a media-button toggle into
    /// <c>OnPlay</c>/<c>OnPause</c> based on the current state, so declaring only the toggle would leave
    /// the concrete buttons unavailable to anything that offers them separately.
    /// </para>
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
        return actions;
    }

    private void Raise(PlaybackCommand command, TimeSpan? position = null)
    {
        var handler = CommandReceived;
        if (handler is null) return;
        var request = new PlaybackCommandRequest { Command = command, Position = position };
        // The ONE guard. These run on the session's handler thread, where an escaping exception has no
        // caller and takes the process with it.
        AppCallback.Run(() => handler(request),
            ex => Log(() => $"[Shenora.Mobile] A {command} handler threw ({ex.GetType().Name}: {ex.Message})."));
    }

    private void Try(Action action, string what)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log(() => $"[Shenora.Mobile] MediaSession.{what} failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    private void Log(Func<string> message) => AppCallback.Log(_log, message);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Try(() =>
        {
            _session.Active = false;
            // Release, not just inactive: the session is a system-registered object and leaking it keeps
            // the app in the media session list for the life of the process.
            _session.Release();
        }, nameof(Dispose));
        _session.Dispose();
    }

    /// <summary>
    /// Android's side of the two-way contract. Every override is a control the OS, a headphone or a car
    /// stereo asked for; there is no <c>OnTogglePlayPause</c> because the platform resolves a toggle into
    /// <see cref="OnPlay"/>/<see cref="OnPause"/> itself.
    /// </summary>
    private sealed class CommandCallback(MobilePlaybackSession owner) : MediaSession.Callback
    {
        public override void OnPlay() => owner.Raise(PlaybackCommand.Play);

        public override void OnPause() => owner.Raise(PlaybackCommand.Pause);

        public override void OnStop() => owner.Raise(PlaybackCommand.Stop);

        public override void OnSkipToNext() => owner.Raise(PlaybackCommand.Next);

        public override void OnSkipToPrevious() => owner.Raise(PlaybackCommand.Previous);

        public override void OnSeekTo(long pos) =>
            owner.Raise(PlaybackCommand.Seek, TimeSpan.FromMilliseconds(pos));
    }
}
#endif
