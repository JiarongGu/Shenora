using Shenora.Modules.Media;

using Android.Media;
using Shenora;

// `MediaPlayer` is ambiguous in this file — Shenora.Modules.Media has the page-backed one — and the
// platform type cannot be called `AndroidMediaPlayer` either, because that is now THIS class. `Platform*`
// is the alias vocabulary for "the OS's own version of the thing this file implements".
using PlatformPlayer = Android.Media.MediaPlayer;

namespace Shenora.Android;

/// <summary>
/// Android's <see cref="IMediaPlayer"/> — the platform's own <c>android.media.MediaPlayer</c>. The state
/// machine is <see cref="MediaPlayerBase"/>'s; this is the platform half.
/// <para>
/// 🔴 <b>Why the platform player and NOT ExoPlayer</b>, which is the obvious suggestion. D51 says the kit
/// ships no codec and no engine, ever — every byte of decoding is the platform's — and ExoPlayer is an
/// ENGINE: a third-party library with its own extractors, its own renderers and a transitive AndroidX
/// graph, landing in every consumer of <c>Shenora.Android</c> whether or not they play anything.
/// <c>android.media.MediaPlayer</c> is already in the SDK, decodes through the same <c>MediaCodec</c> layer
/// <see cref="AndroidMediaCapability"/> reports on, and covers this contract completely.
/// </para>
/// <para>
/// <b>What that costs, stated plainly:</b> adaptive streaming (DASH, smooth HLS switching) and some
/// container edge cases are where ExoPlayer is genuinely better. The contract promises neither. And an app
/// that needs it is no longer stuck — <see cref="MediaPlayerBase"/> makes bringing your own engine a
/// derived class with about forty lines in it, which is a better answer than the kit picking a heavy
/// dependency for everyone.
/// </para>
/// <para>
/// ⚠ <b>Background playback additionally needs the APP's own foreground service and notification</b>, the
/// same division <see cref="AndroidPlaybackSession"/> draws: Android kills a backgrounded process's audio
/// without one. The kit plays; the app decides whether it is allowed to keep playing.
/// </para>
/// </summary>
public sealed class AndroidMediaPlayer : MediaPlayerBase
{
    /// <summary>
    /// <c>PlaybackParams</c> — the only way to set a speed — arrived in Android 6.0. The floor here is 21,
    /// and CA1416 is a build error in this repo, so the guard is required rather than defensive.
    /// </summary>
    private const int PlaybackParamsApi = 23;

    private PlatformPlayer? _player;
    private TaskCompletionSource? _seeking;

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape into a platform callback.</param>
    public AndroidMediaPlayer(Action<string>? log = null)
        : base(log is null ? null : message => log($"[Shenora.Android] {message}"))
    {
    }

    /// <inheritdoc />
    protected override TimeSpan PositionCore =>
        _player is { } player ? TimeSpan.FromMilliseconds(player.CurrentPosition) : TimeSpan.Zero;

    /// <summary>
    /// <inheritdoc />
    /// <para>
    /// ⚠ Android reports <b>-1</b> for a stream whose length it cannot know, and 0 before prepare — neither
    /// is a duration, and both mean the contract's <c>null</c>. Returning the raw value would give a UI a
    /// scrubber that is either negative or pinned at the end.
    /// </para>
    /// </summary>
    protected override TimeSpan? DurationCore =>
        _player is { Duration: > 0 } player ? TimeSpan.FromMilliseconds(player.Duration) : null;

    /// <inheritdoc />
    protected override void OpenCore(MediaSource source, Uri uri)
    {
        var player = new PlatformPlayer();
        _player = player;

        // Music rather than a notification blip — this is what makes ducking, the volume rocker's choice of
        // stream and mute-on-call behave the way a user expects of a player. Must be set BEFORE prepare.
        player.SetAudioAttributes(new AudioAttributes.Builder()
            .SetContentType(AudioContentType.Music)!
            .SetUsage(AudioUsageKind.Media)!
            .Build());

        player.Prepared += OnPrepared;
        player.Completion += OnCompletion;
        player.Error += OnError;
        player.Info += OnInfo;
        player.SeekComplete += OnSeekComplete;

        // The ORIGINAL string for a network URL rather than the parsed Uri round-tripped back to text:
        // System.Uri normalizes escaping, and a signed URL whose signature covers its own encoding stops
        // matching. A file: URI becomes a plain path, which is what SetDataSource wants.
        player.SetDataSource(uri.IsFile ? uri.LocalPath : source.Uri);

        // ASYNC prepare, never Prepare(): the synchronous one blocks the calling thread until the source is
        // parsed, which for a network source is however long the network takes. Readiness arrives on
        // Prepared, which is what the base is waiting for.
        player.PrepareAsync();
    }

    /// <inheritdoc />
    protected override void ApplyStartAt(TimeSpan position) => _player?.SeekTo((int)position.TotalMilliseconds);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ The speed goes on BEFORE <c>Start()</c> and never while paused — on this platform assigning
    /// <c>PlaybackParams</c> to a paused player STARTS it, which is the same trap AVFoundation has with
    /// <c>Rate</c> and the reason <see cref="MediaPlayerBase"/> defers a rate set while paused.
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
    /// <remarks>
    /// Genuinely asynchronous: Android calls back on <c>SeekComplete</c> when the position lands, so
    /// <c>SeekAsync</c> completes then rather than on the request being accepted.
    /// </remarks>
    protected override Task SeekCore(TimeSpan position)
    {
        if (_player is not { } player) return Task.CompletedTask;

        // A seek issued while one is outstanding SUPERSEDES it — Android coalesces, so the earlier callback
        // may never arrive and its caller would await forever. Settle the old one rather than orphan it.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _seeking, completion)?.TrySetResult();

        // The int overload: SeekTo(long, SeekMode) is API 26 and this floor is 21. Milliseconds are the
        // platform's unit throughout.
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
    /// <c>Release()</c> and not just <c>Reset()</c>: a MediaPlayer holds a decoder and an audio track, both
    /// scarce on a device, and a reset-but-unreleased instance keeps them. Handlers come off first — they
    /// are attached to THIS instance, and a callback arriving mid-release would touch a dead object.
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

        // The platform's own codes are LOGGED and never thrown: this string can reach a page, and the same
        // rule the IPC stack applies to every error path applies here. `What` is the coarse reason and
        // `Extra` the vendor detail — together they are what makes a support report actionable.
        Log(() => $"MediaPlayer open failed: what={e.What} extra={e.Extra}");
        OnFailed("The media source could not be played.");
    }

    /// <summary>
    /// Buffering, which Android reports through the general-purpose Info callback rather than its own
    /// event. Start and end are separate codes; watching only the first gives a player that enters
    /// Buffering and never leaves.
    /// </summary>
    private void OnInfo(object? sender, PlatformPlayer.InfoEventArgs e)
    {
        // Conditional on what we are already doing: the platform reports these whether or not anything is
        // playing, and forwarding unconditionally would strand a PAUSED player in Buffering.
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
    /// Set the playback speed where the platform can. Below API 23 there is no mechanism at all, so the
    /// request is logged and dropped — which the contract already allows: <see cref="IMediaPlayer.Rate"/>
    /// reports what was ASKED FOR precisely because a platform may not honour it.
    /// </summary>
    private void ApplySpeed(PlatformPlayer player, double rate)
    {
        // ⚠ The LITERAL, not `PlaybackParamsApi`. CA1416 does not follow the guard through a named constant
        // — measured here, four errors on an ostensibly guarded call — and it is a build error in this repo.
        // The constant stays because it documents the floor; the analyser needs to see the number.
        if (!OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            // Only worth a line when the app actually asked for something other than normal speed.
            if (Math.Abs(rate - 1.0) > 0.001)
                Log(() => $"MediaPlayer rate {rate:F2}x ignored: PlaybackParams needs API {PlaybackParamsApi}.");
            return;
        }

        SetSpeed(player, (float)rate);
    }

    /// <summary>
    /// The API-23 half, split out and ATTRIBUTED so the platform guard above is one the analyser can verify
    /// rather than one a reader has to trust.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("android23.0")]
    private static void SetSpeed(PlatformPlayer player, float rate) =>
        // A FRESH PlaybackParams rather than mutating the player's own: the getter throws IllegalState on a
        // player that has never had one set, which is every player before its first speed change.
        player.PlaybackParams = new PlaybackParams().SetSpeed(rate)!;
}
