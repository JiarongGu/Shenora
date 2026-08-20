using Shenora.Modules.Media;

namespace Shenora.Android;

/// <summary>
/// Android's <see cref="IMediaCapability"/> — what THIS device's <c>MediaCodecList</c> declares.
/// <para>
/// 🔴 <b>Asked at runtime because there is no other honest way:</b> Android codec support is
/// vendor-declared per device, so a hardcoded set would be confidently wrong on the one device that
/// matters (measured — <c>.claude/knowledge/mobile-shells.md</c>).
/// </para>
/// <para>
/// ⚠ <b>This is the PLATFORM's stack, not the webview's</b> — see <see cref="IMediaCapability"/>. A device
/// decoding a codec says nothing about whether the page will play it, and treating the two as one is how
/// an AC-3 file becomes "Direct" and plays silent.
/// </para>
/// </summary>
public sealed class AndroidMediaCapability : IMediaCapability
{
    private readonly Lazy<Sets> _sets = new(Read, isThreadSafe: true);

    private static readonly HashSet<MediaStreamCodec> None = new();

    /// <inheritdoc />
    public IReadOnlySet<MediaStreamCodec> Decodable(MediaStreamKind kind) => kind switch
    {
        MediaStreamKind.Audio => _sets.Value.DecodableAudio,
        MediaStreamKind.Video => _sets.Value.DecodableVideo,
        // An unknown kind answers EMPTY rather than throwing — the safe direction for a planner.
        _ => None,
    };

    /// <inheritdoc />
    public IReadOnlySet<MediaStreamCodec> Encodable(MediaStreamKind kind) => kind switch
    {
        MediaStreamKind.Audio => _sets.Value.EncodableAudio,
        MediaStreamKind.Video => _sets.Value.EncodableVideo,
        _ => None,
    };

    private sealed record Sets(
        HashSet<MediaStreamCodec> DecodableAudio,
        HashSet<MediaStreamCodec> EncodableAudio,
        HashSet<MediaStreamCodec> DecodableVideo,
        HashSet<MediaStreamCodec> EncodableVideo);

    /// <summary>Walk the codec list once and cache it — the set cannot change while the process runs.</summary>
    private static Sets Read()
    {
        var sets = new Sets(New(), New(), New(), New());
        try
        {
            // RegularCodecs, not ALL: the wider set includes codecs an app may not instantiate, so
            // counting them would report a capability that fails the moment it is used.
            var list = new global::Android.Media.MediaCodecList(global::Android.Media.MediaCodecListKind.RegularCodecs);
            foreach (var codec in list.GetCodecInfos() ?? [])
            {
                foreach (var mime in codec.GetSupportedTypes() ?? [])
                {
                    var name = NameOf(mime);
                    if (name is null) continue;

                    var audio = mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
                    var target = (audio, codec.IsEncoder) switch
                    {
                        (true, true) => sets.EncodableAudio,
                        (true, false) => sets.DecodableAudio,
                        (false, true) => sets.EncodableVideo,
                        _ => sets.DecodableVideo,
                    };

                    // 🔴 THE BARE NAME AS WELL AS EVERY PROFILE, and both halves matter. The bare entry
                    // matches a stream probed WITHOUT one (MediaStreamCodec: no profile = any profile).
                    // The profiled entries tell a Main-10-capable device from one that only advertises
                    // `hevc` — where the name alone says "supported" about a stream that decodes nothing.
                    target.Add(name);
                    foreach (var profile in ProfilesOf(codec, mime))
                    {
                        target.Add(new MediaStreamCodec(name, profile));
                    }
                }
            }
        }
        catch (Exception)
        {
            // A device that will not answer reports "knows nothing", which the planner reads as "cannot"
            // — the safe direction. No exception text escapes; this may be answering a page.
        }

        return sets;
    }

    private static HashSet<MediaStreamCodec> New() => new();

    /// <summary>
    /// The profile names this codec declares for <paramref name="mime"/>, as the planner spells them.
    /// ⚠ Best-effort: an unrecognised profile constant yields NO entry rather than a guessed name, and the
    /// bare-name entry added beside these keeps such a device matching every stream it did before.
    /// </summary>
    private static IEnumerable<string> ProfilesOf(
        global::Android.Media.MediaCodecInfo codec, string mime)
    {
        global::Android.Media.MediaCodecInfo.CodecCapabilities? capabilities;
        try { capabilities = codec.GetCapabilitiesForType(mime); }
        catch (Exception) { yield break; }   // a codec that will not describe itself reports nothing

        foreach (var level in capabilities?.ProfileLevels ?? [])
        {
            // The binding types this as MediaCodecProfileType; the constants are the platform's ints.
            var name = ProfileName(mime, (int)level.Profile);
            if (name is not null) yield return name;
        }
    }

    /// <summary>
    /// The profile constants worth naming, spelled as a probe reports them. ⚠ Only profiles that change
    /// whether a stream decodes — an unknown one must add nothing rather than invent vocabulary.
    /// </summary>
    private static string? ProfileName(string mime, int profile) => mime.ToLowerInvariant() switch
    {
        "video/hevc" => profile switch
        {
            0x01 => "Main",
            0x02 => "Main 10",
            0x1000 => "Main 10 HDR10",
            _ => null,
        },
        "video/avc" => profile switch
        {
            0x01 => "Baseline",
            0x02 => "Main",
            0x08 => "High",
            0x10000 => "High 10",
            _ => null,
        },
        _ => null,
    };

    /// <summary>
    /// Android MIME types to the lowercase names the planner and every policy speak. ⚠ An unrecognised
    /// MIME is DROPPED rather than guessed — an invented name in a capability set reads as a capability.
    /// </summary>
    private static string? NameOf(string mime) => mime.ToLowerInvariant() switch
    {
        "audio/mp4a-latm" => "aac",
        "audio/mpeg" => "mp3",
        "audio/opus" => "opus",
        "audio/vorbis" => "vorbis",
        "audio/flac" => "flac",
        "audio/raw" => "pcm",
        "audio/ac3" => "ac3",
        "audio/eac3" or "audio/eac3-joc" => "eac3",
        "audio/vnd.dts" or "audio/vnd.dts.hd" => "dts",
        "audio/alac" => "alac",
        "audio/amr-wb" => "amrwb",
        "audio/3gpp" => "amrnb",
        "video/avc" => "h264",
        "video/hevc" => "hevc",
        "video/x-vnd.on2.vp8" => "vp8",
        "video/x-vnd.on2.vp9" => "vp9",
        "video/av01" => "av1",
        "video/mp4v-es" => "mpeg4",
        "video/mpeg2" => "mpeg2video",
        _ => null,
    };
}
