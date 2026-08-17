using CoreMedia;
using Shenora.Modules.Media;
using VideoToolbox;

using System.Runtime.InteropServices;

namespace Shenora.iOS;

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
/// 🔴 <b>VIDEO IS PROBED, not left empty.</b> An empty set is honest about the gap and still wrong in
/// effect: with no device answer for pictures, the only way to ask "is this convertible" is to build the
/// converter's own decoder and encoder on EVERY query, which fuses what the KIT claims with what the
/// DEVICE can do — producing an over-claim (a promise from the encoder alone) and an under-claim (a
/// refusal for a codec that only lacked its ESDS). Asked once and cached, a session is what answers —
/// see <c>ReadVideo</c> for why nothing cheaper is honest.
/// <para>
/// ⚠ <b>That honesty used to be this platform's alone, and it was an ACCIDENT of the empty set rather than
/// a rule.</b> Android reported real video encoders from <c>MediaCodecList</c> and so promised a transcode
/// the kit has no engine for. Since 2026-08-09 <c>WithDeviceEncoders</c> intersects the device's answer
/// with what the app can actually convert, so both shells refuse it for the same stated reason — see D63's
/// fourth instance.
/// </para>
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
    /// ⚠ <c>h264</c> and <c>hevc</c> are here even though the CONVERTER never offers them: this answers
    /// what the DEVICE can do, and a page that already plays H.264 is exactly what makes the remuxer's copy
    /// path correct. Conflating the two questions is what this type exists to prevent.
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
    /// <remarks>
    /// 🔴 <b>VIDEO IS PROBED, and an empty set here is load-bearing in the wrong direction.</b> Answering
    /// EMPTY "rather than guessing" is honest about the gap and still wrong in effect: with no device
    /// answer for pictures, the only way to learn whether a codec is convertible is to CONSTRUCT the
    /// converter's own sessions on every ask, fusing "the kit claims it" with "the device can do it" and
    /// producing both an over-claim and an under-claim.
    /// See <see cref="ReadVideo"/> for why a session is still what answers, and why once is enough.
    /// </remarks>
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

    /// <summary>
    /// What this device can DECODE and ENCODE for pictures, asked ONCE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Asked by CONSTRUCTING a session, because VideoToolbox has no cheaper honest answer.</b>
    /// <c>VTIsHardwareDecodeSupported</c> exists but reports only the HARDWARE path, and software decoders
    /// are real — a codec this device decodes in software would be reported as unsupported, which is the
    /// "taking what IS supported as unsupported" mistake this file's own header warns about. Creating a
    /// session is the same test the converter applies, so the two cannot disagree.
    /// </para>
    /// <para>
    /// ⚠ <b>Once, cached, and that is why this is a <see cref="Lazy{T}"/> rather than a call per query.</b>
    /// Each probe costs a real codec instance and a device has only a handful; the previous design answered
    /// "can you convert X?" by building two of them on EVERY ask. An app that never plays anything pays
    /// nothing, because nothing touches this until something asks.
    /// </para>
    /// <para>
    /// ⚠ <b>ENCODE is asked only for what the kit would ever encode TO</b> — H.264 today. Probing an encoder
    /// for MPEG-4 Part 2 would answer a question nobody has: the conversion target is decided by what a
    /// webview plays, not by what the device could emit.
    /// </para>
    /// <para>
    /// ⚠ 640x360 is arbitrary, valid, and never encodes anything — the same stand-in
    /// <c>MediaConversionPipeline.CanConvert</c> uses, and for the same reason: a video codec refuses to
    /// configure at 0x0.
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
                // A throwing probe is a "no" like any other: this method's whole contract is to report what
                // works, and a platform that objects to being asked has answered.
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
