namespace Shenora.Modules.Media;


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
    /// <para>
    /// ⚠ <b>Build the set with <see cref="StringComparer.OrdinalIgnoreCase"/>.</b> Callers ask with a
    /// codec name as a CONTAINER spelled it, and the declaration half of the same question
    /// (<c>MediaStreamClaim</c>) is explicitly compared case-insensitively — so an ordinal set here makes
    /// <c>CanConvert</c> answer NO for a codec the kit claims and the device handles, which is the
    /// "taking what IS supported as unsupported" failure this tier exists to avoid. Every implementation
    /// the kit ships does this; it is stated because this is a seam an app may implement, and a plain
    /// <c>HashSet&lt;string&gt;</c> is the wrong default.
    /// </para>
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
    /// <para>
    /// 🔴 <b>The device's answer is INTERSECTED with what the app can actually convert, and that is the
    /// whole point of <paramref name="convertible"/>.</b> <see cref="MediaPlaybackPolicy.Encodable"/> is
    /// read by the planner as *"can this pipeline re-encode a stream of this kind?"* — and until
    /// 2026-08-09 this method answered a different question, *"can the device encode it?"*. On Android the
    /// two disagree: <c>MediaCodecList</c> reports video encoders, so the planner returned
    /// <c>Transcode (video)</c> — while the kit has no video conversion at all (<c>Mp4Remuxer</c> COPIES
    /// video, carrying only H.264 and HEVC), so the track was dropped and merely named in
    /// <see cref="MediaRemuxerResult.Dropped"/>. **A plan that names a conversion nothing implements is
    /// worse than a missing capability, because the plan says the word** (D63's fourth instance).
    /// </para>
    /// <para>
    /// The default is the kit's own honest reach: <see cref="MediaStreamKind.Audio"/>, which is what
    /// <c>IMediaStreamConversion</c> covers on all three shells. An app that supplies its own engine
    /// through <c>MediaConversionOptions.Convert</c> — a video encoder, ffmpeg, anything — passes the
    /// kinds it can really perform and gets them back in the plan.
    /// </para>
    /// </summary>
    /// <param name="policy">The app's playback policy — its container and codec sets are left untouched.</param>
    /// <param name="device">The measured device capability.</param>
    /// <param name="convertible">
    /// The kinds THIS APP's conversion pipeline can actually perform. Null means the kit's own reach,
    /// which is audio alone. Passing a kind the app cannot convert re-creates the defect above.
    /// </param>
    public static MediaPlaybackPolicy WithDeviceEncoders(this MediaPlaybackPolicy policy, IMediaCapability device,
                                                         IReadOnlySet<MediaStreamKind>? convertible = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(device);

        // ⚠ Every kind, not the two that exist today. Keyed on both sides means a kind added later is
        // picked up here with no edit — which is the point of keying rather than naming.
        //
        // ⚠ And a kind added later is NOT convertible by default: it has to earn its place in the set
        // below, which is the direction that fails safe. The planner answers Unsupported for a stream
        // nothing can re-encode, and an honest refusal lets the app hand the file to an external player.
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
    /// writers. Video is absent because no video conversion ships — see <see cref="WithDeviceEncoders"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>AUDIO ALONE, and it stays that way even now a video tier exists</b> (2026-08-12). The video half
    /// is only real when an app REGISTERS an <see cref="IMediaStreamConversion"/> — the kit ships the seam,
    /// the platform ships the codec — so claiming Video here would recreate the exact defect this constant
    /// was written for: a plan naming a conversion nothing implements. An app with the seam wired passes
    /// <c>seams.ConvertibleKinds</c> and gets Video back honestly.
    /// </remarks>
    private static readonly IReadOnlySet<MediaStreamKind> KitConvertible =
        new HashSet<MediaStreamKind> { MediaStreamKind.Audio };

    /// <summary>
    /// Could this device repair <paramref name="codec"/> — decode it, and encode something to replace it?
    /// <para>
    /// Both halves are required and asking only one is the common mistake: a device that decodes AC-3 but
    /// can encode nothing cannot repair a soundtrack, and a device that encodes AAC but cannot decode AC-3
    /// cannot either. The answer is per CODEC, which is why it is a method and not a flag.
    /// </para>
    /// <para>
    /// 🔴 <b>This answers about the DEVICE, not about the kit — and the two differ on purpose.</b> A
    /// <c>true</c> here does NOT promise that <see cref="IMediaStreamConversion.CanConvert"/> will accept
    /// the same codec: the kit's converters implement only the inputs the web actually needs, so on iOS
    /// today <c>flac</c> and <c>alac</c> answer true here and are declined there, and <c>aac</c> is
    /// declined DELIBERATELY because MP4 carries it already and converting would be a lossy round-trip
    /// for nothing.
    /// <b>To decide whether a file can be repaired, ask <see cref="IMediaStreamConversion.CanConvert"/></b>
    /// — that one starts a real codec, so it cannot drift from what happens. Use this one to report what
    /// the hardware is capable of, which is a different and genuinely useful question (D42: the kit ships
    /// the QUESTION, never a codec list).
    /// ⚠ The sample's own cross-check asserted these two must agree and flagged the design as a defect;
    /// only the <c>CanConvert &amp;&amp; !CanRepair</c> direction is actually broken.
    /// </para>
    /// </summary>
    public static bool CanRepair(this IMediaCapability device, MediaStreamKind kind, string codec)
    {
        ArgumentNullException.ThrowIfNull(device);
        // OrdinalIgnoreCase explicitly, for the reason on IMediaCapability.Decodable: `Contains` would
        // otherwise inherit an app-supplied set's comparer, and a container spelling `AC3` must not read
        // as a codec this device cannot decode.
        return !string.IsNullOrWhiteSpace(codec)
            && device.Decodable(kind).Contains(codec, StringComparer.OrdinalIgnoreCase)
            && device.Encodable(kind).Count > 0;
    }

    /// <summary>Shorthand for the audio case, which is the one that actually turns up.</summary>
    public static bool CanRepairAudio(this IMediaCapability device, string codec)
        => device.CanRepair(MediaStreamKind.Audio, codec);
}
