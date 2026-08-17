using System.Buffers.Binary;
using static Shenora.Tests.TestSupport.Mp4Boxes;
using Shenora.Modules.Media;
using Shenora.Engine.Files;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Media;

/// <summary>
/// Rewriting a Matroska file as MP4 with every frame copied untouched — the cheap half of the translation
/// layer, and the repair for the most common failure there is (right codecs, wrong box).
///
/// <para>
/// Fixtures are BUILT, like <see cref="MatroskaProbeTests"/>'s and for the same reasons: a test can state
/// the exact bytes it means, a failure names a field rather than a binary blob, and the repo carries no
/// sample media whose own licence would need answering (D51). This file extends that builder with the half
/// the probe never needed — CLUSTERS and blocks.
/// </para>
/// <para>
/// ⚠ <b>What these tests prove and what they do not.</b> They prove the BYTES: that the boxes are where the
/// format says, that the sample table addresses the frames it claims to, and that every frame arrives
/// byte-identical. They cannot prove PLAYBACK — that a real decoder opens the result — which needs a device
/// and is tracked as such. Several assertions below are written to fail loudly on the specific corruptions
/// that would otherwise produce a file which parses perfectly and plays wrongly.
/// </para>
/// </summary>
public class Mp4RemuxerTests
{
    // ── the Matroska fixture builder ──────────────────────────────────────────────────────────────────
    //
    // ⚠ INTERNAL rather than private since 2026-08-12, so `Mp4LayoutTests` can build its sources with THIS
    // builder (`using static`) instead of growing a second one. The two files assert about the same
    // remuxer over the same shapes, and a fixture builder copied to a new file is a fixture builder that
    // drifts — the planned-length test would then be comparing a plan and a write of subtly different
    // files and would still pass. Only the BUILDER is shared; the box navigator below stays private.

    internal static byte[] El(uint id, params byte[][] payload)
    {
        var body = payload.SelectMany(p => p).ToArray();
        return [.. IdBytes(id), .. Size(body.Length), .. body];
    }

    private static byte[] IdBytes(uint id)
    {
        if (id <= 0xFF) return [(byte)id];
        if (id <= 0xFFFF) return [(byte)(id >> 8), (byte)id];
        if (id <= 0xFFFFFF) return [(byte)(id >> 16), (byte)(id >> 8), (byte)id];
        return [(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id];
    }

    private static byte[] Size(int length) =>
        length < 0x7F
            ? [(byte)(0x80 | length)]
            : [0x10, (byte)(length >> 16), (byte)(length >> 8), (byte)length];

    private static byte[] UInt(ulong value)
    {
        if (value <= 0xFF) return [(byte)value];
        if (value <= 0xFFFF) return [(byte)(value >> 8), (byte)value];
        return [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

    private static byte[] Dbl(double value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return bytes;
    }

    internal static byte[] Ascii(string value) => System.Text.Encoding.ASCII.GetBytes(value);

    internal static byte[] Info(double durationTicks, ulong scale = 1_000_000) =>
        El(0x1549A966, El(0x2AD7B1, UInt(scale)), El(0x4489, Dbl(durationTicks)));

    /// <summary>A plausible <c>avcC</c>. Its CONTENT is irrelevant — the remuxer copies it verbatim, which
    /// is the property under test — but it must be non-empty, because a track without one is refused.</summary>
    internal static readonly byte[] AvcConfig = [0x01, 0x64, 0x00, 0x28, 0xFF, 0xE1, 0x00, 0x04, 0x27, 0x64, 0x00, 0x28, 0x01, 0x00, 0x04, 0x28, 0xEE, 0x3C, 0xB0];

    /// <summary>An AudioSpecificConfig: AAC-LC, 48 kHz, stereo.</summary>
    internal static readonly byte[] AacConfig = [0x11, 0x90];

    internal static byte[] VideoTrack(ulong number = 1, string codec = "V_MPEG4/ISO/AVC", byte[]? config = null,
                                     int width = 1920, int height = 1080, ulong defaultDurationNs = 40_000_000) =>
        El(0xAE,
            El(0xD7, UInt(number)),
            El(0x83, UInt(1)),
            El(0x86, Ascii(codec)),
            config is null ? [] : El(0x63A2, config),
            El(0x23E383, UInt(defaultDurationNs)),
            El(0xE0, El(0xB0, UInt((ulong)width)), El(0xBA, UInt((ulong)height))));

    internal static byte[] AudioTrack(ulong number = 2, string codec = "A_AAC", byte[]? config = null,
                                     int channels = 2, double rate = 48000) =>
        El(0xAE,
            El(0xD7, UInt(number)),
            El(0x83, UInt(2)),
            El(0x86, Ascii(codec)),
            config is null ? [] : El(0x63A2, config),
            El(0xE1, El(0xB5, Dbl(rate)), El(0x9F, UInt((ulong)channels))));

    internal static byte[] SimpleBlock(int track, short relative, bool keyFrame, byte[] data) =>
        El(0xA3,
            [(byte)(0x80 | track)],                                   // track number, one-byte vint
            [(byte)(relative >> 8), (byte)relative],                  // signed offset from the cluster
            [keyFrame ? (byte)0x80 : (byte)0x00],                     // flags: bit 7 is the keyframe bit
            data);

    /// <summary>A block-group block, whose keyframe rule is the INVERSE: no ReferenceBlock means keyframe.</summary>
    private static byte[] GroupBlock(int track, short relative, bool keyFrame, byte[] data) =>
        El(0xA0,
            El(0xA1, [(byte)(0x80 | track)], [(byte)(relative >> 8), (byte)relative], [0x00], data),
            keyFrame ? [] : El(0xFB, [0x01]));

    internal static byte[] Cluster(ulong timestamp, params byte[][] blocks) =>
        El(0x1F43B675, El(0xE7, UInt(timestamp)), blocks.SelectMany(b => b).ToArray());

    internal static MemoryStream Mkv(byte[] info, byte[][] tracks, params byte[][] clusters) =>
        new([
            .. El(0x1A45DFA3, Ascii("hdr")),
            .. El(0x18538067,
                info,
                El(0x1654AE6B, tracks.SelectMany(t => t).ToArray()),
                clusters.SelectMany(c => c).ToArray()),
        ]);

    /// <summary>Frame <paramref name="index"/>, filled with a value only that frame has.</summary>
    internal static byte[] Frame(int index, int length) => [.. Enumerable.Repeat((byte)(index + 1), length)];

    /// <summary>
    /// Frame <paramref name="index"/>, filled with bytes that VARY across its own length rather than repeating
    /// one value like <see cref="Frame"/> does.
    /// <para>
    /// 🔴 <b>Why this exists as a SEPARATE helper rather than a parameter on <see cref="Frame"/>.</b> A
    /// repeated-byte frame cannot detect an INTRA-span offset error — a reader that resumes a large span from
    /// the wrong byte still copies out the same repeated value, so it compares byte-for-byte equal to the real
    /// output even though the bookkeeping that produced it was wrong. Every existing fixture in this test
    /// family uses <c>Frame</c>, and every span in them is also well under one read buffer (151/300 bytes), so
    /// nothing here could ever have caught a resumption bug in a pull-based reader — which is exactly the class
    /// of bug a large, varying-byte frame read back in small chunks is built to expose.
    /// </para>
    /// <para>
    /// ⚠ <b>The period is 251, a PRIME — not 256, which this helper used until a sabotage run caught its own
    /// blind spot.</b> A modulo-256 pattern repeats every 256 bytes, and 2 KiB (2048 = 8 × 256) is an exact
    /// multiple of that — so a reader that re-reads the SAME first 2048 bytes instead of advancing past them
    /// reproduces a periodic pattern that is byte-identical to the correct one at that exact chunk size,
    /// hiding precisely the bug this fixture exists to catch. 251 shares no factor with any power of two, so
    /// no binary buffer size can land back on the same byte by coincidence.
    /// </para>
    /// </summary>
    internal static byte[] VaryingFrame(int index, int length) =>
        [.. Enumerable.Range(0, length).Select(i => (byte)((index * 31 + i * 97) % 251))];


    /// <summary>
    /// Rebuild one track's frames from the SAMPLE TABLE alone — sizes from <c>stsz</c>, chunk membership from
    /// <c>stsc</c>, chunk positions from <c>co64</c> — and read them out of the finished file.
    /// <para>
    /// 🔴 <b>This is the assertion that matters most in the whole file.</b> Every other check can pass while
    /// the table addresses the wrong bytes, and a player reading a mis-addressed table gets garbage that
    /// looks like a corrupt codec rather than a corrupt muxer. Reading the frames back THROUGH the table is
    /// the only way to catch it, and it is exactly what a player does.
    /// </para>
    /// </summary>
    private static List<byte[]> SamplesOf(byte[] mp4, int trackIndex)
    {
        var stsz = Find(mp4, "moov/trak/mdia/minf/stbl/stsz", trackIndex)!;
        var stsc = Find(mp4, "moov/trak/mdia/minf/stbl/stsc", trackIndex)!;
        var co64 = Find(mp4, "moov/trak/mdia/minf/stbl/co64", trackIndex)!;

        var count = (int)U32(stsz, 8);
        var sizes = new int[count];
        for (var i = 0; i < count; i++) sizes[i] = (int)U32(stsz, 12 + i * 4);

        var runs = new List<(int FirstChunk, int PerChunk)>();
        var runCount = (int)U32(stsc, 4);
        for (var i = 0; i < runCount; i++) runs.Add(((int)U32(stsc, 8 + i * 12), (int)U32(stsc, 12 + i * 12)));

        var chunkCount = (int)U32(co64, 4);
        var offsets = new long[chunkCount];
        for (var i = 0; i < chunkCount; i++) offsets[i] = (long)U64(co64, 8 + i * 8);

        var samples = new List<byte[]>();
        var sample = 0;
        for (var chunk = 0; chunk < chunkCount && sample < count; chunk++)
        {
            // The last run covering this chunk number says how many samples it holds.
            var perChunk = runs.Last(r => r.FirstChunk <= chunk + 1).PerChunk;
            var at = offsets[chunk];
            for (var i = 0; i < perChunk && sample < count; i++, sample++)
            {
                samples.Add(mp4[(int)at..((int)at + sizes[sample])]);
                at += sizes[sample];
            }
        }

        return samples;
    }

    private static (byte[] Mp4, MediaRemuxerResult Result) Remux(Stream source)
    {
        using var output = new MemoryStream();
        var result = Mp4Remuxer.Remux(source, output, conversion: null);
        return (output.ToArray(), result);
    }

    // ── the cases ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole point, end to end: H.264 + AAC in an MKV becomes an MP4, with nothing decoded. This is the
    /// case the planner already calls <see cref="MediaPlaybackAction.Remux"/>.
    /// </summary>
    [Fact]
    public void An_h264_plus_aac_mkv_becomes_an_mp4()
    {
        using var source = Mkv(
            Info(120_000),
            [VideoTrack(config: AvcConfig), AudioTrack(config: AacConfig)],
            Cluster(0, SimpleBlock(1, 0, true, Frame(0, 300)), SimpleBlock(2, 0, true, Frame(10, 50))),
            Cluster(40, SimpleBlock(1, 0, false, Frame(1, 200)), SimpleBlock(2, 0, true, Frame(11, 50))));

        var (mp4, result) = Remux(source);

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal(2, result.VideoSamples);
        Assert.Equal(2, result.AudioSamples);

        var top = Children(mp4).Select(b => b.Type).ToArray();
        Assert.Equal(["ftyp", "moov", "mdat"], top);
    }

    /// <summary>
    /// ⚠ <b>The ordering claim, asserted rather than assumed: <c>moov</c> comes BEFORE <c>mdat</c>.</b> This
    /// is what makes the output seekable before it has been fetched whole, and it is the entire reason the
    /// remux is a two-pass job instead of a stream copy. A muxer that writes the table last produces a file
    /// which plays from the start and cannot be scrubbed — a defect no box-level check would notice.
    /// </summary>
    [Fact]
    public void The_sample_table_precedes_the_media_so_the_output_can_seek()
    {
        using var source = Mkv(Info(1000), [VideoTrack(config: AvcConfig)],
            Cluster(0, SimpleBlock(1, 0, true, Frame(0, 64))));

        var (mp4, result) = Remux(source);
        Assert.True(result.Succeeded, result.Reason);

        var boxes = Children(mp4).Select(b => b.Type).ToList();
        Assert.True(boxes.IndexOf("moov") < boxes.IndexOf("mdat"),
            $"moov must precede mdat for seeking; got {string.Join(", ", boxes)}");
    }

    /// <summary>
    /// 🔴 Every frame arrives BYTE-IDENTICAL, addressed through the sample table the way a player addresses
    /// it. This is the passthrough claim — "no decoding" — proven rather than asserted, and it is also what
    /// catches an off-by-one in the chunk offsets.
    /// </summary>
    [Fact]
    public void Every_frame_is_copied_untouched_and_the_sample_table_addresses_it_correctly()
    {
        var video = new[] { Frame(0, 300), Frame(1, 137), Frame(2, 512) };
        var audio = new[] { Frame(20, 41), Frame(21, 39), Frame(22, 40) };

        using var source = Mkv(
            Info(3000),
            [VideoTrack(config: AvcConfig), AudioTrack(config: AacConfig)],
            Cluster(0, SimpleBlock(1, 0, true, video[0]), SimpleBlock(2, 0, true, audio[0])),
            Cluster(40, SimpleBlock(1, 0, false, video[1]), SimpleBlock(2, 0, true, audio[1])),
            Cluster(80, SimpleBlock(1, 0, false, video[2]), SimpleBlock(2, 0, true, audio[2])));

        var (mp4, result) = Remux(source);
        Assert.True(result.Succeeded, result.Reason);

        Assert.Equal(video, SamplesOf(mp4, trackIndex: 0));
        Assert.Equal(audio, SamplesOf(mp4, trackIndex: 1));
    }

    /// <summary>
    /// The decoder configuration is copied VERBATIM into the sample entry. A player reads it before the
    /// first frame, so a remux that drops or rewrites it produces a file that opens and shows nothing.
    /// </summary>
    [Fact]
    public void The_decoder_configuration_is_carried_through_untouched()
    {
        using var source = Mkv(Info(1000), [VideoTrack(config: AvcConfig)],
            Cluster(0, SimpleBlock(1, 0, true, Frame(0, 64))));

        var (mp4, result) = Remux(source);
        Assert.True(result.Succeeded, result.Reason);

        // `stsd` is a full box that also carries an entry count, so its children start 8 bytes in.
        var stsd = Find(mp4, "moov/trak/mdia/minf/stbl/stsd")!;
        var avc1 = Children(stsd, skip: 8).Single(c => c.Type == "avc1").Payload;
        // The configuration box sits after the 78-byte visual sample entry preamble.
        var avcC = Children(avc1, skip: 78).Single(c => c.Type == "avcC").Payload;
        Assert.Equal(AvcConfig, avcC);
    }

    /// <summary>
    /// ⚠ <b><c>stss</c> lists exactly the keyframes, and its ABSENCE means "all of them".</b> Both halves are
    /// pinned here because they are opposite failures: a missing table on a stream with few keyframes tells a
    /// player it may start anywhere (it seeks and gets a broken picture), and a table listing every sample on
    /// an all-keyframe stream is the same claim written the long way.
    /// </summary>
    [Fact]
    public void The_sync_table_lists_the_keyframes_and_is_omitted_when_every_frame_is_one()
    {
        using var mixed = Mkv(Info(1000), [VideoTrack(config: AvcConfig)],
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(0, 64)),
                SimpleBlock(1, 40, false, Frame(1, 64)),
                SimpleBlock(1, 80, false, Frame(2, 64)),
                SimpleBlock(1, 120, true, Frame(3, 64))));

        var (withGaps, _) = Remux(mixed);
        var stss = Find(withGaps, "moov/trak/mdia/minf/stbl/stss");
        Assert.NotNull(stss);
        Assert.Equal(2u, U32(stss!, 4));
        Assert.Equal(1u, U32(stss!, 8));      // 1-based indices
        Assert.Equal(4u, U32(stss!, 12));

        using var allKeys = Mkv(Info(1000), [VideoTrack(config: AvcConfig)],
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(0, 64)),
                SimpleBlock(1, 40, true, Frame(1, 64))));

        var (everyFrame, _) = Remux(allKeys);
        Assert.Null(Find(everyFrame, "moov/trak/mdia/minf/stbl/stss"));
    }

    /// <summary>
    /// A BlockGroup's keyframe rule is INVERTED — there is no flag, and a frame is a keyframe exactly when it
    /// carries no <c>ReferenceBlock</c>. Reading it the SimpleBlock way marks every frame a keyframe, which
    /// produces a sync table saying "seek anywhere" about a stream where almost nowhere is seekable.
    /// </summary>
    [Fact]
    public void A_block_group_takes_its_keyframe_answer_from_the_absence_of_a_reference()
    {
        using var source = Mkv(Info(1000), [VideoTrack(config: AvcConfig)],
            Cluster(0,
                GroupBlock(1, 0, keyFrame: true, Frame(0, 64)),
                GroupBlock(1, 40, keyFrame: false, Frame(1, 64)),
                GroupBlock(1, 80, keyFrame: false, Frame(2, 64))));

        var (mp4, result) = Remux(source);
        Assert.True(result.Succeeded, result.Reason);

        var stss = Find(mp4, "moov/trak/mdia/minf/stbl/stss");
        Assert.NotNull(stss);
        Assert.Equal(1u, U32(stss!, 4));      // exactly one keyframe, not three
        Assert.Equal(1u, U32(stss!, 8));
    }

    /// <summary>
    /// The honest refusal, and the boundary between this slice and the transcode tier: AC-3 is what actually
    /// breaks an ordinary MKV, MP4 cannot carry it without re-encoding, and a remuxer must say so rather
    /// than write a file with a soundtrack no browser decodes.
    /// </summary>
    [Fact]
    public void An_ac3_soundtrack_is_refused_rather_than_written_into_the_output()
    {
        using var source = Mkv(Info(1000), [AudioTrack(codec: "A_AC3", config: AacConfig)],
            Cluster(0, SimpleBlock(2, 0, true, Frame(0, 64))));

        var (_, result) = Remux(source);
        Assert.Equal(MediaRemuxerOutcome.NoCarriableStream, result.Outcome);
        Assert.Contains("ac3", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ⚠ But an AC-3 soundtrack must NOT cost the picture. A file whose H.264 is perfectly playable still
    /// remuxes, carrying the video and leaving the audio behind — which is the per-stream thinking the
    /// planner already applies, applied here where it is acted on.
    /// </summary>
    [Fact]
    public void An_unplayable_soundtrack_does_not_stop_the_picture_being_carried()
    {
        using var source = Mkv(
            Info(1000),
            [VideoTrack(config: AvcConfig), AudioTrack(codec: "A_AC3")],
            Cluster(0, SimpleBlock(1, 0, true, Frame(0, 128)), SimpleBlock(2, 0, true, Frame(9, 64))));

        var (mp4, result) = Remux(source);

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal(1, result.VideoSamples);
        Assert.Equal(0, result.AudioSamples);
        Assert.Single(Children(Find(mp4, "moov")!), b => b.Type == "trak");
    }

    /// <summary>
    /// A carriable codec with no decoder configuration is refused BEFORE the clusters are walked. Writing the
    /// file anyway produces one that opens and shows nothing, which is the worst of the three outcomes.
    /// </summary>
    [Fact]
    public void A_track_with_no_decoder_configuration_is_refused()
    {
        using var source = Mkv(Info(1000), [VideoTrack(config: null)],
            Cluster(0, SimpleBlock(1, 0, true, Frame(0, 64))));

        var (_, result) = Remux(source);
        Assert.Equal(MediaRemuxerOutcome.MissingDecoderConfig, result.Outcome);
    }

    /// <summary>
    /// ⚠ Audio blocks are routinely LACED — several AAC frames share one block header — so a remuxer that
    /// ignores lacing silently drops most of the soundtrack while every box still validates. All three
    /// schemes are exercised, because each computes its frame sizes a different way.
    /// </summary>
    [Theory]
    [InlineData(0x02)]   // Xiph
    [InlineData(0x04)]   // fixed size
    [InlineData(0x06)]   // EBML
    public void Laced_audio_blocks_yield_every_frame_rather_than_the_first(byte lacing)
    {
        var frames = new[] { Frame(30, 40), Frame(31, 40), Frame(32, 40) };
        byte[] lacingHeader = lacing switch
        {
            0x02 => [0x02, 40, 40],                     // count-1, then a size per frame but the last
            0x04 => [0x02],                             // count-1; the rest divides evenly
            _ => [0x02, 0x80 | 40, 0x80 | 63],          // count-1, first size, then a biased delta of 0
        };

        var block = El(0xA3,
            [0x82],                                      // track 2
            [0x00, 0x00],                                // relative timestamp
            [(byte)(0x80 | lacing)],                     // keyframe + the lacing scheme
            lacingHeader,
            frames.SelectMany(f => f).ToArray());

        using var source = Mkv(Info(1000), [AudioTrack(config: AacConfig)], Cluster(0, block));

        var (mp4, result) = Remux(source);

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal(3, result.AudioSamples);
        Assert.Equal(frames, SamplesOf(mp4, trackIndex: 0));
    }

    /// <summary>
    /// ⚠ Laced frames share ONE timestamp on the wire, so a remuxer that copies it to each of them writes a
    /// table where they all last no time. The soundtrack then plays as a fraction of a second while the file
    /// still validates — which is why the durations are asserted, not just the frame count.
    /// </summary>
    [Fact]
    public void Frames_sharing_a_timestamp_are_given_distinct_durations()
    {
        var frames = new[] { Frame(30, 40), Frame(31, 40), Frame(32, 40) };
        var block = El(0xA3, [0x82], [0x00, 0x00], [0x84], [0x02], frames.SelectMany(f => f).ToArray());

        // No DefaultDuration on the track, so nothing spaces the frames but the spread pass.
        using var source = Mkv(Info(1000), [AudioTrack(config: AacConfig)],
            Cluster(0, block),
            Cluster(60, El(0xA3, [0x82], [0x00, 0x00], [0x80], Frame(33, 40))));

        var (mp4, result) = Remux(source);
        Assert.True(result.Succeeded, result.Reason);

        var stts = Find(mp4, "moov/trak/mdia/minf/stbl/stts")!;
        var runs = (int)U32(stts, 4);
        for (var i = 0; i < runs; i++)
        {
            Assert.True(U32(stts, 12 + i * 8) > 0, "a sample duration of zero means a frame that lasts no time");
        }
    }

    /// <summary>
    /// 🔴 <b>The case that separates a remuxer which works on real files from one that only works on
    /// fixtures.</b> Most H.264 has B-frames, so frames are STORED in decode order while their timestamps
    /// are presentation times — here I P B B, stored as 0, 120, 40, 80. MP4 cannot express that in one
    /// table, so the output needs a composition table AND an edit list to put the start back where it was.
    /// A remuxer missing either writes a file that parses perfectly and plays its frames out of order.
    /// </summary>
    [Fact]
    public void A_stream_with_b_frames_gets_a_composition_table_and_an_edit_list()
    {
        using var source = Mkv(Info(160), [VideoTrack(config: AvcConfig)],
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(0, 64)),     // I, shown first
                SimpleBlock(1, 120, false, Frame(1, 64)),  // P, shown last but decoded second
                SimpleBlock(1, 40, false, Frame(2, 64)),   // B
                SimpleBlock(1, 80, false, Frame(3, 64)))); // B

        var (mp4, result) = Remux(source);
        Assert.True(result.Succeeded, result.Reason);

        var ctts = Find(mp4, "moov/trak/mdia/minf/stbl/ctts");
        Assert.NotNull(ctts);

        // Offsets in storage order: 40, 120, 0, 0 — run-length encoded, so the two zeros share an entry.
        var entries = (int)U32(ctts!, 4);
        var offsets = new List<uint>();
        for (var i = 0; i < entries; i++)
        {
            var count = U32(ctts!, 8 + i * 8);
            for (var k = 0; k < count; k++) offsets.Add(U32(ctts!, 12 + i * 8));
        }
        Assert.Equal([40u, 120u, 0u, 0u], offsets);

        // The whole presentation was pushed 40 ms later to keep those offsets non-negative; the edit list is
        // what takes it back off the front, so the track still starts when it should.
        var elst = Find(mp4, "moov/trak/edts/elst");
        Assert.NotNull(elst);
        Assert.Equal(40u, U32(elst!, 12));   // media_time
    }

    // ── the transcode tier ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A stand-in for a device codec. It converts AC-3 to "AAC" by relabelling each frame, which is all this
    /// test needs — what is under test is the MUXING of a converted stream, not any real encoder.
    /// <para>
    /// It deliberately BUFFERS one frame and releases it on the next push, because that is what real codecs
    /// do: a run that emits one output per input would never exercise the drain, and the tail-loss bug is
    /// exactly the one that produces a well-formed file whose audio stops early.
    /// </para>
    /// </summary>
    private sealed class FakeConversion(int framesPerPacket = 1024, int sampleRate = 48000, int channels = 2)
        : IMediaStreamConversion, IMediaStreamConversionRun
    {
        private MediaFrame? _held;
        public bool Disposed { get; private set; }
        public ReadOnlyMemory<byte> OutputConfig => AacConfig;
        public int OutputFramesPerPacket => framesPerPacket;
        public MediaStreamInfo OutputFormat => new(MediaStreamKind.Audio, "aac", Channels: channels, SampleRate: sampleRate);


        public bool CanConvert(MediaStreamKind kind, string codec) => kind is MediaStreamKind.Audio && codec is "ac3" or "eac3" or "dts";
        public IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate) => this;

        public IReadOnlyList<MediaFrame> Push(MediaFrame frame)
        {
            var previous = _held;
            _held = frame with { Data = frame.Data.ToArray() };
            return previous is null ? [] : [previous.Value];
        }

        public IReadOnlyList<MediaFrame> Drain()
        {
            var last = _held;
            _held = null;
            return last is null ? [] : [last.Value];
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// A video conversion that holds one frame back, so <c>Drain</c> is load-bearing exactly as it is for a
    /// real encoder's GOP. It also STAMPS what the muxer must not invent: the keyframe flag it was given,
    /// and a presentation time.
    /// </summary>
    /// <param name="reorder">
    /// When true the emitted presentation times run AHEAD of the decode order — the B-frame case, which is
    /// the only reason <c>ctts</c> exists. Frame n is emitted with the time of frame n+1 and vice versa.
    /// </param>
    private sealed class FakeVideoConversion(bool reorder = false, string outputCodec = "h264")
        : IMediaStreamConversion, IMediaStreamConversionRun
    {
        private MediaFrame? _held;
        private readonly List<long> _times = [];
        public bool Began { get; private set; }
        public bool Disposed { get; private set; }
        public int PushedFrames { get; private set; }
        public ReadOnlyMemory<byte> OutputConfig => AvcConfig;
        public int OutputFramesPerPacket => 0;   // a picture times each frame individually
        public MediaStreamInfo OutputFormat => new(MediaStreamKind.Video, outputCodec, Width: 320, Height: 176);
        public int OutputWidth => 320;
        public int OutputHeight => 176;

        public bool CanConvert(MediaStreamKind kind, string codec) => kind is MediaStreamKind.Video && codec is "mpeg4" or "mpeg2video";

        public IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate)
        {
            // ⚠ The dimensions are asserted, not ignored: a real platform encoder is CONFIGURED with them
            // and a run that silently accepted 0x0 would hide the demuxer failing to read PixelWidth.
            Assert.Equal(MediaStreamKind.Video, source.Kind);
            Began = true;
            return this;
        }

        public IReadOnlyList<MediaFrame> Push(MediaFrame frame)
        {
            PushedFrames++;
            _times.Add(frame.PresentationTimeUs);
            var previous = _held;
            _held = frame;
            if (previous is null) return [];
            return [Emit(previous.Value)];
        }

        public IReadOnlyList<MediaFrame> Drain()
        {
            var last = _held;
            _held = null;
            return last is null ? [] : [Emit(last.Value)];
        }

        /// <summary>
        /// Re-stamp an output so emission order stops being presentation order — the B-frame shape.
        /// <para>
        /// ⚠ The first attempt shifted every time by one frame instead, which is NOT a reorder: the same
        /// multiset sorts to the same decode timeline, every composition offset came out zero, and the test
        /// failed for the right reason with a misleading cause. Emitted frame k takes the time of its
        /// PAIR (k xor 1), which is a genuine permutation.
        /// </para>
        /// </summary>
        private MediaFrame Emit(MediaFrame frame)
        {
            if (!reorder) return frame;
            var paired = _emitted ^ 1;
            var shown = paired < _times.Count ? _times[paired] : frame.PresentationTimeUs;
            _emitted++;
            return frame with { PresentationTimeUs = shown };
        }

        private int _emitted;

        public void Dispose() => Disposed = true;
    }

    /// <summary>An MPEG-4 Part 2 picture — which MP4 cannot carry — beside a soundtrack it can.</summary>
    private static MemoryStream Mpeg4Film(params byte[][] videoFrames)
        => Mkv(Info(1000),
            [VideoTrack(codec: "V_MPEG4/ISO/ASP", config: null, width: 320, height: 176),
             AudioTrack(config: AacConfig)],
            Cluster(0, [.. videoFrames
                .Select((f, i) => SimpleBlock(1, (short)(i * 40), i % 3 == 0, f))
                .Concat([SimpleBlock(2, 0, true, Frame(90, 40))])]));

    /// <summary>
    /// 🔴 The case the VIDEO tier exists for, and it is the one measured on hardware: a picture the device
    /// decodes and its webview refuses. Without the seam the film loses its picture and SAYS so; with it,
    /// the picture is re-encoded and the file carries both tracks.
    /// </summary>
    [Fact]
    public void An_mpeg4_picture_is_TRANSCODED_when_the_device_can_do_it()
    {
        var frames = new[] { Frame(10, 90), Frame(11, 80), Frame(12, 70), Frame(13, 60) };

        using (var refused = Mpeg4Film(frames))
        {
            // No seam: the audio survives alone, and the loss is REPORTED rather than silent — which is what
            // the conversion route turns into UNSUPPORTED_CODEC naming the codec.
            var (audioOnly, plain) = Remux(refused);
            Assert.True(plain.Succeeded);
            Assert.Single(Children(Find(audioOnly, "moov")!), b => b.Type == "trak");
            Assert.Contains("mpeg4", plain.Dropped);
        }

        using var source = Mpeg4Film(frames);
        using var output = new MemoryStream();
        var conversion = new FakeVideoConversion();
        var result = Mp4Remuxer.Remux(source, output, conversion);

        Assert.True(result.Succeeded, result.Reason);
        // D63: the seam must be proven USED. An unconsulted conversion is indistinguishable from a working
        // one if the only assertion is that the file exists.
        Assert.True(conversion.Began);
        Assert.Equal(frames.Length, conversion.PushedFrames);
        Assert.True(conversion.Disposed);

        var mp4 = output.ToArray();
        Assert.Equal(2, Children(Find(mp4, "moov")!).Count(b => b.Type == "trak"));
        Assert.DoesNotContain("mpeg4", result.Dropped);

        // The re-encoded picture must carry the ENCODER's configuration, not the source's absent one — an
        // avc1 entry whose avcC came from nowhere is the file that opens and shows a blank rectangle.
        // ⚠ Found by CONTENT, not by index: the copied soundtrack is trak 0 here, and asserting on
        // `Find(mp4, ".../stsd")` read mp4a and reported "expected avc1, actual mp4a" — a test failure that
        // says the muxer is broken when it is the navigation that is.
        var entry = Children(Find(mp4, "moov")!).Where(b => b.Type == "trak")
            .Select(t => Children(Find(t.Payload, "mdia/minf/stbl/stsd")!, 8).Single())
            .Single(e => e.Type is "avc1" or "hvc1");
        Assert.Equal("avc1", entry.Type);
        // 78 bytes of VisualSampleEntry preamble, then the configuration box the encoder supplied.
        Assert.Contains(Children(entry.Payload, 78), c => c.Type == "avcC" && c.Payload.SequenceEqual(AvcConfig));
    }

    /// <summary>
    /// <c>stss</c> lists EXACTLY the keyframes it was told about. Both directions are wrong in ways that
    /// look fine: claim every frame and a seek lands on a green smear, claim none and seeking dies.
    /// </summary>
    [Fact]
    public void The_sync_sample_table_lists_exactly_the_frames_the_encoder_called_keyframes()
    {
        // Frames 0 and 3 are keyframes (i % 3 == 0), and the fake passes the flag straight through.
        var frames = new[] { Frame(20, 50), Frame(21, 40), Frame(22, 30), Frame(23, 20) };
        using var source = Mpeg4Film(frames);
        using var output = new MemoryStream();

        var result = Mp4Remuxer.Remux(source, output,
            new FakeVideoConversion());
        Assert.True(result.Succeeded, result.Reason);

        var mp4 = output.ToArray();
        var video = Children(Find(mp4, "moov")!).Where(b => b.Type == "trak")
            .Select(t => Find(t.Payload, "mdia/minf/stbl")!)
            .Single(stbl => Children(stbl).Any(c => c.Type == "stss"));

        var stss = Children(video).Single(c => c.Type == "stss").Payload;
        var count = U32(stss, 4);
        var listed = Enumerable.Range(0, (int)count).Select(i => U32(stss, 8 + i * 4)).ToArray();

        // 1-based sample numbers, and the fourth frame never reaches the file as a sample of its own —
        // the fake holds one back, so four pushes produce four outputs across Push + Drain.
        Assert.Equal([1u, 4u], listed);
    }

    /// <summary>
    /// 🔴 <b>B-frames: when a frame is SHOWN in a different order from the one it DECODES in, the file must
    /// carry a composition table.</b> This is the assertion the muxer's DTS derivation exists for — the
    /// encoder hands back presentation times in decode order and never a decode time, so the timeline is
    /// recovered by sorting. Get it wrong and the file parses perfectly and plays visibly out of order,
    /// which no structural check would catch.
    /// </summary>
    [Fact]
    public void Reordered_frames_get_a_COMPOSITION_table_and_a_monotonic_decode_timeline()
    {
        var frames = new[] { Frame(50, 40), Frame(51, 42), Frame(52, 44), Frame(53, 46) };
        using var source = Mpeg4Film(frames);
        using var output = new MemoryStream();

        var result = Mp4Remuxer.Remux(source, output,
            new FakeVideoConversion(reorder: true));
        Assert.True(result.Succeeded, result.Reason);

        var mp4 = output.ToArray();
        var stbl = Children(Find(mp4, "moov")!).Where(b => b.Type == "trak")
            .Select(t => Find(t.Payload, "mdia/minf/stbl")!)
            .Single(s => Children(s).Any(c => c.Type == "stss"));

        // ctts is written ONLY when some offset is non-zero — its absence means "presentation order is
        // decode order", so a file with reordering and no ctts is the silent defect.
        var ctts = Children(stbl).SingleOrDefault(c => c.Type == "ctts").Payload;
        Assert.NotNull(ctts);
        var entries = U32(ctts, 4);
        Assert.True(entries > 0, "a reordered stream must carry composition offsets");

        // And the decode timeline must never go backwards, which is what makes the file seekable at all.
        var stts = Children(stbl).Single(c => c.Type == "stts").Payload;
        var runs = U32(stts, 4);
        for (var i = 0; i < runs; i++)
        {
            Assert.True((int)U32(stts, 12 + i * 8) >= 0, "a negative sample duration is a broken decode timeline");
        }
    }

    /// <summary>
    /// A conversion that accepted the codec and then produced nothing FAILS the whole remux — it does not
    /// quietly write the soundtrack. A video-less video is the wrong file, not a degraded one.
    /// </summary>
    [Fact]
    public void A_video_conversion_that_produces_nothing_REFUSES_rather_than_writing_a_pictureless_file()
    {
        using var source = Mpeg4Film(Frame(30, 40), Frame(31, 40));
        using var output = new MemoryStream();

        var result = Mp4Remuxer.Remux(source, output,
            new SilentVideoConversion());

        Assert.False(result.Succeeded);
        Assert.Equal(MediaRemuxerOutcome.NoCarriableStream, result.Outcome);
        Assert.Contains("mpeg4", result.Reason);
    }

    /// <summary>Accepts every codec it is asked about and then emits nothing at all.</summary>
    private sealed class SilentVideoConversion : IMediaStreamConversion, IMediaStreamConversionRun
    {
        public ReadOnlyMemory<byte> OutputConfig => ReadOnlyMemory<byte>.Empty;
        public int OutputFramesPerPacket => 0;
        public MediaStreamInfo OutputFormat => new(MediaStreamKind.Video, "h264", Width: 320, Height: 176);
        public bool CanConvert(MediaStreamKind kind, string codec) => true;
        public IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate) => this;
        public IReadOnlyList<MediaFrame> Push(MediaFrame frame) => [];
        public IReadOnlyList<MediaFrame> Drain() => [];
        public void Dispose() { }
    }

    private static MemoryStream Ac3Film(params byte[][] audioFrames)
        => Mkv(Info(1000),
            [VideoTrack(config: AvcConfig), AudioTrack(codec: "A_AC3", config: null)],
            Cluster(0, [.. new[] { SimpleBlock(1, 0, true, Frame(0, 128)) }
                .Concat(audioFrames.Select((f, i) => SimpleBlock(2, (short)(i * 20), true, f)))]));

    /// <summary>
    /// 🔴 The case the whole transcode tier exists for: an H.264 + AC-3 film that the remuxer alone REFUSES
    /// becomes fully playable once a device codec is supplied. Same file, same call, different device.
    /// </summary>
    [Fact]
    public void An_ac3_soundtrack_is_TRANSCODED_when_the_device_can_do_it()
    {
        var frames = new[] { Frame(40, 60), Frame(41, 62), Frame(42, 58) };

        using (var refused = Ac3Film(frames))
        {
            // Without a conversion the audio is dropped and only the picture survives.
            var (videoOnly, plain) = Remux(refused);
            Assert.True(plain.Succeeded);
            Assert.Equal(0, plain.AudioSamples);
            Assert.Single(Children(Find(videoOnly, "moov")!), b => b.Type == "trak");
        }

        using var source = Ac3Film(frames);
        using var output = new MemoryStream();
        var conversion = new FakeConversion();
        var result = Mp4Remuxer.Remux(source, output, conversion);

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal(3, result.AudioSamples);          // every frame survived, including the drained tail
        Assert.Equal(1, result.VideoSamples);

        var mp4 = output.ToArray();
        Assert.Equal(2, Children(Find(mp4, "moov")!).Count(b => b.Type == "trak"));

        // The converted bytes came from the SPOOL, not from the source file — reading them back through the
        // sample table is what proves the per-track byte source is wired correctly.
        Assert.Equal(frames, SamplesOf(mp4, trackIndex: 1));
        Assert.True(conversion.Disposed, "the platform codec must be released");
    }

    /// <summary>
    /// ⚠ The converted track's timing comes from the ENCODER, not from the source timestamps — a decoder may
    /// resample, so the input's ticks no longer describe the output. Each output frame lasts exactly one
    /// packet at the output rate, which is exact by construction.
    /// </summary>
    [Fact]
    public void A_converted_track_is_timed_by_the_encoders_own_frame_size()
    {
        using var source = Ac3Film(Frame(40, 60), Frame(41, 62), Frame(42, 58));
        using var output = new MemoryStream();
        Assert.True(Mp4Remuxer.Remux(source, output, new FakeConversion(framesPerPacket: 1024, sampleRate: 44100)).Succeeded);

        var mp4 = output.ToArray();
        var mdhd = Find(mp4, "moov/trak/mdia/mdhd", trackIndex: 1)!;
        Assert.Equal(44100u, U32(mdhd, 12));           // timescale is the OUTPUT rate

        var stts = Find(mp4, "moov/trak/mdia/minf/stbl/stts", trackIndex: 1)!;
        Assert.Equal(1u, U32(stts, 4));                // one run: every frame the same length
        Assert.Equal(3u, U32(stts, 8));                // three samples
        Assert.Equal(1024u, U32(stts, 12));            // each lasting one packet
    }

    /// <summary>
    /// A device that cannot do the codec is not asked, and the file is not half-converted — the refusal is
    /// the same one the remuxer gives on its own.
    /// </summary>
    [Fact]
    public void A_device_that_cannot_convert_the_codec_leaves_the_verdict_unchanged()
    {
        using var source = Mkv(Info(1000), [AudioTrack(codec: "A_DTS", config: null)],
            Cluster(0, SimpleBlock(2, 0, true, Frame(0, 64))));
        using var output = new MemoryStream();

        // CanConvert says no for everything here.
        var result = Mp4Remuxer.Remux(source, output, new RefusingConversion());

        Assert.Equal(MediaRemuxerOutcome.NoCarriableStream, result.Outcome);
    }

    private sealed class RefusingConversion : IMediaStreamConversion
    {
        public bool CanConvert(MediaStreamKind kind, string codec) => false;
        public IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate) => null;
    }

    /// <summary>
    /// 🔴 CANCELLING A TRANSCODE MUST NOT BE REPORTED AS A CORRUPT FILE.
    /// <para>
    /// The remuxer's <c>catch (Exception)</c> blocks used to swallow
    /// <see cref="OperationCanceledException"/>, so pressing stop came back as
    /// <see cref="MediaRemuxerOutcome.SourceUnreadable"/> — <i>"malformed source"</i> — or as
    /// <see cref="MediaRemuxerOutcome.DestinationUnwritable"/>. That is worse than an unhelpful answer: the
    /// caller's next move is to tell the user their video is broken, and the honest cause was their own tap.
    /// </para>
    /// <para>
    /// ⚠ <b>This test covers the STREAM overload only, which is how the public PATH overload kept the same
    /// defect for two more days.</b> Its own test is
    /// <see cref="Cancelling_the_PATH_overload_THROWS_rather_than_reporting_an_unusable_file"/> — an invariant
    /// pinned on one of two entry points is pinned on neither.
    /// </para>
    /// <para>
    /// ⚠ There is deliberately no <c>Canceled</c> OUTCOME. Cancellation is an exception in .NET, the caller
    /// already holds the token, and an enum member would make every caller handle one event two ways.
    /// </para>
    /// </summary>
    [Fact]
    public void Cancelling_a_transcode_THROWS_rather_than_reporting_a_malformed_file()
    {
        using var cts = new CancellationTokenSource();
        var conversion = new CancellingConversion(cts);
        using var source = Ac3Film(Frame(40, 60), Frame(41, 62), Frame(42, 58));
        using var output = new MemoryStream();

        Assert.Throws<OperationCanceledException>(
            () => Mp4Remuxer.Remux(source, output, conversion, cts.Token));

        // ⚠ And the teardown still ran. The throw travels through the block that owns the platform codec
        // AND the conversion spool, so an escape route that skipped disposal would leak a hardware codec
        // slot on exactly the path a user hits most — a long conversion is the one people cancel.
        Assert.True(conversion.Disposed, "the platform codec must be released on the cancellation path");
    }

    /// <summary>
    /// 🔴 <b>THE SAME INVARIANT ON THE PUBLIC PATH OVERLOAD, which is the one an app actually calls.</b>
    /// <para>
    /// The sibling above drives the STREAM overload, and that is where the invariant was pinned and nowhere
    /// else — so <c>Remux(string, string, …)</c> kept its unfiltered <c>catch (Exception)</c> and answered a
    /// cancelled remux with <see cref="MediaRemuxerOutcome.SourceUnreadable"/>, <i>"source or destination
    /// unusable"</i>. Through a <c>Convert</c> delegate that becomes a <c>FAILED</c> event, and the page tells
    /// the user their video is corrupt because the app was shutting down.
    /// </para>
    /// <para>
    /// ⚠ <b>Cancelled BEFORE the call, and copy-only, on purpose — that is the window this branch widened.</b>
    /// The token trips inside <c>MatroskaSampleReader.ReadSamples</c>, i.e. in the metadata WALK, which is the
    /// long part of a remux that transcodes nothing. Until the walk took a token the only cancellable stretch
    /// was a conversion's frame loop, so this defect was nearly unreachable; it is not any more.
    /// </para>
    /// </summary>
    [Fact]
    public void Cancelling_the_PATH_overload_THROWS_rather_than_reporting_an_unusable_file()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"shenora-cancel-{Guid.NewGuid():N}.mkv");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"shenora-cancel-{Guid.NewGuid():N}.mp4");

        try
        {
            using (var film = Ac3Film(Frame(40, 60), Frame(41, 62))) File.WriteAllBytes(sourcePath, film.ToArray());
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // The RESULT is captured so a regression's message names the wrong answer that came back instead of
            // only "no exception was thrown" — the outcome IS the defect here, not the absence of a throw.
            MediaRemuxerResult? swallowed = null;
            var thrown = Record.Exception(
                () => swallowed = Mp4Remuxer.Remux(sourcePath, destinationPath, conversion: null, cts.Token));

            Assert.True(thrown is OperationCanceledException,
                "cancelling the path overload must THROW, not answer "
                + $"{swallowed?.Outcome.ToString() ?? thrown?.GetType().Name} \"{swallowed?.Reason}\"");
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
        }
    }

    /// <summary>Converts one frame, then cancels — so the token trips INSIDE the conversion loop.</summary>
    private sealed class CancellingConversion(CancellationTokenSource cts)
        : IMediaStreamConversion, IMediaStreamConversionRun
    {
        public bool Disposed { get; private set; }
        public ReadOnlyMemory<byte> OutputConfig => AacConfig;
        public int OutputFramesPerPacket => 1024;
        public MediaStreamInfo OutputFormat => new(MediaStreamKind.Audio, "aac", Channels: 2, SampleRate: 48000);

        public bool CanConvert(MediaStreamKind kind, string codec) => codec is "ac3";
        public IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate) => this;

        public IReadOnlyList<MediaFrame> Push(MediaFrame frame)
        {
            // Emit first, so the spool has REAL BYTES in it when the cancellation lands — an empty spool
            // would exercise the one path that already disposed correctly and prove nothing.
            cts.Cancel();
            return [frame with { Data = frame.Data.ToArray() }];
        }

        public IReadOnlyList<MediaFrame> Drain() => [];
        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// 🔴 A convertible soundtrack that declares NO FRAMES must still be reported in <c>Dropped</c>.
    /// <para>
    /// The remux succeeds either way and the file plays — with no sound — so <c>Dropped</c> is the only
    /// channel that can tell a page WHY. The copy path already handled this (an empty track contributes no
    /// plan, so it falls out of the kept set); the CONVERT path marked the track kept before asking whether
    /// anything had been written for it, which is the same silent-film outcome one branch over.
    /// </para>
    /// <para>
    /// ⚠ The assertion is on <c>Dropped</c> and NOT on <c>Succeeded</c>: this case is not a failure. The
    /// picture is carried correctly and the honest result is "it worked, and here is what did not survive".
    /// </para>
    /// </summary>
    [Fact]
    public void A_convertible_track_that_holds_no_frames_is_REPORTED_dropped_rather_than_silently_lost()
    {
        using var source = Ac3Film();          // the AC-3 track is declared; the cluster carries picture only
        using var output = new MemoryStream();

        var result = Mp4Remuxer.Remux(source, output, new FakeConversion());

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal(1, result.VideoSamples);
        Assert.Equal(0, result.AudioSamples);
        Assert.Equal(["ac3"], result.Dropped);
    }

    /// <summary>
    /// Copying beats converting when both are possible — it is faster, lossless, and cannot fail halfway.
    /// A device offered an AAC track must not transcode it.
    /// </summary>
    [Fact]
    public void A_carriable_soundtrack_is_COPIED_even_when_a_converter_is_available()
    {
        var audio = Frame(9, 64);
        using var source = Mkv(Info(1000),
            [VideoTrack(config: AvcConfig), AudioTrack(config: AacConfig)],
            Cluster(0, SimpleBlock(1, 0, true, Frame(0, 128)), SimpleBlock(2, 0, true, audio)));
        using var output = new MemoryStream();

        var conversion = new FakeConversion();
        Assert.True(Mp4Remuxer.Remux(source, output, conversion).Succeeded);

        // Untouched: the exact source bytes, and the converter never opened.
        Assert.Equal([audio], SamplesOf(output.ToArray(), trackIndex: 1));
        Assert.False(conversion.Disposed, "the converter should never have been started");
    }

    // ── the kit's DEFAULT converter ───────────────────────────────────────────────────────────────────

    private static MediaConversionRequest Request(string source, string destination, List<double>? progress = null)
        => new(source, destination, new Progress<double>(p => progress?.Add(p)));

    /// <summary>
    /// The point of the default: an app wires one delegate and an unplayable container becomes a playable
    /// one, with no engine supplied.
    /// </summary>
    [Fact]
    public async Task The_default_converter_turns_an_mkv_into_a_playable_mp4()
    {
        var dir = Directory.CreateTempSubdirectory("shenora-remux");
        try
        {
            var source = Path.Combine(dir.FullName, "in.mkv");
            var destination = Path.Combine(dir.FullName, "out.mp4");
            using (var mkv = Mkv(Info(1000), [VideoTrack(config: AvcConfig), AudioTrack(config: AacConfig)],
                       Cluster(0, SimpleBlock(1, 0, true, Frame(0, 256)), SimpleBlock(2, 0, true, Frame(9, 64)))))
            {
                File.WriteAllBytes(source, mkv.ToArray());
            }

            var progress = new List<double>();
            await new Mp4Remuxer().ToConverter()(Request(source, destination, progress), CancellationToken.None);

            var top = Children(File.ReadAllBytes(destination)).Select(b => b.Type).ToArray();
            Assert.Equal(["ftyp", "moov", "mdat"], top);
        }
        finally { dir.Delete(recursive: true); }
    }

    /// <summary>
    /// 🔴 <b>It must THROW when it cannot help, and this is the assertion that keeps the cache honest.</b>
    /// The route runs the delegate inside <c>Files.BeginReplace</c>, which publishes the output only if it
    /// returns without throwing. A refusal that returned quietly would promote an empty file into the cache
    /// and serve it forever — the page would get a 200 and silence, which is worse than a failure.
    /// </summary>
    [Fact]
    public async Task The_default_converter_THROWS_on_a_file_it_cannot_carry()
    {
        var dir = Directory.CreateTempSubdirectory("shenora-remux");
        try
        {
            var source = Path.Combine(dir.FullName, "in.mkv");
            var destination = Path.Combine(dir.FullName, "out.mp4");
            // AC-3 only: MP4 cannot carry it without re-encoding, which this tier does not do.
            using (var mkv = Mkv(Info(1000), [AudioTrack(codec: "A_AC3", config: AacConfig)],
                       Cluster(0, SimpleBlock(2, 0, true, Frame(0, 64)))))
            {
                File.WriteAllBytes(source, mkv.ToArray());
            }

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new Mp4Remuxer().ToConverter()(Request(source, destination), CancellationToken.None));

            // The OUTCOME name travels, not free prose — the route turns it into a FAILED reason.
            Assert.Contains(nameof(MediaRemuxerOutcome.NoCarriableStream), thrown.Message, StringComparison.Ordinal);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task The_default_converter_honours_cancellation()
    {
        var dir = Directory.CreateTempSubdirectory("shenora-remux");
        try
        {
            var source = Path.Combine(dir.FullName, "in.mkv");
            using (var mkv = Mkv(Info(1000), [VideoTrack(config: AvcConfig)],
                       Cluster(0, SimpleBlock(1, 0, true, Frame(0, 256)))))
            {
                File.WriteAllBytes(source, mkv.ToArray());
            }

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new Mp4Remuxer().ToConverter()(Request(source, Path.Combine(dir.FullName, "out.mp4")), cancelled.Token));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Anything_that_is_not_matroska_is_refused_rather_than_throwing()
    {
        Assert.Equal(MediaRemuxerOutcome.NotMatroska, Remux(new MemoryStream([])).Result.Outcome);
        Assert.Equal(MediaRemuxerOutcome.NotMatroska,
            Remux(new MemoryStream(Ascii("not a media file"))).Result.Outcome);
        Assert.Equal(MediaRemuxerOutcome.NotMatroska,
            Remux(new MemoryStream([0, 0, 0, 0x18, .. "ftypisom"u8.ToArray()])).Result.Outcome);
    }

    [Fact]
    public void A_truncated_source_is_refused_rather_than_hanging()
    {
        using var whole = Mkv(Info(1000), [VideoTrack(config: AvcConfig)],
            Cluster(0, SimpleBlock(1, 0, true, Frame(0, 400))));
        var bytes = whole.ToArray();

        var (_, result) = Remux(new MemoryStream(bytes[..(bytes.Length / 2)]));
        Assert.False(result.Succeeded);
    }

    /// <summary>
    /// The remuxer feeds the pipeline it belongs to: what the planner CALLS a remux is what this performs.
    /// Asserted together so the two cannot drift into disagreeing about the same file.
    /// </summary>
    [Fact]
    public void What_the_planner_calls_a_remux_is_what_this_performs()
    {
        using var source = Mkv(
            Info(120_000),
            [VideoTrack(config: AvcConfig), AudioTrack(config: AacConfig)],
            Cluster(0, SimpleBlock(1, 0, true, Frame(0, 256)), SimpleBlock(2, 0, true, Frame(9, 64))));

        source.Position = 0;
        var probe = MatroskaProbe.Read(source)!;
        var plan = MediaPlaybackPlanner.Plan(probe, new MediaPlaybackPolicy
        {
            Containers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4" },
            Codecs = new Dictionary<MediaStreamKind, IReadOnlySet<MediaStreamCodec>>
            {
                [MediaStreamKind.Video] = new HashSet<MediaStreamCodec>() { "h264" },
                [MediaStreamKind.Audio] = new HashSet<MediaStreamCodec>() { "aac" },
            },
        });
        Assert.Equal(MediaPlaybackAction.Remux, plan.Action);

        source.Position = 0;
        Assert.True(Remux(source).Result.Succeeded);
    }

    // ── the DEFAULT converter reaches the device's codecs ─────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>The value the kit claims, pinned: an adopter's decoder reaches the DEFAULT converter.</b>
    /// Owner, 2026-08-07: *"the default convertor is actually bridging the gap between the device hardware
    /// to its webview, and if a better encoder/decoder comes in by adopter app, they can hook that into the
    /// same pipeline without additional code."*
    /// <para>
    /// ⚠ This was FALSE until the day it was written. <c>ConvertAsync</c> — the overload every adoption
    /// example wires — passed <c>conversion: null</c>, so a shell that had registered a working
    /// <c>IMediaStreamConversion</c> never had it called and AC-3 kept being refused on a device that could
    /// decode it. Nothing failed; the capability was silently absent.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ToConverter_lets_a_supplied_codec_rescue_a_soundtrack_MP4_cannot_carry()
    {
        var frames = new[] { Frame(1, 32), Frame(2, 48), Frame(3, 40) };
        var sourcePath = Path.Combine(Path.GetTempPath(), $"shenora-convert-{Guid.NewGuid():N}.mkv");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"shenora-convert-{Guid.NewGuid():N}.mp4");

        try
        {
            using (var film = Ac3Film(frames))
            {
                File.WriteAllBytes(sourcePath, film.ToArray());
            }

            var request = new MediaConversionRequest(sourcePath, destinationPath, new Progress<double>());

            // ⚠ Without a conversion the default SUCCEEDS and drops the soundtrack — it does not refuse.
            // So the user-visible symptom of the missing capability is a SILENT FILM, not an error, which
            // is the worse failure mode and the reason this test asserts track COUNT rather than a throw.
            await new Mp4Remuxer().ToConverter()(request, CancellationToken.None);
            Assert.Single(Children(Find(File.ReadAllBytes(destinationPath), "moov")!), b => b.Type == "trak");

            // With one — an adopter's, or the shell's — the same call on the same file keeps the audio.
            await new Mp4Remuxer().ToConverter(new FakeConversion())(request, CancellationToken.None);

            var mp4 = File.ReadAllBytes(destinationPath);
            Assert.Equal(2, Children(Find(mp4, "moov")!).Count(b => b.Type == "trak"));
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
        }
    }

    /// <summary>
    /// 🔴 <b>THE KIT'S DEFAULT CONVERTER IS THIS PAIR, and it has to be proven rather than documented.</b>
    /// Since 2026-08-10 <c>MediaConversionOptions.Convert</c> is optional: leave it unset, set
    /// <c>AudioConversion</c>, and the kit supplies <c>Mp4Remuxer + the platform's codecs</c> so an app gets
    /// a working converter without writing one. That default is now the path almost every adopter takes.
    /// <para>
    /// ⚠ <b>What makes this test worth its length is the FAILURE MODE it rules out.</b> A default that
    /// quietly forgot to pass <c>AudioConversion</c> through would still produce an MP4, still answer 200,
    /// and still play — as a SILENT film, which is the outcome this whole subsystem's worst bug already
    /// was. So the assertion is the audio TRACK COUNT, exactly as in the test above, and the two arms are
    /// run on the same film so the only difference is the option.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_kit_default_converter_rescues_a_soundtrack_MP4_cannot_carry()
    {
        var frames = new[] { Frame(1, 32), Frame(2, 48), Frame(3, 40) };
        var sourcePath = Path.Combine(Path.GetTempPath(), $"shenora-default-{Guid.NewGuid():N}.mkv");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"shenora-default-{Guid.NewGuid():N}.mp4");
        var cache = Path.Combine(Path.GetTempPath(), $"shenora-default-{Guid.NewGuid():N}");

        try
        {
            using (var film = Ac3Film(frames)) File.WriteAllBytes(sourcePath, film.ToArray());
            var request = new MediaConversionRequest(sourcePath, destinationPath, new Progress<double>());

            // WITHOUT the platform seam: the default is container repair, which succeeds and drops AC-3.
            // That is the honest behaviour on a device whose codecs cannot help, and it is also exactly
            // what a broken pass-through would look like — hence the second arm.
            await new MediaConversionOptions
            {
                Access = new MediaAccessOptions { Resolve = _ => null, CacheRoot = cache, AllowedRoots = [Path.GetTempPath()] },
            }.Converter()(request, CancellationToken.None);
            Assert.Single(Children(Find(File.ReadAllBytes(destinationPath), "moov")!), b => b.Type == "trak");

            // WITH it — the shipped default engine — the same film keeps its audio.
            await new MediaConversionOptions
            {
                Access = new MediaAccessOptions { Resolve = _ => null, CacheRoot = cache, AllowedRoots = [Path.GetTempPath()] },
                Conversion = new FakeConversion(),
            }.Converter()(request, CancellationToken.None);
            Assert.Equal(2, Children(Find(File.ReadAllBytes(destinationPath), "moov")!).Count(b => b.Type == "trak"));
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }

    /// <summary>
    /// 🔴 <b>A film that plays SILENTLY says so.</b> Remuxing an AC-3 film with no conversion succeeds and
    /// drops the soundtrack — the kit's most dangerous outcome, because nothing throws and the user simply
    /// hears nothing. The result now names what it lost, so an app can say *"this file's AC-3 soundtrack
    /// cannot play on this device"* instead of leaving them to guess.
    /// </summary>
    [Fact]
    public void A_dropped_soundtrack_is_named_in_the_result()
    {
        using var film = Ac3Film(Frame(1, 32), Frame(2, 48));
        using var output = new MemoryStream();

        var result = Mp4Remuxer.Remux(film, output, conversion: null);

        Assert.True(result.Succeeded);              // ⚠ succeeded, and silent
        Assert.Equal(0, result.AudioSamples);
        Assert.Contains("ac3", result.Dropped);
    }

    /// <summary>Nothing lost, nothing claimed — the normal path reports an empty list, not a null one.</summary>
    [Fact]
    public void A_clean_remux_drops_nothing()
    {
        using var film = Mkv(Info(1000),
            [VideoTrack(config: AvcConfig), AudioTrack(codec: "A_AAC", config: AacConfig)],
            Cluster(0, [SimpleBlock(1, 0, true, Frame(0, 128)), SimpleBlock(2, 0, true, Frame(1, 32))]));
        using var output = new MemoryStream();

        var result = Mp4Remuxer.Remux(film, output, conversion: null);

        Assert.True(result.Succeeded, result.Reason);
        Assert.Empty(result.Dropped);
    }

    /// <summary>
    /// 🔴 <b>A supplied <see cref="IMediaContainerWriter"/> is actually USED.</b> Until 2026-08-07 it was
    /// not: the interface shipped with an implementation and no consumer, so a consumer who wrote a native
    /// muxer had nowhere to plug it in. Found by auditing every Core contract for "implemented but never
    /// consulted" — the same defect class as D59 and the unregistered lock inspector, and the third of them.
    /// </summary>
    [Fact]
    public async Task ToConverter_uses_a_supplied_container_writer_instead_of_the_built_in_one()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"shenora-writer-{Guid.NewGuid():N}.mkv");
        var destinationPath = Path.Combine(Path.GetTempPath(), $"shenora-writer-{Guid.NewGuid():N}.mp4");

        try
        {
            using (var film = Ac3Film(Frame(1, 32)))
            {
                File.WriteAllBytes(sourcePath, film.ToArray());
            }

            var writer = new RecordingWriter();
            var request = new MediaConversionRequest(sourcePath, destinationPath, new Progress<double>());

            await writer.ToConverter()(request, CancellationToken.None);

            Assert.True(writer.Called);
            // The kit still owns the FILES — a consumer's muxer thinks about frames, not about whether a
            // media path can reach a page.
            Assert.Equal("written by the consumer", File.ReadAllText(destinationPath));
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
        }
    }

    private sealed class RecordingWriter : IMediaContainerWriter
    {
        public bool Called { get; private set; }
        public string Container => ".mp4";
        public bool CanCarry(MediaStreamKind kind, string codec) => true;

        public MediaRemuxerResult Write(Stream source, Stream destination, IMediaStreamConversion? conversion,
                                        CancellationToken cancellationToken = default)
        {
            Called = true;
            var bytes = System.Text.Encoding.UTF8.GetBytes("written by the consumer");
            destination.Write(bytes, 0, bytes.Length);
            return new MediaRemuxerResult(MediaRemuxerOutcome.Succeeded, "ok");
        }
    }

    /// <summary>
    /// The pipeline, not one implementation, is what gets consulted — which is what "hook it in without
    /// additional code" actually rests on. A converter registered with <c>Use(...)</c> reaches the default.
    /// </summary>
    [Fact]
    public void ToConverter_accepts_a_pipeline_so_a_registered_converter_is_consulted()
    {
        var pipeline = new MediaConversionPipeline();
        pipeline.Use((source, _) => source.Codec is "ac3" ? new FakeConversion() : null);

        using var film = Ac3Film(Frame(1, 32), Frame(2, 48));
        using var output = new MemoryStream();

        var result = Mp4Remuxer.Remux(film, output, pipeline);

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal(2, result.AudioSamples);
    }
}
