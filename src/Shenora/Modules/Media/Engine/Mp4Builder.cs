using System.Buffers.Binary;
using System.Text;

namespace Shenora.Modules.Media;

/// <summary>
/// Writes ISO base media file format boxes — the output side of the remux. A box's length is not known
/// until its payload is written, so every box is opened as a SCOPE that back-patches its own size on close.
/// ⚠ Write a size by hand and the file parses for a while and then does not.
/// </summary>
internal sealed class BoxWriter(Stream target)
{
    public long Position => target.Position;

    /// <summary>Open a box; the returned scope patches the size when disposed.</summary>
    public BoxScope Box(string type)
    {
        var start = target.Position;
        U32(0);                    // placeholder, patched on close
        Ascii(type);
        return new BoxScope(this, start);
    }

    /// <summary>A box with the version+flags word every "full box" starts with.</summary>
    public BoxScope FullBox(string type, byte version, uint flags)
    {
        var scope = Box(type);
        U32((uint)(version << 24) | (flags & 0x00FFFFFF));
        return scope;
    }

    public void U8(int value) => target.WriteByte((byte)value);

    public void U16(int value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)value);
        target.Write(b);
    }

    public void U24(int value)
    {
        U8(value >> 16);
        U8(value >> 8);
        U8(value);
    }

    public void U32(long value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)value);
        target.Write(b);
    }

    public void U64(long value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)value);
        target.Write(b);
    }

    /// <summary>A 16.16 fixed-point number — how MP4 writes widths, heights and rates.</summary>
    public void Fixed1616(double value) => U32((long)Math.Round(value * 65536.0));

    public void Ascii(string value) => target.Write(Encoding.ASCII.GetBytes(value));

    public void Raw(byte[] value) => target.Write(value);

    public void Zeros(int count)
    {
        Span<byte> zero = stackalloc byte[64];
        while (count > 0)
        {
            var take = Math.Min(count, zero.Length);
            target.Write(zero[..take]);
            count -= take;
        }
    }

    /// <summary>The unity transform. Every player expects it and a zero matrix renders nothing.</summary>
    public void UnityMatrix()
    {
        U32(0x00010000); U32(0); U32(0);
        U32(0); U32(0x00010000); U32(0);
        U32(0); U32(0); U32(0x40000000);
    }

    internal void PatchSize(long start)
    {
        var end = target.Position;
        target.Position = start;
        U32(end - start);
        target.Position = end;
    }

    internal readonly struct BoxScope(BoxWriter writer, long start) : IDisposable
    {
        public void Dispose() => writer.PatchSize(start);
    }
}

/// <summary>One track, resolved from what Matroska said into what MP4 has to store.</summary>
internal sealed class Mp4TrackPlan
{
    public required MatroskaTrack Source { get; init; }
    public bool IsVideo => Source.Kind is MediaStreamKind.Video;

    /// <summary>Ticks per second on this track's own timeline.</summary>
    public required uint Timescale { get; init; }

    /// <summary>The <c>stsd</c> child — an <c>avc1</c>/<c>hvc1</c>/<c>mp4a</c> entry, already built.</summary>
    public required byte[] SampleEntry { get; init; }

    /// <summary>Samples in storage order, which for every track is also decode order.</summary>
    public required MatroskaSample[] Samples { get; init; }

    /// <summary>
    /// Where this track's sample bytes actually live. Null means the SOURCE file, which is the copy case.
    /// 🔴 A CONVERTED track's bytes came out of a codec, so they are spooled to a temporary stream and its
    /// offsets point there instead — same offset/length model, so nothing downstream knows which it has.
    /// </summary>
    public Stream? ByteSource { get; set; }

    public required long[] Decode { get; init; }
    public required long[] Composition { get; init; }
    public required long[] Durations { get; init; }

    /// <summary>How far the presentation was moved to keep every composition offset non-negative.</summary>
    public required long Shift { get; init; }

    /// <summary>Samples per chunk, in chunk order — filled once the interleaved write order is known.</summary>
    public List<int> ChunkSamples { get; } = [];

    /// <summary>Each chunk's offset RELATIVE to the start of the media payload.</summary>
    public List<long> ChunkOffsets { get; } = [];

    public long Duration => Samples.Length == 0 ? 0 : Decode[^1] + Durations[^1];
}

/// <summary>
/// Assembles the <c>moov</c> — the whole sample table for every track.
/// <para>
/// 🔴 <b>Chunk offsets are written as 64-bit (<c>co64</c>) ALWAYS, never <c>stco</c>.</b> The offsets are
/// unknown until <c>moov</c>'s own length is, because the media follows it — and with 32-bit offsets the
/// table's SIZE depends on the values, so a file crossing 4 GB mid-write changes the length of the box that
/// states where everything is. A fixed-WIDTH table removes the circularity: build once to learn the length,
/// build again with real values, and the second is byte-for-byte as long.
/// </para>
/// </summary>
internal static class Mp4Builder
{
    /// <summary>The movie timeline. Milliseconds — enough for a duration, and never used for sample times.</summary>
    public const uint MovieTimescale = 1000;

    /// <summary>ISO-639-2/T <c>und</c>, packed five bits per letter. Undeclared, rather than a guess.</summary>
    private const int LanguageUndetermined = 0x55C4;

    public static byte[] Ftyp()
    {
        using var buffer = new MemoryStream();
        var w = new BoxWriter(buffer);
        using (w.Box("ftyp"))
        {
            w.Ascii("isom");
            w.U32(0x200);              // minor version, as every muxer writes it
            w.Ascii("isom");
            w.Ascii("iso2");
            w.Ascii("avc1");
            w.Ascii("mp41");
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Build the movie box. Call once with <paramref name="mediaStart"/> = 0 to learn the length, then again
    /// with the real media offset — the two are the same size by construction (see the type remarks).
    /// </summary>
    public static byte[] Moov(IReadOnlyList<Mp4TrackPlan> tracks, long mediaStart)
    {
        using var buffer = new MemoryStream();
        var w = new BoxWriter(buffer);

        var movieDuration = 0L;
        foreach (var track in tracks)
        {
            var scaled = track.Timescale == 0 ? 0 : track.Duration * MovieTimescale / track.Timescale;
            if (scaled > movieDuration) movieDuration = scaled;
        }

        using (w.Box("moov"))
        {
            using (w.FullBox("mvhd", 0, 0))
            {
                w.U32(0);                       // creation time — deliberately zero, see Mp4Remuxer
                w.U32(0);                       // modification time
                w.U32(MovieTimescale);
                w.U32(movieDuration);
                w.U32(0x00010000);              // rate 1.0
                w.U16(0x0100);                  // volume 1.0
                w.U16(0);                       // reserved
                w.Zeros(8);                     // reserved
                w.UnityMatrix();
                w.Zeros(24);                    // pre-defined
                w.U32(tracks.Count + 1);        // next track id
            }

            for (var i = 0; i < tracks.Count; i++) Trak(w, tracks[i], i + 1, mediaStart);
        }

        return buffer.ToArray();
    }

    private static void Trak(BoxWriter w, Mp4TrackPlan track, int trackId, long mediaStart)
        => Trak(w, new TrakShape
        {
            TrackId = trackId,
            Timescale = track.Timescale,
            MediaDuration = track.Duration,
            IsVideo = track.IsVideo,
            Width = track.Source.Width,
            Height = track.Source.Height,
            Shift = track.Shift,
        }, sampleTable => Stbl(sampleTable, track, mediaStart));

    /// <summary>
    /// Everything about a <c>trak</c> that does NOT depend on how its samples are indexed — the header, the
    /// media/handler boxes, the media-information header and the self-contained data reference. Shared with
    /// <c>Mp4FragmentWriter</c>, whose init segment is this skeleton with an EMPTY sample table and a
    /// <c>mvex</c> beside it.
    /// </summary>
    /// <param name="w">The writer, positioned where the <c>trak</c> should begin.</param>
    /// <param name="shape">The track's identity, timescale, duration and dimensions.</param>
    /// <param name="writeSampleTable">Writes the <c>stbl</c>. Called with the writer positioned inside <c>minf</c>.</param>
    internal static void Trak(BoxWriter w, TrakShape shape, Action<BoxWriter> writeSampleTable)
    {
        var track = shape;
        var trackId = shape.TrackId;
        var trackDuration = track.Timescale == 0 ? 0 : track.MediaDuration * MovieTimescale / track.Timescale;

        using (w.Box("trak"))
        {
            // ⚠ flags 7 = enabled + in movie + in preview. Without them the track is present and never
            // played, which reads exactly like a remux that dropped the stream.
            using (w.FullBox("tkhd", 0, 7))
            {
                w.U32(0);
                w.U32(0);
                w.U32(trackId);
                w.U32(0);                       // reserved
                w.U32(trackDuration);
                w.Zeros(8);                     // reserved
                w.U16(0);                       // layer
                w.U16(0);                       // alternate group
                w.U16(track.IsVideo ? 0 : 0x0100);
                w.U16(0);                       // reserved
                w.UnityMatrix();
                w.Fixed1616(track.IsVideo ? track.Width : 0);
                w.Fixed1616(track.IsVideo ? track.Height : 0);
            }

            // The presentation was pushed later to keep composition offsets non-negative; an edit list
            // takes exactly that much back off the front, so the track still starts when it should.
            if (track.Shift > 0)
            {
                using (w.Box("edts"))
                using (w.FullBox("elst", 0, 0))
                {
                    w.U32(1);
                    w.U32(trackDuration);
                    w.U32(track.Shift);         // start at this point on the media timeline
                    w.U16(1);                   // rate 1.0
                    w.U16(0);
                }
            }

            using (w.Box("mdia"))
            {
                using (w.FullBox("mdhd", 0, 0))
                {
                    w.U32(0);
                    w.U32(0);
                    w.U32(track.Timescale);
                    w.U32(track.MediaDuration);
                    w.U16(LanguageUndetermined);
                    w.U16(0);                   // pre-defined
                }

                using (w.FullBox("hdlr", 0, 0))
                {
                    w.U32(0);                   // pre-defined
                    w.Ascii(track.IsVideo ? "vide" : "soun");
                    w.Zeros(12);                // reserved
                    w.Ascii(track.IsVideo ? "VideoHandler\0" : "SoundHandler\0");
                }

                using (w.Box("minf"))
                {
                    if (track.IsVideo)
                    {
                        using (w.FullBox("vmhd", 0, 1))
                        {
                            w.U16(0);           // graphics mode: copy
                            w.Zeros(6);         // opcolor
                        }
                    }
                    else
                    {
                        using (w.FullBox("smhd", 0, 0))
                        {
                            w.U16(0);           // balance
                            w.U16(0);           // reserved
                        }
                    }

                    // ⚠ The 'url ' entry's FLAG is the whole meaning of the box — set, the media is in this
                    // file; clear, a player goes looking for an external one.
                    using (w.Box("dinf"))
                    using (w.FullBox("dref", 0, 0))
                    {
                        w.U32(1);
                        using (w.FullBox("url ", 0, 1)) { }
                    }

                    writeSampleTable(w);
                }
            }
        }
    }

    /// <summary>The <c>trak</c> facts that are independent of how samples are indexed.</summary>
    internal readonly record struct TrakShape
    {
        /// <summary>1-based, and unique within the movie.</summary>
        public required int TrackId { get; init; }

        /// <summary>Ticks per second on this track's own timeline.</summary>
        public required uint Timescale { get; init; }

        /// <summary>
        /// On the TRACK's own timescale. <b>Zero for a fragmented init segment</b>, whose length is not known
        /// when it is written and is carried by each fragment's <c>tfdt</c> instead.
        /// </summary>
        public required long MediaDuration { get; init; }

        /// <summary>Picture or sound — decides the handler, the media header and the dimensions.</summary>
        public required bool IsVideo { get; init; }

        /// <summary>Written into <c>tkhd</c> for a video track; ignored for sound.</summary>
        public int Width { get; init; }

        /// <inheritdoc cref="Width"/>
        public int Height { get; init; }

        /// <summary>
        /// How far presentation was pushed to keep composition offsets non-negative. Zero writes no edit
        /// list, which is the fragmented case: a fragment states its own decode time directly.
        /// </summary>
        public long Shift { get; init; }
    }

    private static void Stbl(BoxWriter w, Mp4TrackPlan track, long mediaStart)
    {
        using (w.Box("stbl"))
        {
            using (w.FullBox("stsd", 0, 0))
            {
                w.U32(1);
                w.Raw(track.SampleEntry);
            }

            // stts — run-length encoded sample durations.
            using (w.FullBox("stts", 0, 0))
            {
                var runs = RunLength(track.Durations);
                w.U32(runs.Count);
                foreach (var (count, value) in runs)
                {
                    w.U32(count);
                    w.U32(value);
                }
            }

            // ctts — omitted when every offset is zero (the no-B-frame case, and most audio).
            if (Array.Exists(track.Composition, offset => offset != 0))
            {
                using (w.FullBox("ctts", 0, 0))
                {
                    var runs = RunLength(track.Composition);
                    w.U32(runs.Count);
                    foreach (var (count, value) in runs)
                    {
                        w.U32(count);
                        w.U32(value);
                    }
                }
            }

            // stss — the sync sample table. ⚠ ABSENCE means "every sample is a sync sample", so it is
            // omitted only when they all are, and a stream with NO keyframes must still write it.
            if (track.IsVideo && Array.Exists(track.Samples, s => !s.KeyFrame))
            {
                using (w.FullBox("stss", 0, 0))
                {
                    var keys = new List<int>();
                    for (var i = 0; i < track.Samples.Length; i++)
                    {
                        if (track.Samples[i].KeyFrame) keys.Add(i + 1);   // 1-based
                    }
                    w.U32(keys.Count);
                    foreach (var index in keys) w.U32(index);
                }
            }

            // stsc — sample-to-chunk, itself run-length encoded over chunks with the same sample count.
            using (w.FullBox("stsc", 0, 0))
            {
                var entries = new List<(int FirstChunk, int Samples)>();
                for (var i = 0; i < track.ChunkSamples.Count; i++)
                {
                    if (entries.Count == 0 || entries[^1].Samples != track.ChunkSamples[i])
                    {
                        entries.Add((i + 1, track.ChunkSamples[i]));
                    }
                }

                w.U32(entries.Count);
                foreach (var (firstChunk, samples) in entries)
                {
                    w.U32(firstChunk);
                    w.U32(samples);
                    w.U32(1);                   // sample description index
                }
            }

            using (w.FullBox("stsz", 0, 0))
            {
                w.U32(0);                       // 0 = sizes vary, listed below
                w.U32(track.Samples.Length);
                foreach (var sample in track.Samples) w.U32(sample.Length);
            }

            using (w.FullBox("co64", 0, 0))
            {
                w.U32(track.ChunkOffsets.Count);
                foreach (var offset in track.ChunkOffsets) w.U64(mediaStart + offset);
            }
        }
    }

    private static List<(int Count, long Value)> RunLength(IReadOnlyList<long> values)
    {
        var runs = new List<(int Count, long Value)>();
        foreach (var value in values)
        {
            if (runs.Count > 0 && runs[^1].Value == value) runs[^1] = (runs[^1].Count + 1, value);
            else runs.Add((1, value));
        }
        return runs;
    }

    // ── sample entries ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A visual sample entry — <c>avc1</c> for H.264, <c>hvc1</c> for HEVC — wrapping
    /// <paramref name="config"/> VERBATIM: Matroska's <c>CodecPrivate</c> already IS an <c>avcC</c> payload,
    /// which is why a remux needs no codec work. ⚠ A source carrying Annex-B start codes would need
    /// converting first; this does not do it.
    /// </summary>
    public static byte[] VisualSampleEntry(string type, string configBox, int width, int height, byte[] config)
    {
        using var buffer = new MemoryStream();
        var w = new BoxWriter(buffer);
        using (w.Box(type))
        {
            w.Zeros(6);                         // reserved
            w.U16(1);                           // data reference index
            w.U16(0);                           // pre-defined
            w.U16(0);                           // reserved
            w.Zeros(12);                        // pre-defined
            w.U16(width);
            w.U16(height);
            w.U32(0x00480000);                  // 72 dpi horizontal
            w.U32(0x00480000);                  // 72 dpi vertical
            w.U32(0);                           // reserved
            w.U16(1);                           // frame count
            w.Zeros(32);                        // compressor name — a length-prefixed field, left empty
            w.U16(0x0018);                      // depth: colour with no alpha
            w.U16(0xFFFF);                      // pre-defined
            using (w.Box(configBox)) w.Raw(config);
        }
        return buffer.ToArray();
    }

    /// <summary>An audio sample entry — <c>mp4a</c> plus the elementary-stream descriptor AAC needs.</summary>
    public static byte[] AudioSampleEntry(int channels, double sampleRate, byte[] config)
    {
        using var buffer = new MemoryStream();
        var w = new BoxWriter(buffer);
        using (w.Box("mp4a"))
        {
            w.Zeros(6);                         // reserved
            w.U16(1);                           // data reference index
            w.U16(0);                           // version
            w.U16(0);                           // revision
            w.U32(0);                           // vendor
            w.U16(channels);
            w.U16(16);                          // sample size, in bits
            w.U16(0);                           // pre-defined
            w.U16(0);                           // reserved
            // ⚠ 16.16 fixed point cannot express a rate above 65535. CLAMPED rather than wrapped: the low
            // half of a wrapped rate is a plausible-looking number that plays at the wrong speed.
            w.Fixed1616(Math.Min(sampleRate, 65535));
            Esds(w, config);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// The MPEG-4 elementary stream descriptor: a nest of tag/length records whose only real cargo is the
    /// AudioSpecificConfig. ⚠ Lengths are written in the EXPANDED four-byte form
    /// (<c>0x80 0x80 0x80 n</c>), a fixed width, so a longer config cannot reshape the record around it.
    /// </summary>
    private static void Esds(BoxWriter w, byte[] config)
    {
        using (w.FullBox("esds", 0, 0))
        {
            w.U8(0x03);                                     // ES_Descriptor
            // ES id+flags (3) + DecoderConfig tag+len (5) + its content (13) + DecoderSpecificInfo
            // tag+len (5) + config + SLConfig (6). 🔴 That last term is 6 and not 3 — in the expanded form
            // an SLConfigDescriptor is tag+len+content — or every AAC esds declares itself three bytes
            // shorter than what follows it.
            DescriptorLength(w, 3 + 5 + 13 + 5 + config.Length + 6);
            w.U16(0);                                       // ES id
            w.U8(0);                                        // no dependency, no URL, no OCR

            w.U8(0x04);                                     // DecoderConfigDescriptor
            DescriptorLength(w, 13 + 5 + config.Length);
            w.U8(0x40);                                     // MPEG-4 audio
            w.U8(0x15);                                     // audio stream
            w.U24(0);                                       // buffer size
            w.U32(0);                                       // max bitrate — unknown, and not required
            w.U32(0);                                       // average bitrate

            w.U8(0x05);                                     // DecoderSpecificInfo
            DescriptorLength(w, config.Length);
            w.Raw(config);

            w.U8(0x06);                                     // SLConfigDescriptor
            DescriptorLength(w, 1);
            w.U8(0x02);                                     // predefined: MP4
        }
    }

    private static void DescriptorLength(BoxWriter w, int length)
    {
        w.U8(0x80 | ((length >> 21) & 0x7F));
        w.U8(0x80 | ((length >> 14) & 0x7F));
        w.U8(0x80 | ((length >> 7) & 0x7F));
        w.U8(length & 0x7F);
    }

    /// <summary>The sampling-frequency indices an AudioSpecificConfig encodes.</summary>
    private static readonly int[] AacSampleRates =
        [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary>
    /// Build a two-byte AAC-LC AudioSpecificConfig from a rate and channel count — the fallback for a track
    /// with no <c>CodecPrivate</c>. Null when the rate is not one AAC can index, because guessing produces a
    /// file that plays at the wrong speed.
    /// </summary>
    public static byte[]? SynthesiseAacConfig(double sampleRate, int channels)
    {
        var index = Array.IndexOf(AacSampleRates, (int)Math.Round(sampleRate));
        if (index < 0 || channels is < 1 or > 7) return null;

        // 5 bits object type (2 = AAC LC), 4 bits rate index, 4 bits channel config, 3 bits of flags.
        var bits = (2 << 11) | (index << 7) | (channels << 3);
        return [(byte)(bits >> 8), (byte)bits];
    }
}
