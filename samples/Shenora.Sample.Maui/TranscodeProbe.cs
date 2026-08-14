using Shenora.Modules.Media;

namespace Shenora.Sample.Maui;

/// <summary>
/// Does this device's audio converter actually PRODUCE A FILE THAT PLAYS?
///
/// <para>
/// 🔴 <b>The tier it covers had never run a real encoder.</b> The muxing is well covered by unit tests
/// against a FAKE codec — box order, timing, the drained tail, copy-beats-convert — and both mobile shells
/// report <c>convert ac3: accepted=True</c>. None of that is evidence: this repo has already measured an
/// encoder that accepted every frame, wrote <c>video:0KiB</c> and exited 0. <b>"Exit 0 is not evidence"
/// applies hardest here</b>, so this probe asserts the OUTPUT, twice over — bytes, then playback.
/// </para>
///
/// <para>
/// ⚠ <b>The fixture is MP3-in-Matroska, and the codec choice is the point.</b> Proving this needs a source
/// the device can DECODE but MP4 cannot CARRY, so the remuxer is forced down the decode → encode → mux path
/// rather than copying the stream. AC-3 is the real-world case and would be the ideal fixture — but AOSP has
/// no AC-3 decoder, so an emulator could never run it. MP3 exercises the IDENTICAL chain on every target,
/// which is what makes the result portable rather than hardware-specific.
/// </para>
///
/// <para>
/// ⚠ It reports SKIPPED, never FAIL, where the shell registers no converter. A platform that ships none is
/// answering honestly; turning that into a failure would make the probe cry wolf on exactly the shells the
/// tier does not claim.
/// </para>
/// </summary>
internal static class TranscodeProbe
{
    /// <summary>
    /// Fixtures, and the probe uses the first one this shell's converter CLAIMS.
    /// <para>
    /// 🔴 <b>Two are needed because the platforms implement different input sets, and one fixture would
    /// have measured only the platform it happened to suit.</b> Android's converter takes mp3 and refuses
    /// AC-3 on AOSP (no decoder exists there); iOS takes AC-3 and does not claim mp3. Running the mp3
    /// fixture on iOS reported SKIPPED — correct, and worth nothing as evidence.
    /// </para>
    /// <para>
    /// ⚠ AC-3 was previously listed as needing a fixture "only a handset or the owner can supply", because
    /// macOS cannot ENCODE it (`afconvert` answers `fmt?`). ffmpeg can, on any machine — so the blocker was
    /// the tool being asked, not the codec.
    /// </para>
    /// </summary>
    private static readonly (string Codec, string File)[] Fixtures =
    [
        ("mp3", "clip-mp3.mkv"),
        ("ac3", "clip-ac3.mkv"),
    ];

    public static async Task RunAsync(IMediaStreamConversion? conversion, IMediaPlayer? player, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (conversion is null)
        {
            log("TRANSCODE: SKIPPED — this shell registers no IMediaStreamConversion (container repair only)");
            return;
        }

        // Honest and not a failure when none matches: the converter is allowed to implement fewer inputs
        // than the device can decode (`IMediaCapability` answers about the DEVICE, `CanConvert` about the KIT).
        if (Array.Find(Fixtures, f => conversion.CanConvert(MediaStreamKind.Audio, f.Codec)) is not { File: not null } chosen)
        {
            log($"TRANSCODE: SKIPPED — this shell's converter claims none of: "
                + string.Join(", ", Fixtures.Select(f => f.Codec)));
            return;
        }
        log($"TRANSCODE: using the {chosen.Codec} fixture ({chosen.File})");

        var root = Path.Combine(FileSystem.CacheDirectory, "media");
        var source = Path.Combine(root, chosen.File);
        var destination = Path.Combine(root, $"{chosen.Codec}-transcoded.mp4");

        try
        {
            // ONE owner for "get this fixture out of the app package". It was inline here, and
            // ConversionRouteProbe silently depended on this copy having run — which made it fail on every
            // cold install and pass forever after. Both call the same helper now.
            await MediaRangeProbe.EnsureStagedAsync(root, chosen.File, log);

            // ⚠ DIAGNOSTIC BEFORE THE REMUX, because `Mp4Remuxer` swallows exception text ON PURPOSE — a
            // media path must never reach a page. That is right for the shipped path and blinding for this
            // one, so the probe opens the converter itself first and reports what the platform said.
            try
            {
                await using var probing = File.OpenRead(source);
                var info = MatroskaProbe.Read(probing);
                var track = info?.Streams.FirstOrDefault(s => s.Kind == MediaStreamKind.Audio);
                log($"TRANSCODE: source codec={track?.Codec} rate={track?.SampleRate} channels={track?.Channels}");
                if (track is not null)
                {
                    using var run = conversion.Begin(track, ReadOnlyMemory<byte>.Empty);
                    log(run is null
                        ? "TRANSCODE: Begin returned null — the converter declined this stream"
                        : $"TRANSCODE: Begin ok — out={run.OutputFormat.SampleRate}Hz/{run.OutputFormat.Channels}ch "
                          + $"framesPerPacket={run.OutputFramesPerPacket}");
                }
            }
            catch (Exception ex)
            {
                log($"TRANSCODE: Begin THREW — {ex.GetType().Name}: {ex.Message}");
            }

            File.Delete(destination);
            var result = Mp4Remuxer.Remux(source, destination, conversion);
            var length = File.Exists(destination) ? new FileInfo(destination).Length : 0;
            log($"TRANSCODE: outcome={result.Outcome} audioSamples={result.AudioSamples} "
                + $"dropped=[{string.Join(' ', result.Dropped)}] bytes={length} reason={result.Reason}");

            if (!result.Succeeded)
            {
                log($"TRANSCODE: FAIL — the remux refused: {result.Reason}");
                return;
            }

            // 🔴 A SUCCESSFUL REMUX THAT DROPPED THE AUDIO IS THE DANGEROUS OUTCOME — nothing throws, the
            // file plays, and it is silent. For this fixture the soundtrack is the ONLY thing under test, so
            // dropping it is a failure rather than a caveat.
            if (result.Dropped.Count > 0)
            {
                log($"TRANSCODE: FAIL — the audio was DROPPED ([{string.Join(' ', result.Dropped)}]), so the "
                    + "output is a silent file the converter declined to rescue");
                return;
            }

            // 🔴 SAMPLES AND SIZE, because this is the check the measured failure would have caught: an
            // encoder that accepts every frame and writes nothing still returns success. A header-only MP4
            // is ~1 KB, and zero samples is that same failure stated by the muxer itself.
            if (result.AudioSamples <= 0 || length < 8 * 1024)
            {
                log($"TRANSCODE: FAIL — {result.AudioSamples} audio sample(s) and {length} bytes; the encoder "
                    + "accepted the input and produced nothing");
                return;
            }

            if (player is null)
            {
                // Bytes are real but unproven as PLAYABLE. Say exactly that rather than claiming a pass —
                // a file can be well-formed and still decode to silence.
                log("TRANSCODE: PARTIAL — a real-sized file was produced, but this shell has no IMediaPlayer "
                    + "to prove it plays. Bytes are not playback.");
                return;
            }

            await player.OpenAsync(new MediaSource { Uri = destination });
            await player.PlayAsync();
            await Task.Delay(1500);
            var first = player.Status.Position;
            await Task.Delay(1500);
            var second = player.Status.Position;
            log($"TRANSCODE: playback {first.TotalSeconds:F2}s -> {second.TotalSeconds:F2}s state={player.Status.State}");

            // ⚠ Names the codec it ACTUALLY used. This said "decoded mp3" unconditionally, so the iOS run —
            // which uses the AC-3 fixture, mp3 being one the shell does not claim — reported a pass for a
            // conversion it had not performed. A verdict naming the wrong input is worse than a bare PASS.
            log(second > first
                ? $"TRANSCODE: PASS — the device decoded {chosen.Codec}, encoded AAC, muxed MP4, and PLAYED the result"
                : "TRANSCODE: FAIL — the file was produced but its clock does not move (it decodes to nothing)");
        }
        catch (Exception ex)
        {
            log($"TRANSCODE: FAIL — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (player is not null)
            {
                try { await player.CloseAsync(); } catch { /* teardown must not mask the verdict above */ }
            }
        }
    }
}
