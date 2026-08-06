using System.Buffers.Binary;
using System.IO.Compression;
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

            // 🔴 THE AUDIO SESSION, which is the APP's job and not the kit's — and is the difference
            // between playback that survives a swipe and playback that dies on it.
            //
            // `UIBackgroundModes: [audio]` in Info.plist and an ACTIVE AVAudioSession are a PAIR: neither
            // half does anything alone, and the symptom of missing either is identical — plays fine in the
            // foreground, stops the instant the app is backgrounded. Measured on a device, 2026-08-07.
            //
            // The kit deliberately stays out of this (see MobilePlaybackSession's remarks): the CATEGORY,
            // whether it mixes with other audio, and what happens on an interruption are product decisions
            // — `.Playback` here means "this is the point of the app, stop the music app". A sample has to
            // make that choice like any other app, which is exactly why it is shown rather than hidden.
            ConfigureAudioSession(log);

            // 🔴 ARTWORK IS WHAT MAKES THE DYNAMIC ISLAND SHOW ANYTHING, and leaving it out is why this
            // probe produced "a long bar with nothing in it" on an iPhone 17 Pro (2026-08-07). With title
            // and duration but no image, iOS knows something is playing, falls back to the app icon, and
            // the Island has nothing to draw — a public sibling's iOS notes describe the identical symptom
            // and name the identical cause. It is the one field a probe is most likely to skip, because it
            // is the only one that is not a string.
            var artwork = LoadArtwork();
            session.Publish(new PlaybackInfo
            {
                Title = Title,
                Subtitle = Subtitle,
                GroupName = GroupName,
                Artwork = artwork,
                Duration = TimeSpan.FromSeconds(240),
            });
            session.Report(new PlaybackProgress
            {
                State = PlaybackState.Playing,
                Position = TimeSpan.FromSeconds(42),
                Rate = 1.0,
            });

            log($"[PLAYBACK] published title='{Title}' subtitle='{Subtitle}' group='{GroupName}' "
                + $"artwork={artwork.Length}B duration=240s state=Playing position=42s");
            if (artwork.IsEmpty)
            {
                log("[PLAYBACK] ⚠ NO ARTWORK — the Island will fall back to the app icon and look empty. "
                    + "That is a probe problem, not a kit one.");
            }
            log($"[PLAYBACK] session type={session.GetType().Name}");
            log("[PLAYBACK] PUBLISHED — now read the OS back: Android `adb shell dumpsys media_session`, "
                + "iOS the mediaremoted log. Press a transport control to exercise the return path.");
        }
        catch (Exception ex)
        {
            log($"[PLAYBACK] FAIL — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Tell iOS this app is a playback app, so it keeps running when backgrounded.
    /// <para>
    /// Paired with <c>UIBackgroundModes: [audio]</c> in <c>Platforms/iOS/Info.plist</c> — see the comment
    /// there. On Android the equivalent is a foreground service plus audio focus, which the sample does not
    /// need because it is not the platform under test here; the call is a no-op off iOS.
    /// </para>
    /// </summary>
    private static void ConfigureAudioSession(Action<string> log)
    {
#if IOS || MACCATALYST
        try
        {
            var session = AVFoundation.AVAudioSession.SharedInstance();
            // Playback, not Ambient: Ambient is silenced by the ring switch and stops on background, which
            // is the exact failure this call exists to prevent.
            var error = session.SetCategory(AVFoundation.AVAudioSessionCategory.Playback);
            if (error is not null) { log($"[PLAYBACK] audio session category REFUSED: {error.LocalizedDescription}"); return; }
            error = session.SetActive(true);
            if (error is not null) { log($"[PLAYBACK] audio session activate REFUSED: {error.LocalizedDescription}"); return; }
            log("[PLAYBACK] audio session active (category=Playback) — playback survives backgrounding");
        }
        catch (Exception ex)
        {
            log($"[PLAYBACK] audio session FAILED: {ex.GetType().Name}: {ex.Message}");
        }
#else
        log("[PLAYBACK] audio session: not iOS — nothing to configure");
#endif
    }

    /// <summary>
    /// A 300×300 cover, drawn in code.
    /// <para>
    /// Generated rather than shipped as a file on purpose: a MAUI image resource goes through the platform
    /// asset pipeline and comes out under a name that differs per platform, so reading one back at runtime
    /// is its own small research project — and this probe exists to test the SESSION, not the asset
    /// pipeline. Bytes with no dependencies keep the thing under test the thing under test.
    /// </para>
    /// </summary>
    private static ReadOnlyMemory<byte> LoadArtwork()
    {
        const int size = 300;
        var pixels = new byte[size * (size * 3 + 1)];          // one filter byte per scanline, then RGB

        for (var y = 0; y < size; y++)
        {
            var row = y * (size * 3 + 1);
            pixels[row] = 0;                                   // filter: none
            for (var x = 0; x < size; x++)
            {
                // A soft diagonal so the image is obviously OURS rather than a flat block — a flat colour
                // is hard to tell apart from "the system drew nothing".
                var t = (x + y) / (2.0 * size);
                var i = row + 1 + x * 3;
                pixels[i] = (byte)(40 + 60 * t);               // R
                pixels[i + 1] = (byte)(90 + 90 * t);           // G
                pixels[i + 2] = (byte)(160 + 80 * t);          // B

                // A centred bar, so there is a shape at Island size rather than only a gradient.
                if (y > size * 0.42 && y < size * 0.58 && x > size * 0.18 && x < size * 0.82)
                {
                    pixels[i] = 245;
                    pixels[i + 1] = 247;
                    pixels[i + 2] = 250;
                }
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], size);
        BinaryPrimitives.WriteInt32BigEndian(header[4..8], size);
        header[8] = 8;                                          // bit depth
        header[9] = 2;                                          // colour type: truecolour
        Chunk(png, "IHDR", header);

        using var deflated = new MemoryStream();
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(pixels);
        Chunk(png, "IDAT", deflated.ToArray());
        Chunk(png, "IEND", []);

        return png.ToArray();
    }

    /// <summary>One PNG chunk: length, type, payload, CRC32 over type+payload.</summary>
    private static void Chunk(Stream target, string type, ReadOnlySpan<byte> payload)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        target.Write(length);

        var typed = System.Text.Encoding.ASCII.GetBytes(type);
        target.Write(typed);
        target.Write(payload);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32([.. typed, .. payload]));
        target.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
