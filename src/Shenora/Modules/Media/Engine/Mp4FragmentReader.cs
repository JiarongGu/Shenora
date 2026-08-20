using System.Buffers.Binary;

namespace Shenora.Modules.Media;

/// <summary>
/// Reads back what a fragment actually CONTAINS — how many bytes of real samples a track contributed, which
/// is how <see cref="ISegmentEngine.HasRenderedPicture"/> answers "did the encoder write any picture?".
/// <para>
/// 🔴 <b>"Has a video stream" is the WRONG test, which is why this reads SIZES rather than structure.</b> A
/// hardware encoder can open cleanly, accept every frame, write <c>video:0KiB</c> and exit 0, with every
/// capability check saying it was there. The <c>trun</c> states each sample's size, so the answer is a
/// SUBTRACTION and not a guess (D71 — MPEG-TS cannot do this; its PMT names the stream regardless).
/// </para>
/// <para>
/// It parses only what that question needs, and honours the <c>trun</c> flags, so a fragment from any writer
/// is read correctly or reported as zero.
/// </para>
/// </summary>
internal static class Mp4FragmentReader
{
    private const uint TrunDataOffset = 0x000001;
    private const uint TrunFirstSampleFlags = 0x000004;
    private const uint TrunSampleDuration = 0x000100;
    private const uint TrunSampleSize = 0x000200;
    private const uint TrunSampleFlags = 0x000400;
    private const uint TrunCompositionOffsets = 0x000800;

    /// <summary>
    /// Total sample bytes <paramref name="trackId"/> contributed to this fragment, summed from every
    /// <c>trun</c>. Zero when the track is absent, carries no samples, or the file is not a fragment.
    /// ⚠ <b>Zero and "unreadable" are the SAME answer</b>: the caller's question is whether this segment is
    /// usable, and a fragment that cannot be parsed is not.
    /// </summary>
    public static long SampleBytes(byte[] fragment, int trackId)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        var total = 0L;
        foreach (var moof in TopLevel(fragment, "moof"))
        {
            foreach (var traf in Children(fragment, moof.Body, moof.End, "traf"))
            {
                if (TrackOf(fragment, traf) != trackId) continue;
                foreach (var trun in Children(fragment, traf.Body, traf.End, "trun"))
                {
                    total += TrunBytes(fragment, trun);
                }
            }
        }
        return total;
    }

    /// <inheritdoc cref="SampleBytes(byte[], int)"/>
    /// <param name="path">A segment on disk. A file that cannot be read answers zero, for the reason above.</param>
    /// <param name="trackId">The track to measure — the engine knows it, having declared it in the init segment.</param>
    public static long SampleBytes(string path, int trackId)
    {
        try
        {
            return SampleBytes(File.ReadAllBytes(path), trackId);
        }
        catch (Exception)
        {
            return 0;                       // unreadable is unusable — see the remarks
        }
    }

    /// <summary>
    /// The decode time this fragment declares for <paramref name="trackId"/> — its <c>tfdt</c>
    /// <c>baseMediaDecodeTime</c>, in the track's own timescale. Null when the track is absent or the
    /// fragment cannot be read.
    /// <para>
    /// 🔴 <b>WHERE a fragment sits is not checkable from its byte count.</b> One written at the wrong time is
    /// the right size, carries the right samples, appends without error, and only fails as a stream that will
    /// not play — so a suite asserting only <see cref="SampleBytes(byte[], int)"/> passes over it.
    /// </para>
    /// </summary>
    public static long? BaseDecodeTime(byte[] fragment, int trackId)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        foreach (var moof in TopLevel(fragment, "moof"))
        {
            foreach (var traf in Children(fragment, moof.Body, moof.End, "traf"))
            {
                if (TrackOf(fragment, traf) != trackId) continue;
                foreach (var tfdt in Children(fragment, traf.Body, traf.End, "tfdt"))
                {
                    // FullBox: version byte then three flag bytes, then a 32- or 64-bit time.
                    if (tfdt.Body >= fragment.Length) continue;
                    var version = fragment[tfdt.Body];
                    var at = tfdt.Body + 4;
                    if (version == 1)
                    {
                        if (at + 8 > tfdt.End) continue;
                        return BinaryPrimitives.ReadInt64BigEndian(fragment.AsSpan(at, 8));
                    }
                    if (at + 4 > tfdt.End) continue;
                    return BinaryPrimitives.ReadUInt32BigEndian(fragment.AsSpan(at, 4));
                }
            }
        }
        return null;
    }

    /// <inheritdoc cref="BaseDecodeTime(byte[], int)"/>
    /// <param name="path">A segment on disk.</param>
    /// <param name="trackId">The track to ask about.</param>
    public static long? BaseDecodeTime(string path, int trackId)
    {
        try
        {
            return BaseDecodeTime(File.ReadAllBytes(path), trackId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A box's payload span within the buffer.</summary>
    private readonly record struct Span(int Body, int End);

    private static IEnumerable<Span> TopLevel(byte[] data, string type) => Children(data, 0, data.Length, type);

    /// <summary>
    /// Boxes of one type directly inside <paramref name="from"/>..<paramref name="to"/>.
    /// ⚠ The 64-bit length form (a declared size of 1) is honoured — a reader that handles only the common
    /// header reports a perfectly good file as having no boxes.
    /// </summary>
    private static List<Span> Children(byte[] data, int from, int to, string type)
    {
        var found = new List<Span>();
        var at = from;
        while (at + 8 <= to)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at, 4));
            var header = 8;
            if (size == 1)
            {
                if (at + 16 > to) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at + 8, 8));
                header = 16;
            }
            if (size < header || at + size > to) break;

            if (System.Text.Encoding.ASCII.GetString(data, at + 4, 4) == type)
            {
                found.Add(new Span(at + header, (int)(at + size)));
            }
            at += (int)size;
        }
        return found;
    }

    /// <summary>The track a <c>traf</c> belongs to, from its <c>tfhd</c>. -1 when it states none.</summary>
    private static int TrackOf(byte[] data, Span traf)
    {
        foreach (var tfhd in Children(data, traf.Body, traf.End, "tfhd"))
        {
            // version/flags word, then the track id.
            if (tfhd.Body + 8 > tfhd.End) continue;
            return (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(tfhd.Body + 4, 4));
        }
        return -1;
    }

    /// <summary>
    /// Sum one run's sample sizes, walking the per-sample record whose WIDTH the flags decide.
    /// <para>
    /// 🔴 <b>A run with no size flag is not an error and not zero bytes — it is sizes this box does not
    /// state</b>, the movie's <c>trex</c> having supplied a default. Reading it as zero reports a perfectly
    /// good segment as picture-less, the exact false alarm this check must not raise.
    /// </para>
    /// </summary>
    private static long TrunBytes(byte[] data, Span trun)
    {
        if (trun.Body + 8 > trun.End) return 0;

        var flags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(trun.Body, 4)) & 0x00FFFFFF;
        if ((flags & TrunSampleSize) == 0) return 0;   // see the remarks — defaulted elsewhere, not measurable here

        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(trun.Body + 4, 4));
        var at = trun.Body + 8;
        if ((flags & TrunDataOffset) != 0) at += 4;
        if ((flags & TrunFirstSampleFlags) != 0) at += 4;

        // The size field's position inside each record depends on which optional fields precede it.
        var before = (flags & TrunSampleDuration) != 0 ? 4 : 0;
        var record = before + 4
                     + ((flags & TrunSampleFlags) != 0 ? 4 : 0)
                     + ((flags & TrunCompositionOffsets) != 0 ? 4 : 0);

        var total = 0L;
        for (var i = 0; i < count; i++)
        {
            var field = at + before;
            if (field + 4 > trun.End) break;          // truncated: stop at what is actually there
            total += BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(field, 4));
            at += record;
        }
        return total;
    }
}
