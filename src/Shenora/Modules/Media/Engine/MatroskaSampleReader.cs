using System.Buffers.Binary;

namespace Shenora.Modules.Media;

/// <summary>
/// Walks a Matroska file's CLUSTERS and reports where every frame lives — the half
/// <see cref="MatroskaProbe"/> skips. A second EBML reader rather than a reuse of the probe's, which is
/// bounded, never seeks, and cannot express an absolute position.
/// <para>
/// Read in two stages: <see cref="ReadHeader"/> stops at the first cluster so the caller can choose its
/// tracks, then <see cref="ReadSamples"/> continues from exactly there and records only those.
/// </para>
/// </summary>
internal sealed class MatroskaSampleReader(Stream source)
{
    // Ids as they appear on the wire, INCLUDING the length-descriptor bits, so comparison is equality.
    private const ulong IdEbmlHeader = 0x1A45DFA3;
    private const ulong IdSegment = 0x18538067;
    private const ulong IdInfo = 0x1549A966;
    private const ulong IdTimestampScale = 0x2AD7B1;
    private const ulong IdDuration = 0x4489;
    private const ulong IdTracks = 0x1654AE6B;
    private const ulong IdTrackEntry = 0xAE;
    private const ulong IdTrackNumber = 0xD7;
    private const ulong IdTrackType = 0x83;
    private const ulong IdCodecId = 0x86;
    private const ulong IdCodecPrivate = 0x63A2;
    private const ulong IdDefaultDuration = 0x23E383;
    private const ulong IdVideo = 0xE0;
    private const ulong IdPixelWidth = 0xB0;
    private const ulong IdPixelHeight = 0xBA;
    private const ulong IdAudio = 0xE1;
    private const ulong IdSamplingFrequency = 0xB5;
    private const ulong IdChannels = 0x9F;
    private const ulong IdSeekHead = 0x114D9B74;
    private const ulong IdSeek = 0x4DBB;
    private const ulong IdSeekId = 0x53AB;
    private const ulong IdSeekPosition = 0x53AC;
    private const ulong IdCues = 0x1C53BB6B;
    private const ulong IdCuePoint = 0xBB;
    private const ulong IdCueTime = 0xB3;
    private const ulong IdCueTrackPositions = 0xB7;
    private const ulong IdCueTrack = 0xF7;
    private const ulong IdCueClusterPosition = 0xF1;
    private const ulong IdCluster = 0x1F43B675;
    private const ulong IdClusterTimestamp = 0xE7;
    private const ulong IdSimpleBlock = 0xA3;
    private const ulong IdBlockGroup = 0xA0;
    private const ulong IdBlock = 0xA1;
    private const ulong IdReferenceBlock = 0xFB;

    private const ulong TrackTypeVideo = 1;
    private const ulong TrackTypeAudio = 2;

    /// <summary>
    /// The most frames this will record before refusing. ⚠ A bound rather than trust — this parses a file
    /// the PAGE can point at, and each frame costs a record. Four million is roughly a nine-hour film at
    /// 60 fps with a soundtrack.
    /// </summary>
    private const int MaxSamples = 4_000_000;

    /// <summary>A declared element size larger than the file itself is a malformed length, not a big element.</summary>
    private long Length => source.Length;

    private long _segmentEnd;
    private long _firstClusterAt = -1;

    /// <summary>
    /// Where the Segment's DATA begins. Every position Matroska stores — a <c>SeekPosition</c>, a
    /// <c>CueClusterPosition</c> — is relative to this, not to the start of the file.
    /// </summary>
    private long _segmentAt;

    /// <summary>The Cues element's absolute position, or -1 when the file declares none.</summary>
    private long _cuesAt = -1;

    /// <summary>Where the next chunk of the walk resumes; -1 before it has started.</summary>
    private long _resumeAt = -1;

    /// <summary>Total samples recorded so far, across every chunk — what <see cref="MaxSamples"/> bounds.</summary>
    private int _recorded;

    /// <summary>The timestamp of the last cluster read, which is what a bounded walk stops against.</summary>
    private long _lastClusterTicks;

    /// <summary>True once the walk has reached the end of the segment: nothing more will ever be added.</summary>
    public bool SamplesComplete { get; private set; }

    /// <summary>Nanoseconds per tick. Matroska's default, and what almost every real file uses.</summary>
    public long TimestampScaleNs { get; private set; } = 1_000_000;

    /// <summary>The Info duration in TICKS, when the file declared one.</summary>
    public double? DurationTicks { get; private set; }

    public List<MatroskaTrack> Tracks { get; } = [];

    /// <summary>
    /// Read the EBML header, Info and Tracks, stopping at the first cluster. False when this is not a
    /// Matroska file at all, or carries no track this kit can act on.
    /// </summary>
    public bool ReadHeader()
    {
        if (!source.CanSeek || !source.CanRead) return false;
        source.Position = 0;

        if (!TryReadElement(out var id, out var size) || id != IdEbmlHeader) return false;
        // ⚠ size < 0 is the unknown-size sentinel; unguarded it seeks BACKWARD one byte here
        // (Position - 1 passes SkipTo's range check) and misparses.
        if (size < 0 || !SkipTo(source.Position + size)) return false;

        if (!TryReadElement(out id, out size) || id != IdSegment) return false;
        _segmentAt = source.Position;
        // An unknown-size Segment means "to the end of the file", which is what a live-muxed file writes.
        _segmentEnd = size < 0 ? Length : Math.Min(source.Position + size, Length);

        while (source.Position < _segmentEnd)
        {
            var elementAt = source.Position;
            if (!TryReadElement(out var childId, out var childSize)) break;
            var payloadAt = source.Position;

            switch (childId)
            {
                case IdInfo when childSize >= 0:
                    ReadInfo(payloadAt + childSize);
                    break;
                case IdTracks when childSize >= 0:
                    ReadTracks(payloadAt + childSize);
                    break;
                case IdSeekHead when childSize >= 0:
                    // Where the INDEX is; Cues themselves are normally at the end. ⚠ Moves the position, so
                    // the skip below is not optional.
                    ReadSeekHead(payloadAt + childSize, depth: 0);
                    if (!SkipTo(payloadAt + childSize)) return Tracks.Count > 0;
                    break;
                case IdCues when childSize >= 0:
                    // Cues at the FRONT — what `cues_to_front` and `mkclean --keep-cues` produce.
                    _cuesAt = elementAt;
                    if (!SkipTo(payloadAt + childSize)) return Tracks.Count > 0;
                    break;
                case IdCluster:
                    // Stop here and remember where, so ReadSamples resumes without a second walk.
                    _firstClusterAt = elementAt;
                    return Tracks.Count > 0;
                default:
                    if (childSize < 0) return Tracks.Count > 0;   // unknown-size non-cluster: nothing safe to skip
                    if (!SkipTo(payloadAt + childSize)) return Tracks.Count > 0;
                    break;
            }
        }

        return Tracks.Count > 0;
    }

    /// <summary>Record where the SeekHead says the index is. ⚠ Moves the stream; the caller restores it.</summary>
    /// <param name="end">One past the SeekHead's payload.</param>
    /// <param name="depth">
    /// 🔴 <b>A SeekHead may point at ANOTHER SeekHead</b> — MKVToolNix writes that layout whenever an
    /// in-place header edit outgrows its reserved space, so a reader handling only one level reports "no
    /// index" for ordinary files. Followed ONCE: a cycle would otherwise hang, on a file the page chose.
    /// </param>
    private void ReadSeekHead(long end, int depth)
    {
        while (source.Position < end)
        {
            if (!TryReadElement(out var id, out var size) || size < 0) return;
            var payloadAt = source.Position;
            if (payloadAt + size > end) return;
            if (id == IdSeek) ReadSeekEntry(payloadAt + size, depth);
            if (!SkipTo(payloadAt + size)) return;
        }
    }

    private void ReadSeekEntry(long end, int depth)
    {
        ulong target = 0;
        var position = -1L;

        while (source.Position < end)
        {
            if (!TryReadElement(out var id, out var size) || size < 0) return;
            var payloadAt = source.Position;
            if (payloadAt + size > end) return;

            // A SeekID's bytes ARE an element id, length marker included — the same form these constants use.
            if (id == IdSeekId) target = ReadUnsigned(size);
            else if (id == IdSeekPosition) position = (long)ReadUnsigned(size);

            if (!SkipTo(payloadAt + size)) return;
        }

        if (position < 0) return;
        var at = _segmentAt + position;
        if (at < _segmentAt || at >= _segmentEnd) return;      // outside the segment is malformed, not large

        if (target == IdCues) { _cuesAt = at; return; }
        if (target != IdSeekHead || depth >= 1) return;

        var resume = source.Position;
        if (SkipTo(at) && TryReadElement(out var nested, out var nestedSize)
            && nested == IdSeekHead && nestedSize >= 0)
        {
            ReadSeekHead(Math.Min(source.Position + nestedSize, _segmentEnd), depth + 1);
        }
        source.Position = resume;
    }

    /// <summary>
    /// The track's keyframe times, in the file's own ticks, taken from its INDEX rather than by walking it —
    /// two small reads instead of touching a third of the file.
    /// <para>
    /// ⚠ <b>Null means "ask the clusters instead" and is an ORDINARY answer</b> — no index, an index for
    /// other tracks, or one that fails the checks below.
    /// </para>
    /// </summary>
    /// <param name="trackNumber">
    /// ⚠ Cues are PER TRACK, and every audio frame is a sync sample — so the soundtrack's cues yield picture
    /// boundaries no decoder can start at.
    /// </param>
    /// <param name="cancellationToken">This runs on a web request, like the walk it replaces.</param>
    public IReadOnlyList<long>? KeyFrameTicksFromCues(ulong trackNumber,
                                                     CancellationToken cancellationToken = default)
    {
        if (_cuesAt < 0 || !source.CanSeek) return null;

        try
        {
            if (!SkipTo(_cuesAt)) return null;
            if (!TryReadElement(out var id, out var size) || id != IdCues || size < 0) return null;
            var end = Math.Min(source.Position + size, _segmentEnd);

            var ticks = new List<long>();
            var firstClusterAt = -1L;

            while (source.Position < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadElement(out var childId, out var childSize) || childSize < 0) break;
                var payloadAt = source.Position;
                if (payloadAt + childSize > end) break;

                if (childId == IdCuePoint && ReadCuePoint(payloadAt + childSize, trackNumber) is { } cue)
                {
                    ticks.Add(cue.Ticks);
                    if (firstClusterAt < 0) firstClusterAt = cue.ClusterAt;
                    if (ticks.Count > MaxSamples) return null;
                }

                if (!SkipTo(payloadAt + childSize)) break;
            }

            return IsUsableIndex(ticks) && PointsAtACluster(firstClusterAt) ? ticks : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A malformed index is one the cluster walk still answers. Never a throw.
            return null;
        }
    }

    /// <summary>One CuePoint's time and cluster, when it is about <paramref name="trackNumber"/>.</summary>
    private (long Ticks, long ClusterAt)? ReadCuePoint(long end, ulong trackNumber)
    {
        long? time = null;
        var clusterAt = -1L;
        var mine = false;

        while (source.Position < end)
        {
            if (!TryReadElement(out var id, out var size) || size < 0) return null;
            var payloadAt = source.Position;
            if (payloadAt + size > end) return null;

            if (id == IdCueTime)
            {
                time = (long)ReadUnsigned(size);
            }
            else if (id == IdCueTrackPositions)
            {
                var (track, at) = ReadCueTrackPositions(payloadAt + size);
                if (track == trackNumber) { mine = true; clusterAt = at; }
            }

            if (!SkipTo(payloadAt + size)) return null;
        }

        return mine && time is { } ticks ? (ticks, clusterAt) : null;
    }

    /// <summary>Which track a CueTrackPositions is about, and where its cluster is. Track 0 means "could not tell".</summary>
    private (ulong Track, long ClusterAt) ReadCueTrackPositions(long end)
    {
        ulong track = 0;
        var clusterAt = -1L;

        while (source.Position < end)
        {
            if (!TryReadElement(out var id, out var size) || size < 0) return (0, -1);
            var payloadAt = source.Position;
            if (payloadAt + size > end) return (0, -1);

            if (id == IdCueTrack) track = ReadUnsigned(size);
            else if (id == IdCueClusterPosition) clusterAt = _segmentAt + (long)ReadUnsigned(size);

            if (!SkipTo(payloadAt + size)) return (0, -1);
        }

        return (track, clusterAt);
    }

    /// <summary>
    /// Is this index worth believing? ⚠ A BROKEN index is worse than an absent one — absent falls back to
    /// the walk, broken puts every cut in the wrong place and nothing reports it.
    /// </summary>
    private bool IsUsableIndex(List<long> ticks)
    {
        // Fewer than two points says nothing about spacing, and is what ffmpeg refuses outright.
        if (ticks.Count < 2 || ticks[0] < 0) return false;

        // Strictly ascending, or it is not a timeline.
        for (var i = 1; i < ticks.Count; i++)
        {
            if (ticks[i] <= ticks[i - 1]) return false;
        }

        // A last cue past the declared duration is a real shape from real tools. One percent of tolerance
        // absorbs rounding without accepting a nonsense tail.
        if (DurationTicks is { } duration) return ticks[^1] <= duration * 1.01 + 1;

        // No declared duration: refuse an implausible magnitude, as ffmpeg does at 1e14 ns (~27 hours).
        return TimestampScaleNs <= 0 || ticks[^1] <= 100_000_000_000_000L / TimestampScaleNs;
    }

    /// <summary>
    /// Does the first cue's position actually land on a Cluster?
    /// <para>
    /// 🔴 <b>The classic Matroska index bug is an OFF-BY-SEGMENT one</b> — a stored position is relative to
    /// the Segment's data, and reading it as a file offset (or the reverse) yields an index that is
    /// structurally perfect, points at nothing, and produces cut boundaries no decoder can start at.
    /// </para>
    /// <para>
    /// ⚠ <b>It does NOT prove the cue TIMES are keyframes.</b> True when there was no position to check.
    /// </para>
    /// </summary>
    private bool PointsAtACluster(long clusterAt)
    {
        if (clusterAt < 0) return true;
        if (clusterAt < _segmentAt || clusterAt >= _segmentEnd) return false;
        var resume = source.Position;
        try
        {
            return SkipTo(clusterAt) && TryReadElement(out var id, out _) && id == IdCluster;
        }
        finally
        {
            source.Position = resume;
        }
    }

    /// <summary>
    /// Walk every cluster, recording each frame's POSITION and length for the requested tracks. The frame
    /// bytes are never read, which is what makes a remux cost one copy rather than a decode.
    /// </summary>
    /// <param name="trackNumbers">The tracks worth recording. Everything else is parsed and discarded.</param>
    /// <param name="cancellationToken">
    /// 🔴 <b>Checked once per CLUSTER, the only place in this class a caller can be let go.</b> This is the
    /// long walk and it is reached from a WEB REQUEST, where an abandoned request must stop costing a phone
    /// its disk and its thread.
    /// </param>
    /// <returns>False when the file is malformed past repair or exceeds <see cref="MaxSamples"/>.</returns>
    /// <exception cref="OperationCanceledException">
    /// The caller cancelled mid-walk. ⚠ A THROW rather than a <c>false</c> return, because false means "this
    /// file is malformed" — reporting a cancellation that way tells a user their video is broken.
    /// </exception>
    public bool ReadSamples(IReadOnlySet<ulong> trackNumbers, CancellationToken cancellationToken = default)
        => ReadSamplesUntil(trackNumbers, long.MaxValue, cancellationToken);

    /// <summary>
    /// The same walk, but STOPPING once it has read past <paramref name="untilTicks"/> — resumable, so a
    /// producer can index the window it is about to write and come back for the rest. This is what keeps a
    /// whole-file walk off the first-paint path.
    /// <para>
    /// ⚠ <b>It stops on a CLUSTER boundary, never inside one</b>, because block times are relative to their
    /// cluster's timestamp — a position halfway through one means nothing on its own.
    /// </para>
    /// <para>
    /// ⚠ Calls ACCUMULATE into <see cref="MatroskaTrack.Samples"/>, and the sample bound is counted across
    /// all of them. <see cref="SamplesComplete"/> is the only way to know there is no more; an empty chunk
    /// is not evidence of the end.
    /// </para>
    /// </summary>
    /// <param name="trackNumbers">The tracks worth recording. ⚠ The same set on every call, or a track that
    /// joins late has a hole where the earlier chunks should be.</param>
    /// <param name="untilTicks">Read at least up to this time on the file's own clock, then stop.</param>
    /// <param name="cancellationToken">Checked once per cluster, as in the unbounded walk.</param>
    public bool ReadSamplesUntil(IReadOnlySet<ulong> trackNumbers, long untilTicks,
                                 CancellationToken cancellationToken = default)
    {
        if (SamplesComplete) return true;
        if (_firstClusterAt < 0)
        {
            SamplesComplete = true;              // header-only file: no clusters, no samples, not an error
            return true;
        }

        source.Position = _resumeAt >= 0 ? _resumeAt : _firstClusterAt;
        var wanted = Tracks.Where(t => trackNumbers.Contains(t.Number)).ToDictionary(t => t.Number);

        while (source.Position < _segmentEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadElement(out var id, out var size)) break;
            var payloadAt = source.Position;

            if (id != IdCluster)
            {
                if (size < 0 || !SkipTo(payloadAt + size)) break;
                continue;
            }

            // ⚠ An unknown-size CLUSTER is REFUSED, not guessed: bounding it means scanning for the next
            // top-level id, which is guesswork on a file a page supplied, and a wrong guess corrupts
            // silently. It is a live-muxing shape, vanishingly rare on a file at rest.
            if (size < 0) return false;

            if (!ReadCluster(Math.Min(payloadAt + size, _segmentEnd), wanted, ref _recorded)) return false;
            if (!SkipTo(payloadAt + size)) break;

            // Resume HERE next time — past a whole cluster, which is the only resumable point.
            _resumeAt = source.Position;
            if (_lastClusterTicks > untilTicks) return true;
        }

        SamplesComplete = true;
        return true;
    }

    private bool ReadCluster(long end, Dictionary<ulong, MatroskaTrack> wanted, ref int total)
    {
        long clusterTicks = 0;

        while (source.Position < end)
        {
            if (!TryReadElement(out var id, out var size) || size < 0) return false;
            var payloadAt = source.Position;
            if (payloadAt + size > end) return false;

            switch (id)
            {
                case IdClusterTimestamp:
                    clusterTicks = (long)ReadUnsigned(size);
                    _lastClusterTicks = clusterTicks;       // what a BOUNDED walk stops against
                    break;

                case IdSimpleBlock:
                    // In a SimpleBlock the keyframe flag is in the block itself.
                    if (!ReadBlock(payloadAt, size, clusterTicks, wanted, keyFrame: null, ref total)) return false;
                    break;

                case IdBlockGroup:
                    if (!ReadBlockGroup(payloadAt + size, clusterTicks, wanted, ref total)) return false;
                    break;

                default:
                    if (!SkipTo(payloadAt + size)) return false;
                    break;
            }

            if (!SkipTo(payloadAt + size)) return false;
        }

        return true;
    }

    /// <summary>
    /// A BlockGroup is the older shape, and 🔴 <b>its keyframe rule is INVERTED relative to a SimpleBlock</b>:
    /// there is no flag, and a frame is a keyframe exactly when it carries no <c>ReferenceBlock</c>. Reading
    /// it the SimpleBlock way marks every frame a keyframe, so the sync table says "seek anywhere" about a
    /// stream where almost nowhere is seekable.
    /// </summary>
    private bool ReadBlockGroup(long end, long clusterTicks, Dictionary<ulong, MatroskaTrack> wanted, ref int total)
    {
        long blockAt = -1;
        long blockSize = 0;
        var referenced = false;

        while (source.Position < end)
        {
            if (!TryReadElement(out var id, out var size) || size < 0) return false;
            var payloadAt = source.Position;
            if (payloadAt + size > end) return false;

            if (id == IdBlock) { blockAt = payloadAt; blockSize = size; }
            else if (id == IdReferenceBlock) referenced = true;

            if (!SkipTo(payloadAt + size)) return false;
        }

        if (blockAt < 0) return true;
        return ReadBlock(blockAt, blockSize, clusterTicks, wanted, keyFrame: !referenced, ref total);
    }

    /// <summary>
    /// One (Simple)Block: track number, a 16-bit SIGNED offset from the cluster's timestamp, flags, then
    /// the frame — or several frames when the block is LACED.
    /// </summary>
    private bool ReadBlock(long at, long size, long clusterTicks,
                           Dictionary<ulong, MatroskaTrack> wanted, bool? keyFrame, ref int total)
    {
        source.Position = at;
        var end = at + size;

        if (!TryReadVarInt(keepMarker: false, out var trackNumber)) return false;
        if (source.Position + 3 > end) return false;

        Span<byte> head = stackalloc byte[3];
        if (source.ReadAtLeast(head, 3, throwOnEndOfStream: false) != 3) return false;
        var relative = BinaryPrimitives.ReadInt16BigEndian(head[..2]);
        var flags = head[2];

        // Not a track we are copying. The track number is INSIDE the block, so there was no cheaper way.
        if (!wanted.TryGetValue(trackNumber, out var track)) return true;

        var ticks = clusterTicks + relative;
        var isKey = keyFrame ?? ((flags & 0x80) != 0);
        var payloadAt = source.Position;
        var payloadSize = end - payloadAt;
        if (payloadSize < 0) return false;

        // 🔴 Bits 0x06 select the LACING scheme. Audio blocks are routinely laced — several AAC frames share
        // one block header — so a remuxer that ignores lacing silently drops most of the soundtrack.
        var frames = (flags & 0x06) switch
        {
            0x00 => [payloadSize],
            0x02 => ReadXiphLacing(end),
            0x04 => ReadFixedLacing(end),
            _ => ReadEbmlLacing(end),
        };
        if (frames is null) return false;

        var offset = source.Position;
        for (var i = 0; i < frames.Length; i++)
        {
            var length = frames[i];
            if (length < 0 || offset + length > end || length > int.MaxValue) return false;
            if (++total > MaxSamples) return false;

            // ⚠ Laced frames share ONE timestamp on the wire. DefaultDuration spaces them when the track
            // declares it; otherwise they stay tied and Mp4Remuxer's timing pass spreads them.
            var frameTicks = track.DefaultDurationNs > 0 && TimestampScaleNs > 0
                ? ticks + i * (track.DefaultDurationNs / TimestampScaleNs)
                : ticks;

            track.Samples.Add(new MatroskaSample(offset, (int)length, frameTicks, isKey));
            offset += length;
        }

        return true;
    }

    /// <summary>Xiph lacing: each size but the last is a run of 0xFF bytes terminated by a byte below 0xFF.</summary>
    private long[]? ReadXiphLacing(long end)
    {
        var count = source.ReadByte();
        if (count < 0) return null;
        var sizes = new long[count + 1];

        long known = 0;
        for (var i = 0; i < count; i++)
        {
            long size = 0;
            while (true)
            {
                var b = source.ReadByte();
                if (b < 0 || source.Position > end) return null;
                size += b;
                if (b != 0xFF) break;
            }
            sizes[i] = size;
            known += size;
        }

        sizes[count] = end - source.Position - known;
        return sizes[count] < 0 ? null : sizes;
    }

    /// <summary>Fixed-size lacing: one count, and the remaining bytes divide evenly between the frames.</summary>
    private long[]? ReadFixedLacing(long end)
    {
        var count = source.ReadByte();
        if (count < 0) return null;

        var frames = count + 1;
        var remaining = end - source.Position;
        if (remaining < 0 || remaining % frames != 0) return null;

        var each = remaining / frames;
        var sizes = new long[frames];
        Array.Fill(sizes, each);
        return sizes;
    }

    /// <summary>
    /// EBML lacing: the first size is an unsigned vint, and each one after it is a SIGNED delta from the
    /// previous, biased by half its own range.
    /// </summary>
    private long[]? ReadEbmlLacing(long end)
    {
        var count = source.ReadByte();
        if (count < 0) return null;
        var sizes = new long[count + 1];

        long known = 0;

        // 🔴 `count` is frames MINUS ONE and EBML lacing codes exactly `count` sizes, so a block declaring
        // ONE frame codes NONE. Reading the first vint unconditionally consumes the frame's own bytes as a
        // length — refusing the whole file, or planning the wrong bytes. Xiph and fixed lacing are immune.
        // Pinned by `An_EBML_laced_block_with_a_SINGLE_frame_codes_no_sizes`.
        if (count > 0)
        {
            if (!TryReadVarInt(keepMarker: false, out var first)) return null;
            sizes[0] = (long)first;
            known = sizes[0];

            for (var i = 1; i < count; i++)
            {
                if (!TryReadVarInt(keepMarker: false, out var raw, out var width)) return null;
                // The bias is 2^(7·width − 1) − 1: a vint of `width` bytes carries 7·width value bits.
                var bias = (1L << (7 * width - 1)) - 1;
                sizes[i] = sizes[i - 1] + ((long)raw - bias);
                if (sizes[i] < 0) return null;
                known += sizes[i];
            }
        }

        sizes[count] = end - source.Position - known;
        return sizes[count] < 0 ? null : sizes;
    }

    private void ReadInfo(long end)
    {
        while (source.Position < end)
        {
            if (!TryReadElement(out var id, out var size) || size < 0) return;
            var payloadAt = source.Position;
            if (payloadAt + size > end) return;

            switch (id)
            {
                case IdTimestampScale when size is > 0 and <= 8:
                    TimestampScaleNs = (long)ReadUnsigned(size);
                    break;
                case IdDuration when size is 4 or 8:
                    DurationTicks = ReadFloat(size);
                    break;
            }

            if (!SkipTo(payloadAt + size)) return;
        }
    }

    private void ReadTracks(long end)
    {
        while (source.Position < end)
        {
            if (!TryReadElement(out var id, out var size) || size < 0) return;
            var payloadAt = source.Position;
            if (payloadAt + size > end) return;

            if (id == IdTrackEntry) ReadTrackEntry(payloadAt + size);
            if (!SkipTo(payloadAt + size)) return;
        }
    }

    private void ReadTrackEntry(long end)
    {
        var track = new MatroskaTrack();
        ulong type = 0;

        while (source.Position < end)
        {
            if (!TryReadElement(out var id, out var size) || size < 0) return;
            var payloadAt = source.Position;
            if (payloadAt + size > end) return;

            switch (id)
            {
                case IdTrackNumber when size <= 8: track.Number = ReadUnsigned(size); break;
                case IdTrackType when size <= 8: type = ReadUnsigned(size); break;
                case IdCodecId: track.CodecId = ReadAscii(size); break;
                case IdCodecPrivate: track.CodecPrivate = ReadBytes(size); break;
                case IdDefaultDuration when size <= 8: track.DefaultDurationNs = (long)ReadUnsigned(size); break;
                case IdVideo: ReadVideo(track, payloadAt + size); break;
                case IdAudio: ReadAudio(track, payloadAt + size); break;
            }

            if (!SkipTo(payloadAt + size)) return;
        }

        track.Kind = type switch
        {
            TrackTypeVideo => MediaStreamKind.Video,
            TrackTypeAudio => MediaStreamKind.Audio,
            _ => MediaStreamKind.Unknown,
        };

        // ⚠ Only the two kinds a remux can carry are kept — subtitles never reach `Tracks`.
        if (track.Kind is MediaStreamKind.Video or MediaStreamKind.Audio) Tracks.Add(track);
    }

    private void ReadVideo(MatroskaTrack track, long end)
    {
        while (source.Position < end)
        {
            if (!TryReadElement(out var id, out var size) || size < 0) return;
            var payloadAt = source.Position;
            if (payloadAt + size > end) return;

            if (id == IdPixelWidth && size <= 8) track.Width = (int)ReadUnsigned(size);
            else if (id == IdPixelHeight && size <= 8) track.Height = (int)ReadUnsigned(size);

            if (!SkipTo(payloadAt + size)) return;
        }
    }

    private void ReadAudio(MatroskaTrack track, long end)
    {
        while (source.Position < end)
        {
            if (!TryReadElement(out var id, out var size) || size < 0) return;
            var payloadAt = source.Position;
            if (payloadAt + size > end) return;

            if (id == IdSamplingFrequency && size is 4 or 8) track.SampleRate = ReadFloat(size);
            else if (id == IdChannels && size <= 8) track.Channels = (int)ReadUnsigned(size);

            if (!SkipTo(payloadAt + size)) return;
        }
    }

    // ── EBML primitives ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Read one element's id and payload size. Size is −1 for EBML's "unknown size".</summary>
    private bool TryReadElement(out ulong id, out long size)
    {
        id = 0;
        size = 0;
        if (source.Position >= Length) return false;
        if (!TryReadVarInt(keepMarker: true, out id)) return false;
        if (!TryReadVarInt(keepMarker: false, out var rawSize)) return false;

        size = rawSize == ulong.MaxValue ? -1 : (long)rawSize;
        // A length that promises more than the file holds is malformed, not merely large.
        if (size > Length - source.Position) size = -1;
        return true;
    }

    private bool TryReadVarInt(bool keepMarker, out ulong value) => TryReadVarInt(keepMarker, out value, out _);

    /// <summary>
    /// EBML's variable-length integer: the first set bit of the first byte says how many bytes it spans.
    /// <para>
    /// 🔴 <b>An ID keeps that marker and a SIZE drops it</b> — the marker is part of an id's identity and is
    /// not part of a size's value. Conflating them reads every element wrong. This is the one rule
    /// <see cref="MatroskaProbe"/>'s reader and this one must never disagree about, which is why both state
    /// it.
    /// </para>
    /// </summary>
    private bool TryReadVarInt(bool keepMarker, out ulong value, out int width)
    {
        value = 0;
        width = 0;

        var first = source.ReadByte();
        if (first < 0) return false;

        width = 1;
        var mask = 0x80;
        while (width <= 8 && (first & mask) == 0)
        {
            width++;
            mask >>= 1;
        }
        if (width > 8) return false;   // a leading zero byte is not a legal length descriptor

        var allBitsSet = (first & (mask - 1)) == mask - 1;
        value = keepMarker ? (ulong)first : (ulong)(first & (mask - 1));

        for (var i = 1; i < width; i++)
        {
            var next = source.ReadByte();
            if (next < 0) return false;
            value = (value << 8) | (byte)next;
            allBitsSet &= next == 0xFF;
        }

        if (!keepMarker && allBitsSet) value = ulong.MaxValue;
        return true;
    }

    private ulong ReadUnsigned(long size)
    {
        ulong value = 0;
        for (var i = 0L; i < size; i++)
        {
            var b = source.ReadByte();
            if (b < 0) return value;
            value = (value << 8) | (byte)b;
        }
        return value;
    }

    private double ReadFloat(long size)
    {
        Span<byte> buffer = stackalloc byte[8];
        var take = (int)size;
        if (source.ReadAtLeast(buffer[..take], take, throwOnEndOfStream: false) != take) return 0;
        return take == 4
            ? BinaryPrimitives.ReadSingleBigEndian(buffer[..4])
            : BinaryPrimitives.ReadDoubleBigEndian(buffer[..8]);
    }

    private string? ReadAscii(long size)
    {
        var take = (int)Math.Min(size, 256);
        var buffer = new byte[take];
        var read = source.ReadAtLeast(buffer, take, throwOnEndOfStream: false);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, read).TrimEnd('\0');
    }

    /// <summary>
    /// CodecPrivate, bounded. A decoder configuration record is tens of bytes, so a declared megabyte is a
    /// malformed file rather than a rich one.
    /// </summary>
    private byte[]? ReadBytes(long size)
    {
        if (size is <= 0 or > 64 * 1024) return null;
        var buffer = new byte[size];
        var read = source.ReadAtLeast(buffer, (int)size, throwOnEndOfStream: false);
        return read == size ? buffer : null;
    }

    /// <summary>Seek forward to an absolute position, refusing anything outside the file.</summary>
    private bool SkipTo(long position)
    {
        if (position < 0 || position > Length) return false;
        source.Position = position;
        return true;
    }
}

/// <summary>One track as Matroska declared it, plus where its frames are.</summary>
internal sealed class MatroskaTrack
{
    public ulong Number { get; set; }
    public MediaStreamKind Kind { get; set; }

    /// <summary>The raw Matroska CodecID (<c>V_MPEG4/ISO/AVC</c>), untranslated — the remuxer maps it itself.</summary>
    public string? CodecId { get; set; }

    /// <summary>The decoder configuration record, which MP4 needs verbatim in its sample entry.</summary>
    public byte[]? CodecPrivate { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }
    public double SampleRate { get; set; }
    public int Channels { get; set; }

    /// <summary>Nanoseconds per frame when the track declares it. 0 when it does not.</summary>
    public long DefaultDurationNs { get; set; }

    public List<MatroskaSample> Samples { get; } = [];
}

/// <summary>
/// Where one frame lives in the source and when it is shown — a POSITION, not the bytes, so a remux copies
/// each frame exactly once straight from the source into the output.
/// </summary>
/// <param name="Offset">Absolute position of the frame in the source file.</param>
/// <param name="Length">Frame length in bytes.</param>
/// <param name="Ticks">Presentation time, in the file's own timestamp ticks.</param>
/// <param name="KeyFrame">Whether a player may start decoding here.</param>
internal readonly record struct MatroskaSample(long Offset, int Length, long Ticks, bool KeyFrame);
