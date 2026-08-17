namespace Shenora.Modules.Media;


/// <summary>
/// What THIS DEVICE's own media stack can decode and encode, asked at runtime — the kit ships no codec
/// list (D42) because the answer is a property of the hardware in hand.
/// <para>
/// 🔴 <b>This is the PLATFORM's codec stack, NOT what the webview will play.</b> Different sets, and
/// asking the wrong one ships a silent bug: what the PLAYER opens is
/// <see cref="MediaPlaybackPolicy.Containers"/> and <see cref="MediaPlaybackPolicy.Codecs"/>, answered
/// only by the page (<c>canPlayType</c>) and deciding Direct vs Remux; this interface is what a TRANSCODE
/// could read and write, deciding whether Transcode is possible at all. A device routinely decodes more
/// than its webview plays — an iPhone decodes AC-3 via AudioToolbox while no browser will touch it — and
/// that gap is what the transcode tier exists for.
/// </para>
/// <para>
/// Keyed by <see cref="MediaStreamKind"/>, so a kind the kit does not act on today needs no new member.
/// An unknown kind answers with an empty set rather than throwing.
/// </para>
/// </summary>
public interface IMediaCapability
{
    /// <summary>
    /// Codec names this device can DECODE for <paramref name="kind"/>, in the planner's lowercase
    /// vocabulary (<c>ac3</c>, <c>h264</c>). Empty when the device knows of none.
    /// <para>
    /// ⚠ <b>Build the set with <see cref="StringComparer.OrdinalIgnoreCase"/>.</b> Callers ask with a
    /// codec name as a CONTAINER spelled it, so an ordinal set here makes <c>CanConvert</c> answer NO for
    /// a codec the kit claims and the device handles. This is a seam an app may implement, and a plain
    /// <c>HashSet&lt;string&gt;</c> is the wrong default.
    /// </para>
    /// </summary>
    IReadOnlySet<MediaStreamCodec> Decodable(MediaStreamKind kind);

    /// <summary>
    /// Codec names this device can ENCODE for <paramref name="kind"/>. For audio this is usually the
    /// present half — both mobile platforms encode AAC — so a soundtrack repair needs only a decoder for
    /// what the file HAS. The platform encoder is also the only licence-clean video encoder: an LGPL
    /// ffmpeg has none either, libx264 being GPL (D51/D52).
    /// </summary>
    IReadOnlySet<MediaStreamCodec> Encodable(MediaStreamKind kind);
}

/// <summary>Folding a device's measured capability into the app's playback policy.</summary>
public static class MediaCapabilityExtensions
{
    /// <summary>Audio codecs this device can decode.</summary>
    public static IReadOnlySet<MediaStreamCodec> DecodableAudio(this IMediaCapability device)
        => Ask(device).Decodable(MediaStreamKind.Audio);

    /// <summary>Audio codecs this device can encode.</summary>
    public static IReadOnlySet<MediaStreamCodec> EncodableAudio(this IMediaCapability device)
        => Ask(device).Encodable(MediaStreamKind.Audio);

    /// <summary>Video codecs this device can decode.</summary>
    public static IReadOnlySet<MediaStreamCodec> DecodableVideo(this IMediaCapability device)
        => Ask(device).Decodable(MediaStreamKind.Video);

    /// <summary>Video codecs this device can encode.</summary>
    public static IReadOnlySet<MediaStreamCodec> EncodableVideo(this IMediaCapability device)
        => Ask(device).Encodable(MediaStreamKind.Video);

    private static IMediaCapability Ask(IMediaCapability device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device;
    }

    /// <summary>
    /// Answer <see cref="MediaPlaybackPolicy.Encodable"/> from what the DEVICE can actually do, leaving
    /// every other field alone.
    /// <para>
    /// 🔴 <b>It touches only the encode flags.</b> The container and codec sets describe what the PLAYER
    /// opens — the webview, which this object knows nothing about (<see cref="IMediaCapability"/>).
    /// Filling them from the platform's list would tell the planner a device that DECODES AC-3 can PLAY
    /// it, turning every AC-3 file from Transcode into Direct: a file that serves perfectly and plays
    /// silent.
    /// </para>
    /// <para>
    /// 🔴 <b>The device's answer is INTERSECTED with <paramref name="convertible"/>.</b> The planner reads
    /// <see cref="MediaPlaybackPolicy.Encodable"/> as *"can this pipeline re-encode a stream of this
    /// kind?"*, not *"can the device encode it?"* — and the two disagree: Android's <c>MediaCodecList</c>
    /// reports video encoders while the kit converts no video at all (<c>Mp4Remuxer</c> COPIES it), so the
    /// plan says <c>Transcode (video)</c> and the track is dropped into
    /// <see cref="MediaRemuxerResult.Dropped"/> instead (D63).
    /// </para>
    /// </summary>
    /// <param name="policy">The app's playback policy — its container and codec sets are left untouched.</param>
    /// <param name="device">The measured device capability.</param>
    /// <param name="convertible">
    /// The kinds THIS APP's conversion pipeline can actually perform — an app with its own engine in
    /// <c>MediaConversionOptions.Convert</c> passes those. Null means the kit's own reach, audio alone;
    /// passing a kind the app cannot convert re-creates the defect above.
    /// </param>
    public static MediaPlaybackPolicy WithDeviceEncoders(this MediaPlaybackPolicy policy, IMediaCapability device,
                                                         IReadOnlySet<MediaStreamKind>? convertible = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(device);

        // ⚠ Every kind, not the two that exist today — keyed on both sides, so a kind added later is
        // picked up with no edit, and is NOT convertible until it earns a place in the set below. The
        // planner then answers Unsupported rather than promising a re-encode nothing performs.
        return policy with
        {
            Encodable = new HashSet<MediaStreamKind>(
                Enum.GetValues<MediaStreamKind>()
                    .Where(kind => device.Encodable(kind).Count > 0)
                    .Where(kind => (convertible ?? KitConvertible).Contains(kind))),
        };
    }

    /// <summary>
    /// What the KIT itself can re-encode: audio, through <c>IMediaStreamConversion</c> and the container
    /// writers. Video stays absent even though a video tier exists — that half is only real once an app
    /// REGISTERS an <see cref="IMediaStreamConversion"/>, and claiming it here would put a conversion in
    /// the plan that nothing performs (see <see cref="WithDeviceEncoders"/>). Such an app passes
    /// <c>seams.ConvertibleKinds</c> and gets Video back honestly.
    /// </summary>
    private static readonly IReadOnlySet<MediaStreamKind> KitConvertible =
        new HashSet<MediaStreamKind> { MediaStreamKind.Audio };

    /// <summary>
    /// Could this device repair <paramref name="codec"/> — decode it, and encode something to replace it?
    /// Both halves are required: a device that decodes AC-3 but encodes nothing cannot repair a
    /// soundtrack, and one that encodes AAC but cannot decode AC-3 cannot either.
    /// <para>
    /// 🔴 <b>This answers about the DEVICE, not about the kit.</b> A <c>true</c> here does NOT promise
    /// <see cref="IMediaStreamConversion.CanConvert"/> accepts the same codec — the kit's converters
    /// implement only the inputs the web needs, so on iOS <c>flac</c> and <c>alac</c> answer true here and
    /// are declined there, and <c>aac</c> is declined because MP4 carries it already. <b>To decide whether
    /// a file can be repaired, ask <see cref="IMediaStreamConversion.CanConvert"/></b> — it starts a real
    /// codec, so it cannot drift. Use this one to report what the hardware is capable of.
    /// </para>
    /// </summary>
    public static bool CanRepair(this IMediaCapability device, MediaStreamKind kind, MediaStreamCodec codec)
    {
        ArgumentNullException.ThrowIfNull(device);
        // Case-insensitivity now lives in MediaCodec.Equals rather than being passed per call site — an
        // app-supplied set's own comparer can no longer decide whether `AC3` reads as a codec this
        // device cannot decode. `Covers` adds the profile rule on top.
        return !string.IsNullOrWhiteSpace(codec.Name)
            && device.Decodable(kind).Covers(codec)
            && device.Encodable(kind).Count > 0;
    }

    /// <summary>Shorthand for the audio case.</summary>
    public static bool CanRepairAudio(this IMediaCapability device, MediaStreamCodec codec)
        => device.CanRepair(MediaStreamKind.Audio, codec);
}
