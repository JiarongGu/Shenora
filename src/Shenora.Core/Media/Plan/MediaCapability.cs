namespace Shenora.Media;

/// <summary>
/// What THIS DEVICE's own media stack can decode and encode, asked at runtime.
///
/// <para>
/// <b>Why this exists.</b> <see cref="MediaPlaybackPolicy"/> is the app's, and the kit ships no codec list
/// (D42) — for the good reason that there is no correct universal one. But "the app's" has meant "the app
/// GUESSES", because the sets it must fill are properties of the hardware in hand: Android's codec support
/// is vendor-declared per device, which is exactly why <c>MediaCodecList</c> is a runtime query rather than
/// a table. This is the kit shipping the QUESTION, not the answer.
/// </para>
///
/// <para>
/// 🔴 <b>This is the PLATFORM's codec stack, which is NOT what the webview will play, and conflating the two
/// is the mistake this doc exists to prevent.</b> They are different sets and they answer different
/// questions:
/// </para>
/// <list type="bullet">
/// <item><b>What the PLAYER can open</b> — <see cref="MediaPlaybackPolicy.Containers"/>,
/// <see cref="MediaPlaybackPolicy.Codecs"/>. That is
/// the WEBVIEW, and only the page can answer it (<c>canPlayType</c>). It decides Direct vs Remux.</item>
/// <item><b>What a TRANSCODE could read and write</b> — this interface. It decides whether Transcode is
/// possible at all.</item>
/// </list>
/// <para>
/// A device routinely decodes more than its webview plays — measured 2026-08-07: an iPhone decodes AC-3 via
/// AudioToolbox while no browser will touch it. That gap IS the transcode tier's whole reason to exist, so
/// reporting the platform's set as if it were the player's would erase the very case the layer is for.
/// </para>
///
/// <para>
/// ⚠ <b>Keyed by <see cref="MediaStreamKind"/> rather than four fixed properties</b>, so a kind the kit
/// does not act on today needs no new member — the first shape hardcoded audio/video as a cross-product and
/// would have needed a rename to grow. Implementations answer an unknown kind with an empty set rather than
/// throwing: "I know of nothing" is the honest answer and the safe direction.
/// </para>
/// </summary>
public interface IMediaCapability
{
    /// <summary>
    /// Codec names this device can DECODE for <paramref name="kind"/>, in the planner's lowercase
    /// vocabulary (<c>ac3</c>, <c>h264</c>). Empty when the device knows of none.
    /// </summary>
    IReadOnlySet<string> Decodable(MediaStreamKind kind);

    /// <summary>
    /// Codec names this device can ENCODE for <paramref name="kind"/>.
    /// <para>
    /// For audio this is usually the present half — both mobile platforms encode AAC — so a soundtrack
    /// repair needs only a decoder for what the file HAS. ⚠ And note what having a video encoder at all
    /// means for licensing: an LGPL ffmpeg has no H.264 encoder either (libx264 is GPL), so the platform
    /// encoder was always the only licence-clean option (D51/D52).
    /// </para>
    /// </summary>
    IReadOnlySet<string> Encodable(MediaStreamKind kind);
}

/// <summary>
/// Folding a device's measured capability into the app's playback policy.
/// </summary>
public static class MediaCapabilityExtensions
{
    /// <summary>Audio codecs this device can decode. Shorthand for the common question.</summary>
    public static IReadOnlySet<string> DecodableAudio(this IMediaCapability device)
        => Ask(device).Decodable(MediaStreamKind.Audio);

    /// <summary>Audio codecs this device can encode.</summary>
    public static IReadOnlySet<string> EncodableAudio(this IMediaCapability device)
        => Ask(device).Encodable(MediaStreamKind.Audio);

    /// <summary>Video codecs this device can decode.</summary>
    public static IReadOnlySet<string> DecodableVideo(this IMediaCapability device)
        => Ask(device).Decodable(MediaStreamKind.Video);

    /// <summary>Video codecs this device can encode.</summary>
    public static IReadOnlySet<string> EncodableVideo(this IMediaCapability device)
        => Ask(device).Encodable(MediaStreamKind.Video);

    private static IMediaCapability Ask(IMediaCapability device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device;
    }

    /// <summary>
    /// Answer <see cref="MediaPlaybackPolicy.Encodable"/>
    /// from what the DEVICE can actually do, leaving every other field alone.
    ///
    /// <para>
    /// 🔴 <b>It touches only the two encode flags, deliberately.</b> The container and codec SETS describe
    /// what the PLAYER opens — the webview — and this object knows nothing about that (see
    /// <see cref="IMediaCapability"/>). Overwriting them from the platform's list would tell the planner a
    /// device that DECODES AC-3 can PLAY it, which is exactly false and would turn every AC-3 file from
    /// "Transcode" into "Direct" — a file that serves perfectly and plays silent.
    /// </para>
    /// <para>
    /// ⚠ A transcode also needs somewhere to put the result, so encode capability is what the flags mean.
    /// Whether the DECODER for a given file exists is per-file and stays the planner's per-stream question.
    /// </para>
    /// </summary>
    public static MediaPlaybackPolicy WithDeviceEncoders(this MediaPlaybackPolicy policy, IMediaCapability device)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(device);

        // ⚠ Every kind, not the two that exist today. Keyed on both sides means a kind added later is
        // picked up here with no edit — which is the point of keying rather than naming.
        return policy with
        {
            Encodable = new HashSet<MediaStreamKind>(
                Enum.GetValues<MediaStreamKind>().Where(kind => device.Encodable(kind).Count > 0)),
        };
    }

    /// <summary>
    /// Could this device repair <paramref name="codec"/> — decode it, and encode something to replace it?
    /// <para>
    /// Both halves are required and asking only one is the common mistake: a device that decodes AC-3 but
    /// can encode nothing cannot repair a soundtrack, and a device that encodes AAC but cannot decode AC-3
    /// cannot either. The answer is per CODEC, which is why it is a method and not a flag.
    /// </para>
    /// </summary>
    public static bool CanRepair(this IMediaCapability device, MediaStreamKind kind, string codec)
    {
        ArgumentNullException.ThrowIfNull(device);
        return !string.IsNullOrWhiteSpace(codec)
            && device.Decodable(kind).Contains(codec)
            && device.Encodable(kind).Count > 0;
    }

    /// <summary>Shorthand for the audio case, which is the one that actually turns up.</summary>
    public static bool CanRepairAudio(this IMediaCapability device, string codec)
        => device.CanRepair(MediaStreamKind.Audio, codec);
}
