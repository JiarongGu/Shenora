namespace Shenora.Modules.Media;

/// <summary>
/// What MP4 can hold from a Matroska source WITHOUT re-encoding, and the sample entry each such track
/// becomes — asked of the raw CodecID, in ONE place.
///
/// <para>
/// 🔴 <b>One place because two writers now depend on the answer, and they must agree about a given file.</b>
/// <see cref="Mp4Remuxer"/> asks it to decide whether a whole output is COMPUTABLE (a copied track's size is
/// derivable, a re-encoded one's is not); <see cref="SegmentRunWriter"/> asks it per track to decide whether
/// to copy a stream into fragments or spend a hardware codec on it. Its own comment already said a second
/// spelling of this question is how the plan and the write come to disagree about one file — a second
/// CALLER is the same hazard, so the predicate moved here rather than being repeated.
/// </para>
///
/// <para>
/// ⚠ <b>Asked of the raw Matroska CodecID rather than of a translated codec name</b> (<c>V_MPEG4/ISO/AVC</c>,
/// not <c>h264</c>). The CodecID is what decides whether the frames can be carried verbatim: Matroska already
/// stores H.264 and HEVC in the length-prefixed form MP4 uses, so a copy is a byte move. A translated name
/// loses the distinction that makes that true.
/// </para>
/// </summary>
internal static class Mp4Carriage
{
    /// <summary>Matroska CodecIDs MP4 can carry into a picture track, and the boxes each becomes.</summary>
    private static readonly Dictionary<string, (string Entry, string Config)> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ["V_MPEG4/ISO/AVC"] = ("avc1", "avcC"),
        ["V_MPEGH/ISO/HEVC"] = ("hvc1", "hvcC"),
    };

    /// <summary>
    /// Could the output carry this track's frames UNTOUCHED?
    /// <para>
    /// ⚠ <b>Declaring a carriable codec is not enough — the DECODER CONFIGURATION has to be there too</b>, and
    /// that is why this is not merely a codec lookup. A track whose <c>CodecPrivate</c> is missing cannot be
    /// copied at all: the <c>avcC</c>/<c>hvcC</c>/AudioSpecificConfig is what the sample entry is built from,
    /// and a copy without one produces a track a decoder cannot start. <see cref="EntryFor"/> answers null in
    /// that case and the caller falls back to converting, which is the honest repair.
    /// </para>
    /// </summary>
    public static bool CanCarry(MatroskaTrack track) => track.Kind switch
    {
        MediaStreamKind.Video => CanCarryVideo(track),
        MediaStreamKind.Audio => CanCarryAudio(track),
        // Subtitles are a FORMAT conversion rather than a container rewrite, and the planner already treats
        // them as droppable — so nothing carries one, and saying so beats dropping it silently.
        _ => false,
    };

    public static bool CanCarryVideo(MatroskaTrack track) =>
        track.CodecId is not null && Video.ContainsKey(track.CodecId);

    /// <summary>AAC, in any of the profile-qualified spellings Matroska uses (<c>A_AAC/MPEG4/LC</c>).</summary>
    public static bool CanCarryAudio(MatroskaTrack track) =>
        track.CodecId is not null
        && (track.CodecId.Equals("A_AAC", StringComparison.OrdinalIgnoreCase)
            || track.CodecId.StartsWith("A_AAC/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The <c>stsd</c> entry a copied track needs, or null when the track cannot be carried after all —
    /// including the case where it declares a carriable codec but ships no decoder configuration.
    /// </summary>
    public static byte[]? EntryFor(MatroskaTrack track) => track.Kind switch
    {
        MediaStreamKind.Video when CanCarryVideo(track) => VideoEntry(track),
        MediaStreamKind.Audio when CanCarryAudio(track) => AudioEntry(track),
        _ => null,
    };

    private static byte[]? VideoEntry(MatroskaTrack track)
    {
        if (track.CodecPrivate is not { Length: > 0 } config) return null;
        var (entry, configBox) = Video[track.CodecId!];

        // A zero dimension makes a track a player lays out as nothing. Refuse rather than write a file that
        // decodes into a window with no area.
        if (track.Width <= 0 || track.Height <= 0) return null;

        return Mp4Builder.VisualSampleEntry(entry, configBox, track.Width, track.Height, config);
    }

    private static byte[]? AudioEntry(MatroskaTrack track)
    {
        var channels = track.Channels > 0 ? track.Channels : 2;
        var rate = track.SampleRate > 0 ? track.SampleRate : 48000;

        // A real file ships its own AudioSpecificConfig and it is copied untouched; synthesising one is the
        // fallback for a track that shipped none, and it refuses rather than guess a rate AAC cannot index.
        var config = track.CodecPrivate is { Length: > 0 } shipped
            ? shipped
            : Mp4Builder.SynthesiseAacConfig(rate, channels);

        return config is null ? null : Mp4Builder.AudioSampleEntry(channels, rate, config);
    }
}
