namespace Shenora.Modules.Media;

/// <summary>
/// The arithmetic a segment engine runs on: which segment a time belongs to, where a segment starts, and
/// where a run must seek in the SOURCE to produce one. Pure.
/// <para>
/// 🔴 <b>THE GRID IS ONLY LEGAL BECAUSE OF A COUPLING IN TWO OTHER FILES</b>: what makes a GRID hittable is
/// that both platform encoders emit a keyframe every SECOND — <c>AndroidMediaVideoConversion</c> sets
/// <c>KeyIFrameInterval = 1</c>, <c>IosMediaVideoConversion</c> sets <c>MaxKeyFrameIntervalDuration = 1</c>.
/// ⚠ <b>Which is why a fractional grid is REFUSED rather than rounded silently</b>:
/// <c>SegmentStreamOptions.SegmentSeconds</c> is app-settable, a 2.5-second grid puts every second boundary
/// where no keyframe exists, and those segments still PLAY — the fault appears only when somebody seeks.
/// </para>
/// <para>
/// ⚠ None of that applies to a COPIED track (D76), which keeps the ORIGINAL encoder's keyframes: those runs
/// take their boundaries from <see cref="KeyFrameStarts(IReadOnlyList{long}, SourceTimeline, double)"/>.
/// </para>
/// </summary>
internal static class SegmentGrid
{
    /// <summary>
    /// How often the kit's own encoders emit a keyframe, in seconds — it mirrors what the two platform video
    /// converters are configured with, and is not settable here.
    /// </summary>
    public const double EncoderKeyFrameSeconds = 1.0;

    /// <summary>Can a run actually deliver this grid? False with a reason naming the coupling.</summary>
    /// <param name="segmentSeconds">The grid the manifest already declared.</param>
    /// <param name="reason">Empty when usable.</param>
    public static bool IsUsable(double segmentSeconds, out string reason)
    {
        if (double.IsNaN(segmentSeconds) || segmentSeconds <= 0)
        {
            reason = $"A segment length must be a positive number of seconds; got {segmentSeconds}.";
            return false;
        }

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

    /// <summary>
    /// The boundaries a COPIED track can actually be cut on: its own keyframes, taking the first one at or
    /// past every <paramref name="targetSeconds"/> step.
    /// <para>
    /// 🔴 <b>Greedy forward, never nearest.</b> The nearest keyframe sometimes falls BEFORE the target, and a
    /// run of short segments is how a player ends up making one request per second for a two-hour film.
    /// ⚠ The result always opens with 0 whether or not a keyframe sits there: a source whose first frame is
    /// not a sync sample is playable from its start and nowhere else.
    /// </para>
    /// </summary>
    /// <param name="samples">The lead track's samples, in storage order.</param>
    /// <param name="timeline">
    /// The source's clock. ⚠ <b>The same converter the RUN uses</b> — a boundary the producer cannot land on
    /// exactly moves to the next keyframe, making one segment far longer than the playlist says it is.
    /// </param>
    /// <param name="targetSeconds">The length asked for. Every segment is at least this long except the last.</param>
    /// <returns>Segment start times in seconds, ascending, beginning with 0.</returns>
    public static IReadOnlyList<double> KeyFrameStarts(IReadOnlyList<MatroskaSample> samples,
                                                      SourceTimeline timeline, double targetSeconds)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var ticks = new List<long>();
        foreach (var sample in samples)
        {
            if (sample.KeyFrame) ticks.Add(sample.Ticks);
        }

        return KeyFrameStarts(ticks, timeline, targetSeconds);
    }

    /// <summary>
    /// The same boundaries from keyframe TIMES alone — what a file's own Cues index states directly, without
    /// a walk of every cluster. 🔴 <b>ONE implementation, shared</b>: a file planned from Cues and the same
    /// file planned from the walk must cut in exactly the same places.
    /// </summary>
    /// <param name="keyFrameTicks">Keyframe times in the source's ticks, in storage order.</param>
    /// <param name="timeline">The source's clock. ⚠ The same converter the RUN uses.</param>
    /// <param name="targetSeconds">The length asked for. Every segment is at least this long except the last.</param>
    public static IReadOnlyList<double> KeyFrameStarts(IReadOnlyList<long> keyFrameTicks,
                                                      SourceTimeline timeline, double targetSeconds)
        => KeyFrameStarts(keyFrameTicks, timeline, SegmentLengths.Of(targetSeconds));

    /// <summary>
    /// The same, with a HEAD RAMP: the first segments aim at <see cref="SegmentLengths.Head"/> and the rest
    /// at the steady length. Greedy forward throughout, so every segment is at least as long as was asked.
    /// </summary>
    public static IReadOnlyList<double> KeyFrameStarts(IReadOnlyList<long> keyFrameTicks,
                                                      SourceTimeline timeline, SegmentLengths lengths)
    {
        ArgumentNullException.ThrowIfNull(keyFrameTicks);
        ArgumentNullException.ThrowIfNull(lengths);

        var starts = new List<double> { 0 };
        if (timeline.Timescale == 0 || lengths.Seconds <= 0) return starts;

        var last = 0.0;
        foreach (var tick in keyFrameTicks)
        {
            var at = timeline.SecondsOf(tick);
            // ⚠ The target is the segment being CLOSED (`starts.Count - 1`); reading it as the one being
            // OPENED shifts the whole ramp by one and gives away the short first segment. Also rejects a
            // keyframe whose time went BACKWARDS, which would make the plan non-ascending.
            if (at < last + lengths.TargetAt(starts.Count - 1)) continue;
            starts.Add(at);
            last = at;
        }

        return starts;
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
    /// <para>
    /// 🔴 <b>At or BEFORE.</b> A decoder handed a non-keyframe produces garbage until the next one arrives, so
    /// a run that seeks to the exact boundary and feeds from there emits a segment whose opening frames are
    /// macroblock soup. ⚠ These are the SOURCE's keyframes, not the output's — those come from the encoder's
    /// own one-second interval (see the type remarks).
    /// </para>
    /// </summary>
    /// <param name="samples">One track's samples, in storage order (which is also decode order).</param>
    /// <param name="startTicks">The segment's start on the media timeline.</param>
    /// <returns>
    /// An index into <paramref name="samples"/>. Zero when the track declares no keyframe at all — such a
    /// track is playable from its start and nowhere else, so the caller logs rather than refuses.
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
    /// Should the frame at <paramref name="ticks"/> OPEN a new segment, given the one being written? The
    /// tick-based twin of <see cref="SegmentPlan.StartsNewSegment"/>, which states why both halves matter.
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

/// <summary>
/// The source's own clock, expressed EXACTLY as something MP4 can state — which is what a COPIED track is
/// written on (D76).
/// <para>
/// 🔴 <b>The two containers state the same clock in opposite directions, and the naive conversion
/// truncates.</b> Matroska declares NANOSECONDS PER TICK; MP4 declares TICKS PER SECOND. An unusual scale
/// (1 500 000 ns) divides to 666 instead of 666⅔, and a copied track on that timescale plays 0.05 % slow for
/// the whole film while every box in the file validates. So the ratio is reduced instead: every tick is
/// multiplied by <see cref="Factor"/>.
/// </para>
/// </summary>
/// <param name="Timescale">Ticks per second, as MP4 states it.</param>
/// <param name="Factor">What a source tick is multiplied by to land on <paramref name="Timescale"/>.</param>
internal readonly record struct SourceTimeline(uint Timescale, long Factor)
{
    private const long NanosecondsPerSecond = 1_000_000_000L;

    /// <summary>Milliseconds — the sample reader's own fallback, and what a malformed scale falls back to.</summary>
    private static SourceTimeline Milliseconds => new(1000, 1);

    /// <summary>Reduce Matroska's <c>TimestampScale</c> to an exact MP4 timescale.</summary>
    public static SourceTimeline For(long timestampScaleNs)
    {
        if (timestampScaleNs is <= 0 or > NanosecondsPerSecond) return Milliseconds;

        var divisor = Gcd(timestampScaleNs, NanosecondsPerSecond);
        var timescale = NanosecondsPerSecond / divisor;
        return timescale is > 0 and <= uint.MaxValue
            ? new SourceTimeline((uint)timescale, timestampScaleNs / divisor)
            : Milliseconds;
    }

    /// <summary>A source tick count as a time in seconds.</summary>
    public double SecondsOf(long ticks) => Timescale == 0 ? 0 : ticks * (double)Factor / Timescale;

    /// <summary>A time in seconds back to the source's OWN ticks — what a sample index is searched by.</summary>
    public long TicksAt(double seconds) => Factor <= 0 ? 0 : (long)Math.Round(seconds * Timescale / Factor);

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
