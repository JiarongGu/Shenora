using CoreMedia;
using Shenora.Modules.Media;
using VideoToolbox;

using System.Runtime.InteropServices;

namespace Shenora.iOS;

/// <summary>
/// iOS's <see cref="IMediaCapability"/> — answered by asking the platform to BUILD a codec, which succeeds
/// only when one for the pair actually exists on the device.
/// <para>
/// 🔴 <b>A converter, never a format list.</b> <c>kAudioFormatProperty_DecodeFormatIDs</c> is macOS-only and
/// answers OSStatus <c>'prop'</c> (<c>kAudioFormatUnsupportedPropertyError</c>) on a device; constructing an
/// <c>AudioConverter</c> is what an engine has to do anyway. Pictures are asked the same way — see
/// <see cref="ReadVideo"/>.
/// </para>
/// <para>
/// ⚠ The answer differs per PLATFORM and per DEVICE — this one decodes AC-3 and E-AC-3 where the AOSP
/// Android emulator has neither — so neither may be baked in. Tables: <c>docs/design/mobile-shells.md</c>.
/// </para>
/// </summary>
public sealed class IosMediaCapability : IMediaCapability
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

    private readonly Lazy<(HashSet<MediaStreamCodec> Decode, HashSet<MediaStreamCodec> Encode)> _audio =
        new(ReadAudio, isThreadSafe: true);

    /// <summary>
    /// The picture codecs worth asking about, as VideoToolbox's own four-character codes.
    /// <para>
    /// ⚠ <c>h264</c> and <c>hevc</c> are here even though the CONVERTER never offers them: this answers what
    /// the DEVICE can do, and a page that already plays H.264 is what makes the remuxer's copy path correct.
    /// </para>
    /// </summary>
    private static readonly (string Name, CMVideoCodecType Type)[] VideoCandidates =
    [
        ("h264", CMVideoCodecType.H264), ("hevc", CMVideoCodecType.Hevc),
        ("mpeg4", CMVideoCodecType.Mpeg4Video), ("mpeg2video", CMVideoCodecType.Mpeg2Video),
        ("h263", CMVideoCodecType.H263),
    ];

    private readonly Lazy<(HashSet<MediaStreamCodec> Decode, HashSet<MediaStreamCodec> Encode)> _video =
        new(ReadVideo, isThreadSafe: true);

    private static readonly HashSet<MediaStreamCodec> None = new();

    /// <inheritdoc />
    public IReadOnlySet<MediaStreamCodec> Decodable(MediaStreamKind kind) => kind switch
    {
        MediaStreamKind.Audio => _audio.Value.Decode,
        MediaStreamKind.Video => _video.Value.Decode,
        _ => None,
    };

    /// <inheritdoc />
    public IReadOnlySet<MediaStreamCodec> Encodable(MediaStreamKind kind) => kind switch
    {
        MediaStreamKind.Audio => _audio.Value.Encode,
        MediaStreamKind.Video => _video.Value.Encode,
        _ => None,
    };

    /// <summary>What this device can DECODE and ENCODE for pictures, asked ONCE.</summary>
    /// <remarks>
    /// 🔴 <b>Asked by CONSTRUCTING a session, because VideoToolbox has no cheaper honest answer.</b>
    /// <c>VTIsHardwareDecodeSupported</c> reports only the HARDWARE path, so a codec this device decodes in
    /// SOFTWARE would come back unsupported. Creating a session is the same test the converter applies, so
    /// the two cannot disagree.
    /// <para>
    /// ⚠ <b>Once, cached — hence the <see cref="Lazy{T}"/>.</b> Each probe costs a real codec instance and a
    /// device has only a handful; an earlier design built two of them on EVERY ask. Nothing touches this
    /// until something asks.
    /// </para>
    /// <para>
    /// ⚠ ENCODE is asked only for what the kit would ever encode TO — H.264 today. 640x360 is arbitrary,
    /// valid, and never encodes anything: a video codec refuses to configure at 0x0.
    /// </para>
    /// </remarks>
    private static (HashSet<MediaStreamCodec> Decode, HashSet<MediaStreamCodec> Encode) ReadVideo()
    {
        var decode = new HashSet<MediaStreamCodec>();
        var encode = new HashSet<MediaStreamCodec>();

        foreach (var (name, type) in VideoCandidates)
        {
            try
            {
                using var format = new CMVideoFormatDescription(type, new CMVideoDimensions(640, 360));
                using var session = VTDecompressionSession.Create((_, _, _, _, _, _) => { }, format);
                if (session is not null) decode.Add(name);
            }
            catch (Exception)
            {
                // A throwing probe is a "no" like any other — a platform that objects has answered.
            }

            // The kit only ever encodes TO H.264 — see the remarks.
            if (type is not CMVideoCodecType.H264) continue;
            try
            {
                using var encoder = VTCompressionSession.Create(640, 360, type, (_, _, _, _) => { });
                if (encoder is not null) encode.Add(name);
            }
            catch (Exception)
            {
            }
        }

        return (decode, encode);
    }

    private static (HashSet<MediaStreamCodec> Decode, HashSet<MediaStreamCodec> Encode) ReadAudio()
    {
        var decode = new HashSet<MediaStreamCodec>();
        var encode = new HashSet<MediaStreamCodec>();

        foreach (var (name, format) in Candidates)
        {
            // ⚠ Six channels for the surround formats, two for the rest: asked only at 5.1, a downmix-only
            // decoder reports as absent, which is a different answer from "no decoder".
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
