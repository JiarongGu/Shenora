using Shenora.Modules.Media;

namespace Shenora.Sample.Maui;

/// <summary>
/// 🔴 <b>Does the host's believed playhead survive on the HOOK's path?</b>
///
/// <para>
/// <c>BackgroundPlaybackTransfer</c> hands the native player <c>IMediaPlayer.Status.Position</c>, and
/// <c>useMediaPlayer</c> reports on TRANSITIONS ONLY — never <c>timeupdate</c>. So under the hook the host's
/// position is whatever the last transition left, and it is refreshed at background time only if the
/// platform's <c>pause</c> report crosses IPC before the process freezes. If it does not, a React adopter
/// resumes from the moment playback STARTED.
/// </para>
/// <para>
/// ⚠ <b>The sample cannot ask this in its default shape</b>, because it also reports on <c>timeupdate</c>
/// ~4× a second and the position is therefore always fresh. This arms
/// <c>window.__shenoraSetHookParity(true)</c> first, which silences that one listener and leaves the sample
/// behaving exactly as the hook does.
/// </para>
/// <para>
/// 🔴 <b>Driven from C# rather than <c>dev.mjs android eval</c>, deliberately.</b> The eval channel wedges
/// after any failed eval and then answers empty for everything after it, and a <c>const</c> at eval top
/// level persists in page global scope so the NEXT call dies on redeclaration. <c>PageProbe.EvaluateAsync</c>
/// has the same reach and none of that.
/// </para>
/// </summary>
internal static class PlayheadProbe
{
    /// <summary>How long to play with NO transition, so a stale position is unmistakably stale.</summary>
    private static readonly TimeSpan Drift = TimeSpan.FromSeconds(20);

    /// <summary>Where playback starts. A stale reading lands here; a fresh one lands ~<see cref="Drift"/> later.</summary>
    private const double StartAt = 3.0;

    /// <summary>
    /// Put the page into hook parity, start muted playback, let it drift, then report BOTH clocks.
    ///
    /// <para>
    /// ⚠ <b>MUTED on purpose.</b> An injected <c>play()</c> carries no user activation, so an unmuted one is
    /// refused by autoplay policy — that is the platform behaving correctly, and a probe that read it as a
    /// fault would be measuring its own harness.
    /// </para>
    /// <para>
    /// ⚠ <b>Mid-clip on purpose.</b> Backgrounding at the END fires <c>ended</c>, which IS a transition and
    /// refreshes the position for the wrong reason — a run that did exactly that read a perfect 60.00 s and
    /// proved nothing.
    /// </para>
    /// </summary>
    public static async Task<string> ArmAsync(Microsoft.Maui.Controls.HybridWebView webView,
                                              IMediaPlayer? player, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(webView);
        ArgumentNullException.ThrowIfNull(log);

        var parity = await PageProbe.EvaluateAsync(webView, "window.__shenoraSetHookParity(true)")
                                    .ConfigureAwait(false);
        if (parity is null) return "PLAYHEAD: FAIL — could not set hook parity (is the page up?)";
        if (!parity.Contains("true", StringComparison.OrdinalIgnoreCase))
            return $"PLAYHEAD: FAIL — hook parity answered '{parity}'";

        // One statement, no `const` at top level — see the type remarks.
        var started = await PageProbe.EvaluateAsync(webView,
            "(function(){var e=document.querySelector('video');if(!e)return 'no-element';"
          + "e.muted=true;e.src='media/clip-faststart.mp4';e.load();e.currentTime=" + StartAt + ";"
          + "e.play();return 'go';})()").ConfigureAwait(false);
        if (started is null || !started.Contains("go", StringComparison.Ordinal))
            return $"PLAYHEAD: FAIL — could not start playback ({started ?? "null"})";

        await Task.Delay(Drift).ConfigureAwait(false);

        var page = await PageProbe.EvaluateAsync(webView,
            "(function(){var e=document.querySelector('video');"
          + "return JSON.stringify({t:+e.currentTime.toFixed(2),paused:e.paused,ended:e.ended});})()")
            .ConfigureAwait(false);

        // 🔴 The comparison the whole probe exists for, taken BEFORE anything backgrounds: what the PAGE is
        // actually at, against what the HOST believes. Under hook parity the host's figure comes from the
        // last transition, so a gap here is the defect in miniature — visible without leaving the app.
        var host = player?.Status.Position.TotalSeconds;
        log($"[PLAYHEAD] page={page} host={host?.ToString("F2") ?? "n/a"}s");

        return $"PLAYHEAD: ARMED — page {page}, host believes {host?.ToString("F2") ?? "n/a"}s. "
             + $"Background it now (`adb shell input keyevent KEYCODE_HOME`) and read HANDOFF: "
             + $"≈{StartAt:F2}s means the position went STALE, ≈{StartAt + Drift.TotalSeconds:F2}s means the "
             + "pause report crossed IPC in time.";
    }
}
