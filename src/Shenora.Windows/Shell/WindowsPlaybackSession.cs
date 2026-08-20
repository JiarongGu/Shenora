using Microsoft.Extensions.Logging;
using Shenora.Modules.Platform;
using Shenora.Modules.Media;

// SystemMediaTransportControls is WinRT, so this package MULTI-TARGETS: this whole file is the versioned
// half and WindowsPlaybackSession.Unsupported.cs is the plain one, which carries the reference statement for
// every such split here. The FILE is guarded rather than each body, so the public shape is written twice and
// the plain TFM is gated by its own metadata baseline — see MetadataSurfaceTests.
#if WINDOWS10_0_17763_0_OR_GREATER
using Shenora;
// 🔴 `global::` on every WinRT namespace below, and it is not optional: inside `namespace Shenora.Windows`
// the bare identifier `Windows` binds to THIS namespace, so `Windows.Media` resolves to
// `Shenora.Windows.Windows.Media` and fails with a CS0234 that blames `Shenora.Windows`.
using SmtcButton = global::Windows.Media.SystemMediaTransportControlsButton;
using SmtcStatus = global::Windows.Media.MediaPlaybackStatus;

namespace Shenora.Windows;

/// <summary>
/// The desktop shell's <see cref="IPlaybackSession"/> — Windows' own media flyout, the one the volume
/// keys and the lock screen drive, via <c>SystemMediaTransportControls</c>.
/// <para>
/// Obtained through a <c>MediaPlayer</c>, which exposes an SMTC with no window and no interop:
/// <c>GetForCurrentView()</c> needs a <c>CoreWindow</c> a Win32 app does not have, and
/// <c>ISystemMediaTransportControlsInterop.GetForWindow</c> needs hand-rolled activation since .NET 5
/// dropped built-in WinRT marshalling. The cost is one idle media pipeline.
/// </para>
/// <para>
/// 🔴 <c>CommandManager.IsEnabled = false</c> is load-bearing: left on, the <c>MediaPlayer</c> answers the
/// transport buttons ITSELF against its own empty pipeline and <see cref="CommandReceived"/> never fires —
/// the controls appear to work and do nothing.
/// </para>
/// </summary>
public sealed class WindowsPlaybackSession : IPlaybackSession, IDisposable
{
    private readonly global::Windows.Media.Playback.MediaPlayer _player;
    private readonly global::Windows.Media.SystemMediaTransportControls _controls;
    private readonly ILogger? _log;
    private readonly object _gate = new();
    private PlaybackCommands _supported;
    private TimeSpan _skipInterval = TimeSpan.FromSeconds(15);
    // Remembered from Publish so Report can build a complete timeline: SMTC splits what one PlaybackInfo
    // says across two calls, the duration belonging to the ITEM and the position to the moment.
    private TimeSpan? _duration;
    private bool _disposed;

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into a WinRT callback.</param>
    public WindowsPlaybackSession(ILogger? log = null)
    {
        _log = log;
        _player = new global::Windows.Media.Playback.MediaPlayer();
        _player.CommandManager.IsEnabled = false;   // see the remarks — load-bearing
        _controls = _player.SystemMediaTransportControls;
        _controls.IsEnabled = true;
        _controls.ButtonPressed += OnButtonPressed;
        _controls.PlaybackPositionChangeRequested += OnPositionChangeRequested;
    }

    /// <inheritdoc />
    public PlaybackCommands Supported
    {
        get { lock (_gate) return _supported; }
        set
        {
            lock (_gate) _supported = value;
            // Windows has no TogglePlayPause flag — it derives the single button from the two separate
            // ones — so a caller asking only for Toggle must still light both.
            var toggle = value.HasFlag(PlaybackCommands.TogglePlayPause);
            Try(() =>
            {
                _controls.IsPlayEnabled = toggle || value.HasFlag(PlaybackCommands.Play);
                _controls.IsPauseEnabled = toggle || value.HasFlag(PlaybackCommands.Pause);
                _controls.IsStopEnabled = value.HasFlag(PlaybackCommands.Stop);
                _controls.IsNextEnabled = value.HasFlag(PlaybackCommands.Next);
                _controls.IsPreviousEnabled = value.HasFlag(PlaybackCommands.Previous);
                // ⚠ SMTC has no skip-by-interval button, so FastForward/Rewind stand in — an approximation
                // (they are continuous-seek on some surfaces), which is why the request still carries the
                // interval the app configured.
                _controls.IsFastForwardEnabled = value.HasFlag(PlaybackCommands.SkipForward);
                _controls.IsRewindEnabled = value.HasFlag(PlaybackCommands.SkipBackward);
            }, nameof(Supported));
        }
    }

    /// <inheritdoc />
    public TimeSpan SkipInterval
    {
        get { lock (_gate) return _skipInterval; }
        // Nothing to push to SMTC: Windows has no preferred-interval concept, so this is only what the
        // request carries back.
        set { lock (_gate) _skipInterval = value; }
    }

    /// <inheritdoc />
    public event Action<PlaybackCommandRequest>? CommandReceived;

    /// <inheritdoc />
    public void Publish(PlaybackInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Before the Try, and unconditional: a track published WITHOUT a duration must clear the previous
        // one, or the OS keeps drawing the last item's length under the new item's position.
        lock (_gate) _duration = info.Duration;

        Try(() =>
        {
            var updater = _controls.DisplayUpdater;
            updater.Type = global::Windows.Media.MediaPlaybackType.Music;
            // MusicProperties whatever the content is: it is the only one of the three shapes with all of
            // title/artist/album, and `Video` has no second line, which would silently drop Subtitle.
            updater.MusicProperties.Title = info.Title ?? string.Empty;
            updater.MusicProperties.Artist = info.Subtitle ?? string.Empty;
            updater.MusicProperties.AlbumTitle = info.GroupName ?? string.Empty;
            // ⚠ Update() COMMITS the properties above; without it the session is visible to the OS with an
            // EMPTY title, which is a different symptom from having no session at all.
            updater.Update();
        }, nameof(Publish));

        // ⚠ Artwork LAST and off this thread: a thumbnail means an InMemoryRandomAccessStream whose only
        // write path is async, and blocking on it from a WinForms thread is the classic deadlock.
        if (!info.Artwork.IsEmpty) ApplyArtwork(info.Artwork);
    }

    /// <inheritdoc />
    public void Report(PlaybackProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Try(() =>
        {
            _controls.PlaybackStatus = progress.State switch
            {
                PlaybackState.Playing => SmtcStatus.Playing,
                PlaybackState.Paused => SmtcStatus.Paused,
                PlaybackState.Buffering => SmtcStatus.Changing,
                _ => SmtcStatus.Stopped,
            };

            TimeSpan? duration;
            lock (_gate) duration = _duration;
            var (position, end) = TimelineFor(progress.Position, duration);

            // The OS extrapolates from these, so they are a snapshot as of now — see PlaybackProgress.
            var timeline = new global::Windows.Media.SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position = position,
                // BOTH, not one: EndTime is what the flyout draws the scrubber against and MaxSeekTime
                // bounds a DRAG, so setting only EndTime renders a length the user cannot reach.
                EndTime = end,
                MaxSeekTime = end,
            };
            _controls.UpdateTimelineProperties(timeline);
        }, nameof(Report));
    }

    /// <summary>
    /// The timeline to hand SMTC, from the position the app reported and the duration
    /// <see cref="Publish"/> remembered. Pure and <c>internal</c> so it is unit-testable with no media
    /// pipeline. A null OR non-positive duration means UNKNOWN and leaves <c>EndTime</c> at zero (an item
    /// that ends at 0 renders a permanently-full scrubber, and a live stream has no end).
    /// <para>
    /// ⚠ <b>A position past the end is CLAMPED, not passed through.</b> SMTC wants
    /// <c>StartTime ≤ Position ≤ MaxSeekTime ≤ EndTime</c> and silently rejects the whole timeline
    /// otherwise, losing the duration too — and a position a tick past the end is ordinary at the moment a
    /// track finishes.
    /// </para>
    /// </summary>
    internal static (TimeSpan Position, TimeSpan End) TimelineFor(TimeSpan position, TimeSpan? duration)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (duration is not { } total || total <= TimeSpan.Zero) return (position, TimeSpan.Zero);
        return (position > total ? total : position, total);
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (_disposed) return;
        // The duration belongs to the item going away; left set, the next Publish that omits one inherits it.
        lock (_gate) _duration = null;
        Try(() =>
        {
            _controls.DisplayUpdater.ClearAll();
            _controls.DisplayUpdater.Update();
            // Closed, not Stopped: Stopped keeps the app in the flyout with nothing playing, which is what
            // PlaybackState.Stopped means. Clear() means take us off it.
            _controls.PlaybackStatus = SmtcStatus.Closed;
        }, nameof(Clear));
    }

    private void ApplyArtwork(ReadOnlyMemory<byte> artwork)
    {
        var bytes = artwork.ToArray();
        _ = Task.Run(async () =>
        {
            try
            {
                var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
                // CryptographicBuffer: `byte[].AsBuffer()` lived in System.Runtime.WindowsRuntime, which
                // does not exist after .NET 5.
                await stream.WriteAsync(
                    global::Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(bytes));
                stream.Seek(0);
                var updater = _controls.DisplayUpdater;
                updater.Thumbnail =
                    global::Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream);
                updater.Update();
            }
            catch (Exception ex)
            {
                // Artwork is decoration. A malformed image must never take the transport surface with it.
                Log(() => "[Shenora.Windows] Playback artwork rejected; metadata is still published.", ex);
            }
        });
    }

    private void OnButtonPressed(global::Windows.Media.SystemMediaTransportControls sender,
                                 global::Windows.Media.SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var command = args.Button switch
        {
            SmtcButton.Play => PlaybackCommand.Play,
            SmtcButton.Pause => PlaybackCommand.Pause,
            SmtcButton.Stop => PlaybackCommand.Stop,
            SmtcButton.Next => PlaybackCommand.Next,
            SmtcButton.Previous => PlaybackCommand.Previous,
            SmtcButton.FastForward => PlaybackCommand.SkipForward,
            SmtcButton.Rewind => PlaybackCommand.SkipBackward,
            _ => (PlaybackCommand?)null,
        };
        // An unmapped button (record, channel up…) is dropped: the app declared what it supports, and
        // inventing a mapping would fire a command it never offered.
        if (command is not { } mapped) return;
        var interval = mapped is PlaybackCommand.SkipForward or PlaybackCommand.SkipBackward
            ? SkipInterval
            : (TimeSpan?)null;
        Raise(new PlaybackCommandRequest { Command = mapped, Interval = interval });
    }

    private void OnPositionChangeRequested(
        global::Windows.Media.SystemMediaTransportControls sender,
        global::Windows.Media.PlaybackPositionChangeRequestedEventArgs args)
    {
        if (!Supported.HasFlag(PlaybackCommands.Seek)) return;
        Raise(new PlaybackCommandRequest { Command = PlaybackCommand.Seek, Position = args.RequestedPlaybackPosition });
    }

    /// <summary>
    /// Hand a command to the app through the ONE guard. ⚠ This runs on a WinRT callback thread, NOT the
    /// UI thread, so an escaping exception has no catcher and would take the process down.
    /// </summary>
    private void Raise(PlaybackCommandRequest request)
    {
        var handler = CommandReceived;
        if (handler is null) return;
        AppCallback.Run(() => handler(request),
            ex => Log(() => $"[Shenora.Windows] A {request.Command} handler threw " +
                            $"({ex.GetType().Name}: {ex.Message})."));
    }

    /// <summary>
    /// Every SMTC call goes through here: these are cross-process COM calls to a system service that can be
    /// restarting or gone, and a media-key press against a torn-down session must not crash the app.
    /// </summary>
    private void Try(Action action, string what)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log(() => $"[Shenora.Windows] SystemMediaTransportControls.{what} failed " +
                      $"({ex.GetType().Name}: {ex.Message}).");
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
            _controls.ButtonPressed -= OnButtonPressed;
            _controls.PlaybackPositionChangeRequested -= OnPositionChangeRequested;
            _controls.IsEnabled = false;
        }, nameof(Dispose));
        // The player is ours; leaving it keeps the app in the flyout for the rest of the process.
        try { _player.Dispose(); } catch (Exception ex) { Log(() => "[Shenora.Windows] Player dispose", ex); }
    }
}
#endif
