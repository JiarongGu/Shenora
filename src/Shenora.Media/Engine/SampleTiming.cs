namespace Shenora.Media;

/// <summary>
/// Turns Matroska's PRESENTATION times into the DECODE timeline MP4 stores — the one genuinely subtle
/// calculation in a remux, and a pure function so a test can pin it exactly.
///
/// <para>
/// 🔴 <b>Why this is needed at all, and why it is not obvious.</b> The two containers disagree about what
/// a timestamp MEANS. A Matroska block carries the time the frame is SHOWN (its PTS), and stores frames in
/// decode order. MP4's <c>stts</c> table carries the time a frame is DECODED (its DTS), with a separate
/// <c>ctts</c> table holding the difference. For a stream with no B-frames the two are identical and this
/// is a no-op — which is exactly why a remuxer can be written, tested against simple content, and ship
/// looking correct while mangling the majority of real H.264 files, where B-frames are routine.
/// </para>
///
/// <para>
/// <b>The derivation.</b> Decoding must happen in storage order and every frame must be decoded before it
/// is shown, so the DTS sequence is the same set of times sorted ascending. That alone is not enough: the
/// k-th smallest time can still land after the k-th frame's own presentation time, which would ask a
/// player to decode a frame after it was due. Shifting the whole presentation later by the worst such
/// overshoot fixes it, and the shift is reported so the caller can cancel it with an edit list rather than
/// leave the track a few frames late.
/// </para>
/// </summary>
internal static class SampleTiming
{
    /// <summary>
    /// Derive a decode timeline from presentation times given in storage order.
    /// </summary>
    /// <param name="presentation">Presentation ticks, in the order the frames appear in the file.</param>
    /// <returns>
    /// The decode times, the per-sample composition offsets (<c>PTS − DTS</c>, never negative), and the
    /// shift applied to the whole presentation.
    /// </returns>
    public static (long[] Decode, long[] Composition, long Shift) Derive(IReadOnlyList<long> presentation)
    {
        var count = presentation.Count;
        var decode = new long[count];
        var composition = new long[count];
        if (count == 0) return (decode, composition, 0);

        for (var i = 0; i < count; i++) decode[i] = presentation[i];
        Array.Sort(decode);

        // The worst overshoot: how far the decode timeline runs ahead of the frame that must be shown.
        // Zero for any stream in presentation order, which is the common case and costs nothing.
        long shift = 0;
        for (var i = 0; i < count; i++)
        {
            var overshoot = decode[i] - presentation[i];
            if (overshoot > shift) shift = overshoot;
        }

        for (var i = 0; i < count; i++) composition[i] = presentation[i] + shift - decode[i];
        return (decode, composition, shift);
    }

    /// <summary>
    /// Give frames that share a timestamp distinct times, spread evenly up to the next real one.
    ///
    /// <para>
    /// ⚠ <b>The case this exists for is LACING, and it is silent without this.</b> A Matroska block may
    /// carry several frames under ONE timestamp — routine for audio, where a dozen AAC frames share a block
    /// header. When the track also declares a <c>DefaultDuration</c> the reader has already spaced them; when
    /// it does not, they all arrive tied. Tied times become zero-length entries in <c>stts</c>, and a
    /// soundtrack whose frames all claim to last no time plays as a fraction of a second of noise — while
    /// every box in the file validates.
    /// </para>
    /// <para>
    /// Only ever called on a track's own samples, in presentation order.
    /// </para>
    /// </summary>
    public static long[] SpreadTies(IReadOnlyList<long> presentation, long fallbackStep)
    {
        var count = presentation.Count;
        var spread = new long[count];
        for (var i = 0; i < count; i++) spread[i] = presentation[i];
        if (count < 2) return spread;

        var index = 0;
        while (index < count)
        {
            var runEnd = index;
            while (runEnd + 1 < count && spread[runEnd + 1] == spread[index]) runEnd++;

            var length = runEnd - index + 1;
            if (length > 1)
            {
                // Spread up to the next distinct time. At the end of the track there is none, so fall back
                // to the track's declared frame duration, then to the last real gap, then to one tick — the
                // last two only matter for a malformed file and any of them beats a zero.
                var span = runEnd + 1 < count
                    ? spread[runEnd + 1] - spread[index]
                    : Math.Max(fallbackStep, 1) * length;

                for (var k = 1; k < length; k++) spread[index + k] = spread[index] + span * k / length;
            }

            index = runEnd + 1;
        }

        return spread;
    }

    /// <summary>
    /// Per-sample durations from a decode timeline: each is the gap to the next frame, and the last one
    /// borrows the track's declared duration or the gap before it.
    /// </summary>
    public static long[] Durations(IReadOnlyList<long> decode, long fallbackStep)
    {
        var count = decode.Count;
        var durations = new long[count];
        if (count == 0) return durations;

        for (var i = 0; i < count - 1; i++) durations[i] = Math.Max(0, decode[i + 1] - decode[i]);

        durations[count - 1] = fallbackStep > 0
            ? fallbackStep
            : count > 1 ? durations[count - 2] : 1;

        // A final zero would make the last frame occupy no time, which shortens the reported duration and
        // is the shape a player reads as a truncated file.
        if (durations[count - 1] <= 0) durations[count - 1] = 1;
        return durations;
    }
}
