using Shenora.Modules.Media;

namespace Shenora.Sample.Maui;

/// <summary>
/// Does the HOST-OWNED player actually play? (D54.)
/// <para>
/// ⚠ <b>Nothing else can answer this.</b> There is no managed player — every implementation is a shell
/// talking to AVFoundation — so the unit tests pin the CONTRACT and cannot decode a byte.
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
    /// AVFoundation and nothing more — the same mistake this sample's <c>MEDIA: PASS</c> made when it
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
            log("PLAYER: background survival UNPROVEN by this probe — background the app manually");
        }
        catch (Exception ex)
        {
            log($"PLAYER: FAIL — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { await player.CloseAsync(); } catch { /* teardown must not mask the result above */ }
        }
    }
}
