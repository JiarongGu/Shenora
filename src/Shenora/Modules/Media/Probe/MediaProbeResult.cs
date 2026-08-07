namespace Shenora.Modules.Media;

/// <summary>What a stream inside a container carries. Deliberately coarse — the planner only branches on
/// the kinds that decide playability.</summary>
public enum MediaStreamKind
{
    /// <summary>The probe reported a kind this kit does not model (data, timecode, attachments).</summary>
    Unknown = 0,

    /// <summary>A picture track.</summary>
    Video,

    /// <summary>A sound track. ⚠ The one that fails most often in practice — see <see cref="MediaPlaybackPlanner"/>.</summary>
    Audio,

    /// <summary>Subtitles or captions.</summary>
    Subtitle,
}

/// <summary>
/// One stream inside a media container, as a probe reported it.
/// <para>
/// <b>Every field except <see cref="Kind"/> is BEST-EFFORT and may be null</b>, and that is a contract
/// rather than laziness: both surveyed implementations of this admit the same thing in their own types —
/// one says so in a comment ("all fields are best-effort; any may be null/0"), the other simply returns a
/// tuple of nullables. A probe is an external tool that may be absent, may not understand the file, and
/// may report a codec name nobody has heard of. Code that treats a null here as an error will fail on
/// files that play perfectly.
/// </para>
/// </summary>
/// <param name="Kind">What the stream carries. The only field the planner requires.</param>
/// <param name="Codec">
/// The probe's codec name, lowercase by convention (<c>h264</c>, <c>hevc</c>, <c>aac</c>, <c>ac3</c>,
/// <c>pcm_s16le</c>). Null when nothing probed the file — which is a normal state, not a failure, and
/// <see cref="MediaPlaybackPlanner"/> is written to fall back rather than punish it.
/// </param>
/// <param name="Profile">
/// The codec profile when the probe reported one (<c>Main 10</c>, <c>High</c>). It matters more than it
/// looks: HEVC <c>Main10</c> is a different capability from the <c>hevc</c> a device may advertise, so a
/// codec name alone can say "supported" about a stream that will not decode.
/// </param>
/// <param name="Channels">Audio channel count, when known. A 5.1 track can need downmixing even when its
/// codec is supported.</param>
/// <param name="SampleRate">
/// Audio sample rate in Hz, when known. Needed to CONFIGURE a decoder — a platform codec is told the rate
/// and channel count before it is fed anything, and guessing 48 kHz for a 44.1 kHz track produces audio
/// that plays at the wrong speed rather than failing.
/// </param>
public sealed record MediaStreamInfo(
    MediaStreamKind Kind,
    string? Codec = null,
    string? Profile = null,
    int? Channels = null,
    int? SampleRate = null);

/// <summary>
/// What a probe found in a media file: the container, its streams, and whatever else it could report.
/// <para>
/// <b>The shape, not the prober.</b> <see cref="MatroskaProbe"/> fills one in for the container that
/// actually needs it, and anything else the APP uses fills one in the same way — ffprobe, a platform
/// metadata reader, an engine, a header parser. There is deliberately no <c>IMediaProbe</c> seam:
/// <see cref="MediaPlaybackPlanner.Plan"/> takes the RECORD, so a probe is a function that returns one and
/// needs no interface to be pluggable.
/// </para>
/// <para>
/// ⚠ <b>Nothing here is required.</b> An entirely empty result is legal and
/// <see cref="MediaPlaybackPlanner.Plan"/> answers it from the file extension alone — the case that
/// arises whenever the app has no probe installed, which both donors treat as a first-class state where
/// "nothing fails" rather than an error.
/// </para>
/// </summary>
public sealed record MediaProbeResult
{
    /// <summary>
    /// The container, as a lowercase file extension INCLUDING the dot (<c>.mp4</c>, <c>.mkv</c>).
    /// <para>
    /// An extension rather than the probe's format name on purpose: the extension is always available,
    /// the format name is not, and the planner's container decision has to work when nothing probed.
    /// ⚠ Extensions do lie — a <c>.mp4</c> may hold anything — which is precisely why the planner checks
    /// the container AND the streams instead of trusting either alone.
    /// </para>
    /// </summary>
    public string? Container { get; init; }

    /// <summary>The streams, in probe order. Empty when nothing probed the file.</summary>
    public IReadOnlyList<MediaStreamInfo> Streams { get; init; } = [];

    /// <summary>Duration, when known. Best-effort like everything else here.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Picture width in pixels, when known.</summary>
    public int? Width { get; init; }

    /// <summary>Picture height in pixels, when known.</summary>
    public int? Height { get; init; }
}
