using Shenora.Modules.Media;

namespace Shenora.Android;

/// <summary>
/// Android's <see cref="IMediaCapability"/> — what THIS device's <c>MediaCodecList</c> declares.
///
/// <para>
/// 🔴 <b>Asked at runtime because there is no other honest way.</b> Android codec support is vendor-declared
/// per device, which is exactly why <c>MediaCodecList</c> exists as a query rather than a table. Measured
/// 2026-08-07 on an API 36 AOSP emulator: audio decode was
/// <c>aac flac mp3 opus pcm vorbis</c> plus telephony, with an AAC encoder and <b>no AC-3, E-AC-3 or DTS at
/// all</b> — while an iPhone on the same day decoded AC-3 happily. A handset may differ again from the
/// emulator, so a hardcoded set here would be confidently wrong on the one device that matters.
/// </para>
/// <para>
/// ⚠ <b>This is the PLATFORM's stack, not the webview's</b> — see <see cref="IMediaCapability"/>. A device
/// decoding a codec says nothing about whether the page will play it, and treating the two as one is how an
/// AC-3 file becomes "Direct" and plays silent.
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
        // A kind this device knows nothing about answers EMPTY rather than throwing: "I know of none" is
        // the honest answer and the safe direction for a planner reading it.
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

    /// <summary>
    /// Walk the codec list once and cache it — the set cannot change while the process runs, and the walk
    /// allocates a Java object per codec.
    /// </summary>
    private static Sets Read()
    {
        var sets = new Sets(New(), New(), New(), New());
        try
        {
            // RegularCodecs, not ALL: the wider set includes codecs an app may not instantiate, so counting
            // them would report a capability that fails the moment it is used — the same "advertised but
            // does nothing" shape `ISegmentEngine.HasRenderedPicture` exists for one layer down.
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

                    // 🔴 THE BARE NAME AS WELL AS EVERY PROFILE, and both halves matter.
                    // The bare entry is what keeps a device that reports profiles matching a stream
                    // probed WITHOUT one (MediaStreamCodec's rule: no profile = any profile). The
                    // profiled entries are what let a Main-10-capable device be told apart from one
                    // that only advertises `hevc` — the case where a name alone says "supported" about
                    // a stream that decodes nothing, with no error anywhere.
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
            // A device that will not answer is reported as "knows nothing", which the planner reads as
            // "cannot encode" — the safe direction. No exception text escapes; this is asked on behalf of
            // app logic that may be answering a page.
        }

        return sets;
    }

    private static HashSet<MediaStreamCodec> New() => new();

    /// <summary>
    /// The profile names this codec declares for <paramref name="mime"/>, as the planner spells them.
    /// <para>
    /// ⚠ <b>Android is the ONE platform that can answer this</b>, which is why the profile half of
    /// <see cref="MediaStreamCodec"/> is worth carrying at all: <c>MediaCodecList</c> hands back
    /// <c>ProfileLevels</c> and this used to discard them, so a Main-10 HEVC file on a Main-only
    /// decoder was planned as playable and rendered nothing.
    /// </para>
    /// <para>
    /// Best-effort by construction: an unrecognised profile constant yields NO entry rather than a
    /// guessed name, and the bare-name entry added beside these keeps such a device matching every
    /// stream it did before.
    /// </para>
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
    /// The profile constants worth naming, spelled as a probe reports them. Deliberately SHORT: only
    /// profiles that change whether a stream decodes are listed, because an unknown profile must add
    /// nothing rather than invent vocabulary a policy cannot match.
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
    /// Android MIME types to the lowercase names the planner and every policy speak.
    /// <para>
    /// Translated rather than passed through for the same reason <c>MatroskaProbe</c> translates its
    /// CodecIDs: a policy written against one vocabulary must not have to know three. An unrecognised MIME
    /// is DROPPED rather than guessed — an invented name in a capability set reads as a capability.
    /// </para>
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
