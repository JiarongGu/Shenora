#if ANDROID
using Shenora.Media;

namespace Shenora.Mobile;

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
public sealed class MobileMediaCapability : IMediaCapability
{
    private readonly Lazy<Sets> _sets = new(Read, isThreadSafe: true);

    /// <inheritdoc />
    public IReadOnlySet<string> DecodableAudio => _sets.Value.DecodableAudio;

    /// <inheritdoc />
    public IReadOnlySet<string> EncodableAudio => _sets.Value.EncodableAudio;

    /// <inheritdoc />
    public IReadOnlySet<string> DecodableVideo => _sets.Value.DecodableVideo;

    /// <inheritdoc />
    public IReadOnlySet<string> EncodableVideo => _sets.Value.EncodableVideo;

    private sealed record Sets(
        HashSet<string> DecodableAudio,
        HashSet<string> EncodableAudio,
        HashSet<string> DecodableVideo,
        HashSet<string> EncodableVideo);

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
            var list = new Android.Media.MediaCodecList(Android.Media.MediaCodecListKind.RegularCodecs);
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
                    target.Add(name);
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

    private static HashSet<string> New() => new(StringComparer.OrdinalIgnoreCase);

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
#endif
