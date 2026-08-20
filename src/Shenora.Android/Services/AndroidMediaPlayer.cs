using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;

using Android.Media;
using Shenora;

// `MediaPlayer` is ambiguous here: Shenora.Modules.Media has the page-backed one.
using PlatformPlayer = Android.Media.MediaPlayer;

namespace Shenora.Android;

/// <summary>
/// Android's <see cref="IMediaPlayer"/> — the platform's own <c>android.media.MediaPlayer</c> and not
/// ExoPlayer, because D51 forbids shipping an engine. The state machine is
/// <see cref="MediaPlayerBase"/>'s; this is the platform half.
/// <para>
/// The contract promises no adaptive streaming (DASH, smooth HLS switching), which is where ExoPlayer is
/// genuinely better; an app that needs it derives its own <see cref="MediaPlayerBase"/>.
/// </para>
/// <para>
/// ⚠ <b>Background playback additionally needs the APP's own foreground service and notification</b> —
/// Android kills a backgrounded process's audio without one. Same division as
/// <see cref="AndroidPlaybackSession"/>: the kit plays, the app decides whether it may keep playing.
/// </para>
/// </summary>
public sealed class AndroidMediaPlayer : MediaPlayerBase
{
    /// <summary><c>PlaybackParams</c> — the only way to set a speed — arrived in Android 6.0; the floor is 21.</summary>
    private const int PlaybackParamsApi = 23;

    private PlatformPlayer? _player;
    private TaskCompletionSource? _seeking;

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into a platform callback.</param>
    public AndroidMediaPlayer(ILogger? log = null)
        : base(log)
    {
    }

    /// <inheritdoc />
    protected override TimeSpan PositionCore =>
        _player is { } player ? TimeSpan.FromMilliseconds(player.CurrentPosition) : TimeSpan.Zero;

    /// <summary>
    /// <inheritdoc />
    /// ⚠ Android reports <b>-1</b> for a stream whose length it cannot know and 0 before prepare; neither
    /// is a duration, and both mean the contract's <c>null</c>.
    /// </summary>
    protected override TimeSpan? DurationCore =>
        _player is { Duration: > 0 } player ? TimeSpan.FromMilliseconds(player.Duration) : null;

    /// <inheritdoc />
    protected override void OpenCore(MediaSource source, Uri uri)
    {
        var player = new PlatformPlayer();
        _player = player;

        // Music, not a notification blip: this drives ducking, the volume rocker's choice of stream and
        // mute-on-call. Must be set BEFORE prepare.
        player.SetAudioAttributes(new AudioAttributes.Builder()
            .SetContentType(AudioContentType.Music)!
            .SetUsage(AudioUsageKind.Media)!
            .Build());

        player.Prepared += OnPrepared;
        player.Completion += OnCompletion;
        player.Error += OnError;
        player.Info += OnInfo;
        player.SeekComplete += OnSeekComplete;

        // The ORIGINAL string for a network URL: System.Uri normalizes escaping, and a signed URL whose
        // signature covers its own encoding stops matching. A file: URI becomes the plain path it wants.
        player.SetDataSource(uri.IsFile ? uri.LocalPath : source.Uri);

        // ASYNC prepare, never Prepare(): the synchronous one blocks the caller for however long the
        // network takes. Readiness arrives on Prepared, which is what the base is waiting for.
        player.PrepareAsync();
    }

    /// <inheritdoc />
    protected override void ApplyStartAt(TimeSpan position) => _player?.SeekTo((int)position.TotalMilliseconds);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ The speed goes on BEFORE <c>Start()</c> and never while paused — assigning <c>PlaybackParams</c>
    /// to a paused player STARTS it, which is why <see cref="MediaPlayerBase"/> defers a rate set.
    /// </remarks>
    protected override void PlayCore(double rate)
    {
        if (_player is not { } player) return;
        ApplySpeed(player, rate);
        player.Start();
    }

    /// <inheritdoc />
    protected override void PauseCore() => _player?.Pause();

    /// <inheritdoc />
    /// <remarks>Completes on the platform's <c>SeekComplete</c>, not on the request being accepted.</remarks>
    protected override Task SeekCore(TimeSpan position)
    {
        if (_player is not { } player) return Task.CompletedTask;

        // A seek issued while one is outstanding SUPERSEDES it — Android coalesces, so the earlier callback
        // may never arrive and its caller would await forever. Settle the old one rather than orphan it.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _seeking, completion)?.TrySetResult();

        // The int overload: SeekTo(long, SeekMode) is API 26 and this floor is 21.
        player.SeekTo((int)position.TotalMilliseconds);
        return completion.Task;
    }

    /// <inheritdoc />
    protected override void ApplyRateCore(double rate)
    {
        if (_player is { } player) ApplySpeed(player, rate);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>Release()</c> and not just <c>Reset()</c>: a MediaPlayer holds a decoder and an audio track,
    /// both scarce. Handlers come off first — a callback arriving mid-release touches a dead object.
    /// </remarks>
    protected override void TeardownCore()
    {
        if (_player is not { } player) return;
        _player = null;

        player.Prepared -= OnPrepared;
        player.Completion -= OnCompletion;
        player.Error -= OnError;
        player.Info -= OnInfo;
        player.SeekComplete -= OnSeekComplete;

        // An in-flight seek must not outlive the source it was seeking in.
        Interlocked.Exchange(ref _seeking, null)?.TrySetResult();

        player.Reset();
        player.Release();
        player.Dispose();
    }

    private void OnPrepared(object? sender, EventArgs e) => OnOpened();

    private void OnCompletion(object? sender, EventArgs e) => OnEnded();

    private void OnSeekComplete(object? sender, EventArgs e) =>
        Interlocked.Exchange(ref _seeking, null)?.TrySetResult();

    private void OnError(object? sender, PlatformPlayer.ErrorEventArgs e)
    {
        // Handled = true stops the platform ALSO firing Completion for the same failure, which would
        // otherwise report a failed source as one that played to its end.
        e.Handled = true;

        // LOGGED, never thrown: this string can reach a page. `What` is the coarse reason, `Extra` the
        // vendor detail.
        Log(() => $"MediaPlayer open failed: what={e.What} extra={e.Extra}");
        OnFailed("The media source could not be played.");
    }

    /// <summary>
    /// Buffering, which Android reports through the general-purpose Info callback. ⚠ Start and end are
    /// separate codes; watching only the first gives a player that enters Buffering and never leaves.
    /// </summary>
    private void OnInfo(object? sender, PlatformPlayer.InfoEventArgs e)
    {
        // The platform reports these whether or not anything is playing — forwarding unconditionally
        // would strand a PAUSED player in Buffering.
        switch (e.What)
        {
            case MediaInfo.BufferingStart when State == MediaPlayerState.Playing:
                OnPlatformState(MediaPlayerState.Buffering);
                break;
            case MediaInfo.BufferingEnd when State == MediaPlayerState.Buffering:
                OnPlatformState(MediaPlayerState.Playing);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Set the playback speed where the platform can. Below API 23 there is no mechanism, so the request
    /// is logged and dropped — <see cref="IMediaPlayer.SetRateAsync"/> reports what was ASKED FOR.
    /// </summary>
    private void ApplySpeed(PlatformPlayer player, double rate)
    {
        // ⚠ The LITERAL, not `PlaybackParamsApi`: CA1416 does not follow the guard through a named
        // constant (measured — four errors on a guarded call) and it is a build error in this repo.
        if (!OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            // Only worth a line when the app actually asked for something other than normal speed.
            if (Math.Abs(rate - 1.0) > 0.001)
                Log(() => $"MediaPlayer rate {rate:F2}x ignored: PlaybackParams needs API {PlaybackParamsApi}.");
            return;
        }

        SetSpeed(player, (float)rate);
    }

    /// <summary>The API-23 half, split out and ATTRIBUTED so the analyser can verify the guard above.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("android23.0")]
    private static void SetSpeed(PlatformPlayer player, float rate) =>
        // A FRESH PlaybackParams rather than mutating the player's own: the getter throws IllegalState on a
        // player that has never had one set, which is every player before its first speed change.
        player.PlaybackParams = new PlaybackParams().SetSpeed(rate)!;
}
