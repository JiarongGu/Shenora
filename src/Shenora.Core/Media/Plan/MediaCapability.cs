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
/// <see cref="MediaPlaybackPolicy.VideoCodecs"/>, <see cref="MediaPlaybackPolicy.AudioCodecs"/>. That is
/// the WEBVIEW, and only the page can answer it (<c>canPlayType</c>). It decides Direct vs Remux.</item>
/// <item><b>What a TRANSCODE could read and write</b> — this interface. It decides whether Transcode is
/// possible at all, which is <see cref="MediaPlaybackPolicy.CanEncodeAudio"/> /
/// <see cref="MediaPlaybackPolicy.CanEncodeVideo"/>.</item>
/// </list>
/// <para>
/// A device routinely decodes more than its webview plays — measured 2026-08-07: an iPhone decodes AC-3 via
/// AudioToolbox while no browser will touch it. That gap IS the transcode tier's whole reason to exist, so
/// reporting the platform's set as if it were the player's would erase the very case the layer is for.
/// </para>
///
/// <para>
/// ⚠ <b>Measured per device, never assumed.</b> The same query on an API 36 AOSP emulator and an iPhone 17
/// Pro gave different answers for AC-3 (absent / present), and a handset may differ again from the emulator.
/// An implementation that returns a hardcoded table is worse than none, because it is confidently wrong on
/// the one device that matters.
/// </para>
/// </summary>
public interface IMediaCapability
{
    /// <summary>Audio codec names this device can DECODE, in the planner's lowercase vocabulary (<c>ac3</c>).</summary>
    IReadOnlySet<string> DecodableAudio { get; }

    /// <summary>
    /// Audio codec names this device can ENCODE.
    /// <para>
    /// The cheap half of a transcode and usually the present one: both mobile platforms encode AAC, so a
    /// soundtrack repair needs only a decoder for what the file HAS.
    /// </para>
    /// </summary>
    IReadOnlySet<string> EncodableAudio { get; }

    /// <summary>Video codec names this device can DECODE.</summary>
    IReadOnlySet<string> DecodableVideo { get; }

    /// <summary>
    /// Video codec names this device can ENCODE.
    /// <para>
    /// ⚠ Note what having this at all means for licensing: an LGPL ffmpeg has no H.264 encoder either
    /// (libx264 is GPL), so the platform encoder was always the only licence-clean option (D51/D52).
    /// </para>
    /// </summary>
    IReadOnlySet<string> EncodableVideo { get; }
}

/// <summary>
/// Folding a device's measured capability into the app's playback policy.
/// </summary>
public static class MediaCapabilityExtensions
{
    /// <summary>
    /// Answer <see cref="MediaPlaybackPolicy.CanEncodeAudio"/>/<see cref="MediaPlaybackPolicy.CanEncodeVideo"/>
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

        return policy with
        {
            CanEncodeAudio = device.EncodableAudio.Count > 0,
            CanEncodeVideo = device.EncodableVideo.Count > 0,
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
    public static bool CanRepairAudio(this IMediaCapability device, string codec)
    {
        ArgumentNullException.ThrowIfNull(device);
        return !string.IsNullOrWhiteSpace(codec)
            && device.DecodableAudio.Contains(codec)
            && device.EncodableAudio.Count > 0;
    }
}
