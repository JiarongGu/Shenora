using System.Globalization;

namespace Shenora.Modules.Media;

/// <summary>
/// How long each segment should be — a short HEAD so playback can start, then a steady length.
///
/// <para>
/// 🔴 <b>The first segment is the entire startup budget for a VOD stream.</b> A page cannot play until the
/// init segment arrives, that request drives segment 0, and a VOD playlist starts at segment 0 (the
/// "start three target durations from the end" rule is a LIVE one). So a six-second first segment is six
/// seconds of production before the first frame, whatever the rest of the stream costs.
/// </para>
/// <para>
/// ⚠ <b>A ramp, not simply short segments throughout.</b> Short segments cost a request each and cost
/// quality — a keyframe every second measurably raises the bitrate needed for the same picture. Starting
/// small and settling onto the full length pays that only where it buys something.
/// </para>
/// <para>
/// ⚠ <b>It is a REQUEST, not a promise.</b> A copied picture can only be cut where the source already has a
/// keyframe, so a source with a ten-second GOP gives a ten-second first segment however short the head asks
/// for — <see cref="SegmentGrid.KeyFrameStarts(IReadOnlyList{long}, SourceTimeline, SegmentLengths)"/> is
/// greedy forward and never cuts early.
/// </para>
/// </summary>
/// <param name="Seconds">The steady length, once the head is past.</param>
/// <param name="Head">
/// Lengths for the first segments, in order. Empty means a uniform stream. ⚠ Each must be a whole multiple
/// of <see cref="SegmentGrid.EncoderKeyFrameSeconds"/>, or a RE-ENCODED picture cannot land on it.
/// </param>
public sealed record SegmentLengths(double Seconds, IReadOnlyList<double> Head)
{
    /// <summary>A uniform stream — no head at all.</summary>
    public static SegmentLengths Of(double seconds) => new(seconds, []);

    /// <summary>
    /// Where each segment would begin if every length were delivered exactly — what a RE-ENCODED stream
    /// gets, since its encoder puts a keyframe on every whole second. Always opens with 0.
    /// </summary>
    public IReadOnlyList<double> StartsFor(TimeSpan total)
    {
        var starts = new List<double> { 0 };
        if (total <= TimeSpan.Zero || Seconds <= 0) return starts;

        var at = TargetAt(0);
        while (at < total.TotalSeconds)
        {
            starts.Add(at);
            at += TargetAt(starts.Count - 1);
        }

        return starts;
    }

    /// <summary>How long segment <paramref name="index"/> should aim to be.</summary>
    public double TargetAt(int index) =>
        Head is not null && index >= 0 && index < Head.Count ? Head[index] : Seconds;

    /// <summary>
    /// Is every length one a run could actually deliver? False with a reason, at composition time — the same
    /// policy <see cref="SegmentGrid.IsUsable"/> applies to the steady length, for the same reason: a
    /// boundary the encoder has no keyframe at still PLAYS, and only a seek misbehaves.
    /// </summary>
    public bool IsUsable(out string reason)
    {
        if (!SegmentGrid.IsUsable(Seconds, out reason)) return false;

        foreach (var length in Head ?? [])
        {
            if (!SegmentGrid.IsUsable(length, out reason)) return false;
            if (length <= Seconds) continue;
            reason = $"A head segment of {length}s is longer than the steady {Seconds}s, so it would delay "
                   + "playback rather than start it sooner.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}

/// <summary>
/// Where a <see cref="SegmentPlan"/>'s boundaries came from — which is the question "may this run COPY the
/// picture?" in the only form that cannot be got wrong.
/// <para>
/// 🔴 <b>A copied picture and a boundary the source has no keyframe at cannot both hold.</b> Copied frames
/// keep the ORIGINAL encoder's keyframes; a re-encoded picture lands on whole seconds because the kit's own
/// encoders emit a keyframe every second. A plan that does not say which it is leaves the run to guess, and
/// a wrong guess slips every cut to the next source keyframe — segments that play, and a seek that does not.
/// </para>
/// </summary>
public enum SegmentBoundaries
{
    /// <summary>Uniform, every boundary a whole multiple of the encoder's keyframe interval. Re-encoded.</summary>
    Grid,

    /// <summary>
    /// The SOURCE's own keyframes, of differing length. <b>The only shape a copy may be cut on</b>, and the
    /// only one a copy can hit exactly.
    /// </summary>
    SourceKeyFrames,

    /// <summary>
    /// Explicit whole-second boundaries — a head ramp. Hittable by an ENCODER and by nothing else, so a run
    /// handed these re-encodes exactly as it would on a grid.
    /// </summary>
    EncoderCuts,
}

/// <summary>
/// WHERE THE CUTS ARE: the boundaries a stream's segments actually fall on, in seconds from the start of the
/// presentation.
/// <para>
/// 🔴 <b>ONE object, handed to BOTH the manifest and the production run, because a playlist that disagrees
/// with the producer fails silently.</b> The manifest names every segment and states each length before any
/// exists; the run decides where to cut. Computed separately — even from the same number — a seek lands
/// somewhere other than where the playlist promised, and the symptom is a player that jumps to the wrong
/// minute rather than an error anyone can see. So they travel on <see cref="SegmentRunRequest.Plan"/>.
/// </para>
/// <para>
/// Two shapes, differing in what the producer can promise: <see cref="Grid"/>, uniform, and
/// <see cref="Cuts"/>, explicit boundaries of differing length that <c>#EXTINF</c> states one by one.
/// </para>
/// <para>
/// ⚠ <b>Seconds, as a <see cref="double"/>, throughout</b> — a run converts a boundary into each of its
/// tracks' own timescales, and a picture and a soundtrack do not share one, so no tick unit could serve both
/// halves of this contract.
/// </para>
/// </summary>
public sealed class SegmentPlan
{
    private readonly double[]? _starts;

    private SegmentPlan(SegmentBoundaries origin, double? gridSeconds, double[]? starts, TimeSpan total, int count)
    {
        Origin = origin;
        GridSeconds = gridSeconds;
        _starts = starts;
        Total = total;
        Count = count;
    }

    /// <summary>
    /// WHERE these boundaries came from, which decides whether a run may COPY a picture onto them.
    /// <para>
    /// 🔴 <b>Stated rather than inferred, because the inference was wrong the moment a third shape
    /// existed.</b> The run used to read "is this a grid?" as "must I re-encode?", which held only while
    /// every non-grid plan came from the source's own keyframes. Hand it explicit boundaries from anywhere
    /// else and it copies onto cuts the source has no keyframe at: every cut SLIPS to the next source
    /// keyframe, the segments still play, and only a seek shows it.
    /// </para>
    /// </summary>
    public SegmentBoundaries Origin { get; }

    /// <summary>
    /// A uniform grid of <paramref name="segmentSeconds"/> covering <paramref name="total"/> — what a
    /// RE-ENCODING run produces, the kit's platform encoders emitting a keyframe every second so that any
    /// whole-second boundary is hittable (D75). The last segment carries the REMAINDER rather than a flat
    /// length: a playlist's declared total is the sum of its <c>EXTINF</c>s, so a flat final entry overstates
    /// the source by up to one whole segment and a scrub bar built on it seeks past the end.
    /// </summary>
    public static SegmentPlan Grid(double segmentSeconds, TimeSpan total)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentSeconds);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(total, TimeSpan.Zero);

        var count = (int)Math.Ceiling(total.TotalSeconds / segmentSeconds);
        return new SegmentPlan(SegmentBoundaries.Grid, segmentSeconds, starts: null, total, Math.Max(count, 1));
    }

    /// <summary>
    /// Explicit boundaries a RE-ENCODER can hit — what a head ramp is, so playback can start on a short
    /// first segment and settle onto the full length afterwards.
    /// <para>
    /// 🔴 <b>Every boundary must be a whole multiple of <see cref="SegmentGrid.EncoderKeyFrameSeconds"/></b>,
    /// for the same reason a fractional grid is refused: what makes a boundary hittable is that the kit's
    /// encoders emit a keyframe every second, and one placed anywhere else still PLAYS — only a seek
    /// misbehaves. Null when any boundary fails that, rather than a rounded plan nobody asked for.
    /// </para>
    /// <para>
    /// ⚠ <b>A COPIED picture cannot use these</b>, which is what <see cref="Origin"/> exists to say: copied
    /// frames keep the original encoder's keyframes, so a run handed this re-encodes (D76).
    /// </para>
    /// </summary>
    public static SegmentPlan? EncoderCuts(IReadOnlyList<double> startSeconds, TimeSpan total)
    {
        ArgumentNullException.ThrowIfNull(startSeconds);
        if (Shape(startSeconds, total) is not { } starts) return null;

        foreach (var start in starts)
        {
            var multiple = start / SegmentGrid.EncoderKeyFrameSeconds;
            if (Math.Abs(multiple - Math.Round(multiple)) > 1e-9) return null;
        }

        return new SegmentPlan(SegmentBoundaries.EncoderCuts, null, starts, total, starts.Length);
    }

    /// <summary>
    /// Explicit boundaries — what a COPYING run produces, whose frames land on whatever GOP the ORIGINAL
    /// encoder chose so that no fixed grid can be hit (D76). <paramref name="startSeconds"/>[k] is where
    /// segment k begins, ascending, and the first must be zero.
    /// </summary>
    /// <returns>
    /// Null when the boundaries are not a plan a stream could serve — empty, not starting at zero, not
    /// ascending, or running past <paramref name="total"/>. ⚠ <b>Null rather than a throw</b>: these come from
    /// a file a page pointed at, so a malformed one falls back to the grid rather than faulting the caller.
    /// </returns>
    public static SegmentPlan? Cuts(IReadOnlyList<double> startSeconds, TimeSpan total)
    {
        ArgumentNullException.ThrowIfNull(startSeconds);
        return Shape(startSeconds, total) is { } starts
            ? new SegmentPlan(SegmentBoundaries.SourceKeyFrames, null, starts, total, starts.Length)
            : null;
    }

    /// <summary>
    /// The checks every explicit plan shares: non-empty, starting at zero, ascending, finite, and ending
    /// inside <paramref name="total"/>. Null when the boundaries are not a plan a stream could serve.
    /// </summary>
    private static double[]? Shape(IReadOnlyList<double> startSeconds, TimeSpan total)
    {
        if (startSeconds.Count == 0 || total <= TimeSpan.Zero) return null;
        if (Math.Abs(startSeconds[0]) > 1e-9) return null;

        var starts = new double[startSeconds.Count];
        for (var i = 0; i < startSeconds.Count; i++)
        {
            starts[i] = startSeconds[i];
            if (double.IsNaN(starts[i]) || starts[i] < 0) return null;
            if (i > 0 && starts[i] <= starts[i - 1]) return null;
        }

        return starts[^1] >= total.TotalSeconds ? null : starts;
    }

    /// <summary>
    /// The uniform grid this plan is, or null when its boundaries are explicit — the one thing a caller may
    /// ask about the SHAPE, because <see cref="SegmentGrid.IsUsable"/>'s refusal applies to a grid and cannot
    /// apply to boundaries taken from a source's own keyframes.
    /// </summary>
    public double? GridSeconds { get; }

    /// <summary>How many segments the manifest names. Always at least one.</summary>
    public int Count { get; }

    /// <summary>How long the whole presentation is — the sum of every segment's length.</summary>
    public TimeSpan Total { get; }

    /// <summary>Where segment <paramref name="index"/> begins, in seconds. Clamped to the plan's range.</summary>
    public double StartOf(int index)
    {
        if (index <= 0) return 0;
        if (index >= Count) return Total.TotalSeconds;
        return _starts is null ? index * GridSeconds!.Value : _starts[index];
    }

    /// <summary>How long segment <paramref name="index"/> is, in seconds — what <c>#EXTINF</c> states.</summary>
    public double LengthOf(int index)
    {
        if (index < 0 || index >= Count) return 0;
        var end = index + 1 < Count ? StartOf(index + 1) : Total.TotalSeconds;
        return Math.Max(end - StartOf(index), 0);
    }

    /// <summary>
    /// The longest segment, which is what <c>#EXT-X-TARGETDURATION</c> must be at least as large as.
    /// ⚠ <b>Not simply the grid</b> — a derived plan's segments are the gap between two of the source's
    /// keyframes, routinely longer than what was asked for, and a TARGETDURATION smaller than an EXTINF
    /// breaks a MUST in the playlist spec.
    /// </summary>
    public double LongestSeconds
    {
        get
        {
            if (_starts is null) return Math.Min(GridSeconds!.Value, Total.TotalSeconds);
            var longest = 0.0;
            for (var i = 0; i < Count; i++) longest = Math.Max(longest, LengthOf(i));
            return longest;
        }
    }

    /// <summary>Which segment a time falls in. Times before the start clamp to 0, past the end to the last.</summary>
    public int IndexOf(double seconds)
    {
        if (seconds <= 0) return 0;
        if (_starts is null)
        {
            // The grid's own arithmetic, on the finest unit .NET states a time in — see SegmentGrid.
            var index = SegmentGrid.SegmentOf((long)Math.Round(seconds * TimeSpan.TicksPerSecond),
                                              TimeSpan.TicksPerSecond, GridSeconds!.Value);
            return Math.Min(index, Count - 1);
        }

        // The last boundary at or before the time. Binary, because a two-hour film's plan is a thousand
        // entries and this is asked once per output frame.
        var low = 0;
        var high = Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (_starts[middle] <= seconds + 1e-9) low = middle;
            else high = middle - 1;
        }
        return low;
    }

    /// <summary>
    /// Should the frame at <paramref name="seconds"/> OPEN a new segment, given the one being written?
    /// <para>
    /// Both halves are required, and dropping either produces a stream that appends without complaint:
    /// without the boundary test the segments stop matching the manifest; without the keyframe test a segment
    /// starts mid-GOP and cannot be decoded on its own, which is what a page seeking into the middle of a
    /// film asks it to do. ⚠ Never cuts backwards — a late frame must not reopen a finished segment.
    /// </para>
    /// </summary>
    public bool StartsNewSegment(double seconds, bool keyFrame, int current)
        => keyFrame && IndexOf(seconds) > current;

    /// <summary>What this plan is, for a log line. Never null.</summary>
    public override string ToString() => _starts is null
        ? string.Create(CultureInfo.InvariantCulture, $"a {GridSeconds!.Value:0.###}s grid of {Count} segments")
        : string.Create(CultureInfo.InvariantCulture,
            $"{Count} segments cut on the source's own keyframes (longest {LongestSeconds:0.###}s)");
}
