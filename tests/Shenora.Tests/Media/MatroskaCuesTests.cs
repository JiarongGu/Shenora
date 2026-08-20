using Shenora.Modules.Media;
using static Shenora.Tests.Media.Mp4RemuxerTests;

namespace Shenora.Tests.Media;

/// <summary>
/// Reading a source's OWN keyframe index instead of walking every cluster to rediscover it.
///
/// <para>
/// 🔴 <b>A broken index is worse than an absent one, which is the whole reason these checks exist.</b>
/// Absent falls back to the walk and everything still works; broken puts every segment boundary somewhere
/// no decoder can start, in a stream whose bytes are individually valid and whose manifest agrees with
/// itself. Nothing downstream can notice — so the refusals are the feature, not the fast path.
/// </para>
/// <para>
/// ⚠ These fixtures are BUILT, so they prove the checks and not the format. The claim that a real file's
/// index agrees with a real walk is <c>RealSourceSegmentTests</c>'s, against an ffmpeg-muxed clip.
/// </para>
/// </summary>
public class MatroskaCuesTests
{
    private const ulong Video = 1;

    /// <summary>Three keyframes two seconds apart, on the default 1 ms tick.</summary>
    private static byte[][] Clusters =>
    [
        Cluster(0, SimpleBlock(1, 0, keyFrame: true, Frame(0, 40))),
        Cluster(2000, SimpleBlock(1, 0, keyFrame: true, Frame(1, 40))),
        Cluster(4000, SimpleBlock(1, 0, keyFrame: true, Frame(2, 40))),
    ];

    private static readonly (ulong Time, int Cluster)[] EveryCluster = [(0, 0), (2000, 1), (4000, 2)];

    private static MemoryStream Build((ulong Time, int Cluster)[] cues, bool viaSecondSeekHead = false,
                                      long positionShift = 0, ulong cueTrack = Video,
                                      double durationTicks = 6000) =>
        MkvIndexed(Info(durationTicks), [VideoTrack(config: AvcConfig)], cueTrack, cues,
                   viaSecondSeekHead, positionShift, Clusters);

    /// <summary>Read the header, then ask for the index. Null is the "walk it instead" answer.</summary>
    private static IReadOnlyList<long>? IndexOf(MemoryStream mkv, ulong track = Video)
    {
        using (mkv)
        {
            var reader = new MatroskaSampleReader(mkv);
            Assert.True(reader.ReadHeader(), "the fixture is not readable Matroska");
            return reader.KeyFrameTicksFromCues(track);
        }
    }

    // ── the fast path ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ordinary real-world layout: a SeekHead at the front naming Cues written at the end. The reader
    /// stops at the first cluster, so the SeekHead is the ONLY thing that can tell it where the index is.
    /// </summary>
    [Fact]
    public void An_index_named_by_a_SeekHead_is_read()
    {
        Assert.Equal([0, 2000, 4000], IndexOf(Build(EveryCluster)));
    }

    /// <summary>
    /// 🔴 <b>A SeekHead pointing at ANOTHER SeekHead is followed.</b> MKVToolNix writes exactly this
    /// whenever an in-place header edit outgrows the space reserved for it — it is spec-legal and it is on
    /// real people's disks. Handling only one level reports "no index" for a file that has a perfectly good
    /// one, which costs a full walk on every open and, elsewhere, has hung a player outright.
    /// </summary>
    [Fact]
    public void A_SeekHead_that_points_at_another_SeekHead_is_followed()
    {
        Assert.Equal([0, 2000, 4000], IndexOf(Build(EveryCluster, viaSecondSeekHead: true)));
    }

    // ── the refusals ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>Cues are PER TRACK, and the soundtrack's are worthless for cutting.</b> Every audio frame is a
    /// sync sample, so an audio index says "you may cut anywhere" — boundaries no video decoder can start
    /// at. The segments would still append, and only a seek would show it.
    /// </summary>
    [Fact]
    public void An_index_that_describes_a_different_track_is_not_used()
    {
        Assert.Null(IndexOf(Build(EveryCluster, cueTrack: 2)));
    }

    /// <summary>One point says nothing about spacing — the same refusal ffmpeg's demuxer makes.</summary>
    [Fact]
    public void An_index_with_fewer_than_two_points_is_refused()
    {
        Assert.Null(IndexOf(Build([(0, 0)])));
    }

    /// <summary>
    /// Times that do not ascend are not a timeline, and a plan cannot express a boundary that goes
    /// backwards — <c>SegmentPlan.Cuts</c> would reject it later, after the walk had been skipped for it.
    /// </summary>
    [Fact]
    public void An_index_whose_times_go_backwards_is_refused()
    {
        Assert.Null(IndexOf(Build([(0, 0), (4000, 2), (2000, 1)])));
    }

    /// <summary>
    /// A last cue past the file's declared duration. ⚠ Not hypothetical: this shape has broken playback on
    /// real hardware for at least one shipping media server, whose fix was to clamp it.
    /// </summary>
    [Fact]
    public void An_index_reaching_past_the_declared_duration_is_refused()
    {
        Assert.Null(IndexOf(Build([(0, 0), (2000, 1), (99_000, 2)])));
    }

    /// <summary>
    /// 🔴 <b>The off-by-segment bug, which is the one that produces a STRUCTURALLY PERFECT index pointing
    /// at nothing.</b> Stored positions are relative to the Segment's data; treat them as file offsets (or
    /// the reverse) and every time is still ascending, still inside the duration, still on the right track.
    /// Only the positions are nonsense — and a reader that never looks at one cannot tell.
    /// </summary>
    [Fact]
    public void An_index_whose_positions_do_not_land_on_a_cluster_is_refused()
    {
        Assert.Null(IndexOf(Build(EveryCluster, positionShift: 7)));
    }

    /// <summary>
    /// The other direction for the check above, or it would be satisfied by refusing everything: the SAME
    /// fixture with the shift removed is accepted.
    /// </summary>
    [Fact]
    public void The_same_index_without_the_shift_is_accepted()
    {
        Assert.NotNull(IndexOf(Build(EveryCluster, positionShift: 0)));
    }
}
