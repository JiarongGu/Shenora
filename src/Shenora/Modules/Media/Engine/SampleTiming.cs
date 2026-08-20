namespace Shenora.Modules.Media;

/// <summary>
/// Turns Matroska's PRESENTATION times into the DECODE timeline MP4 stores — pure, so a test can pin it.
/// <para>
/// 🔴 <b>The two containers disagree about what a timestamp MEANS.</b> A Matroska block carries the time the
/// frame is SHOWN (its PTS) and stores frames in decode order; MP4's <c>stts</c> carries the time it is
/// DECODED (its DTS), with <c>ctts</c> holding the difference. With no B-frames the two are identical and
/// this is a no-op — which is why a remuxer can be written, tested against simple content, and ship looking
/// correct while mangling the majority of real H.264 files.
/// </para>
/// </summary>
internal static class SampleTiming
{
    /// <summary>
    /// Derive a decode timeline from presentation times given in storage order: the same times sorted
    /// ascending, then the whole presentation shifted later so no frame is asked for after it was due.
    /// </summary>
    /// <param name="presentation">Presentation ticks, in the order the frames appear in the file.</param>
    /// <returns>
    /// The decode times, the per-sample composition offsets (<c>PTS − DTS</c>, never negative), and the
    /// shift applied — reported so the caller can cancel it rather than leave the track a few frames late.
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
        // Zero for any stream already in presentation order.
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
    /// Give frames that share a timestamp distinct times, spread evenly up to the next real one. Called only
    /// on a track's own samples, in presentation order.
    /// <para>
    /// ⚠ <b>The case this exists for is LACING, and it is silent without this.</b> A Matroska block may carry
    /// several frames under ONE timestamp — routine for audio — and without a <c>DefaultDuration</c> to space
    /// them they arrive tied. Tied times become zero-length <c>stts</c> entries, and a soundtrack whose frames
    /// all claim to last no time plays as a fraction of a second of noise while every box in the file validates.
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
                // Spread up to the next distinct time; at the end of the track there is none, so fall back
                // to the track's declared frame duration.
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

        // ⚠ A final zero makes the last frame occupy no time, which a player reads as a truncated file.
        if (durations[count - 1] <= 0) durations[count - 1] = 1;
        return durations;
    }
}
