using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// WHERE THE CUTS ARE (D76) — the object a manifest and a production run are both built from, and the
/// arithmetic that derives it from a source's own keyframes.
///
/// <para>
/// Every case below is one whose failure is SILENT. A boundary the producer cannot land on exactly, a
/// TARGETDURATION under an EXTINF, a plan that skips a second: all of them append, all of them play, and the
/// fault only shows up as a seek that arrives somewhere else. That is why this is a pure type with its own
/// suite rather than something the codec loop is trusted to get right.
/// </para>
/// </summary>
public class SegmentPlanTests
{
    private static readonly TimeSpan Twenty = TimeSpan.FromSeconds(20);

    private static MatroskaSample Sample(long ticks, bool keyFrame) => new(0, 1, ticks, keyFrame);

    /// <summary>Milliseconds — what every real Matroska file uses, and the reader's own fallback.</summary>
    private static readonly SourceTimeline Ms = SourceTimeline.For(1_000_000);

    // ── the uniform grid ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_grid_covers_the_whole_source_and_its_TAIL_is_the_remainder()
    {
        var plan = SegmentPlan.Grid(6.0, Twenty);

        Assert.Equal(6.0, plan.GridSeconds);
        Assert.Equal(4, plan.Count);                 // 6 + 6 + 6 + 2
        Assert.Equal(18.0, plan.StartOf(3), 6);
        Assert.Equal(6.0, plan.LengthOf(0), 6);
        // ⚠ The load-bearing one: a playlist's declared total is the SUM of its lengths, so a flat last entry
        // overstates the source by up to a whole segment and a scrub bar built on it seeks past the end.
        Assert.Equal(2.0, plan.LengthOf(3), 6);
        Assert.Equal(20.0, Enumerable.Range(0, plan.Count).Sum(plan.LengthOf), 6);
    }

    [Fact]
    public void A_grid_shorter_than_one_segment_is_a_single_segment_of_the_whole_source()
    {
        var plan = SegmentPlan.Grid(6.0, TimeSpan.FromSeconds(2));

        Assert.Equal(1, plan.Count);
        Assert.Equal(2.0, plan.LengthOf(0), 6);
        // TARGETDURATION must not claim six seconds for a two-second stream.
        Assert.Equal(2.0, plan.LongestSeconds, 6);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(5.999, 0)]
    [InlineData(6.0, 1)]        // the boundary belongs to the NEW segment
    [InlineData(17.999, 2)]
    [InlineData(18.0, 3)]
    [InlineData(50.0, 3)]       // past the end clamps to the last, rather than naming a segment nobody has
    public void A_time_lands_in_the_grid_segment_that_contains_it(double seconds, int expected)
        => Assert.Equal(expected, SegmentPlan.Grid(6.0, Twenty).IndexOf(seconds));

    [Fact]
    public void A_nonsense_grid_is_refused_at_construction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SegmentPlan.Grid(0, Twenty));
        Assert.Throws<ArgumentOutOfRangeException>(() => SegmentPlan.Grid(-6, Twenty));
        Assert.Throws<ArgumentOutOfRangeException>(() => SegmentPlan.Grid(6, TimeSpan.Zero));
    }

    // ── explicit cuts ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Explicit_cuts_state_each_segment_s_own_length()
    {
        var plan = SegmentPlan.Cuts([0, 7.5, 13.2], Twenty)!;

        Assert.Null(plan.GridSeconds);
        Assert.Equal(3, plan.Count);
        Assert.Equal(7.5, plan.LengthOf(0), 6);
        Assert.Equal(5.7, plan.LengthOf(1), 6);
        Assert.Equal(6.8, plan.LengthOf(2), 6);
        Assert.Equal(20.0, Enumerable.Range(0, plan.Count).Sum(plan.LengthOf), 6);
    }

    /// <summary>
    /// 🔴 <b>The LONGEST segment, which is what <c>#EXT-X-TARGETDURATION</c> has to cover.</b> A derived plan
    /// routinely holds a segment longer than the length that was asked for, and a TARGETDURATION below any
    /// EXTINF breaks a MUST in the playlist spec — so a strict reader may refuse a stream whose bytes are
    /// perfectly good.
    /// </summary>
    [Fact]
    public void The_longest_segment_is_reported_for_the_playlist_s_target()
    {
        Assert.Equal(7.5, SegmentPlan.Cuts([0, 7.5, 13.2], Twenty)!.LongestSeconds, 6);
        Assert.Equal(9.0, SegmentPlan.Cuts([0, 4.0, 11.0], Twenty)!.LongestSeconds, 6);   // the TAIL is longest
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(7.4999, 0)]
    [InlineData(7.5, 1)]        // exactly on a boundary is the new segment, as the grid does
    [InlineData(13.2, 2)]
    [InlineData(19.9, 2)]
    public void A_time_lands_in_the_explicit_segment_that_contains_it(double seconds, int expected)
        => Assert.Equal(expected, SegmentPlan.Cuts([0, 7.5, 13.2], Twenty)!.IndexOf(seconds));

    /// <summary>
    /// Boundaries a playlist cannot express are REFUSED, and the caller falls back to the grid rather than
    /// serving a manifest whose entries overlap or run past the source.
    /// </summary>
    [Fact]
    public void Cuts_that_are_not_a_playlist_are_refused()
    {
        Assert.Null(SegmentPlan.Cuts([], Twenty));
        Assert.Null(SegmentPlan.Cuts([1.0, 7.5], Twenty));            // segment 0 must start at 0
        Assert.Null(SegmentPlan.Cuts([0, 7.5, 7.5], Twenty));         // not ascending
        Assert.Null(SegmentPlan.Cuts([0, 9.0, 4.0], Twenty));         // backwards
        Assert.Null(SegmentPlan.Cuts([0, 25.0], Twenty));             // past the end of the source
        Assert.Null(SegmentPlan.Cuts([0, double.NaN], Twenty));
        Assert.Null(SegmentPlan.Cuts([0], TimeSpan.Zero));
    }

    // ── cutting ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Both halves of a cut are required. Without the boundary test the segments are whatever length the
    /// producer felt like and stop matching the manifest; without the keyframe test a segment opens mid-GOP
    /// and cannot be decoded alone — which is exactly what a page seeking into a film asks of it.
    /// </summary>
    [Fact]
    public void A_cut_needs_BOTH_a_new_boundary_and_a_keyframe()
    {
        var plan = SegmentPlan.Cuts([0, 7.5, 13.2], Twenty)!;

        Assert.True(plan.StartsNewSegment(7.5, keyFrame: true, current: 0));
        Assert.False(plan.StartsNewSegment(7.5, keyFrame: false, current: 0));   // undecodable if cut here
        Assert.False(plan.StartsNewSegment(3.0, keyFrame: true, current: 0));    // inside the current segment
        // Never backwards: a late frame must not reopen a finished segment.
        Assert.False(plan.StartsNewSegment(7.5, keyFrame: true, current: 1));
        Assert.False(plan.StartsNewSegment(2.0, keyFrame: true, current: 1));
    }

    /// <summary>
    /// A run that starts mid-source cuts against ABSOLUTE indices, not a count of its own output. Getting
    /// this wrong is how a seek produces segment 0 named seg40 — the numbers agree with the manifest and the
    /// content does not.
    /// </summary>
    [Fact]
    public void A_run_that_starts_late_cuts_on_absolute_indices()
    {
        var plan = SegmentPlan.Grid(6.0, TimeSpan.FromMinutes(60));

        Assert.Equal(240.0, plan.StartOf(40), 6);
        Assert.False(plan.StartsNewSegment(240.0, keyFrame: true, current: 40));
        Assert.True(plan.StartsNewSegment(246.0, keyFrame: true, current: 40));
        Assert.Equal(41, plan.IndexOf(246.0));
    }

    // ── deriving the cuts from a source ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>The boundaries a COPIED track can be cut on are its OWN keyframes.</b> A keyframe every three
    /// seconds means three-second segments however short the target is — nothing in a copy can move one, and
    /// a boundary anywhere else produces a segment no player can start at.
    /// </summary>
    [Fact]
    public void Boundaries_are_the_source_s_keyframes_at_or_past_each_step()
    {
        // Keyframes at 0, 3 and 6 s; frames every 250 ms, nine seconds of them.
        var samples = Enumerable.Range(0, 36)
            .Select(i => Sample(i * 250, keyFrame: i % 12 == 0))
            .ToList();

        Assert.Equal([0, 3.0, 6.0], SegmentGrid.KeyFrameStarts(samples, Ms, 1.0));
        // A larger target skips keyframes rather than cutting between them.
        Assert.Equal([0, 6.0], SegmentGrid.KeyFrameStarts(samples, Ms, 4.0));
    }

    /// <summary>
    /// ⚠ The FIRST one at or past the step, never the nearest. The nearest would sometimes fall BEFORE the
    /// target, and a run of short segments is how a player ends up making one request per second for a
    /// two-hour film.
    /// </summary>
    [Fact]
    public void The_first_keyframe_at_or_past_the_step_is_taken_not_the_nearest()
    {
        var samples = Enumerable.Range(0, 40).Select(i => Sample(i * 250, keyFrame: i % 4 == 0)).ToList();

        // Keyframes every second, a 2.5 s target: 3 and 6, not 2 and 5.
        Assert.Equal([0, 3.0, 6.0, 9.0], SegmentGrid.KeyFrameStarts(samples, Ms, 2.5));
    }

    /// <summary>
    /// A track with no keyframe past its start is ONE segment. It is playable from its start and nowhere
    /// else, which is a fact about that file — the caller then decides whether such a plan is servable at all.
    /// </summary>
    [Fact]
    public void A_track_with_no_further_keyframe_is_a_single_segment()
    {
        var samples = Enumerable.Range(0, 8).Select(i => Sample(i * 250, keyFrame: i == 0)).ToList();

        Assert.Equal([0], SegmentGrid.KeyFrameStarts(samples, Ms, 1.0));
        // Typed, because an empty collection expression cannot pick between the samples overload and the
        // keyframe-ticks one. Both are asserted: the two sources must agree on an empty source too.
        Assert.Equal([0], SegmentGrid.KeyFrameStarts(Array.Empty<MatroskaSample>(), Ms, 1.0));
        Assert.Equal([0], SegmentGrid.KeyFrameStarts(Array.Empty<long>(), Ms, 1.0));
        // The result always opens with 0 even when nothing sits there — segment 0 starts at the start.
        Assert.Equal([0], SegmentGrid.KeyFrameStarts([Sample(0, false), Sample(250, false)], Ms, 1.0));
    }

    /// <summary>
    /// 🔴 <b>A boundary the RUN cannot land on exactly is the whole bug this pairing exists to avoid.</b> The
    /// plan is stated in seconds and the producer compares source ticks, so every boundary has to convert
    /// back to the exact tick it came from — one tick out and the cut moves to the NEXT keyframe, making one
    /// segment far longer than the playlist says.
    /// </summary>
    [Theory]
    [InlineData(1_000_000)]      // 1 ms — every real file
    [InlineData(1_500_000)]      // an awkward scale, where the naive ticks-per-second division truncates
    [InlineData(100)]            // 100 ns
    public void Every_derived_boundary_converts_back_to_the_exact_tick_it_came_from(long scaleNs)
    {
        var timeline = SourceTimeline.For(scaleNs);
        var perFrame = timeline.TicksAt(0.25);
        var samples = Enumerable.Range(0, 40)
            .Select(i => Sample(i * perFrame, keyFrame: i % 4 == 0))
            .ToList();

        var starts = SegmentGrid.KeyFrameStarts(samples, timeline, 2.5);
        var keyFrameTicks = samples.Where(s => s.KeyFrame).Select(s => s.Ticks).ToHashSet();

        Assert.True(starts.Count > 1, "the fixture produced no boundary to check");
        // The origin at [0] is the presentation start rather than a keyframe's time, so it is not checked.
        foreach (var start in starts.Skip(1)) Assert.Contains(timeline.TicksAt(start), keyFrameTicks);
    }

    // ── the source's clock ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>Matroska states nanoseconds PER TICK and MP4 states ticks PER SECOND, and the naive division
    /// truncates.</b> A 1.5 ms scale divides to 666 instead of 666⅔, and a copied track declared on that
    /// timescale plays 0.05 % slow for the whole film while every box in the file validates. The ratio is
    /// reduced instead.
    /// </summary>
    [Theory]
    [InlineData(1_000_000, 1000, 1)]
    [InlineData(1_500_000, 2000, 3)]
    [InlineData(1, 1_000_000_000, 1)]
    [InlineData(0, 1000, 1)]                  // malformed: milliseconds, the reader's own fallback
    [InlineData(-5, 1000, 1)]
    [InlineData(2_000_000_000, 1000, 1)]      // a scale past one second per tick is malformed, not slow
    public void The_source_clock_is_reduced_rather_than_divided(long scaleNs, long timescale, long factor)
    {
        var timeline = SourceTimeline.For(scaleNs);

        Assert.Equal((uint)timescale, timeline.Timescale);
        Assert.Equal(factor, timeline.Factor);
    }

    [Fact]
    public void A_time_round_trips_through_the_source_clock()
    {
        var awkward = SourceTimeline.For(1_500_000);

        Assert.Equal(1.5, awkward.SecondsOf(1_000), 9);
        Assert.Equal(1_000, awkward.TicksAt(1.5));
        Assert.Equal(6.0, awkward.SecondsOf(awkward.TicksAt(6.0)), 9);
    }
}
