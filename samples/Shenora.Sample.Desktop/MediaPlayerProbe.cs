using Shenora.Modules.Media;
using Shenora.Windows;

using Shenora;
namespace Shenora.Sample.Desktop;

/// <summary>
/// The SEAM TEST for <see cref="WindowsMediaPlayer"/>: does Media Foundation actually play, and does the
/// CLOCK ADVANCE?
/// <para>
/// <b>Nothing else can answer this.</b> The unit tests pin the contract's shapes and promises and cannot
/// decode a byte — every implementation is a shell talking to a platform pipeline. And the failure this
/// guards against is the one D63 names: a capability that is ABSENT rather than broken produces no error,
/// no log line and no failing test. <c>PlayAsync</c> completing proves a message reached Media Foundation
/// and nothing more, which is the same mistake this sample's early media probe made when it asserted bytes
/// rather than pixels.
/// </para>
/// <para>
/// <b>It brings its own audio</b> — a couple of seconds of silent 16-bit PCM, written to the OS temp and
/// deleted after. Two reasons that beats shipping a clip: WAV is decoded by every Windows install with no
/// codec dependency at all, so a FAIL here means the player is broken rather than the machine being short
/// a codec; and SILENT means running the sample does not make noise every time. The decode and render path
/// is identical either way — silence is still samples.
/// </para>
/// </summary>
internal static class MediaPlayerProbe
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartAt = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan SeekTo = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Open, play, seek, pause, close — asserting the platform's own reported state at each step, and
    /// reporting a one-line verdict. Never a bare boolean.
    /// </summary>
    public static async Task<string> RunAsync()
    {
        var clip = Path.Combine(Path.GetTempPath(), $"shenora-player-probe-{Environment.ProcessId}.wav");

        WindowsMediaPlayer player;
        try
        {
            await File.WriteAllBytesAsync(clip, BuildSilentWav(Duration)).ConfigureAwait(false);
            player = new WindowsMediaPlayer(AppCallback.Logger(Console.WriteLine));
        }
        catch (Exception ex)
        {
            return $"MEDIA PLAYER: FAIL — could not stage the probe ({ex.GetType().Name}: {ex.Message})";
        }

        var transitions = 0;
        player.StateChanged += _ => Interlocked.Increment(ref transitions);

        try
        {
            var failures = new List<string>();

            // ---- OPEN. Completes when the platform can report a duration and accept a seek, so everything
            // asserted here is the platform's answer and not ours.
            await player.OpenAsync(new MediaSource { Uri = clip, StartAt = StartAt }).ConfigureAwait(false);
            var opened = player.Status;

            if (opened.State != MediaPlayerState.Paused)
                failures.Add($"open left state={opened.State}, expected Paused (opening must not start playback)");
            if (opened.Duration is null)
                failures.Add("duration is null after open — the platform never parsed the source");
            else if (Math.Abs(opened.Duration.Value.TotalSeconds - Duration.TotalSeconds) > 0.25)
                failures.Add($"duration={opened.Duration.Value.TotalSeconds:F2}s, expected ~{Duration.TotalSeconds:F2}s");
            // StartAt is applied as part of OPENING, not by a seek afterwards — the difference a caller sees
            // is whether a resumed item visibly starts at zero and jumps.
            if (opened.Position < StartAt - TimeSpan.FromSeconds(0.15))
                failures.Add($"StartAt was ignored: position={opened.Position.TotalSeconds:F2}s, expected ~{StartAt.TotalSeconds:F2}s");

            // ---- PLAY. THE assertion of this probe: the position must MOVE. Polled rather than slept on,
            // so the verdict can say how long it took instead of being either flaky or slow.
            await player.PlayAsync().ConfigureAwait(false);
            var advancedAfter = TimeSpan.Zero;
            var startedFrom = player.Status.Position;
            for (var attempt = 0; attempt < 60; attempt++)          // ~3 s
            {
                await Task.Delay(50).ConfigureAwait(false);
                if (player.Status.Position > startedFrom + TimeSpan.FromSeconds(0.1))
                {
                    advancedAfter = TimeSpan.FromMilliseconds((attempt + 1) * 50);
                    break;
                }
            }
            if (advancedAfter == TimeSpan.Zero)
                failures.Add($"the clock never advanced — position stuck at {startedFrom.TotalSeconds:F2}s with state={player.Status.State}");

            // ---- PAUSE. Held, not merely reported as held: a player that says Paused and keeps decoding is
            // the failure worth catching, and only re-reading after a wait can tell them apart.
            await player.PauseAsync().ConfigureAwait(false);
            var pausedAt = player.Status.Position;
            await Task.Delay(250).ConfigureAwait(false);
            var stillAt = player.Status.Position;
            if (player.Status.State != MediaPlayerState.Paused)
                failures.Add($"pause left state={player.Status.State}");
            if (stillAt > pausedAt + TimeSpan.FromSeconds(0.1))
                failures.Add($"pause did not hold: {pausedAt.TotalSeconds:F2}s -> {stillAt.TotalSeconds:F2}s");

            // ---- SEEK. Absolute, and a paused player stays paused.
            await player.SeekAsync(SeekTo).ConfigureAwait(false);
            var sought = player.Status;
            if (Math.Abs(sought.Position.TotalSeconds - SeekTo.TotalSeconds) > 0.25)
                failures.Add($"seek landed at {sought.Position.TotalSeconds:F2}s, expected ~{SeekTo.TotalSeconds:F2}s");
            if (sought.State != MediaPlayerState.Paused)
                failures.Add($"seek changed state to {sought.State}; a paused player must stay paused");

            // ---- CLOSE. Back to Empty with the source released — the counterpart an app owes every open.
            await player.CloseAsync().ConfigureAwait(false);
            var closed = player.Status;
            if (closed.State != MediaPlayerState.Empty) failures.Add($"close left state={closed.State}");
            if (closed.Duration is not null) failures.Add("close left a duration behind");

            // The EVENT seam. Without this the whole probe would pass against a player that never told
            // anyone anything — which is exactly how ReportTo would silently publish nothing to the taskbar.
            if (transitions < 3) failures.Add($"StateChanged fired {transitions}x; expected one per transition");

            var report = $"duration={opened.Duration?.TotalSeconds:F2}s|startAt={opened.Position.TotalSeconds:F2}s"
                + $"|advancedIn={advancedAfter.TotalMilliseconds:F0}ms|seek={sought.Position.TotalSeconds:F2}s"
                + $"|transitions={transitions}";

            return failures.Count == 0
                ? $"MEDIA PLAYER: PASS ({report})"
                : $"MEDIA PLAYER: FAIL — {string.Join("; ", failures)}  [raw: {report}]";
        }
        catch (MediaPlayerException ex)
        {
            return $"MEDIA PLAYER: FAIL — {ex.Message}";
        }
        finally
        {
            player.Dispose();
            // After Dispose, not before: the player holds the file until its source is released, and
            // deleting underneath it is what the Teardown comment in WindowsMediaPlayer is about.
            try { File.Delete(clip); } catch (IOException) { /* a probe must not fail on cleanup */ }
        }
    }

    /// <summary>
    /// A minimal RIFF/WAVE file: 44-byte header, then silence. Written by hand rather than pulled from a
    /// package because the whole point is to depend on nothing but the platform's own PCM decoder.
    /// </summary>
    private static byte[] BuildSilentWav(TimeSpan duration)
    {
        const int SampleRate = 44100;
        const short Channels = 1;
        const short Bits = 16;
        const short BlockAlign = Channels * Bits / 8;

        var frames = (int)(SampleRate * duration.TotalSeconds);
        var dataBytes = frames * BlockAlign;
        var buffer = new byte[44 + dataBytes];

        using var writer = new BinaryWriter(new MemoryStream(buffer));
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);            // everything after this field
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                        // PCM format chunk length
        writer.Write((short)1);                  // PCM, uncompressed
        writer.Write(Channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * BlockAlign);   // byte rate
        writer.Write(BlockAlign);
        writer.Write(Bits);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        // The samples themselves stay zero. Silence is still samples: the decoder and the renderer run.
        return buffer;
    }
}
