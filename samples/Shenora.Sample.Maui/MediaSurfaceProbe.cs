using Shenora.Modules.Media;

namespace Shenora.Sample.Maui;

/// <summary>
/// Does the shell's own player open what the WEBVIEW will not? (D80.)
///
/// <para>
/// 🔴 <b>This is the one question the adopter's device run cannot answer for the kit.</b> They drive
/// ExoPlayer; the kit's default is <c>android.media.MediaPlayer</c> (D51/D42 — no engine ships). Both sit
/// on <c>MediaCodec</c>, so the DECODERS are identical and the whole difference is the EXTRACTOR set —
/// which is exactly the delta D80 exists for: D52's <c>.mkv</c> holding perfectly playable H.264.
/// </para>
///
/// <para>
/// ⚠ <b>It plays a DIFFERENT clip from <see cref="MediaPlayerProbe"/></b> — an <c>.mkv</c> rather than an
/// <c>.mp4</c> — so that the two together say something about CONTAINER reach rather than only that a
/// player runs.
/// </para>
///
/// <para>
/// 🔴 <b>IT DOES NOT ASSERT THAT THE WEBVIEW REFUSES THE FILE, because on Android it does not.</b> Measured
/// 2026-09-04 on API 36 / WebView 133.0.6943.137: the page's own <c>&lt;video&gt;</c> element loaded the
/// same MKV (<c>dur=60.023</c>, matching the host exactly) and PLAYED it (<c>t=1.81</c>,
/// <c>readyState=4</c>). ⚠ <c>canPlayType('video/x-matroska; …')</c> answered <c>""</c> on that same
/// webview, which is the trap: the advisory answer says no and the element then plays it.
/// </para>
/// <para>
/// So this probe measures REACH — the shell's own player opens what the app hands it — and the D80 case
/// for the surface on Android rests on the other two things, both already measured: playback that survives
/// backgrounding (45 s native vs ~15 s for the page's element) and a picture composited UNDER the page.
/// </para>
///
/// <para>
/// ⚠ <b>It stages its own fixture</b> rather than inheriting another probe's, because a probe that reads a
/// file some earlier probe happened to leave behind passes for the wrong reason on a warm device and fails
/// on a cold one — a per-device difference that is really disk state (`mobile-harness`).
/// </para>
/// </summary>
public static class MediaSurfaceProbe
{
    /// <summary>The container the WebView refuses and the platform's own extractor opens. D52's case.</summary>
    private const string Clip = "clip-h264-aac.mkv";

    /// <summary>
    /// Stage the MKV, hand it to the shell's player through the picture surface, and report whether the
    /// clock MOVED.
    /// <para>
    /// The position is the assertion for the same reason it is in <see cref="MediaPlayerProbe"/>:
    /// <c>OpenAsync</c> completing proves the platform accepted the source, and a clock that advances
    /// proves it is decoding it.
    /// </para>
    /// <para>
    /// ⚠ <b>What this does NOT prove is PIXELS.</b> A moving clock and a composited picture are different
    /// claims, and only a screenshot through a transparent page region can settle the second.
    /// </para>
    /// </summary>
    /// <param name="player">The shell's own player, or null where none ships.</param>
    /// <param name="surface">The picture surface, or null when the app registered none.</param>
    /// <param name="log">Sink.</param>
    public static async Task RunAsync(IMediaPlayer? player, IMediaSurface? surface, Action<string> log)
    {
        if (player is null)
        {
            log("SURFACE: no shell player on this platform — nothing to draw with");
            return;
        }
        if (surface is null)
        {
            log("SURFACE: no IMediaSurface registered — the app did not call AddShenoraMediaSurface()");
            return;
        }

        string clip;
        try
        {
            clip = await StageAsync();
        }
        catch (Exception ex)
        {
            log($"SURFACE: FAIL — could not stage {Clip}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        try
        {
            // A region the page has NOT made transparent yet, so nothing is expected to be visible. It is
            // sent anyway because the geometry path is part of what is under test, and a surface that
            // throws on Show would otherwise only be found by a human looking at glass.
            surface.Show(new MediaSurfaceRegion(0, 0, 320, 180));
            log("SURFACE: region sent (0,0 320x180 css px)");

            await player.OpenAsync(new MediaSource { Uri = clip });
            log($"SURFACE: opened {Clip} — engine={player.Status.Engine} duration={player.Status.Duration}");

            await player.PlayAsync();
            await Task.Delay(1500);
            var first = player.Status.Position;
            await Task.Delay(1500);
            var second = player.Status.Position;

            log($"SURFACE: {first.TotalSeconds:F2}s -> {second.TotalSeconds:F2}s "
                + $"state={player.Status.State} engine={player.Status.Engine}");
            // ⚠ The verdict says what was MEASURED and no more. An earlier version of this line claimed
            // the container was "one the WebView refuses" — a premise it never tested, and one the A/B
            // then refuted on this very device. A probe that asserts its own motivation is not evidence.
            log(second > first
                ? "SURFACE: PASS — the shell's player opened an MKV by path and advanced a real clock"
                : $"SURFACE: FAIL — the clock did not move (error={player.Status.Error ?? "none"})");
        }
        catch (Exception ex)
        {
            // ⚠ A refusal HERE is the interesting outcome, not a harness fault: it means the kit's default
            // engine does not reach the case D80 was built for, and the seam is the answer rather than a
            // wider default.
            log($"SURFACE: FAIL — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { await player.CloseAsync(); } catch { /* teardown must not mask the result above */ }
            try { surface.Hide(); } catch { /* nor this */ }
        }
    }

    /// <summary>Copy the bundled clip into the cache, where a native player can open it by PATH.</summary>
    private static async Task<string> StageAsync()
    {
        var root = Path.Combine(FileSystem.CacheDirectory, "media");
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, Clip);
        if (File.Exists(destination)) return destination;

        await using var source = await FileSystem.OpenAppPackageFileAsync($"wwwroot/media/{Clip}");
        await using var target = File.Create(destination);
        await source.CopyToAsync(target);
        await target.FlushAsync();
        return destination;
    }
}
