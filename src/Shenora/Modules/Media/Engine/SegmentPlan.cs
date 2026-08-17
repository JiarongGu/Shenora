using System.Globalization;

namespace Shenora.Modules.Media;

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

    private SegmentPlan(double? gridSeconds, double[]? starts, TimeSpan total, int count)
    {
        GridSeconds = gridSeconds;
        _starts = starts;
        Total = total;
        Count = count;
    }

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
        return new SegmentPlan(segmentSeconds, starts: null, total, Math.Max(count, 1));
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
        if (startSeconds.Count == 0 || total <= TimeSpan.Zero) return null;
        if (Math.Abs(startSeconds[0]) > 1e-9) return null;

        var starts = new double[startSeconds.Count];
        for (var i = 0; i < startSeconds.Count; i++)
        {
            starts[i] = startSeconds[i];
            if (double.IsNaN(starts[i]) || starts[i] < 0) return null;
            if (i > 0 && starts[i] <= starts[i - 1]) return null;
        }

        return starts[^1] >= total.TotalSeconds ? null : new SegmentPlan(null, starts, total, starts.Length);
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
