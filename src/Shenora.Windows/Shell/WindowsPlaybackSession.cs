// SystemMediaTransportControls is WinRT, and the WinRT projections only exist when the target framework
// names a Windows SDK version — with a bare `net10.0-windows`, `Windows.Media` is not a namespace at all
// (measured: CS0234). So this package MULTI-TARGETS and this whole file is the versioned half; the plain
// half is WindowsPlaybackSession.Unsupported.cs, which refuses by name.
//
// Guarding the FILE rather than each body: the alternative was a dozen #if blocks inside one class, which
// is harder to read and easy to get subtly wrong. The cost is that the public shape is written twice, so
// the plain TFM is gated by its own metadata baseline — see MetadataSurfaceTests.
#if WINDOWS10_0_17763_0_OR_GREATER
using Shenora;
// `global::` on every WinRT namespace below, and it is not optional: inside `namespace Shenora.Windows`
// the bare identifier `Windows` binds to THIS namespace, so `Windows.Media` resolves to
// `Shenora.Windows.Windows.Media` and fails with a confusing CS0234 about `Shenora.Windows`. Same trap
// the WebView2 control alias already documents.
using SmtcButton = global::Windows.Media.SystemMediaTransportControlsButton;
using SmtcStatus = global::Windows.Media.MediaPlaybackStatus;

namespace Shenora.Windows;

/// <summary>
/// The desktop shell's <see cref="IPlaybackSession"/> — Windows' own media flyout, the one the volume
/// keys and the lock screen drive, via <c>SystemMediaTransportControls</c>.
/// <para>
/// <b>Obtained through a <c>MediaPlayer</c>, and that is a deliberate choice between three bad options.</b>
/// <c>SystemMediaTransportControls.GetForCurrentView()</c> needs a <c>CoreWindow</c>, which a Win32 app
/// does not have. The documented route for a windowed app is
/// <c>ISystemMediaTransportControlsInterop.GetForWindow(hwnd)</c> — but .NET 5+ removed the built-in WinRT
/// marshalling, so a <c>ComImport</c> interface returning a projected WinRT type no longer marshals for
/// free, and hand-rolling it means <c>RoGetActivationFactory</c>, HSTRINGs and manual reference counting.
/// A <c>MediaPlayer</c> exposes an SMTC with no window and no interop at all, which is why this is the
/// widely-used route. The cost is one idle media pipeline; the alternative was inventing exactly the kind
/// of interop this kit prefers to extract rather than write.
/// </para>
/// <para>
/// <c>CommandManager.IsEnabled = false</c> is load-bearing: left on, the <c>MediaPlayer</c> handles the
/// transport buttons ITSELF against the empty pipeline it owns, and the app's
/// <see cref="CommandReceived"/> never fires — the controls appear to work and do nothing.
/// </para>
/// </summary>
public sealed class WindowsPlaybackSession : IPlaybackSession, IDisposable
{
    private readonly global::Windows.Media.Playback.MediaPlayer _player;
    private readonly global::Windows.Media.SystemMediaTransportControls _controls;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private PlaybackCommands _supported;
    private TimeSpan _skipInterval = TimeSpan.FromSeconds(15);
    // Remembered from Publish so Report can build a complete timeline. SMTC splits what one PlaybackInfo
    // says across two calls — the duration belongs to the ITEM and the position to the moment — and nothing
    // carried it between them, so EndTime could only ever be zero (found by the first adopter, 2026-08-05).
    private TimeSpan? _duration;
    private bool _disposed;

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into a WinRT callback.</param>
    public WindowsPlaybackSession(Action<string>? log = null)
    {
        _log = log;
        _player = new global::Windows.Media.Playback.MediaPlayer();
        // See the remarks: without this the player answers the buttons instead of the app.
        _player.CommandManager.IsEnabled = false;
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
            // Windows has no TogglePlayPause flag: the OS derives the single play/pause button from the
            // two separate ones, so a caller asking only for Toggle must still light both or the flyout
            // shows nothing pressable.
            var toggle = value.HasFlag(PlaybackCommands.TogglePlayPause);
            Try(() =>
            {
                _controls.IsPlayEnabled = toggle || value.HasFlag(PlaybackCommands.Play);
                _controls.IsPauseEnabled = toggle || value.HasFlag(PlaybackCommands.Pause);
                _controls.IsStopEnabled = value.HasFlag(PlaybackCommands.Stop);
                _controls.IsNextEnabled = value.HasFlag(PlaybackCommands.Next);
                _controls.IsPreviousEnabled = value.HasFlag(PlaybackCommands.Previous);
                // ⚠ SMTC has no skip-by-interval button. FastForward/Rewind are the closest thing it
                // offers and are CONTINUOUS-seek semantics on some surfaces, so this is an honest
                // approximation rather than an exact match — documented on IPlaybackSession, and the
                // reason the request still carries the interval the app configured.
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
        // request carries back. The property still exists on every shell because the CONTRACT is portable —
        // an app sets it once and the platforms that can render it, do.
        set { lock (_gate) _skipInterval = value; }
    }

    /// <inheritdoc />
    public event Action<PlaybackCommandRequest>? CommandReceived;

    /// <inheritdoc />
    public void Publish(PlaybackInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Before the Try, and assigned unconditionally: a track published WITHOUT a duration must clear the
        // previous one, or the OS keeps drawing the last item's length under the new item's position.
        lock (_gate) _duration = info.Duration;

        Try(() =>
        {
            var updater = _controls.DisplayUpdater;
            updater.Type = global::Windows.Media.MediaPlaybackType.Music;
            // MusicProperties is the only one of the three shapes with all of title/artist/album, so the
            // generic contract maps onto it regardless of what the content actually is. `Video` has no
            // second line at all, which would silently drop Subtitle.
            updater.MusicProperties.Title = info.Title ?? string.Empty;
            updater.MusicProperties.Artist = info.Subtitle ?? string.Empty;
            updater.MusicProperties.AlbumTitle = info.GroupName ?? string.Empty;
            // ⚠ Update() COMMITS the properties above; without it every field is set and nothing is
            // published. Sabotage-verified: removing it leaves our session visible to the OS with an
            // EMPTY title, which is a different symptom from having no session at all.
            updater.Update();
        }, nameof(Publish));

        // Artwork LAST and off this thread. Building a thumbnail means an InMemoryRandomAccessStream,
        // whose only write path is async — and blocking on it from a WinForms thread (which has a
        // SynchronizationContext) is the classic deadlock. So the text lands immediately and the image
        // follows, which is also how every real player behaves.
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
                // BOTH, not one: EndTime is what the flyout draws the scrubber against and MaxSeekTime is
                // what bounds a DRAG, so setting only EndTime renders a length the user is not allowed to
                // reach. They stay zero when the duration is unknown — see TimelineFor.
                EndTime = end,
                MaxSeekTime = end,
            };
            _controls.UpdateTimelineProperties(timeline);
        }, nameof(Report));
    }

    /// <summary>
    /// The timeline to hand SMTC, from the position the app reported and the duration
    /// <see cref="Publish"/> remembered.
    /// <para>
    /// Pure and <c>internal</c> so it is unit-testable with no media pipeline. That matters here more than
    /// usual: the defect this exists for shipped because the only evidence was "the call did not throw", and
    /// a timeline is the kind of thing that comes out silently wrong rather than loudly broken.
    /// </para>
    /// <para>
    /// ⚠ <b>A position past the end is CLAMPED, not passed through.</b> SMTC wants
    /// <c>StartTime ≤ Position ≤ MaxSeekTime ≤ EndTime</c> and rejects the whole timeline otherwise — which
    /// would lose the duration as well as the position. An app reporting a position a tick past the end is
    /// ordinary at the moment a track finishes, so the incoherent input must not cost the coherent field.
    /// </para>
    /// <para>
    /// A null OR non-positive duration both mean UNKNOWN and both leave <c>EndTime</c> at zero. Telling the
    /// OS an item ends at 0 renders a permanently-full scrubber, and a live stream legitimately has no end.
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
        // The duration belongs to the item that is going away. Left set, the next Publish that omits one
        // would inherit it — the same staleness the assignment in Publish prevents, from the other side.
        lock (_gate) _duration = null;
        Try(() =>
        {
            _controls.DisplayUpdater.ClearAll();
            _controls.DisplayUpdater.Update();
            // Closed, not Stopped: Stopped keeps the app in the flyout with nothing playing, which is
            // what PlaybackState.Stopped means. Clear() means take us off it.
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
                // CryptographicBuffer, because `byte[].AsBuffer()` lived in System.Runtime.WindowsRuntime
                // and that assembly does not exist after .NET 5. This is the projection-only equivalent.
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
                Log(() => $"[Shenora.Windows] Playback artwork rejected ({ex.GetType().Name}: {ex.Message}); " +
                          "metadata is still published.");
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
        // An unmapped button (record, channel up, fast-forward…) is dropped rather than guessed at. The
        // app declared what it supports; inventing a mapping here would fire a command it never offered.
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
    /// Hand a command to the app through the ONE guard. ⚠ This runs on a WinRT callback thread — NOT the
    /// UI thread — so an escaping exception has no catcher and would take the process down;
    /// <see cref="AppCallback"/> is what stops that, and the contract tells the app to marshal.
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
    /// Every SMTC call goes through here. These are cross-process COM calls to a system service that can
    /// be restarting or gone, and each one throws on its own — a media-key press against a torn-down
    /// session must not become an app crash.
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

    private void Log(Func<string> message) => AppCallback.Log(_log, message);

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
        // The player is ours and holds a media pipeline; leaving it would keep the app in the flyout for
        // the rest of the process.
        try { _player.Dispose(); } catch (Exception ex) { Log(() => $"[Shenora.Windows] Player dispose: {ex.Message}"); }
    }
}
#endif
