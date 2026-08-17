using Shenora.Modules.Media;

#if IOS
using System.Runtime.InteropServices;
#endif
// For MediaCapabilityExtensions. An extension method needs the NAMESPACE imported — naming the interface
// fully-qualified below is not enough, which is what the first build of this file discovered.

namespace Shenora.Sample.Maui;

/// <summary>
/// Asks the PLATFORM what audio it can decode and encode — the measurement D52's slice 3 turns on.
///
/// <para>
/// <b>The question, and why it is a measurement and not a design.</b> <c>Shenora.Modules.Media</c> can already repair a
/// container for nothing (<c>Mp4Remuxer</c> — no decoding at all). What it cannot yet repair is the
/// SOUNDTRACK: AC-3, E-AC-3 and DTS are routine inside an <c>.mkv</c> and play in no browser. Fixing that
/// means decoding one stream and re-encoding it as AAC, and the whole shape of that work depends on one
/// fact nobody had measured — <b>does the platform itself decode AC-3?</b> If it does, the transcode is two
/// platform calls and costs zero bytes and zero licence weight (D51/D52 tier 2). If it does not, it is a
/// clean-room decoder from the freely published ATSC A/52 spec, which is a real project.
/// </para>
///
/// <para>
/// ⚠ <b>ENCODE is measured too, and it is the half that is easy to forget.</b> A transcode needs a decoder
/// for what the file has AND an encoder for what the web accepts. Note what that means for the licence
/// question: an LGPL ffmpeg has no H.264 encoder either (libx264 is GPL), so the platform encoder was
/// always the only licence-clean option — dropping ffmpeg costs nothing on the encode side.
/// </para>
///
/// <para>
/// 🔴 <b>What this can and cannot conclude.</b> On Android codec support is VENDOR-DECLARED per device —
/// <c>MediaCodecList</c> is a runtime query for exactly that reason — so an emulator answers for AOSP and a
/// handset may answer differently. A result here is evidence about THE DEVICE IT RAN ON, and the log says
/// so rather than letting one run stand in for a platform.
/// </para>
/// </summary>
internal static class CodecProbe
{
    /// <summary>The three that actually stop an ordinary file playing, named so a run can be grepped.</summary>
    private static readonly string[] Interesting = ["ac3", "eac3", "dts"];

    /// <summary>
    /// Report what this device decodes and encodes. Never throws — a probe that takes the sample down
    /// teaches nothing, and this one runs on a device where a crash costs a whole deploy cycle.
    /// </summary>
    public static void Run(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            RunCore(log);
        }
        catch (Exception ex)
        {
            log($"[CODEC] probe failed: {ex.GetType().Name}");
        }
    }

#if ANDROID
    private static void RunCore(Action<string> log)
    {
        var decoders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var encoders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        // RegularCodecs is the set an app may actually use — it excludes the vendor-hidden ones a
        // capability check would otherwise count and then fail to instantiate.
        var list = new global::Android.Media.MediaCodecList(global::Android.Media.MediaCodecListKind.RegularCodecs);
        foreach (var codec in list.GetCodecInfos() ?? [])
        {
            foreach (var type in codec.GetSupportedTypes() ?? [])
            {
                if (!type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) continue;
                (codec.IsEncoder ? encoders : decoders).Add(type);
            }
        }

        // ⚠ PARENTHESISED, and they are load-bearing. Inside an interpolation hole a `:` starts the FORMAT
        // specifier, so `{global::Android.OS.Build.Model}` parses as the expression `global` with the format
        // string `:Android.OS.Build.Model` — CS0103, "the name 'global' does not exist". Balanced parens
        // make the parser read the whole thing as one expression.
        log($"[CODEC] platform=Android api={((int)global::Android.OS.Build.VERSION.SdkInt)} "
            + $"device={(global::Android.OS.Build.Manufacturer)}/{(global::Android.OS.Build.Model)}");
        log($"[CODEC] decode: {string.Join(' ', decoders)}");
        log($"[CODEC] encode: {string.Join(' ', encoders)}");

        // The MIME spellings Android uses for the three. `audio/vnd.dts` covers plain DTS; the HD variants
        // add suffixes and are deliberately not chased — if plain DTS is absent, HD certainly is.
        Verdict(log, "ac3", decoders.Contains("audio/ac3"), encoders.Contains("audio/ac3"));
        Verdict(log, "eac3", decoders.Contains("audio/eac3"), encoders.Contains("audio/eac3"));
        Verdict(log, "dts", decoders.Contains("audio/vnd.dts"), encoders.Contains("audio/vnd.dts"));
        Verdict(log, "aac", decoders.Contains("audio/mp4a-latm"), encoders.Contains("audio/mp4a-latm"));
    }
#elif IOS
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    // FourCCs. 'aac ' really does carry a trailing space — it is kAudioFormatMPEG4AAC.
    private const uint FormatAc3 = 0x61632D33;      // 'ac-3'
    private const uint FormatEac3 = 0x65632D33;     // 'ec-3'
    private const uint FormatAac = 0x61616320;      // 'aac '
    private const uint FormatPcm = 0x6C70636D;      // 'lpcm'

    /// <summary>Signed integer + packed — the plain interleaved PCM every decoder can produce.</summary>
    private const uint PcmFlags = 0x4 | 0x8;

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

    /// <summary>
    /// Can this device build a converter between these two formats? Constructing one is the DIRECT test —
    /// AudioToolbox only succeeds when a codec for the pair actually exists on the machine.
    /// <para>
    /// ⚠ <b>Why not the format-id list.</b> The obvious API is
    /// <c>kAudioFormatProperty_DecodeFormatIDs</c>, and on the device it returns OSStatus <c>'prop'</c>
    /// (<c>kAudioFormatUnsupportedPropertyError</c>) — measured on an iPhone 17 Pro, iOS 26.5.2. That
    /// property is macOS-only. Asking the converter is both the portable question and the honest one,
    /// because it is exactly what an engine would have to do anyway.
    /// </para>
    /// </summary>
    private static (bool Ok, int Status) CanConvert(StreamDescription source, StreamDescription destination)
    {
        var status = AudioConverterNew(ref source, ref destination, out var converter);
        if (status == 0 && converter != IntPtr.Zero) AudioConverterDispose(converter);
        return (status == 0, status);
    }

    private static void RunCore(Action<string> log)
    {
        // MAUI's own device API rather than ObjCRuntime.Runtime.Arch — it reads the same on both platforms
        // and does not depend on a binding that has moved between iOS workload bands.
        var virtualDevice = DeviceInfo.Current.DeviceType == DeviceType.Virtual;
        log($"[CODEC] platform=iOS {UIKit.UIDevice.CurrentDevice.SystemVersion} "
            + $"model={DeviceInfo.Current.Model} sim={(virtualDevice ? "yes" : "no")}");

        // 🔴 AAC is asked FIRST and is a CONTROL, not a result. iOS decodes AAC everywhere, so a "no" here
        // means the probe is broken and every other line is worthless. The first version of this file had
        // no control and reported `aac: decode=no` from an iPhone — which is how a broken measurement gets
        // mistaken for a finding.
        var (aacDecode, _) = CanConvert(Compressed(FormatAac, 2), Pcm(2));
        var (aacEncode, _) = CanConvert(Pcm(2), Compressed(FormatAac, 2));
        Verdict(log, "aac", aacDecode, aacEncode);
        if (!aacDecode)
        {
            log("[CODEC] ⚠ INCONCLUSIVE — the AAC control FAILED, which cannot be true on iOS. "
                + "Treat every line here as unmeasured, not as a negative.");
            return;
        }

        // AC-3 is 5.1 in practice, and channel count is part of what a codec is asked to support — so it is
        // asked for six, which is the case that actually turns up inside an .mkv.
        var (ac3Decode, ac3Status) = CanConvert(Compressed(FormatAc3, 6), Pcm(6));
        var (eac3Decode, eac3Status) = CanConvert(Compressed(FormatEac3, 6), Pcm(6));
        Verdict(log, "ac3", ac3Decode, CanConvert(Pcm(6), Compressed(FormatAc3, 6)).Ok);
        Verdict(log, "eac3", eac3Decode, CanConvert(Pcm(6), Compressed(FormatEac3, 6)).Ok);
        log($"[CODEC] status ac3={ac3Status} eac3={eac3Status} (0 = a converter was built)");

        // Stereo too: a device may carry a downmixing decoder and refuse 5.1, which would change the answer
        // from "no decoder" to "no 5.1 decoder" — a completely different design conclusion.
        var (ac3Stereo, _) = CanConvert(Compressed(FormatAc3, 2), Pcm(2));
        log($"[CODEC] ac3 stereo: decode={(ac3Stereo ? "YES" : "no")}");
    }
#else
    private static void RunCore(Action<string> log)
        => log("[CODEC] no platform codec query on this target");
#endif

    /// <summary>One line per codec, in the shape the harness greps for.</summary>
    private static void Verdict(Action<string> log, string codec, bool decode, bool encode)
        => log($"[CODEC] {codec}: decode={(decode ? "YES" : "no")} encode={(encode ? "YES" : "no")}");

    /// <summary>What the whole probe is for, in one greppable line.</summary>
    public static string Question => $"[CODEC] the question: can this device DECODE {string.Join('/', Interesting)}?";

    /// <summary>
    /// Cross-check the KIT's <see cref="Shenora.Modules.Media.IMediaCapability"/> against what this probe asked the
    /// platform directly.
    ///
    /// <para>
    /// 🔴 <b>Two independent routes to the same fact, which is the only reason this is worth running.</b>
    /// The probe above queries `MediaCodecList`/AudioToolbox itself; the kit's implementation does its own
    /// query behind a portable contract. If they agree, the contract is carrying the truth. If they
    /// disagree, one of them is inventing — and a capability set that is confidently wrong is worse than
    /// none, because the planner will act on it.
    /// </para>
    /// <para>
    /// ⚠ This is NOT the "a sample answering a question a second way cannot detect the kit stopped
    /// answering it" trap: there the page had a FALLBACK that masked the kit's silence. Here neither route
    /// feeds the other, and disagreement is the whole signal.
    /// </para>
    /// </summary>
    public static void CrossCheck(Shenora.Modules.Media.IMediaCapability device,
                                  Shenora.Modules.Media.IMediaStreamConversion? conversion, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            log($"[CODEC] kit decode audio: {string.Join(' ', device.DecodableAudio().OrderBy(c => c.ToString(), StringComparer.Ordinal))}");
            log($"[CODEC] kit encode audio: {string.Join(' ', device.EncodableAudio().OrderBy(c => c.ToString(), StringComparer.Ordinal))}");
            log($"[CODEC] kit decode video: {string.Join(' ', device.DecodableVideo().OrderBy(c => c.ToString(), StringComparer.Ordinal))}");
            log($"[CODEC] kit encode video: {string.Join(' ', device.EncodableVideo().OrderBy(c => c.ToString(), StringComparer.Ordinal))}");

            // AAC is the control here for the same reason it is one above: every target decodes it, so a
            // "no" means the contract is broken rather than that the device lacks it.
            if (!device.DecodableAudio().Contains("aac"))
            {
                log("[CODEC] ⚠ CROSS-CHECK INCONCLUSIVE — the kit reports no AAC decoder, which cannot be "
                    + "true on any target. Treat the sets above as unmeasured.");
                return;
            }

            log($"[CODEC] kit says ac3 repairable={device.CanRepairAudio("ac3")} "
                + $"eac3 repairable={device.CanRepairAudio("eac3")}");

            // The TRANSCODE tier's own answer, which must agree with the capability above: a device that
            // can repair a codec must also accept converting it, and one that cannot must refuse. A
            // disagreement here means the planner would promise work the engine then declines.
            if (conversion is not null)
            {
                // ⚠ `aac` and `alac` are in this list ON PURPOSE — they are the cases that prove the check
                // below is not crying wolf. See the direction rule in the loop.
                foreach (var codec in new[] { "ac3", "eac3", "dts", "vorbis", "mp3", "flac", "alac", "aac" })
                {
                    var accepts = conversion.CanConvert(MediaStreamKind.Audio, codec);
                    var repairable = device.CanRepairAudio(codec);

                    // 🔴 ONE DIRECTION IS A DEFECT AND THE OTHER IS THE DESIGN, and asserting equality
                    // here was wrong — it fired on iOS for `flac` and would have fired for `aac` too.
                    //
                    // accepts && !repairable  → BROKEN. The converter claims a codec the DEVICE cannot
                    //   decode, so the planner would start work that cannot finish.
                    // !accepts && repairable  → EXPECTED. The device could, and the kit's converter
                    //   declines anyway: either it never needs to (`aac` is excluded deliberately —
                    //   MP4 carries it already, so converting is a lossy round-trip for nothing) or it
                    //   simply implements no input for that codec (`flac`, `alac` on iOS today).
                    //   `IMediaCapability` answers about the DEVICE; `CanConvert` answers about the KIT.
                    //   They are different questions and the kit's answer is deliberately the narrower.
                    var verdict = accepts && !repairable
                        ? "  ⚠ BROKEN — the converter accepts a codec this device cannot decode"
                        : "";
                    log($"[CODEC] convert {codec}: accepted={accepts} repairable={repairable}{verdict}");
                }

                // 🔴 THE PICTURE HALF, AND ITS ABSENCE IS WHY NOBODY NOTICED iOS HAD NO VIDEO CONVERTER.
                // This loop asked about soundtracks only, so the seam's answer for a PICTURE was never
                // printed on any shell — while `kit decode video:` above reports the DEVICE's view and looks
                // like it covers the question. It does not: `IMediaCapability` answers about the device,
                // `CanConvert` answers about the KIT, and only the second one decides whether a track is
                // converted or dropped.
                //
                // ⚠ `h264` and `hevc` are here to prove the check is not crying wolf: both SHOULD answer
                // false, because MP4 carries them and the remuxer copies them losslessly. A true there
                // would mean the kit re-encodes a film it could have remuxed.
                foreach (var codec in new[] { "mpeg4", "mpeg2video", "h263", "h264", "hevc" })
                {
                    log($"[CODEC] convert video {codec}: accepted={conversion.CanConvert(MediaStreamKind.Video, codec)}");
                }
            }
            else
            {
                log("[CODEC] no IMediaStreamConversion registered on this shell — container repair only");
            }
            log("[CODEC] CROSS-CHECK: compare the four 'kit' lines against the platform lines above — "
                + "they are independent queries and must agree.");
        }
        catch (Exception ex)
        {
            log($"[CODEC] cross-check failed: {ex.GetType().Name}");
        }
    }
}
