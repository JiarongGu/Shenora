#if IOS || MACCATALYST
using System.Runtime.InteropServices;
using Shenora.Media;

namespace Shenora.Mobile;

/// <summary>
/// iOS's <see cref="IMediaCapability"/> — answered by asking AudioToolbox to BUILD a converter, which
/// succeeds only when a codec for the pair actually exists on the device.
///
/// <para>
/// 🔴 <b>Why a converter and not a format list.</b> The obvious API is
/// <c>kAudioFormatProperty_DecodeFormatIDs</c>, and on a device it returns OSStatus <c>'prop'</c>
/// (<c>kAudioFormatUnsupportedPropertyError</c>) — measured on an iPhone 17 Pro, iOS 26.5.2. That property
/// is macOS-only. Constructing an <c>AudioConverter</c> is both the portable question and the honest one,
/// because it is exactly what an engine would have to do anyway.
/// </para>
/// <para>
/// <b>What it found, and it is the finding that shapes the transcode tier:</b> AC-3 and E-AC-3 DECODE are
/// present (at 5.1 and at stereo), AAC decodes and encodes — while the AOSP Android emulator had no AC-3 at
/// all. The two platforms genuinely differ, so neither answer may be baked in.
/// </para>
/// <para>
/// ⚠ VIDEO is not probed here and the sets are deliberately EMPTY rather than guessed. The equivalent
/// question is VideoToolbox's, it is asked differently, and an invented set reads as a capability — which
/// is worse than admitting the gap. <see cref="MediaCapabilityExtensions.WithDeviceEncoders"/> therefore
/// reports no video encoder on iOS, which is the safe direction: the planner refuses a video transcode it
/// cannot perform rather than promising one.
/// </para>
/// </summary>
public sealed class MobileMediaCapability : IMediaCapability
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    // FourCCs. 'aac ' really does carry a trailing space — it is kAudioFormatMPEG4AAC.
    private const uint FormatAac = 0x61616320;
    private const uint FormatAc3 = 0x61632D33;
    private const uint FormatEac3 = 0x65632D33;
    private const uint FormatAlac = 0x616C6163;
    private const uint FormatMp3 = 0x2E6D7033;
    private const uint FormatFlac = 0x666C6163;
    private const uint FormatOpus = 0x6F707573;
    private const uint FormatPcm = 0x6C70636D;

    /// <summary>Signed integer + packed — the plain interleaved PCM every codec can produce or accept.</summary>
    private const uint PcmFlags = 0x4 | 0x8;

    /// <summary>The codecs worth asking about: what a real file carries and a browser may refuse.</summary>
    private static readonly (string Name, uint Format)[] Candidates =
    [
        ("aac", FormatAac), ("ac3", FormatAc3), ("eac3", FormatEac3),
        ("alac", FormatAlac), ("mp3", FormatMp3), ("flac", FormatFlac), ("opus", FormatOpus),
    ];

    private readonly Lazy<(HashSet<string> Decode, HashSet<string> Encode)> _audio =
        new(ReadAudio, isThreadSafe: true);

    private static readonly HashSet<string> None = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ VIDEO answers EMPTY rather than guessed — the equivalent question is VideoToolbox's, asked
    /// differently, and an invented set reads as a capability. The planner then refuses a video transcode
    /// rather than promising one, which is the safe direction.
    /// </remarks>
    public IReadOnlySet<string> Decodable(MediaStreamKind kind)
        => kind == MediaStreamKind.Audio ? _audio.Value.Decode : None;

    /// <inheritdoc />
    public IReadOnlySet<string> Encodable(MediaStreamKind kind)
        => kind == MediaStreamKind.Audio ? _audio.Value.Encode : None;

    private static (HashSet<string> Decode, HashSet<string> Encode) ReadAudio()
    {
        var decode = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var encode = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, format) in Candidates)
        {
            // Six channels for the surround formats and two for the rest: a downmix-only decoder would
            // otherwise be reported as absent, which is a different answer from "no decoder".
            var channels = format is FormatAc3 or FormatEac3 ? 6 : 2;
            if (CanConvert(Compressed(format, channels), Pcm(channels))) decode.Add(name);
            if (CanConvert(Pcm(channels), Compressed(format, channels))) encode.Add(name);
        }

        return (decode, encode);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [DllImport(AudioToolbox)]
    private static extern int AudioConverterNew(ref StreamDescription source, ref StreamDescription destination, out IntPtr converter);

    [DllImport(AudioToolbox)]
    private static extern int AudioConverterDispose(IntPtr converter);

    private static StreamDescription Compressed(uint formatId, int channels) => new()
    {
        SampleRate = 48000,
        FormatId = formatId,
        ChannelsPerFrame = (uint)channels,
        FramesPerPacket = formatId == FormatAac ? 1024u : 1536u,
    };

    private static StreamDescription Pcm(int channels) => new()
    {
        SampleRate = 48000,
        FormatId = FormatPcm,
        FormatFlags = PcmFlags,
        ChannelsPerFrame = (uint)channels,
        FramesPerPacket = 1,
        BitsPerChannel = 16,
        BytesPerFrame = (uint)(channels * 2),
        BytesPerPacket = (uint)(channels * 2),
    };

    private static bool CanConvert(StreamDescription source, StreamDescription destination)
    {
        try
        {
            var status = AudioConverterNew(ref source, ref destination, out var converter);
            if (status == 0 && converter != IntPtr.Zero) AudioConverterDispose(converter);
            return status == 0;
        }
        catch (Exception)
        {
            // Reported as "cannot", which the planner reads as a refusal rather than a promise.
            return false;
        }
    }
}
#endif
