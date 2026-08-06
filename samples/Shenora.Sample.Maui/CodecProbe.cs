#if IOS
using System.Runtime.InteropServices;
#endif

namespace Shenora.Sample.Maui;

/// <summary>
/// Asks the PLATFORM what audio it can decode and encode — the measurement D52's slice 3 turns on.
///
/// <para>
/// <b>The question, and why it is a measurement and not a design.</b> `Shenora.Media` can already repair a
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
        var list = new Android.Media.MediaCodecList(Android.Media.MediaCodecListKind.RegularCodecs);
        foreach (var codec in list.GetCodecInfos() ?? [])
        {
            foreach (var type in codec.GetSupportedTypes() ?? [])
            {
                if (!type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) continue;
                (codec.IsEncoder ? encoders : decoders).Add(type);
            }
        }

        log($"[CODEC] platform=Android api={(int)Android.OS.Build.VERSION.SdkInt} "
            + $"device={Android.OS.Build.Manufacturer}/{Android.OS.Build.Model}");
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
    // AudioToolbox answers both questions directly, which is why no AVFoundation session has to be built
    // just to ask. The property ids are FourCCs: 'acdi' decode, 'acei' encode.
    private const uint DecodeFormatIds = 0x61636469;
    private const uint EncodeFormatIds = 0x61636569;

    [DllImport("/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox")]
    private static extern int AudioFormatGetPropertyInfo(uint propertyId, uint specifierSize, IntPtr specifier, out uint dataSize);

    [DllImport("/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox")]
    private static extern int AudioFormatGetProperty(uint propertyId, uint specifierSize, IntPtr specifier, ref uint dataSize, [Out] uint[] data);

    private static SortedSet<string> FormatIds(uint propertyId)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        if (AudioFormatGetPropertyInfo(propertyId, 0, IntPtr.Zero, out var size) != 0 || size == 0) return found;

        var values = new uint[size / sizeof(uint)];
        if (AudioFormatGetProperty(propertyId, 0, IntPtr.Zero, ref size, values) != 0) return found;

        foreach (var value in values) found.Add(FourCc(value));
        return found;
    }

    /// <summary>A FourCC is four ASCII bytes packed big-endian — 'ac-3' and friends.</summary>
    private static string FourCc(uint value) => new(
    [
        (char)((value >> 24) & 0xFF), (char)((value >> 16) & 0xFF),
        (char)((value >> 8) & 0xFF), (char)(value & 0xFF),
    ]);

    private static void RunCore(Action<string> log)
    {
        var decoders = FormatIds(DecodeFormatIds);
        var encoders = FormatIds(EncodeFormatIds);

        log($"[CODEC] platform=iOS {UIKit.UIDevice.CurrentDevice.SystemVersion} "
            + $"model={UIKit.UIDevice.CurrentDevice.Model} sim={(Runtime.Arch == Arch.SIMULATOR ? "yes" : "no")}");
        log($"[CODEC] decode: {string.Join(' ', decoders)}");
        log($"[CODEC] encode: {string.Join(' ', encoders)}");

        // 'ac-3' is AC-3; 'ec-3' is Enhanced AC-3. 'cac3' is AC-3 carried over S/PDIF, which is a TRANSPORT
        // and not a decoder — counting it would report a decode capability that does not exist.
        Verdict(log, "ac3", decoders.Contains("ac-3"), encoders.Contains("ac-3"));
        Verdict(log, "eac3", decoders.Contains("ec-3"), encoders.Contains("ec-3"));
        Verdict(log, "aac", decoders.Contains("aac "), encoders.Contains("aac "));
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
}
