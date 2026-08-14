using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// The one genuinely subtle calculation in a remux: Matroska stores when a frame is SHOWN, MP4 stores when
/// it is DECODED, and the two differ exactly when a stream has B-frames — which is most real H.264.
///
/// <para>
/// 🔴 <b>Why this is tested apart from the remuxer.</b> For a stream without B-frames the derivation is the
/// identity, so a remuxer can be built, tested against simple content, and ship looking correct while
/// mangling the majority of real files. Pinning the reordering case as a pure function is what stops that:
/// there is no fixture to get wrong and no container in the way, just the numbers.
/// </para>
/// </summary>
public class SampleTimingTests
{
    /// <summary>
    /// The common case costs nothing: a stream already in presentation order needs no composition offsets
    /// and no shift, so <c>ctts</c> is omitted entirely and the timeline is untouched.
    /// </summary>
    [Fact]
    public void A_stream_already_in_presentation_order_is_left_exactly_as_it_is()
    {
        long[] presentation = [0, 40, 80, 120, 160];

        var (decode, composition, shift) = SampleTiming.Derive(presentation);

        Assert.Equal(presentation, decode);
        Assert.All(composition, offset => Assert.Equal(0, offset));
        Assert.Equal(0, shift);
    }

    /// <summary>
    /// The reordering case, with the numbers written out. Display order is I B B P; decode order is I P B B,
    /// so the presentation times arrive as 0, 3, 1, 2 — the shape every H.264 file with B-frames has.
    /// </summary>
    [Fact]
    public void A_b_frame_reorder_produces_a_monotonic_decode_timeline_and_non_negative_offsets()
    {
        long[] presentation = [0, 3, 1, 2];

        var (decode, composition, shift) = SampleTiming.Derive(presentation);

        Assert.Equal([0, 1, 2, 3], decode);
        Assert.Equal(1, shift);
        Assert.Equal([1, 3, 0, 0], composition);

        // And the property that matters more than the exact numbers: decode + composition reconstructs the
        // original presentation order, shifted as one block. A player reading the two tables gets the frames
        // back in the order they were meant to be shown.
        for (var i = 0; i < presentation.Length; i++)
        {
            Assert.Equal(presentation[i] + shift, decode[i] + composition[i]);
        }
    }

    /// <summary>
    /// ⚠ <b>The two invariants a player depends on, over every reordering shape rather than one.</b> A
    /// negative composition offset cannot be expressed in the table version this writes, and a decode
    /// timeline that goes backwards asks for a frame to be decoded before one already decoded — both produce
    /// a file that parses cleanly and plays wrongly, which is the failure mode with no other detector.
    /// </summary>
    [Theory]
    [InlineData(new long[] { 0, 3, 1, 2 })]
    [InlineData(new long[] { 0, 6, 2, 4, 12, 8, 10 })]
    [InlineData(new long[] { 4, 3, 2, 1, 0 })]                 // fully reversed — the worst case
    [InlineData(new long[] { 0, 1, 2, 3, 4 })]                 // already ordered
    [InlineData(new long[] { 7 })]                             // one frame
    [InlineData(new long[] { 0, 0, 0 })]                       // all tied
    public void The_decode_timeline_never_goes_backwards_and_no_offset_is_ever_negative(long[] presentation)
    {
        var (decode, composition, shift) = SampleTiming.Derive(presentation);

        for (var i = 1; i < decode.Length; i++)
        {
            Assert.True(decode[i] >= decode[i - 1], $"decode went backwards at {i}");
        }

        Assert.All(composition, offset => Assert.True(offset >= 0, "a composition offset was negative"));

        for (var i = 0; i < presentation.Length; i++)
        {
            Assert.Equal(presentation[i] + shift, decode[i] + composition[i]);
        }
    }

    [Fact]
    public void An_empty_track_is_answered_rather_than_thrown_at()
    {
        var (decode, composition, shift) = SampleTiming.Derive([]);
        Assert.Empty(decode);
        Assert.Empty(composition);
        Assert.Equal(0, shift);
    }

    /// <summary>
    /// ⚠ Frames sharing a timestamp come from LACING, where a dozen AAC frames ride one block header. Left
    /// tied they become zero-length entries in the duration table, and the soundtrack plays as a fraction of
    /// a second — while every box in the file validates.
    /// </summary>
    [Fact]
    public void Frames_sharing_a_timestamp_are_spread_up_to_the_next_real_one()
    {
        long[] presentation = [0, 0, 0, 0, 40];

        var spread = SampleTiming.SpreadTies(presentation, fallbackStep: 0);

        Assert.Equal([0, 10, 20, 30, 40], spread);
    }

    /// <summary>At the end of a track there is no next timestamp, so the track's declared frame duration is
    /// what spaces the last run.</summary>
    [Fact]
    public void A_tied_run_at_the_end_of_a_track_falls_back_to_the_declared_frame_duration()
    {
        long[] presentation = [0, 40, 40, 40];

        var spread = SampleTiming.SpreadTies(presentation, fallbackStep: 20);

        Assert.Equal(0, spread[0]);
        Assert.Equal(40, spread[1]);
        Assert.True(spread[2] > spread[1], "a tied frame must not keep the time of the one before it");
        Assert.True(spread[3] > spread[2], "a tied frame must not keep the time of the one before it");
    }

    [Fact]
    public void Untied_timestamps_are_left_alone()
    {
        long[] presentation = [0, 21, 43, 64];
        Assert.Equal(presentation, SampleTiming.SpreadTies(presentation, fallbackStep: 21));
    }

    /// <summary>
    /// A duration of zero means a frame that occupies no time. It shortens the track, and a player reads the
    /// result as a truncated file — so no sample may ever get one, including the last.
    /// </summary>
    [Fact]
    public void No_sample_is_ever_given_a_duration_of_zero()
    {
        Assert.All(SampleTiming.Durations([0, 40, 80], fallbackStep: 40), d => Assert.True(d > 0));
        Assert.All(SampleTiming.Durations([0, 40, 80], fallbackStep: 0), d => Assert.True(d > 0));
        Assert.All(SampleTiming.Durations([0], fallbackStep: 0), d => Assert.True(d > 0));
    }

    [Fact]
    public void A_sample_lasts_until_the_next_one_starts()
    {
        var durations = SampleTiming.Durations([0, 40, 100], fallbackStep: 40);

        Assert.Equal(40, durations[0]);
        Assert.Equal(60, durations[1]);
        Assert.Equal(40, durations[2]);   // the last borrows the track's declared duration
    }

    /// <summary>
    /// The media box takes its 64-bit header only when the compact one cannot express the size. Pinned as a
    /// pure function because the boundary is at 4 GiB, which no test is going to reach with a real file —
    /// and an untested branch there writes a header no player can read.
    /// </summary>
    [Theory]
    [InlineData(0L, 8)]
    [InlineData(1024L, 8)]
    [InlineData(4294967287L, 8)]      // uint.MaxValue - 8: the largest the compact header can announce
    [InlineData(4294967288L, 16)]     // one byte more
    [InlineData(8589934592L, 16)]     // 8 GiB
    public void The_media_header_widens_only_when_the_compact_one_cannot_hold_the_size(long mediaBytes, int expected)
        => Assert.Equal(expected, Mp4Remuxer.MediaHeaderBytesFor(mediaBytes));
}
