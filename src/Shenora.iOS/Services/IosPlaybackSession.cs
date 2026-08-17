using Microsoft.Extensions.Logging;
using Shenora.Modules.Platform;
using Shenora.Modules.Media;

using Foundation;
using MediaPlayer;
using Shenora;
using PortableState = Shenora.Modules.Platform.PlaybackState;

namespace Shenora.iOS;

/// <summary>
/// iOS's <see cref="IPlaybackSession"/> — <c>MPNowPlayingInfoCenter</c> for what is playing and
/// <c>MPRemoteCommandCenter</c> for the controls coming back.
/// <para>
/// Both are process-wide singletons, which is why this class holds no session object of its own: unlike
/// Android's <c>MediaSession</c> or Windows' <c>SystemMediaTransportControls</c> there is nothing to create,
/// only shared state to write. That makes <see cref="Dispose"/> genuinely important — the command targets
/// are added to a singleton and would otherwise outlive this object and keep firing into a dead handler.
/// </para>
/// <para>
/// ⚠ <b>iOS shows this only for an app the system believes is playing audio.</b> Setting the info is
/// necessary and not sufficient: without an active <c>AVAudioSession</c> the lock screen may show nothing,
/// which is the same shape of limit as Android needing a MediaStyle notification. Configuring an audio
/// session is the APP's call — it decides the category, whether it mixes, and what happens on interruption —
/// so the kit publishes and stays out of it.
/// </para>
/// </summary>
public sealed class IosPlaybackSession : IPlaybackSession, IDisposable
{
    private readonly ILogger? _log;
    private readonly object _gate = new();
    private readonly List<(MPRemoteCommand Command, NSObject Target)> _targets = [];
    private PlaybackCommands _supported;
    private TimeSpan _skipInterval = TimeSpan.FromSeconds(15);
    private MPNowPlayingInfo _info = new();
    private bool _disposed;

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into a platform callback.</param>
    public IosPlaybackSession(ILogger? log = null)
    {
        _log = log;
        // Every command is wired ONCE here and enabled/disabled by `Supported` afterwards. Adding and
        // removing targets as the supported set changes is the obvious alternative and it leaks: each
        // AddTarget returns a new token, and the shared command center accumulates them.
        Wire(MPRemoteCommandCenter.Shared.PlayCommand, PlaybackCommand.Play);
        Wire(MPRemoteCommandCenter.Shared.PauseCommand, PlaybackCommand.Pause);
        Wire(MPRemoteCommandCenter.Shared.TogglePlayPauseCommand, PlaybackCommand.TogglePlayPause);
        Wire(MPRemoteCommandCenter.Shared.StopCommand, PlaybackCommand.Stop);
        Wire(MPRemoteCommandCenter.Shared.NextTrackCommand, PlaybackCommand.Next);
        Wire(MPRemoteCommandCenter.Shared.PreviousTrackCommand, PlaybackCommand.Previous);
        WireSeek(MPRemoteCommandCenter.Shared.ChangePlaybackPositionCommand);
        WireSkip(MPRemoteCommandCenter.Shared.SkipForwardCommand, PlaybackCommand.SkipForward);
        WireSkip(MPRemoteCommandCenter.Shared.SkipBackwardCommand, PlaybackCommand.SkipBackward);
        Supported = PlaybackCommands.None;
    }

    /// <inheritdoc />
    public PlaybackCommands Supported
    {
        get { lock (_gate) return _supported; }
        set
        {
            lock (_gate) _supported = value;
            var toggle = value.HasFlag(PlaybackCommands.TogglePlayPause);
            Try(() =>
            {
                var c = MPRemoteCommandCenter.Shared;
                // Same rule as the other two shells: a toggle also lights the concrete pair, because
                // hardware sends whichever it likes and an app that declared only the toggle would find
                // half its buttons dead.
                c.PlayCommand.Enabled = toggle || value.HasFlag(PlaybackCommands.Play);
                c.PauseCommand.Enabled = toggle || value.HasFlag(PlaybackCommands.Pause);
                c.TogglePlayPauseCommand.Enabled = toggle;
                c.StopCommand.Enabled = value.HasFlag(PlaybackCommands.Stop);
                c.NextTrackCommand.Enabled = value.HasFlag(PlaybackCommands.Next);
                c.PreviousTrackCommand.Enabled = value.HasFlag(PlaybackCommands.Previous);
                c.ChangePlaybackPositionCommand.Enabled = value.HasFlag(PlaybackCommands.Seek);
                c.SkipForwardCommand.Enabled = value.HasFlag(PlaybackCommands.SkipForward);
                c.SkipBackwardCommand.Enabled = value.HasFlag(PlaybackCommands.SkipBackward);
                // The interval is re-applied here as well as in the setter, because an app may set
                // Supported first — and without PreferredIntervals iOS draws a bare arrow with no number,
                // which reads to a user as a different control entirely.
                ApplySkipInterval();
            }, nameof(Supported));
        }
    }

    /// <inheritdoc />
    public TimeSpan SkipInterval
    {
        get { lock (_gate) return _skipInterval; }
        set
        {
            lock (_gate) _skipInterval = value;
            ApplySkipInterval();
        }
    }

    /// <summary>
    /// Push the interval to both commands. ⚠ This is what makes iOS render the NUMBER on the button —
    /// PreferredIntervals is a display contract as much as a behavioural one, so an unset interval looks
    /// like a bare skip arrow rather than a 15-second one.
    /// </summary>
    private void ApplySkipInterval() => Try(() =>
    {
        // double[], NOT NSNumber[] — checked against the binding after assuming otherwise, the same way
        // MPNowPlayingInfoCenter.playbackState turned out not to exist here at all.
        double[] seconds = [SkipInterval.TotalSeconds];
        MPRemoteCommandCenter.Shared.SkipForwardCommand.PreferredIntervals = seconds;
        MPRemoteCommandCenter.Shared.SkipBackwardCommand.PreferredIntervals = seconds;
    }, nameof(SkipInterval));

    /// <inheritdoc />
    public event Action<PlaybackCommandRequest>? CommandReceived;

    /// <inheritdoc />
    public void Publish(PlaybackInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Try(() =>
        {
            // The info center takes a WHOLE object, so a partial update erases the rest. This keeps the
            // last one and mutates it, which is why Report() can move the position without re-sending the
            // title — the trap being that the naive version blanks the metadata on every position update.
            _info.Title = info.Title;
            _info.Artist = info.Subtitle;
            _info.AlbumTitle = info.GroupName;
            if (info.Duration is { } duration) _info.PlaybackDuration = duration.TotalSeconds;

            if (!info.Artwork.IsEmpty)
            {
                var image = UIKit.UIImage.LoadFromData(Foundation.NSData.FromArray(info.Artwork.ToArray()));
                if (image is not null)
                {
                    // The size+handler ctor, not MPMediaItemArtwork(UIImage) — that one is obsolete since
                    // iOS 10 and the analyser fails the build on it. The handler is called by the system
                    // with the size it wants; returning the decoded image unscaled is acceptable and is
                    // what a lock-screen thumbnail needs, since the image is already small.
                    _info.Artwork = new MPMediaItemArtwork(image.Size, _ => image);
                }
                else
                {
                    // Decoration only — never let a bad image take the metadata with it.
                    Log(() => "[Shenora.iOS] Playback artwork could not be decoded; metadata still published.");
                }
            }

            MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = _info;
        }, nameof(Publish));
    }

    /// <inheritdoc />
    public void Report(PlaybackProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (_disposed) return;

        Try(() =>
        {
            _info.ElapsedPlaybackTime = progress.Position.TotalSeconds;
            // The RATE is how iOS knows whether to keep counting. A paused session reporting 1.0 shows a
            // clock that keeps advancing over audio that stopped, which is why Buffering reports 0 too:
            // the position is not moving, whatever the app's intent.
            _info.PlaybackRate = progress.State switch
            {
                PortableState.Playing => (float)progress.Rate,
                _ => 0f,
            };
            MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = _info;

            // ⚠ There is NO explicit playback-state property to set here. `MPNowPlayingInfoCenter`'s
            // `playbackState` is macOS/tvOS only — it is absent from the iOS binding entirely, verified
            // against the reference assembly after assuming otherwise and being corrected by the compiler.
            // So on this platform the RATE carries the state, which is why the mapping above is the whole
            // story and Paused/Stopped/Buffering all report 0.
        }, nameof(Report));
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (_disposed) return;
        Try(() =>
        {
            _info = new MPNowPlayingInfo();
            // ObjC `nil`, not an empty info object: an empty one leaves the app on the lock screen with
            // blank text — the "present but broken" look rather than "not playing" — and nil is what
            // Apple documents for clearing. `null!` because the binding annotates this non-nullable, which
            // is stricter than the API it wraps; the suppression is the honest way to say so.
            MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = null!;
        }, nameof(Clear));
    }

    private void Wire(MPRemoteCommand command, PlaybackCommand mapped)
    {
        var target = command.AddTarget(_ =>
        {
            Raise(mapped);
            return MPRemoteCommandHandlerStatus.Success;
        });
        _targets.Add((command, target));
    }

    /// <summary>
    /// iOS sends the interval WITH the event, so that is what is reported rather than the configured value
    /// — honouring what arrived is more correct than assuming what was asked for, and the two can differ if
    /// the system chose from the preferred list.
    /// </summary>
    private void WireSkip(MPSkipIntervalCommand command, PlaybackCommand mapped)
    {
        var target = command.AddTarget(args =>
        {
            // Interval is a plain double on the event (not an NSNumber, unlike the ObjC signature), and
            // it can arrive as 0 — in which case the configured value is the honest fallback.
            var seconds = args is MPSkipIntervalCommandEvent e && e.Interval > 0
                ? e.Interval
                : SkipInterval.TotalSeconds;
            Raise(mapped, interval: TimeSpan.FromSeconds(seconds));
            return MPRemoteCommandHandlerStatus.Success;
        });
        _targets.Add((command, target));
    }

    private void WireSeek(MPChangePlaybackPositionCommand command)
    {
        var target = command.AddTarget(args =>
        {
            if (args is not MPChangePlaybackPositionCommandEvent e)
                return MPRemoteCommandHandlerStatus.CommandFailed;
            Raise(PlaybackCommand.Seek, TimeSpan.FromSeconds(e.PositionTime));
            return MPRemoteCommandHandlerStatus.Success;
        });
        _targets.Add((command, target));
    }

    private void Raise(PlaybackCommand command, TimeSpan? position = null, TimeSpan? interval = null)
    {
        var handler = CommandReceived;
        if (handler is null) return;
        var request = new PlaybackCommandRequest { Command = command, Position = position, Interval = interval };
        AppCallback.Run(() => handler(request),
            ex => Log(() => $"[Shenora.iOS] A {command} handler threw.", ex));
    }

    private void Try(Action action, string what)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log(() => $"[Shenora.iOS] NowPlaying.{what} failed.", ex);
        }
    }

    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_log, message, exception: failure);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        // BEFORE the flag flips: Clear() no-ops on a disposed session, so flipping first made this
        // call — the one that takes the stale entry off the lock screen — a guaranteed no-op.
        Clear();
        _disposed = true;
        // The command center is a SINGLETON, so these targets survive this object unless removed — and a
        // stale target fires into a disposed handler for the rest of the process.
        foreach (var (command, target) in _targets)
        {
            try { command.RemoveTarget(target); }
            catch (Exception ex) { Log(() => "[Shenora.iOS] RemoveTarget", ex); }
        }
        _targets.Clear();
        Clear();
    }
}
