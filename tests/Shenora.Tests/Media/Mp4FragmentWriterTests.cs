using Shenora.Modules.Media;
using static Shenora.Tests.TestSupport.Mp4Boxes;

namespace Shenora.Tests.Media;

/// <summary>
/// Fragmented MP4 — an <c>init</c> segment plus numbered media segments (D71 piece 3, the half that makes
/// <c>Transcode</c> deliverable before the source has been fully read).
///
/// <para>
/// These prove the BYTES and cannot prove PLAYBACK, exactly as <see cref="Mp4RemuxerTests"/> says of its own.
/// What makes that split bearable here is that fMP4's failure modes are almost all silent: a MediaSource
/// <c>appendBuffer</c> rejects a malformed segment with an event nobody listens to, and a segment whose data
/// offset is four bytes wrong appends CLEANLY and plays noise. So every assertion below is aimed at a
/// specific corruption that would otherwise parse.
/// </para>
/// <para>
/// Input is synthetic — this writer has no codec and no demuxer, which is the point of it being a separate
/// type. Nothing here needs media.
/// </para>
/// </summary>
public class Mp4FragmentWriterTests
{
    private const uint VideoTimescale = 90_000;

    private static Mp4FragmentTrack VideoTrack(int trackId = 1) => new()
    {
        TrackId = trackId,
        Timescale = VideoTimescale,
        IsVideo = true,
        Width = 640,
        Height = 360,
        SampleEntry = Mp4Builder.VisualSampleEntry("avc1", "avcC", 640, 360, Mp4RemuxerTests.AvcConfig),
    };

    private static Mp4FragmentTrack AudioTrack(int trackId = 2) => new()
    {
        TrackId = trackId,
        Timescale = 48_000,
        IsVideo = false,
        SampleEntry = Mp4Builder.AudioSampleEntry(2, 48_000, Mp4RemuxerTests.AacConfig),
    };

    /// <summary>Samples whose bytes are a recognisable per-sample pattern, so a mis-addressed run is visible.</summary>
    private static (List<Mp4FragmentSample> Samples, byte[] Data) Payload(int count, int length, int firstKey = 0)
    {
        var samples = new List<Mp4FragmentSample>();
        var data = new List<byte>();
        for (var i = 0; i < count; i++)
        {
            samples.Add(new Mp4FragmentSample(3000, length, 0, KeyFrame: i == firstKey));
            data.AddRange(Enumerable.Repeat((byte)(i + 1), length));
        }
        return (samples, [.. data]);
    }

    private static byte[] Fragment(int sequence, params Mp4FragmentTrackData[] tracks)
    {
        using var buffer = new MemoryStream();
        Mp4FragmentWriter.WriteFragment(buffer, sequence, tracks);
        return buffer.ToArray();
    }

    private static byte[] Init(params Mp4FragmentTrack[] tracks)
    {
        using var buffer = new MemoryStream();
        Mp4FragmentWriter.WriteInitSegment(buffer, tracks);
        return buffer.ToArray();
    }

    // ── the init segment ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b><c>mvex</c> is what separates a fragmented movie from an EMPTY one.</b> The tracks declare zero
    /// samples, which without this box entitles a reader to conclude the movie holds nothing — a file that
    /// opens, reports no duration and plays nothing, with no error anywhere.
    /// </summary>
    [Fact]
    public void The_init_segment_declares_fragments_with_a_trex_per_track()
    {
        var init = Init(VideoTrack(), AudioTrack());

        var mvex = Find(init, "moov/mvex");
        Assert.NotNull(mvex);
        var trex = Children(mvex).Where(c => c.Type == "trex").ToList();
        Assert.Equal(2, trex.Count);

        // trex payload after the version/flags word: track_ID, then the sample description index.
        Assert.Equal(1u, U32(trex[0].Payload, 4));
        Assert.Equal(2u, U32(trex[1].Payload, 4));
        Assert.Equal(1u, U32(trex[0].Payload, 8));
    }

    /// <summary>
    /// The sample tables are empty and the DECODER CONFIGURATION is not. A fragment never repeats the
    /// configuration, so an init segment that dropped it produces segments no decoder can open — and the
    /// first symptom is an append that fails on segment 1 with the movie header looking perfect.
    /// </summary>
    [Fact]
    public void The_init_segment_indexes_no_samples_but_still_carries_the_codec_configuration()
    {
        var init = Init(VideoTrack());

        Assert.Equal(0u, U32(Find(init, "moov/trak/mdia/minf/stbl/stts")!, 4));   // entry count
        Assert.Equal(0u, U32(Find(init, "moov/trak/mdia/minf/stbl/stsc")!, 4));
        Assert.Equal(0u, U32(Find(init, "moov/trak/mdia/minf/stbl/stsz")!, 8));   // sample count
        Assert.Equal(0u, U32(Find(init, "moov/trak/mdia/minf/stbl/stco")!, 4));

        var avc1 = Find(init, "moov/trak/mdia/minf/stbl/stsd/avc1");
        Assert.NotNull(avc1);
        Assert.Contains(Children(avc1, 78), c => c.Type == "avcC");
    }

    /// <summary>
    /// A fragmented movie's duration is UNKNOWN when its init segment is written, and saying otherwise is
    /// worse than saying nothing: a reader that trusts a wrong <c>mvhd</c> duration draws a scrub bar of the
    /// wrong length and refuses to seek past it. The real length reaches the page through the manifest.
    /// </summary>
    [Fact]
    public void The_init_segment_claims_no_duration()
    {
        var mvhd = Find(Init(VideoTrack()), "moov/mvhd")!;

        Assert.Equal(0u, U32(mvhd, 16));      // duration, after version/flags + times + timescale
    }

    [Fact]
    public void An_init_segment_needs_at_least_one_track()
        => Assert.Throws<ArgumentException>(() => Init());

    // ── a media segment ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>THE ASSERTION THAT MATTERS MOST HERE, and the reason this suite exists.</b> <c>trun</c>'s data
    /// offset points from the start of the <c>moof</c> to the track's first sample byte — a distance that
    /// INCLUDES the size of the <c>moof</c> stating it. Every other check in this file can pass while that
    /// number is wrong by the width of one box, and the result appends without error and plays noise.
    /// <para>
    /// So this walks the offset the way a decoder does — from the moof, into the mdat — and demands the
    /// bytes it lands on are the ones the caller supplied.
    /// </para>
    /// </summary>
    [Fact]
    public void The_data_offset_lands_on_the_first_sample_byte()
    {
        var (samples, data) = Payload(count: 4, length: 10);
        var fragment = Fragment(1, new Mp4FragmentTrackData
        {
            Track = VideoTrack(),
            BaseMediaDecodeTime = 0,
            Samples = samples,
            Data = data,
        });

        var boxes = Children(fragment);
        var moofStart = boxes.TakeWhile(b => b.Type != "moof").Sum(b => b.Payload.Length + 8);
        var offset = (int)U32(Find(fragment, "moof/traf/trun")!, 8);      // after version/flags + sample count

        // The offset is measured from the moof, so resolve it against the whole segment and read.
        var landed = fragment[(moofStart + offset)..(moofStart + offset + data.Length)];
        Assert.Equal(data, landed);
    }

    /// <summary>
    /// Two tracks share one <c>mdat</c>, so the SECOND track's offset must clear the first's bytes. An
    /// implementation that gave both the same offset plays the video's bytes as audio — which decodes to
    /// silence or noise rather than failing.
    /// </summary>
    [Fact]
    public void A_second_track_s_data_offset_clears_the_first_track_s_bytes()
    {
        var (videoSamples, videoData) = Payload(count: 3, length: 16);
        var (audioSamples, audioData) = Payload(count: 5, length: 7);

        var fragment = Fragment(1,
            new Mp4FragmentTrackData { Track = VideoTrack(), BaseMediaDecodeTime = 0, Samples = videoSamples, Data = videoData },
            new Mp4FragmentTrackData { Track = AudioTrack(), BaseMediaDecodeTime = 0, Samples = audioSamples, Data = audioData });

        var moofStart = Children(fragment).TakeWhile(b => b.Type != "moof").Sum(b => b.Payload.Length + 8);
        var videoOffset = (int)U32(Find(fragment, "moof/traf/trun", trackIndex: 0)!, 8);
        var audioOffset = (int)U32(Find(fragment, "moof/traf/trun", trackIndex: 1)!, 8);

        Assert.Equal(videoOffset + videoData.Length, audioOffset);
        Assert.Equal(videoData, fragment[(moofStart + videoOffset)..(moofStart + videoOffset + videoData.Length)]);
        Assert.Equal(audioData, fragment[(moofStart + audioOffset)..(moofStart + audioOffset + audioData.Length)]);
    }

    /// <summary>
    /// <c>tfdt</c> is where a fragment says WHEN it is, and it must be the running total rather than zero.
    /// A run that restarts at a seek numbers from the index it was asked for, so a writer that always wrote
    /// zero would stack every segment at the start of the timeline — playback then repeats the first few
    /// seconds forever, which looks like a decoder fault rather than an index one.
    /// </summary>
    [Fact]
    public void The_decode_time_is_written_as_given_and_is_64_bit()
    {
        var (samples, data) = Payload(count: 2, length: 8);
        // Past 2^32 on a 90 kHz timescale — about 13 hours, which the 32-bit form silently wraps.
        const long late = 5_000_000_000L;

        var fragment = Fragment(9, new Mp4FragmentTrackData
        {
            Track = VideoTrack(),
            BaseMediaDecodeTime = late,
            Samples = samples,
            Data = data,
        });

        var tfdt = Find(fragment, "moof/traf/tfdt")!;
        Assert.Equal(1, tfdt[0]);                             // version 1 — the 64-bit form
        Assert.Equal((ulong)late, U64(tfdt, 4));
        Assert.Equal(9u, U32(Find(fragment, "moof/mfhd")!, 4));
    }

    /// <summary>
    /// 🔴 The sync flag's two halves must agree. A sync sample declares <c>sample_depends_on = 2</c> AND
    /// clears <c>sample_is_non_sync_sample</c>; writing only one leaves a stream that looks seekable to one
    /// reader and not to another, and the symptom of seeking to a non-key frame is macroblock soup rather
    /// than an error.
    /// </summary>
    [Fact]
    public void A_key_frame_and_a_non_key_frame_disagree_in_BOTH_halves_of_the_sample_flags()
    {
        var (samples, data) = Payload(count: 2, length: 4, firstKey: 0);
        var trun = Find(Fragment(1, new Mp4FragmentTrackData
        {
            Track = VideoTrack(),
            BaseMediaDecodeTime = 0,
            Samples = samples,
            Data = data,
        }), "moof/traf/trun")!;

        // Per sample: duration, size, flags — 12 bytes each, after version/flags + count + data offset.
        var key = U32(trun, 12 + 8);
        var nonKey = U32(trun, 12 + 12 + 8);

        Assert.Equal(2u << 24, key);                          // depends on nothing, and IS a sync sample
        Assert.Equal(1u << 24, nonKey & (0xFFu << 24));       // depends on something
        Assert.Equal(1u << 16, nonKey & (1u << 16));          // and is flagged non-sync
    }

    /// <summary>
    /// Composition offsets are written ONLY when some sample needs one — the rule <c>Mp4Builder</c> already
    /// applies to <c>ctts</c>. Four bytes per sample a reader walks for nothing is the cost of getting this
    /// wrong in the common (B-frame-less) case.
    /// </summary>
    [Fact]
    public void An_all_zero_composition_table_is_omitted_and_a_needed_one_is_signed()
    {
        var (plain, data) = Payload(count: 2, length: 4);
        var plainTrun = Find(Fragment(1, new Mp4FragmentTrackData
        {
            Track = VideoTrack(), BaseMediaDecodeTime = 0, Samples = plain, Data = data,
        }), "moof/traf/trun")!;

        Assert.Equal(0, plainTrun[0]);                        // version 0
        Assert.Equal(0u, U32(plainTrun, 0) & 0x000800);       // no composition-offset flag
        Assert.Equal(2 * 12, plainTrun.Length - 12);          // 3 fields per sample, not 4

        var composed = new List<Mp4FragmentSample>
        {
            new(3000, 4, -1500, KeyFrame: true),              // negative: legal here, unlike the whole-file writer
            new(3000, 4, 1500, KeyFrame: false),
        };
        var composedTrun = Find(Fragment(1, new Mp4FragmentTrackData
        {
            Track = VideoTrack(), BaseMediaDecodeTime = 0, Samples = composed, Data = data,
        }), "moof/traf/trun")!;

        Assert.Equal(1, composedTrun[0]);                     // version 1 — SIGNED offsets
        Assert.Equal(0x000800u, U32(composedTrun, 0) & 0x000800);
        Assert.Equal(2 * 16, composedTrun.Length - 12);       // 4 fields per sample
        Assert.Equal(unchecked((uint)-1500), U32(composedTrun, 12 + 12));
    }

    /// <summary>
    /// A track contributing nothing to this segment is OMITTED rather than written as an empty <c>traf</c>.
    /// An empty run is legal and pointless; more usefully, omitting it keeps the <c>traf</c> order and the
    /// data offsets in step, which an empty entry would silently shift.
    /// </summary>
    [Fact]
    public void A_track_with_no_samples_in_this_segment_is_left_out()
    {
        var (samples, data) = Payload(count: 2, length: 5);

        var fragment = Fragment(1,
            new Mp4FragmentTrackData { Track = VideoTrack(), BaseMediaDecodeTime = 0, Samples = samples, Data = data },
            new Mp4FragmentTrackData { Track = AudioTrack(), BaseMediaDecodeTime = 0, Samples = [], Data = ReadOnlyMemory<byte>.Empty });

        var moof = Find(fragment, "moof")!;
        Assert.Single(Children(moof).Where(c => c.Type == "traf"));
    }

    [Fact]
    public void A_fragment_carrying_no_samples_at_all_is_refused()
        => Assert.Throws<ArgumentException>(() => Fragment(1, new Mp4FragmentTrackData
        {
            Track = VideoTrack(), BaseMediaDecodeTime = 0, Samples = [], Data = ReadOnlyMemory<byte>.Empty,
        }));

    /// <summary>
    /// The mdat's declared length must cover its payload exactly. A short one truncates the last sample —
    /// which a reader discovers only when it decodes that far, so the segment appends and then stalls.
    /// </summary>
    [Fact]
    public void The_media_box_is_exactly_as_long_as_the_samples_it_holds()
    {
        var (videoSamples, videoData) = Payload(count: 3, length: 11);
        var (audioSamples, audioData) = Payload(count: 2, length: 6);

        var fragment = Fragment(1,
            new Mp4FragmentTrackData { Track = VideoTrack(), BaseMediaDecodeTime = 0, Samples = videoSamples, Data = videoData },
            new Mp4FragmentTrackData { Track = AudioTrack(), BaseMediaDecodeTime = 0, Samples = audioSamples, Data = audioData });

        var mdat = Children(fragment).Single(c => c.Type == "mdat");
        Assert.Equal(videoData.Length + audioData.Length, mdat.Payload.Length);
        // …and the segment ends there: no padding, nothing after.
        Assert.Equal(fragment.Length, Children(fragment).Sum(b => b.Payload.Length + 8));
    }

    /// <summary>
    /// The box ORDER is the contract a streaming reader depends on: <c>styp</c>, then <c>moof</c>, then
    /// <c>mdat</c>. A reader that meets <c>mdat</c> first has no index for it and must buffer the whole thing
    /// before it can do anything — which is the property fragmenting exists to remove.
    /// </summary>
    [Fact]
    public void A_segment_is_styp_then_moof_then_mdat()
        => Assert.Equal(["styp", "moof", "mdat"],
            Children(Fragment(1, new Mp4FragmentTrackData
            {
                Track = VideoTrack(),
                BaseMediaDecodeTime = 0,
                Samples = Payload(count: 1, length: 4).Samples,
                Data = Payload(count: 1, length: 4).Data,
            })).Select(b => b.Type));
}
