using Shenora.Modules.Media;

namespace Shenora.Sample.Maui;

/// <summary>
/// Does the HOST-OWNED player actually play? (D54.)
/// <para>
/// ⚠ <b>Nothing else can answer this.</b> There is no managed player — every implementation is a shell
/// talking to a platform pipeline (AVFoundation on iOS, <c>android.media.MediaPlayer</c> on Android) — so
/// the unit tests pin the CONTRACT and cannot decode a byte.
/// </para>
/// <para>
/// <b>Note how little there is here, and that it is the POINT.</b> Playing a file the device can already
/// decode is four calls; the probe → plan → convert machinery exists only for the gap D59 describes, and
/// stays out of the way when there is none.
/// </para>
/// </summary>
public static class MediaPlayerProbe
{
    /// <summary>
    /// Open the staged clip, play it, and report whether the position MOVED.
    /// <para>
    /// The position is the assertion, deliberately: <c>PlayAsync</c> completing proves a message reached
    /// the platform and nothing more — the same mistake this sample's <c>MEDIA: PASS</c> made when it
    /// asserted bytes rather than pixels.
    /// </para>
    /// </summary>
    /// <param name="player">The shell's player, or null where none ships (Android, Windows — by design).</param>
    /// <param name="log">Sink.</param>
    public static async Task RunAsync(IMediaPlayer? player, Action<string> log)
    {
        if (player is null)
        {
            log("PLAYER: absent on this platform (by design — the page keeps using <video>)");
            return;
        }

        var clip = Path.Combine(FileSystem.CacheDirectory, "media", "clip-faststart.mp4");
        if (!File.Exists(clip))
        {
            log("PLAYER: FAIL — clip not staged; MediaRangeProbe.PrepareAsync runs first");
            return;
        }

        try
        {
            await player.OpenAsync(new MediaSource { Uri = clip });
            await player.PlayAsync();

            await Task.Delay(1500);
            var first = player.Status.Position;
            await Task.Delay(1500);
            var second = player.Status.Position;

            log($"PLAYER: {first.TotalSeconds:F2}s -> {second.TotalSeconds:F2}s state={player.Status.State}");
            log(second > first
                ? "PLAYER: PASS — the host decoded a real file and advanced a real clock"
                : "PLAYER: FAIL — the clock did not move");

            // ⚠ NOT proven here, and the reason the native player exists: that playback SURVIVES
            // BACKGROUNDING. Nothing in this harness can make the app leave the foreground — press home
            // while it is playing and watch the log.
            // 🔴 BACKGROUND SURVIVAL IS MEASURED — 2026-08-12, Android API 36, and it is the reason this
            // player exists. Sampling position every 5 s with the app hidden (HOME pressed right after the
            // first sample, `document.visibilityState` confirmed `hidden`):
            //
            //     [BGPLAY] sample 1/10  t=7.92s  state=Playing     <- foreground
            //     [BGPLAY] sample 2/10  t=12.92s state=Playing     <- hidden from here
            //     …
            //     [BGPLAY] sample 10/10 t=52.95s state=Playing     <- 45 s later, 1:1 with wall clock
            //
            // The page's own `<audio>`, already playing, dies after ~15.3 s on the same device with the same
            // HOME press (measured twice). **So the native player outlives the webview by 3x**, with no
            // foreground service and no MediaSession notification.
            //
            // ⚠ PROVEN FOR 45 s, NOT FOR MINUTES — the staged clip is 60 s, so a longer window needs a
            // longer file, and Android's freezer/Doze can still arrive later. A real handset's vendor power
            // management is more aggressive than an emulator's. For genuinely long playback the app posts a
            // FOREGROUND SERVICE; the kit owns the MediaSession (`IPlaybackSession`) and the app owns the
            // notification, which is already the documented split.
            //
            // ⚠ NOT re-run on every launch, deliberately: it held the audio session for 50 s and the page's
            // own media probes have to run after it. Reproduce with the loop in git history — the recipe is
            // `android run`, poll the log for the first sample, `input keyevent KEYCODE_HOME`.
            log("PLAYER: background survival MEASURED at 45 s on Android (see the remarks above); "
                + "press home while it plays to watch it yourself");
        }
        catch (Exception ex)
        {
            log($"PLAYER: FAIL — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { await player.CloseAsync(); } catch { /* teardown must not mask the result above */ }
        }

        await WhileOpeningAsync(player, clip, log);
    }

    /// <summary>
    /// A seek or a play sent while the source is still OPENING must take effect once it is ready — a page that
    /// resumes or autoplays a video sends them with the load, not after it. Each also reads the status while
    /// opening, which on Android used to fail the open.
    /// <para>
    /// ⚠ <b>The window is short for a local file</b>, which can be ready before the command is sent; the
    /// probe says INCONCLUSIVE then rather than passing a case it never reached.
    /// </para>
    /// </summary>
    private static async Task WhileOpeningAsync(IMediaPlayer player, string clip, Action<string> log)
    {
        var target = TimeSpan.FromSeconds(20);
        try
        {
            // Not awaited: the seek has to go out while the open is in flight.
            var opening = player.OpenAsync(new MediaSource { Uri = clip });
            var stateAtSeek = player.Status.State;
            var seeking = player.SeekAsync(target);
            await opening;
            var landed = await Task.WhenAny(seeking, Task.Delay(TimeSpan.FromSeconds(5))) == seeking;
            var at = player.Status.Position;

            log($"SEEK-EARLY: sent while {stateAtSeek}; landed={landed} position={at.TotalSeconds:F2}s "
                + $"(asked {target.TotalSeconds:F0}s)");
            log(stateAtSeek != MediaPlayerState.Opening
                ? "SEEK-EARLY: INCONCLUSIVE — the source was ready before the seek went out"
                : landed && Math.Abs((at - target).TotalSeconds) < 1
                    ? "SEEK-EARLY: PASS — a seek sent during the open landed where it asked"
                    : "SEEK-EARLY: FAIL — a seek sent during the open did not land");
        }
        catch (Exception ex)
        {
            log($"SEEK-EARLY: FAIL — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { await player.CloseAsync(); } catch { /* teardown must not mask the result above */ }
        }

        // The same window for PLAY: a page that autoplays sends it with the load.
        try
        {
            var opening = player.OpenAsync(new MediaSource { Uri = clip });
            var stateAtPlay = player.Status.State;
            await player.PlayAsync();
            await opening;
            await Task.Delay(1500);
            var first = player.Status.Position;
            await Task.Delay(1500);
            var second = player.Status.Position;

            log($"PLAY-EARLY: sent while {stateAtPlay}; {first.TotalSeconds:F2}s -> {second.TotalSeconds:F2}s "
                + $"state={player.Status.State}");
            log(stateAtPlay != MediaPlayerState.Opening
                ? "PLAY-EARLY: INCONCLUSIVE — the source was ready before the play went out"
                : second > first && player.Status.State == MediaPlayerState.Playing
                    ? "PLAY-EARLY: PASS — a play sent during the open started the player once it opened"
                    : "PLAY-EARLY: FAIL — a play sent during the open did not start the player");
        }
        catch (Exception ex)
        {
            log($"PLAY-EARLY: FAIL — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { await player.CloseAsync(); } catch { /* teardown must not mask the result above */ }
        }
    }
}
