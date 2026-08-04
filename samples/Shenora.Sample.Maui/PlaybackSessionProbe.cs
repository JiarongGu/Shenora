using Shenora.Core;

namespace Shenora.Sample.Maui;

/// <summary>
/// The SEAM TEST for <see cref="IPlaybackSession"/> on the mobile shells — the counterpart of the desktop
/// sample's probe, and it has to be shaped differently for one reason: neither mobile platform lets the app
/// read the OS's own session registry back.
/// <para>
/// So the verification is split. This side publishes a KNOWN item and logs exactly what it sent; the OS's
/// view is read from OUTSIDE, by the device harness — <c>adb shell dumpsys media_session</c> on Android, and
/// the <c>MediaRemote</c>/<c>mediaremoted</c> log on iOS. That is deliberately not "the app says it worked":
/// the values below are distinctive enough to grep for, so a match in the system's own output is real
/// evidence and a mismatch names which field is wrong.
/// </para>
/// </summary>
internal static class PlaybackSessionProbe
{
    /// <summary>Distinctive on purpose — these strings are what the harness greps for in system output.</summary>
    internal const string Title = "Shenora probe title";
    internal const string Subtitle = "Shenora probe subtitle";
    internal const string GroupName = "Shenora probe group";

    /// <summary>
    /// Publish, move the position, and log a one-line verdict plus the commands the OS sends back. Never
    /// throws — a probe that takes the sample down teaches nothing.
    /// </summary>
    public static void Run(IPlaybackSession session, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            // Commands are the half only a HUMAN can trigger (a headphone press, a lock-screen tap), so
            // they are logged rather than asserted — the run is still evidence when someone presses one.
            session.CommandReceived += r =>
                log($"[PLAYBACK] command <= {r.Command}"
                    + (r.Position is { } p ? $" @{p.TotalSeconds:0.00}s" : ""));

            session.SkipInterval = TimeSpan.FromSeconds(15);
            session.Supported = PlaybackCommands.Play | PlaybackCommands.Pause
                | PlaybackCommands.TogglePlayPause | PlaybackCommands.Next
                | PlaybackCommands.Previous | PlaybackCommands.Seek
                | PlaybackCommands.SkipForward | PlaybackCommands.SkipBackward;

            session.Publish(new PlaybackInfo
            {
                Title = Title,
                Subtitle = Subtitle,
                GroupName = GroupName,
                Duration = TimeSpan.FromSeconds(240),
            });
            session.Report(new PlaybackProgress
            {
                State = PlaybackState.Playing,
                Position = TimeSpan.FromSeconds(42),
                Rate = 1.0,
            });

            log($"[PLAYBACK] published title='{Title}' subtitle='{Subtitle}' group='{GroupName}' "
                + "duration=240s state=Playing position=42s");
            log($"[PLAYBACK] session type={session.GetType().Name}");
            log("[PLAYBACK] PUBLISHED — now read the OS back: Android `adb shell dumpsys media_session`, "
                + "iOS the mediaremoted log. Press a transport control to exercise the return path.");
        }
        catch (Exception ex)
        {
            log($"[PLAYBACK] FAIL — {ex.GetType().Name}: {ex.Message}");
        }
    }
}
