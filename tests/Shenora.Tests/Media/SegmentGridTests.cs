using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// The arithmetic a segment engine runs on (D71 piece 3.2b) — separated from the codec loop precisely so it
/// can be pinned here, since the loop itself needs a device.
///
/// <para>
/// Every case below is one whose failure is SILENT. A mis-sliced segment still appends, still plays, and
/// misbehaves only when somebody seeks — so none of this is discoverable by watching a film start.
/// </para>
/// </summary>
public class SegmentGridTests
{
    private const long TicksPerSecond = 1000;    // milliseconds, the reader's fallback unit

    private static MatroskaSample Sample(long ticks, bool keyFrame) => new(0, 1, ticks, keyFrame);

    // ── the grid a run may claim ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>A fractional grid is REFUSED, and the reason it must be is that nothing else would notice.</b>
    /// The kit's encoders emit a keyframe every second, so a 2.5-second grid puts every second boundary where
    /// none exists. Those segments are valid fMP4 and play from the start; only a seek reveals the fault.
    /// </summary>
    [Theory]
    [InlineData(2.5)]
    [InlineData(0.5)]
    [InlineData(6.001)]
    public void A_grid_that_cannot_start_on_a_keyframe_is_refused(double seconds)
    {
        Assert.False(SegmentGrid.IsUsable(seconds, out var reason));
        // The message has to name the coupling, or the next reader re-derives it from three files.
        Assert.Contains("keyframe", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(6.0)]     // SegmentStreamOptions' default
    [InlineData(10.0)]
    public void A_whole_multiple_of_the_encoder_interval_is_usable(double seconds)
    {
        Assert.True(SegmentGrid.IsUsable(seconds, out var reason));
        Assert.Empty(reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-6)]
    [InlineData(double.NaN)]
    public void A_nonsense_grid_is_refused(double seconds)
        => Assert.False(SegmentGrid.IsUsable(seconds, out _));

    // ── placing a time on the grid ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5_999, 0)]        // the last millisecond of segment 0
    [InlineData(6_000, 1)]        // the boundary belongs to the NEW segment
    [InlineData(17_999, 2)]
    [InlineData(18_000, 3)]
    public void A_time_lands_in_the_segment_that_contains_it(long ticks, int expected)
        => Assert.Equal(expected, SegmentGrid.SegmentOf(ticks, TicksPerSecond, 6.0));

    /// <summary>Start times round-trip with <c>SegmentOf</c> — a boundary is the first tick of its own segment.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(600)]
    public void A_segment_s_start_is_the_first_tick_of_that_segment(int index)
    {
        var start = SegmentGrid.StartTicks(index, TicksPerSecond, 6.0);

        Assert.Equal(index, SegmentGrid.SegmentOf(start, TicksPerSecond, 6.0));
        if (index > 0) Assert.Equal(index - 1, SegmentGrid.SegmentOf(start - 1, TicksPerSecond, 6.0));
    }

    /// <summary>Degenerate inputs answer 0 rather than dividing by zero — a run with no timescale is a log line, not a crash.</summary>
    [Fact]
    public void A_missing_timescale_answers_zero_rather_than_throwing()
    {
        Assert.Equal(0, SegmentGrid.SegmentOf(5_000, 0, 6.0));
        Assert.Equal(0, SegmentGrid.StartTicks(3, 0, 6.0));
        Assert.Equal(0, SegmentGrid.SegmentOf(-1_000, TicksPerSecond, 6.0));
    }

    // ── seeking in the source ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>The seek goes BACK to the previous keyframe, never forward and never to the exact boundary.</b>
    /// A decoder handed a non-keyframe emits garbage until the next one, so a run that starts feeding at the
    /// boundary produces a segment whose opening frames are macroblock soup — bytes that append cleanly and
    /// render wrongly.
    /// </summary>
    [Fact]
    public void The_seek_lands_on_the_last_keyframe_at_or_before_the_target()
    {
        // Keyframes at 0, 2s and 8s; the source's GOP is nothing like the output's.
        List<MatroskaSample> samples =
        [
            Sample(0, true), Sample(500, false), Sample(1_000, false),
            Sample(2_000, true), Sample(3_000, false), Sample(6_500, false),
            Sample(8_000, true), Sample(9_000, false),
        ];

        Assert.Equal(3, SegmentGrid.SeekIndex(samples, 6_000));   // 6s -> back to the 2s keyframe
        Assert.Equal(6, SegmentGrid.SeekIndex(samples, 8_000));   // exactly on one -> that one
        Assert.Equal(6, SegmentGrid.SeekIndex(samples, 8_500));
        Assert.Equal(0, SegmentGrid.SeekIndex(samples, 0));
    }

    /// <summary>
    /// A target before the first keyframe, and a track with none at all, both read from the beginning. A
    /// track with no sync sample is playable from its start and nowhere else, which is a log line rather than
    /// a refusal.
    /// </summary>
    [Fact]
    public void A_track_with_no_usable_keyframe_reads_from_the_start()
    {
        List<MatroskaSample> none = [Sample(0, false), Sample(1_000, false), Sample(2_000, false)];

        Assert.Equal(0, SegmentGrid.SeekIndex(none, 2_000));
        Assert.Equal(0, SegmentGrid.SeekIndex([], 5_000));
    }

    /// <summary>
    /// ⚠ The scan must not run past the target. A keyframe LATER in the file is not a seek target — using it
    /// silently drops everything between, so the segment starts late and the audio drifts against it.
    /// </summary>
    [Fact]
    public void A_keyframe_after_the_target_is_not_chosen()
    {
        List<MatroskaSample> samples = [Sample(0, true), Sample(1_000, false), Sample(9_000, true)];

        Assert.Equal(0, SegmentGrid.SeekIndex(samples, 5_000));
    }

    // ── cutting the output ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Both halves of the cut are required. Without the boundary test the segments are whatever length the
    /// encoder felt like and stop matching the manifest; without the keyframe test a segment opens mid-GOP
    /// and cannot be decoded alone — which is exactly what a page seeking into a film asks of it.
    /// </summary>
    [Fact]
    public void A_cut_needs_BOTH_a_new_boundary_and_a_keyframe()
    {
        // Past the boundary and a keyframe: cut.
        Assert.True(SegmentGrid.StartsNewSegment(6_000, keyFrame: true, current: 0, TicksPerSecond, 6.0));

        // Past the boundary but NOT a keyframe: keep writing — cutting here is undecodable.
        Assert.False(SegmentGrid.StartsNewSegment(6_040, keyFrame: false, current: 0, TicksPerSecond, 6.0));

        // A keyframe inside the CURRENT segment: not a boundary, so not a cut. The encoder emits one every
        // second while segments are six, so this is the common case rather than an edge one.
        Assert.False(SegmentGrid.StartsNewSegment(3_000, keyFrame: true, current: 0, TicksPerSecond, 6.0));

        // Never cut backwards — a late frame from a reordering encoder must not reopen a finished segment.
        Assert.False(SegmentGrid.StartsNewSegment(5_900, keyFrame: true, current: 1, TicksPerSecond, 6.0));
    }

    /// <summary>
    /// A run that starts mid-source cuts against ABSOLUTE segment indices, not a count of its own output.
    /// Getting this wrong is how a seek produces segment 0 named seg40 — the numbers agree with the manifest
    /// and the content does not.
    /// </summary>
    [Fact]
    public void A_run_that_starts_late_cuts_on_the_absolute_grid()
    {
        var start = SegmentGrid.StartTicks(40, TicksPerSecond, 6.0);
        Assert.Equal(240_000, start);
        Assert.Equal(40, SegmentGrid.SegmentOf(start, TicksPerSecond, 6.0));

        Assert.False(SegmentGrid.StartsNewSegment(start, keyFrame: true, current: 40, TicksPerSecond, 6.0));
        Assert.True(SegmentGrid.StartsNewSegment(start + 6_000, keyFrame: true, current: 40, TicksPerSecond, 6.0));
    }
}
