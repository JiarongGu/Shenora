using Shenora.Core;
using Shenora.Windows;

namespace Shenora.Sample.Desktop;

/// <summary>
/// The SEAM TEST for <see cref="IPlaybackSession"/> on the desktop: publish to Windows' media transport
/// surface, then read it back through <b>the OS's own session registry</b> and assert what it says.
/// <para>
/// Reading it back is the whole point. "We called <c>Update()</c> and it did not throw" proves nothing — a
/// wrong <c>DisplayUpdater.Type</c>, a forgotten <c>Update()</c>, or a
/// <c>CommandManager</c> left enabled all pass that test and produce a flyout that is empty or dead. So
/// this asks <c>GlobalSystemMediaTransportControlsSessionManager</c> what IT sees, which is the same
/// discipline as reading iOS's <c>pluginkit</c> rather than listing files in a bundle.
/// </para>
/// </summary>
internal static class PlaybackSessionProbe
{
    private const string Title = "Shenora probe title";
    private const string Subtitle = "Shenora probe subtitle";
    private const string GroupName = "Shenora probe group";

    /// <summary>
    /// Publish a known item, read it back from the OS, and report a one-line verdict — PASS, or FAIL
    /// naming what the OS actually reported. Never a bare boolean.
    /// </summary>
    public static async Task<string> RunAsync()
    {
        WindowsPlaybackSession session;
        try
        {
            session = new WindowsPlaybackSession(m => Console.WriteLine(m));
        }
        catch (Exception ex)
        {
            return $"PLAYBACK SESSION: FAIL — could not create the session ({ex.GetType().Name}: {ex.Message})";
        }

        try
        {
            var commands = new List<string>();
            session.CommandReceived += r => { lock (commands) commands.Add(r.Command.ToString()); };

            session.SkipInterval = TimeSpan.FromSeconds(15);
            session.Supported = PlaybackCommands.Play | PlaybackCommands.Pause
                | PlaybackCommands.Next | PlaybackCommands.Previous | PlaybackCommands.Seek
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

            // The OS registry is updated cross-process, so it is eventually-consistent by nature. Poll
            // rather than sleeping once: a fixed delay is either flaky or slow, and this way the verdict
            // says how long it took.
            var report = "";
            for (var attempt = 0; attempt < 40; attempt++)          // ~8 s
            {
                report = await ReadBackAsync().ConfigureAwait(false);
                if (report.Contains(Title, StringComparison.Ordinal)) break;
                await Task.Delay(200).ConfigureAwait(false);
            }

            if (!report.Contains(Title, StringComparison.Ordinal))
            {
                return "PLAYBACK SESSION: FAIL — the OS never reported our title. "
                    + $"What it did report: [{report}]";
            }

            var failures = new List<string>();
            if (!report.Contains(Subtitle, StringComparison.Ordinal)) failures.Add("subtitle (artist) missing");
            if (!report.Contains(GroupName, StringComparison.Ordinal)) failures.Add("groupName (album) missing");
            // Playing, not Paused/Stopped: this is what proves Report() reached the OS and not just
            // Publish(). They are separate calls onto separate SMTC properties and either can be wrong
            // alone.
            if (!report.Contains("status=Playing", StringComparison.Ordinal)) failures.Add("status is not Playing");
            // The skip-by-interval buttons the first adopter needed (2026-08-04). Windows exposes them as
            // fast-forward/rewind, so this is the read-back that proves the mapping actually lit them.
            if (!report.Contains("ff=True", StringComparison.Ordinal)) failures.Add("SkipForward did not enable fast-forward");
            if (!report.Contains("rw=True", StringComparison.Ordinal)) failures.Add("SkipBackward did not enable rewind");

            session.Clear();
            return failures.Count == 0
                ? $"PLAYBACK SESSION: PASS ({report})"
                : $"PLAYBACK SESSION: FAIL — {string.Join("; ", failures)}  [raw: {report}]";
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// What Windows itself believes is playing. Enumerates every media session and returns OURS, or a
    /// list of whatever else it found — because "no session" and "someone else's session" are different
    /// failures and a bare "not found" would hide which.
    /// </summary>
    private static async Task<string> ReadBackAsync()
    {
        try
        {
            var manager = await global::Windows.Media.Control
                .GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = manager.GetSessions();
            var others = new List<string>();

            foreach (var s in sessions)
            {
                var props = await s.TryGetMediaPropertiesAsync();
                var status = s.GetPlaybackInfo().PlaybackStatus;
                if (props?.Title == Title)
                {
                    // Controls, not just metadata: Supported maps onto SMTC button flags through a
                    // different call than Publish does, so reading only the text would leave the whole
                    // PlaybackCommands mapping ungated.
                    var controls = s.GetPlaybackInfo().Controls;
                    return $"app={s.SourceAppUserModelId}|title={props.Title}|artist={props.Artist}"
                        + $"|album={props.AlbumTitle}|status={status}"
                        + $"|next={controls.IsNextEnabled}|ff={controls.IsFastForwardEnabled}"
                        + $"|rw={controls.IsRewindEnabled}";
                }
                others.Add($"{s.SourceAppUserModelId}:{props?.Title}");
            }

            return others.Count == 0
                ? "no media sessions at all"
                : $"{sessions.Count} other session(s): {string.Join(", ", others)}";
        }
        catch (Exception ex)
        {
            return $"read-back threw {ex.GetType().Name}: {ex.Message}";
        }
    }
}
