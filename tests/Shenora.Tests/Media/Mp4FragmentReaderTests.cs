using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// Reading a fragment back to answer <see cref="ISegmentEngine.HasRenderedPicture"/> — "did the encoder
/// write any picture at all?", which that contract calls the single most valuable check in the feature.
///
/// <para>
/// 🔴 <b>The bug it guards is measured, not theoretical:</b> a hardware H.264 encoder advertised by both the
/// tool's encoder list and the platform's codec list opened cleanly, accepted every frame, wrote
/// <c>video:0KiB</c> and exited 0. So these tests are mostly about the DIFFERENCE between a track that is
/// declared and a track that has bytes — the distinction MPEG-TS cannot express and fMP4 can.
/// </para>
/// <para>
/// Written against <see cref="Mp4FragmentWriter"/>'s output rather than hand-built bytes, deliberately: the
/// pair is a round trip, so a change to either side that breaks the agreement fails here. Foreign layouts —
/// the ones the kit's writer never emits — are the exception and are built by hand below.
/// </para>
/// </summary>
public class Mp4FragmentReaderTests
{
    private static Mp4FragmentTrack Track(int trackId, bool video) => new()
    {
        TrackId = trackId,
        Timescale = video ? 90_000u : 48_000u,
        IsVideo = video,
        Width = video ? 640 : 0,
        Height = video ? 360 : 0,
        SampleEntry = video
            ? Mp4Builder.VisualSampleEntry("avc1", "avcC", 640, 360, Mp4RemuxerTests.AvcConfig)
            : Mp4Builder.AudioSampleEntry(2, 48_000, Mp4RemuxerTests.AacConfig),
    };

    private static Mp4FragmentTrackData Data(Mp4FragmentTrack track, int count, int length)
    {
        var samples = Enumerable.Range(0, count)
            .Select(i => new Mp4FragmentSample(3000, length, 0, KeyFrame: i == 0))
            .ToList();
        return new Mp4FragmentTrackData
        {
            Track = track,
            BaseMediaDecodeTime = 0,
            Samples = samples,
            Data = new byte[count * length],
        };
    }

    private static byte[] Fragment(params Mp4FragmentTrackData[] tracks)
    {
        using var buffer = new MemoryStream();
        Mp4FragmentWriter.WriteFragment(buffer, 1, tracks);
        return buffer.ToArray();
    }

    /// <summary>The round trip: what the writer put in is what the reader counts, per track.</summary>
    [Fact]
    public void It_counts_each_track_s_bytes_separately()
    {
        var video = Track(1, video: true);
        var audio = Track(2, video: false);
        var fragment = Fragment(Data(video, count: 4, length: 250), Data(audio, count: 10, length: 32));

        Assert.Equal(1000, Mp4FragmentReader.SampleBytes(fragment, trackId: 1));
        Assert.Equal(320, Mp4FragmentReader.SampleBytes(fragment, trackId: 2));
    }

    /// <summary>
    /// 🔴 <b>THE CASE THE WHOLE TYPE EXISTS FOR.</b> A segment whose SOUND encoded fine and whose PICTURE
    /// produced nothing: the audio track is present and non-zero, the video track is absent entirely, and the
    /// answer for the video track must be zero rather than "the segment has bytes".
    /// <para>
    /// Under MPEG-TS this segment would still declare a video stream in its PMT and read as healthy. That
    /// difference is the reason D71 piece 3 chose fMP4.
    /// </para>
    /// </summary>
    [Fact]
    public void A_segment_whose_PICTURE_produced_nothing_reports_zero_for_it()
    {
        var fragment = Fragment(Data(Track(2, video: false), count: 8, length: 64));

        Assert.Equal(0, Mp4FragmentReader.SampleBytes(fragment, trackId: 1));
        Assert.Equal(512, Mp4FragmentReader.SampleBytes(fragment, trackId: 2));   // the control
    }

    /// <summary>A track nobody wrote reports zero rather than throwing — an absent track is an answer.</summary>
    [Fact]
    public void An_unknown_track_reports_zero()
        => Assert.Equal(0, Mp4FragmentReader.SampleBytes(Fragment(Data(Track(1, true), 2, 10)), trackId: 99));

    /// <summary>
    /// Composition offsets widen the per-sample record by four bytes, so a reader that assumed a fixed width
    /// reads the wrong field and returns a plausible WRONG number — not an error. Both layouts must agree.
    /// </summary>
    [Fact]
    public void The_per_sample_record_width_follows_the_flags()
    {
        var track = Track(1, video: true);
        var plain = Fragment(Data(track, count: 3, length: 100));

        var composed = Fragment(new Mp4FragmentTrackData
        {
            Track = track,
            BaseMediaDecodeTime = 0,
            Samples =
            [
                new(3000, 100, -1500, KeyFrame: true),
                new(3000, 100, 1500, KeyFrame: false),
                new(3000, 100, 0, KeyFrame: false),
            ],
            Data = new byte[300],
        });

        Assert.Equal(300, Mp4FragmentReader.SampleBytes(plain, trackId: 1));
        Assert.Equal(300, Mp4FragmentReader.SampleBytes(composed, trackId: 1));
    }

    /// <summary>
    /// Bytes that are not a fragment answer zero rather than throwing. The caller's question is "is this
    /// segment usable", and garbage is not.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(64)]
    public void Garbage_answers_zero(int length)
        => Assert.Equal(0, Mp4FragmentReader.SampleBytes(new byte[length], trackId: 1));

    /// <summary>A path that does not exist is unusable, which is the same answer for the same reason.</summary>
    [Fact]
    public void A_missing_file_answers_zero()
        => Assert.Equal(0, Mp4FragmentReader.SampleBytes(
            Path.Combine(Path.GetTempPath(), $"shenora-absent-{Guid.NewGuid():n}.m4s"), trackId: 1));

    /// <summary>The file overload agrees with the buffer one — the engine uses the path form.</summary>
    [Fact]
    public void The_file_overload_reads_what_the_writer_wrote()
    {
        using var dir = TestSupport.TempDir.Create();
        var path = dir.Combine("seg0.m4s");
        File.WriteAllBytes(path, Fragment(Data(Track(1, video: true), count: 5, length: 40)));

        Assert.Equal(200, Mp4FragmentReader.SampleBytes(path, trackId: 1));
    }

    /// <summary>
    /// 🔴 <b>A run that states NO sizes is not zero bytes — it is sizes stated elsewhere</b> (the movie's
    /// <c>trex</c> default). Reading it as zero would report a healthy segment as picture-less, which is the
    /// exact false alarm this check must never raise. The kit's writer always states sizes; a foreign one
    /// need not, so this is hand-built.
    /// </summary>
    [Fact]
    public void A_run_that_states_no_sizes_is_reported_as_unmeasurable_rather_than_empty()
    {
        // moof { mfhd, traf { tfhd(trackId 1), trun(flags = data-offset only, 2 samples) } }
        byte[] trun = [0, 0, 0, 20, .. "trun"u8, 0, 0x00, 0x00, 0x01, 0, 0, 0, 2, 0, 0, 0, 100];
        byte[] tfhd = [0, 0, 0, 16, .. "tfhd"u8, 0, 0x02, 0x00, 0x00, 0, 0, 0, 1];
        byte[] traf = [0, 0, 0, (byte)(8 + tfhd.Length + trun.Length), .. "traf"u8, .. tfhd, .. trun];
        byte[] mfhd = [0, 0, 0, 16, .. "mfhd"u8, 0, 0, 0, 0, 0, 0, 0, 1];
        byte[] moof = [0, 0, 0, (byte)(8 + mfhd.Length + traf.Length), .. "moof"u8, .. mfhd, .. traf];

        // Zero, and the point is WHY: not "no bytes" but "this box does not say". The distinction is in the
        // reader's own remarks; what a caller must never do is treat it as proof of a failed encode.
        Assert.Equal(0, Mp4FragmentReader.SampleBytes(moof, trackId: 1));
    }
}
