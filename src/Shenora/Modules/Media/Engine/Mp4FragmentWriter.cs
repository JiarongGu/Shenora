using System.Buffers.Binary;

namespace Shenora.Modules.Media;

/// <summary>
/// Writes FRAGMENTED MP4 — an <c>init</c> segment that declares the tracks, then numbered media segments
/// that each carry their own index, so a producer can emit piece 3 while piece 900 does not exist yet. No
/// codec, no demuxer: it takes samples that already exist and writes the boxes around them.
/// <para>
/// ⚠ <b>fMP4 and NOT MPEG-TS</b>, because only the <c>trun</c>'s sizes make a segment's picture bytes
/// COUNTABLE — MPEG-TS names its streams in the PMT, so a segment whose encoder wrote nothing still declares
/// a video stream and looks correct. That subtraction is what
/// <see cref="ISegmentEngine.HasRenderedPicture"/> rests on.
/// </para>
/// </summary>
internal static class Mp4FragmentWriter
{
    /// <summary><c>trun</c> flags: the data offset, plus a duration, size and flags for every sample.
    /// Composition offsets are added only when some sample needs one — see <see cref="Trun"/>.</summary>
    private const uint TrunDataOffset = 0x000001;
    private const uint TrunSampleDuration = 0x000100;
    private const uint TrunSampleSize = 0x000200;
    private const uint TrunSampleFlags = 0x000400;
    private const uint TrunCompositionOffsets = 0x000800;

    /// <summary><c>tfhd</c> flag <c>default-base-is-moof</c>: every <c>trun</c> offset is measured from the
    /// start of its own <c>moof</c>, so a fragment can be served, cached and replayed with no context.</summary>
    private const uint TfhdDefaultBaseIsMoof = 0x020000;

    /// <summary>
    /// The init segment: <c>ftyp</c> + a <c>moov</c> whose sample tables are EMPTY, plus the <c>mvex</c> that
    /// tells a reader to expect fragments. The duration is zero everywhere — a fragmented movie's length is
    /// not known yet, and each fragment carries its own decode time.
    /// <para>
    /// ⚠ <b><c>mvex</c> is what makes the empty tables legal rather than broken.</b> Without it a reader is
    /// entitled to conclude the movie is empty: the file opens, reports no duration, and plays nothing.
    /// </para>
    /// </summary>
    /// <param name="target">Written from the current position. Need not be seekable.</param>
    /// <param name="tracks">One per track, in the order their <c>traf</c> boxes will appear in every fragment.</param>
    public static void WriteInitSegment(Stream target, IReadOnlyList<Mp4FragmentTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0) throw new ArgumentException("An init segment must declare at least one track.", nameof(tracks));

        // Buffered so the target does not have to be seekable: every box back-patches its own length.
        using var buffer = new MemoryStream();
        var w = new BoxWriter(buffer);

        using (w.Box("ftyp"))
        {
            // `iso5` admits `tfdt`, which every fragment here writes; the rest are the compatibility set.
            w.Ascii("iso5");
            w.U32(0x200);
            w.Ascii("isom");
            w.Ascii("iso5");
            w.Ascii("iso6");
            w.Ascii("avc1");
            w.Ascii("mp41");
            w.Ascii("dash");
        }

        using (w.Box("moov"))
        {
            using (w.FullBox("mvhd", 0, 0))
            {
                w.U32(0);                               // creation time — zero, as the remuxer explains
                w.U32(0);                               // modification time
                w.U32(Mp4Builder.MovieTimescale);
                w.U32(0);                               // duration: unknown for a fragmented movie
                w.U32(0x00010000);                      // rate 1.0
                w.U16(0x0100);                          // volume 1.0
                w.U16(0);                               // reserved
                w.Zeros(8);                             // reserved
                w.UnityMatrix();
                w.Zeros(24);                            // pre-defined
                w.U32(tracks.Count + 1);                // next track id
            }

            foreach (var track in tracks)
            {
                Mp4Builder.Trak(w, new Mp4Builder.TrakShape
                {
                    TrackId = track.TrackId,
                    Timescale = track.Timescale,
                    MediaDuration = 0,                  // see the summary
                    IsVideo = track.IsVideo,
                    Width = track.Width,
                    Height = track.Height,
                }, sampleTable => EmptyStbl(sampleTable, track));
            }

            using (w.Box("mvex"))
            {
                foreach (var track in tracks)
                {
                    using (w.FullBox("trex", 0, 0))
                    {
                        w.U32(track.TrackId);
                        w.U32(1);                       // sample description index — the only entry in stsd
                        // Zero defaults — every fragment states duration, size and flags per sample.
                        w.U32(0);                       // default sample duration
                        w.U32(0);                       // default sample size
                        w.U32(0);                       // default sample flags
                    }
                }
            }
        }

        buffer.Position = 0;
        buffer.CopyTo(target);
    }

    /// <summary>A sample table that indexes NOTHING, but still carries the <c>stsd</c> — which is why a
    /// fragment never repeats it.</summary>
    private static void EmptyStbl(BoxWriter w, Mp4FragmentTrack track)
    {
        using (w.Box("stbl"))
        {
            using (w.FullBox("stsd", 0, 0))
            {
                w.U32(1);
                w.Raw(track.SampleEntry);
            }

            using (w.FullBox("stts", 0, 0)) w.U32(0);
            using (w.FullBox("stsc", 0, 0)) w.U32(0);
            using (w.FullBox("stsz", 0, 0)) { w.U32(0); w.U32(0); }
            // `stco` rather than `co64`: it indexes nothing, so the 32-bit form cannot overflow.
            using (w.FullBox("stco", 0, 0)) w.U32(0);
        }
    }

    /// <summary>
    /// One media segment: <c>styp</c> + <c>moof</c> + <c>mdat</c>.
    /// <para>
    /// 🔴 <b><c>trun</c>'s data offset is circular by construction:</b> it measures from the start of the
    /// <c>moof</c> to that track's first sample byte inside the <c>mdat</c>, a distance that includes the
    /// size of the <c>moof</c> being written. So the <c>moof</c> is buffered with the offsets left blank,
    /// its length becomes known, and each blank is patched. A guessed length produces a segment that appends
    /// without error and plays silence.
    /// </para>
    /// </summary>
    /// <param name="target">Written from the current position. Need not be seekable.</param>
    /// <param name="sequenceNumber">1-based and strictly increasing across a run — <c>mfhd</c>'s only field.</param>
    /// <param name="tracks">The tracks contributing samples, in the SAME order as the init segment declared
    /// them. A track with no samples in this segment is omitted rather than written empty.</param>
    public static void WriteFragment(Stream target, int sequenceNumber, IReadOnlyList<Mp4FragmentTrackData> tracks)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceNumber, 1);

        var contributing = tracks.Where(t => t.Samples.Count > 0).ToArray();
        if (contributing.Length == 0)
        {
            throw new ArgumentException("A fragment must carry at least one sample.", nameof(tracks));
        }

        using var buffer = new MemoryStream();
        var w = new BoxWriter(buffer);

        using (w.Box("styp"))
        {
            w.Ascii("msdh");
            w.U32(0);
            w.Ascii("msdh");
            w.Ascii("msix");
        }

        var stypLength = buffer.Length;
        var offsetFields = new long[contributing.Length];

        using (w.Box("moof"))
        {
            using (w.FullBox("mfhd", 0, 0)) w.U32(sequenceNumber);

            for (var i = 0; i < contributing.Length; i++)
            {
                using (w.Box("traf"))
                {
                    using (w.FullBox("tfhd", 0, TfhdDefaultBaseIsMoof)) w.U32(contributing[i].Track.TrackId);

                    // ⚠ Version 1 — a 64-bit decode time. The 32-bit form wraps after ~13 hours at a 90 kHz
                    // timescale, and a wrapped base time seeks to the wrong place rather than failing.
                    using (w.FullBox("tfdt", 1, 0)) w.U64(contributing[i].BaseMediaDecodeTime);

                    offsetFields[i] = Trun(w, contributing[i].Samples);
                }
            }
        }

        var moofLength = buffer.Length - stypLength;    // a data offset measures from the moof, not the styp
        var dataStart = moofLength + 8;                 // + the mdat header
        var raw = buffer.GetBuffer();

        for (var i = 0; i < contributing.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan((int)offsetFields[i], 4), checked((int)dataStart));
            dataStart += contributing[i].Data.Length;
        }

        buffer.Position = 0;
        buffer.CopyTo(target);

        // The mdat, written straight through rather than buffered.
        var payload = contributing.Sum(t => (long)t.Data.Length);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)(payload + 8)));
        "mdat"u8.CopyTo(header[4..]);
        target.Write(header);
        foreach (var track in contributing) target.Write(track.Data.Span);
    }

    /// <summary>
    /// The sample run. Returns the ABSOLUTE position, in the buffer being written, of the four-byte data
    /// offset the caller must patch once the <c>moof</c>'s length is known.
    /// </summary>
    private static long Trun(BoxWriter w, IReadOnlyList<Mp4FragmentSample> samples)
    {
        // Composition offsets are written only when one is non-zero. 🔴 Version 1, so they are SIGNED — a
        // fragment states its own decode time and never shifts the presentation, so a NEGATIVE offset is
        // ordinary here where the whole-file writer had shifted it away.
        var composed = false;
        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].CompositionOffset == 0) continue;
            composed = true;
            break;
        }

        var flags = TrunDataOffset | TrunSampleDuration | TrunSampleSize | TrunSampleFlags
                    | (composed ? TrunCompositionOffsets : 0);

        long offsetField;
        using (w.FullBox("trun", (byte)(composed ? 1 : 0), flags))
        {
            w.U32(samples.Count);
            offsetField = w.Position;
            w.U32(0);                                   // patched by the caller — see WriteFragment
            foreach (var sample in samples)
            {
                w.U32(sample.Duration);
                w.U32(sample.Length);
                w.U32(SampleFlags(sample.KeyFrame));
                if (composed) w.U32(sample.CompositionOffset);
            }
        }
        return offsetField;
    }

    /// <summary>
    /// The per-sample flags word, which is how a fragment says "you may start here".
    /// <para>
    /// 🔴 <b>The two halves must agree or a seek lands on a frame nothing can decode.</b> A sync sample
    /// declares <c>sample_depends_on = 2</c> AND clears <c>sample_is_non_sync_sample</c>; a non-sync sample
    /// declares <c>1</c> and sets it. Write only one half and every frame looks seekable to one reader and
    /// none to another — a seek that plays macroblock soup rather than an error.
    /// </para>
    /// </summary>
    private static uint SampleFlags(bool keyFrame) => keyFrame
        ? 2u << 24
        : (1u << 24) | (1u << 16);
}

/// <summary>A track as a fragmented movie declares it — everything a fragment does NOT repeat.</summary>
internal sealed class Mp4FragmentTrack
{
    /// <summary>1-based, unique within the movie, and the key every <c>traf</c> refers back to.</summary>
    public required int TrackId { get; init; }

    /// <summary>Ticks per second for this track's sample durations and decode times.</summary>
    public required uint Timescale { get; init; }

    /// <summary>The <c>stsd</c> child — an <c>avc1</c>/<c>hvc1</c>/<c>mp4a</c> entry, built by
    /// <see cref="Mp4Builder.VisualSampleEntry"/> or <see cref="Mp4Builder.AudioSampleEntry"/>. It carries
    /// the decoder configuration, which is why a fragment needs none.</summary>
    public required byte[] SampleEntry { get; init; }

    /// <summary>Picture or sound.</summary>
    public required bool IsVideo { get; init; }

    /// <summary>Written into <c>tkhd</c> for a video track; ignored for sound.</summary>
    public int Width { get; init; }

    /// <inheritdoc cref="Width"/>
    public int Height { get; init; }
}

/// <summary>One sample's index entry. The bytes themselves live in <see cref="Mp4FragmentTrackData.Data"/>.</summary>
/// <param name="Duration">On the track's timescale.</param>
/// <param name="Length">Bytes, and the caller's <c>Data</c> must hold exactly this many for this sample.</param>
/// <param name="CompositionOffset">
/// Presentation minus decode time, on the track's timescale. Zero for everything without B-frames; MAY be
/// negative here, unlike the whole-file writer's, which shifts the presentation instead.
/// </param>
/// <param name="KeyFrame">Whether a decoder may START at this sample. See <c>SampleFlags</c>.</param>
internal readonly record struct Mp4FragmentSample(long Duration, int Length, long CompositionOffset, bool KeyFrame);

/// <summary>One track's contribution to ONE fragment.</summary>
internal sealed class Mp4FragmentTrackData
{
    /// <summary>Which track — must be one the init segment declared.</summary>
    public required Mp4FragmentTrack Track { get; init; }

    /// <summary>
    /// Where this fragment starts on the track's timeline, in its own timescale — <c>tfdt</c>.
    /// ⚠ <b>It must be the RUNNING total rather than zero.</b> A run that restarts mid-source (a seek) numbers
    /// its segments from the index it was asked for, so the decode time has to be computed from that index and
    /// not from how many samples this run has emitted.
    /// </summary>
    public required long BaseMediaDecodeTime { get; init; }

    /// <summary>In decode order, which is also storage order.</summary>
    public required IReadOnlyList<Mp4FragmentSample> Samples { get; init; }

    /// <summary>
    /// The sample bytes, concatenated in the same order as <see cref="Samples"/> and summing to their
    /// lengths. ⚠ Held WHOLE in memory, which is only safe while a segment stays a few seconds long.
    /// </summary>
    public required ReadOnlyMemory<byte> Data { get; init; }
}
