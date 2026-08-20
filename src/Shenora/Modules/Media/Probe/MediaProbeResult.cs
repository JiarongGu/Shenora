namespace Shenora.Modules.Media;

/// <summary>What a stream inside a container carries — only the kinds that decide playability.</summary>
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
/// One stream inside a media container, as a probe reported it. ⚠ <b>Every field except
/// <see cref="Kind"/> is BEST-EFFORT and may be null</b> — code that treats a null here as an error will
/// fail on files that play perfectly.
/// </summary>
/// <param name="Kind">What the stream carries. The only field the planner requires.</param>
/// <param name="Codec">The probe's codec name, lowercase by convention (<c>h264</c>, <c>aac</c>,
/// <c>ac3</c>). Null when nothing probed the file, which <see cref="MediaPlaybackPlanner"/> falls back
/// on rather than punishing.</param>
/// <param name="Profile">The codec profile, when the probe reported one (<c>Main 10</c>, <c>High</c>).
/// HEVC <c>Main10</c> is a different capability from the <c>hevc</c> a device advertises, so a codec name
/// alone can say "supported" about a stream that will not decode.</param>
/// <param name="Channels">Audio channel count, when known. A 5.1 track can need downmixing even when its
/// codec is supported.</param>
/// <param name="SampleRate">Audio sample rate in Hz, when known. Needed to CONFIGURE a decoder: guessing
/// 48 kHz for a 44.1 kHz track produces audio that plays at the wrong speed rather than failing.</param>
/// <param name="Width">Video width in pixels, when known. ⚠ A decoder configured at the wrong size does
/// not fail; it produces a picture that is stretched, cropped or green.</param>
/// <param name="Height">Video height in pixels, when known. See <paramref name="Width"/>.</param>
/// <param name="FrameRate">Frames per second, when known. Only an encoder HINT — the timing that lands in
/// the file comes from each frame's presentation time.</param>
public sealed record MediaStreamInfo(
    MediaStreamKind Kind,
    string? Codec = null,
    string? Profile = null,
    int? Channels = null,
    int? SampleRate = null,
    int? Width = null,
    int? Height = null,
    double? FrameRate = null);

/// <summary>
/// What a probe found in a media file: the container, its streams, and whatever else it could report.
/// <b>The shape, not the prober</b> — <see cref="MatroskaProbe"/> fills one in, and so does anything else
/// the app uses (ffprobe, a platform metadata reader).
/// <para>
/// ⚠ <b>Nothing here is required.</b> An entirely empty result is legal and
/// <see cref="MediaPlaybackPlanner.Plan"/> answers it from the file extension alone — what happens whenever
/// the app has no probe installed.
/// </para>
/// </summary>
public sealed record MediaProbeResult
{
    /// <summary>
    /// The container, as a lowercase file extension INCLUDING the dot (<c>.mp4</c>, <c>.mkv</c>) — an
    /// extension rather than the probe's format name, so the container decision works when nothing probed.
    /// ⚠ Extensions lie, so the planner checks the container AND the streams, never either alone.
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
