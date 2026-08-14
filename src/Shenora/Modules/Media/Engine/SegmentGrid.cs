namespace Shenora.Modules.Media;

/// <summary>
/// The arithmetic a segment engine runs on: which segment a time belongs to, where a segment starts, and
/// where a run must seek in the SOURCE to produce one.
///
/// <para>
/// Pure, and separated from the engine for the reason <c>MediaPlaybackPlanner</c> is separated from the
/// converter: the decisions are where the bugs live, so they should be the part a test can pin exactly. The
/// codec loop around this needs a device; none of this does.
/// </para>
///
/// <para>
/// 🔴 <b>THE GRID IS ONLY LEGAL BECAUSE OF A COUPLING IN TWO OTHER FILES, and it is stated here because
/// nothing stated it anywhere.</b> <c>SegmentRunRequest.SegmentSeconds</c> says the grid is "not negotiable
/// by the engine" and that a run which cannot hit it must not claim it. What makes it hittable is that both
/// platform encoders are configured to emit a keyframe every SECOND —
/// <c>AndroidMediaVideoConversion</c> sets <c>KeyIFrameInterval = 1</c>, <c>IosMediaVideoConversion</c> sets
/// <c>MaxKeyFrameIntervalDuration = 1</c>, each calling it "a SEEKING decision, not a quality one". So every
/// whole-second boundary is a keyframe, and a grid measured in whole seconds lands on one every time.
/// </para>
/// <para>
/// ⚠ <b>Which is exactly why a fractional grid has to be REFUSED rather than rounded silently.</b>
/// <c>SegmentStreamOptions.SegmentSeconds</c> is app-settable, and a 2.5-second grid puts every second
/// boundary where no keyframe exists. The segments still PLAY — the manifest is synthetic and the bytes are
/// valid — and the fault only appears when somebody seeks, as a jump to the wrong place or a burst of
/// macroblocks. A failure that needs a seek to reveal it is one to refuse at composition time.
/// </para>
/// </summary>
internal static class SegmentGrid
{
    /// <summary>
    /// How often the kit's own encoders emit a keyframe, in seconds. <b>Not a preference and not settable
    /// here</b> — it mirrors what the two platform video converters are configured with, and this constant
    /// exists so the dependency is visible from the side that depends on it.
    /// </summary>
    public const double EncoderKeyFrameSeconds = 1.0;

    /// <summary>
    /// Can a run actually deliver this grid? False with a reason naming the coupling, so a composition
    /// mistake is a sentence rather than a seek that misbehaves later.
    /// </summary>
    /// <param name="segmentSeconds">The grid the manifest already declared.</param>
    /// <param name="reason">Empty when usable.</param>
    public static bool IsUsable(double segmentSeconds, out string reason)
    {
        if (double.IsNaN(segmentSeconds) || segmentSeconds <= 0)
        {
            reason = $"A segment length must be a positive number of seconds; got {segmentSeconds}.";
            return false;
        }

        // A whole multiple of the encoder's keyframe interval, so every boundary falls on one.
        var multiple = segmentSeconds / EncoderKeyFrameSeconds;
        if (Math.Abs(multiple - Math.Round(multiple)) > 1e-9)
        {
            reason = $"A segment length of {segmentSeconds}s cannot start on a keyframe: the kit's encoders "
                   + $"emit one every {EncoderKeyFrameSeconds}s, so only whole multiples of that land on one. "
                   + "Segments would still play and only SEEKING would misbehave, which is why this is "
                   + "refused rather than rounded.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Which segment a media time falls in. Negative times clamp to 0 rather than going negative.</summary>
    public static int SegmentOf(long ticks, long ticksPerSecond, double segmentSeconds)
    {
        if (ticksPerSecond <= 0 || segmentSeconds <= 0) return 0;
        var seconds = (double)ticks / ticksPerSecond;
        return seconds <= 0 ? 0 : (int)(seconds / segmentSeconds);
    }

    /// <summary>Where a segment begins on the media timeline, in the source's own ticks.</summary>
    public static long StartTicks(int segment, long ticksPerSecond, double segmentSeconds)
    {
        if (segment <= 0 || ticksPerSecond <= 0 || segmentSeconds <= 0) return 0;
        return (long)(segment * segmentSeconds * ticksPerSecond);
    }

    /// <summary>
    /// Where a run must START READING the source to produce a segment beginning at
    /// <paramref name="startTicks"/> — the last keyframe at or before it.
    ///
    /// <para>
    /// 🔴 <b>At or BEFORE, and the direction is the whole point.</b> A decoder handed a non-keyframe produces
    /// garbage until the next one arrives, so a run that seeks to the exact boundary and starts feeding there
    /// emits a segment whose opening frames are macroblock soup. Seeking BACK to the previous keyframe and
    /// decoding forward costs some frames nobody keeps and is the only way to have a correct picture at the
    /// boundary.
    /// </para>
    /// <para>
    /// ⚠ This is about the SOURCE's keyframes, which have nothing to do with the output's: the source was
    /// encoded by somebody else, with whatever GOP they chose. The output's boundaries are guaranteed by the
    /// encoder's own one-second interval (see the type remarks) — these two are independent, and conflating
    /// them is how a segmenter ends up trusting the wrong file's index.
    /// </para>
    /// </summary>
    /// <param name="samples">One track's samples, in storage order (which is also decode order).</param>
    /// <param name="startTicks">The segment's start on the media timeline.</param>
    /// <returns>
    /// An index into <paramref name="samples"/>. Zero when the track declares no keyframe at all — reading
    /// from the beginning is the only honest answer, and the caller logs it rather than refusing, because a
    /// track with no sync sample is playable from its start and nowhere else.
    /// </returns>
    public static int SeekIndex(IReadOnlyList<MatroskaSample> samples, long startTicks)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var found = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].Ticks > startTicks) break;
            if (samples[i].KeyFrame) found = i;
        }
        return found;
    }

    /// <summary>
    /// Should the frame at <paramref name="ticks"/> OPEN a new segment, given the one being written?
    ///
    /// <para>
    /// Both halves are required, and dropping either produces a stream that appends without complaint:
    /// without the boundary test the segments are whatever length the encoder felt like and stop matching the
    /// manifest; without the keyframe test a segment starts mid-GOP and cannot be decoded on its own, which
    /// is precisely what a page seeking into the middle of a film asks it to do.
    /// </para>
    /// </summary>
    /// <param name="ticks">The candidate frame's time on the media timeline.</param>
    /// <param name="keyFrame">Whether it is a sync sample.</param>
    /// <param name="current">The segment index currently being written.</param>
    /// <param name="ticksPerSecond">The media timeline's unit.</param>
    /// <param name="segmentSeconds">The grid, already checked by <see cref="IsUsable"/>.</param>
    public static bool StartsNewSegment(long ticks, bool keyFrame, int current,
                                        long ticksPerSecond, double segmentSeconds)
        => keyFrame && SegmentOf(ticks, ticksPerSecond, segmentSeconds) > current;
}
