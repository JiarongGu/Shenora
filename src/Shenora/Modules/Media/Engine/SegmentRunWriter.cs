namespace Shenora.Modules.Media;

/// <summary>
/// The pump behind <see cref="DefaultSegmentEngine"/>: take source frames, COPY the ones MP4 can carry and
/// push the rest through the platform's codecs, and cut the output into numbered fMP4 fragments on the plan.
/// Split out of the engine because it is the part with STATE, and the part a fake
/// <see cref="IMediaStreamConversion"/> can drive end to end on a machine with no codecs at all.
/// <para>
/// 🔴 <b>COPYING IS THE DEFAULT AND CONVERTING IS THE FALLBACK</b> (D76): the platform video encoders offer
/// h263/mpeg4/mpeg2video, none of which a webview decodes, so re-encoding everything yields SOUND-ONLY
/// segments for essentially every real film. Only a stream MP4 cannot hold (AC-3, DTS, VP9) costs a codec.
/// </para>
/// <para>
/// 🔴 <b>THE THREE KINDS OF CHANNEL ARE TIMED DIFFERENTLY, and one rule for all of them produces a stream
/// that appends cleanly and buffers NOTHING</b>: a COPIED track on the source's own clock, a CONVERTED
/// picture in microseconds from what the encoder stamped, a CONVERTED soundtrack on its own sample rate from
/// the PACKET COUNT — an audio encoder stamps nothing, so timing it from presentation-time gaps gives every
/// packet a 1 µs duration. ⚠ <b>Which is why a cut is expressed in SECONDS and converted per channel</b>:
/// one <c>upTo</c> in the lead's units measures a video microsecond against an audio sample index, and the
/// sound side of every cut falls wherever the round-robin had reached rather than on the boundary.
/// </para>
/// </summary>
internal sealed class SegmentRunWriter(
    DefaultSegmentEngine owner, SegmentRunRequest request, SourceTimeline timeline) : IDisposable
{
    /// <summary>What a CONVERTED picture is timed in — see the type remarks.</summary>
    private const uint MicrosecondTimescale = 1_000_000;

    private readonly List<IDisposable> _runs = [];
    private bool _initWritten;

    /// <summary>The track ids the init segment declared — the only ones a fragment may carry. See <see cref="Flush"/>.</summary>
    private HashSet<int> _declared = [];

    /// <summary>Tracks already reported as arriving late, so one stall is one line rather than one per fragment.</summary>
    private readonly HashSet<int> _reportedLate = [];

    /// <summary>Read, copy or convert, and write until the source ends or the token fires.</summary>
    /// <param name="source">The source file, seekable — sample bytes are read by offset.</param>
    /// <param name="video">The picture track and how it travels, or null for a sound-only run.</param>
    /// <param name="audio">The sound track and how it travels, or null.</param>
    /// <param name="from">Where to start reading the LEAD track — the seek target, already keyframe-aligned.</param>
    /// <param name="startSeconds">Where the first segment begins on the media timeline.</param>
    /// <param name="conversionOf">The shell's codecs, or null when this shell registered none.</param>
    /// <param name="cancellationToken">Checked between frames; disposing the run fires it.</param>
    public void Run(Stream source, SegmentTrack? video, SegmentTrack? audio,
                    int from, double startSeconds, IMediaStreamConversion? conversionOf,
                    CancellationToken cancellationToken)
    {
        var lead = (video ?? audio)!.Track;
        var tracks = new List<Channel>();

        if (video is not null && Open(video, conversionOf, MediaStreamKind.Video) is { } v) tracks.Add(v);
        if (audio is not null && Open(audio, conversionOf, MediaStreamKind.Audio) is { } a) tracks.Add(a);
        if (tracks.Count == 0)
        {
            owner.Report("segments: nothing could be opened for this source");
            return;
        }

        var segment = request.FirstSegment;
        var frame = Array.Empty<byte>();
        var startTicks = timeline.TicksAt(startSeconds);

        foreach (var channel in tracks)
        {
            // Each track seeks independently: their keyframes are their own, and using the picture's index
            // for both starts the sound in the wrong place.
            channel.Next = ReferenceEquals(channel.Track, lead)
                ? from
                : SegmentGrid.SeekIndex(channel.Track.Samples, startTicks);
            channel.Start = channel.Next;
            // Only a converted soundtrack needs this: a copy carries the source's own times, and a converted
            // picture is stamped by the encoder from the source's.
            channel.StartTime = channel.Conversion is not null && !channel.IsVideo
                ? (long)Math.Round(startSeconds * channel.Timescale)
                : 0;
        }

        // Round-robin by source time, so neither side runs far ahead holding output nobody has cut yet.
        while (!cancellationToken.IsCancellationRequested)
        {
            var channel = tracks
                .Where(c => c.Next < c.Track.Samples.Count)
                .OrderBy(c => c.Track.Samples[c.Next].Ticks)
                .FirstOrDefault();
            if (channel is null) break;

            var index = channel.Next++;
            var sample = channel.Track.Samples[index];
            if (frame.Length < sample.Length) frame = new byte[sample.Length];
            source.Position = sample.Offset;
            if (source.ReadAtLeast(frame.AsSpan(0, sample.Length), sample.Length, throwOnEndOfStream: false) != sample.Length)
            {
                owner.Report("segments: the source ended mid-frame — stopping rather than writing a torn sample");
                break;
            }

            if (channel.Conversion is null)
            {
                // A copy: bytes and timeline both come from the source, so nothing waits and nothing drops.
                Take(channel, index, frame.AsSpan(0, sample.Length).ToArray());
            }
            else
            {
                // Zero outputs is NORMAL: codecs buffer, and a video encoder holds a GOP. Treating an empty
                // return as failure abandons a working conversion in its opening second.
                var micros = (long)Math.Round(timeline.SecondsOf(sample.Ticks) * 1_000_000);
                Accept(channel, channel.Conversion.Push(new MediaFrame(frame.AsMemory(0, sample.Length), micros, sample.KeyFrame)));
            }

            segment = CutIfDue(tracks, segment, cancellationToken);
        }

        // 🔴 Without the drain the tail sits inside the codec and the last segment is short, in a file that is
        // otherwise well-formed — playback simply stops early. A copied channel has no tail: its last frame
        // was written the moment it was read.
        if (!cancellationToken.IsCancellationRequested)
        {
            foreach (var channel in tracks.Where(c => c.Conversion is not null))
            {
                Accept(channel, channel.Conversion!.Drain());
            }
            segment = CutIfDue(tracks, segment, cancellationToken);
            Flush(tracks, segment);                 // whatever is left is the final segment
        }

        // 🔴 What each track actually CONTRIBUTED. A short track is silent by construction — the fragments are
        // well-formed, every append succeeds, and playback stalls only because `SourceBuffer.buffered` is the
        // INTERSECTION of the tracks — so `of` (what the source held), `from` (where this run began) and
        // `emitted` (what came out) are the only way to tell a missing sample from a late seek.
        foreach (var channel in tracks)
        {
            owner.Report($"segments: {(channel.IsVideo ? "picture" : "sound")} "
                       + $"({(channel.Conversion is null ? "copied" : "converted")}) "
                       + $"from={channel.Start} of={channel.Track.Samples.Count} read={channel.Next - channel.Start} "
                       + $"emitted={channel.Emitted}");
        }
    }

    /// <summary>
    /// Begin one track: a copy needs nothing opened, a conversion needs the device to agree. Null when the
    /// device declines it after all.
    /// </summary>
    private Channel? Open(SegmentTrack choice, IMediaStreamConversion? conversion, MediaStreamKind kind)
    {
        var track = choice.Track;
        var isVideo = kind is MediaStreamKind.Video;

        if (choice.Copy)
        {
            // Checked again rather than trusted: the entry is what a decoder starts from, and the selection
            // that chose this track is far enough from here to disagree about a malformed file.
            if (Mp4Carriage.EntryFor(track) is not { } entry)
            {
                owner.Report($"segments: the {kind} track declares a carriable codec but no usable configuration");
                return null;
            }

            var channel = new Channel
            {
                Track = track,
                Conversion = null,
                IsVideo = isVideo,
                TrackId = isVideo ? DefaultSegmentEngine.VideoTrackId : DefaultSegmentEngine.AudioTrackId,
                // The source's own clock, stated exactly — see SourceTimeline.
                Timescale = timeline.Timescale,
                SamplesPerPacket = 0,
                SampleEntry = entry,
            };
            Retime(channel);
            return channel;
        }

        if (conversion is null) return null;

        var codec = MatroskaProbe.CodecNameOf(track.CodecId, track.CodecPrivate ?? ReadOnlyMemory<byte>.Empty);
        var info = new MediaStreamInfo(kind, codec ?? string.Empty)
        {
            Width = track.Width,
            Height = track.Height,
            // ⚠ Rounded rather than truncated — Matroska stores a rate as a double, and a codec configured at
            // 47999 Hz does not fail: it plays everything slightly flat.
            SampleRate = (int)Math.Round(track.SampleRate),
            Channels = track.Channels,
        };

        var run = conversion.Begin(info, track.CodecPrivate ?? ReadOnlyMemory<byte>.Empty);
        if (run is null)
        {
            owner.Report($"segments: the device declined to open a {kind} converter for '{codec}'");
            return null;
        }

        _runs.Add(run);
        var format = run.OutputFormat;
        return new Channel
        {
            Track = track,
            Conversion = run,
            IsVideo = isVideo,
            TrackId = isVideo ? DefaultSegmentEngine.VideoTrackId : DefaultSegmentEngine.AudioTrackId,
            Timescale = isVideo ? MicrosecondTimescale : (uint)Math.Max(format.SampleRate ?? 48_000, 1),
            SamplesPerPacket = isVideo ? 0 : Math.Max(run.OutputFramesPerPacket, 1),
        };
    }

    /// <summary>
    /// Build a COPIED track's decode timeline once, for the whole track.
    /// <para>
    /// 🔴 <b>Matroska stores the time a frame is SHOWN and MP4 states the time it is DECODED, and with
    /// B-frames those are not the same order.</b> <see cref="SampleTiming.Derive"/> is what
    /// <see cref="Mp4Remuxer"/> already uses — a remux and a fragment run disagreeing about one file's timing
    /// is a bug nobody can see from either side.
    /// </para>
    /// <para>
    /// ⚠ <b>The whole track at once, not per fragment, and the shift is CANCELLED</b> — per-fragment
    /// derivation gives neighbours different shifts and leaves a gap between them. Version-1 <c>trun</c>
    /// offsets are SIGNED, so the presentation stays where the source put it and the offsets carry the
    /// difference (<see cref="Mp4FragmentSample.CompositionOffset"/>).
    /// </para>
    /// </summary>
    private void Retime(Channel channel)
    {
        var samples = channel.Track.Samples;
        var presentation = new long[samples.Count];
        for (var i = 0; i < samples.Count; i++) presentation[i] = samples[i].Ticks * timeline.Factor;

        var (decode, composition, shift) = SampleTiming.Derive(presentation);
        for (var i = 0; i < composition.Length; i++) composition[i] -= shift;

        // The track's declared frame duration on this timeline — what the LAST sample falls back to, having
        // no next frame to measure against.
        var step = channel.Track.DefaultDurationNs > 0 && timeline.Timescale > 0
            ? channel.Track.DefaultDurationNs * timeline.Timescale / 1_000_000_000L
            : 0;

        channel.Presentation = presentation;
        channel.Decode = decode;
        channel.Composition = composition;
        channel.Durations = SampleTiming.Durations(decode, step);
    }

    /// <summary>Take one COPIED sample, with the timing derived for the whole track.</summary>
    private static void Take(Channel channel, int index, byte[] data)
    {
        channel.Emitted++;      // counted for the end-of-run line; a copy's TIMES never come from it
        channel.Pending.Add(new Pending(
            Decode: channel.Decode![index],
            Presentation: channel.Presentation![index],
            Duration: channel.Durations![index],
            Composition: channel.Composition![index],
            KeyFrame: channel.Track.Samples[index].KeyFrame,
            Data: data));
    }

    /// <summary>
    /// The time of the output frame about to be accepted, on the CONVERTED channel's own timeline: a picture
    /// matches what the encoder stamped 1:1 in microseconds, a soundtrack is the packet index times the
    /// frames per packet on its own sample rate.
    /// <para>
    /// 🔴 <b>PLUS WHERE THIS RUN BEGAN, because the packet count is RELATIVE and every other clock here is
    /// ABSOLUTE.</b> <see cref="Channel.Emitted"/> starts at zero for every run, so without this a run
    /// producing segment N times its sound from zero while its copied picture sits at N's real start — and
    /// <c>SourceBuffer.buffered</c> being the INTERSECTION of the tracks, the page gets a fraction of a second
    /// of media and stalls. ⚠ <b>Invisible whenever a run starts at segment 0</b>, where relative and absolute
    /// agree — which is every test in this suite and every run that never seeks.
    /// </para>
    /// </summary>
    private static long TimeOf(Channel channel) => channel.IsVideo
        ? channel.LastTime
        : channel.StartTime + channel.Emitted * channel.SamplesPerPacket;

    /// <summary>
    /// Take a codec's outputs into the segment being filled.
    /// <para>
    /// ⚠ <b>Reordered output is REFUSED rather than reordered here.</b> A converted channel states sample
    /// durations as the gap to the next sample, so a backwards presentation time gives a negative duration —
    /// unrepresentable, and rounding it to zero writes a segment that plays frames on top of each other. Both
    /// platform encoders are configured not to reorder, so this is a fail-closed guard on an assumption; a
    /// COPIED channel needs none of it, <see cref="Retime"/> expressing its reordering exactly.
    /// </para>
    /// </summary>
    private void Accept(Channel channel, IReadOnlyList<MediaFrame> outputs)
    {
        foreach (var output in outputs)
        {
            if (output.Data.Length == 0) continue;
            if (channel.Pending.Count > 0 && output.PresentationTimeUs < channel.LastTime)
            {
                if (!channel.WarnedReordering)
                {
                    channel.WarnedReordering = true;
                    owner.Report($"segments: the {(channel.IsVideo ? "video" : "audio")} encoder REORDERED its "
                               + "output, which this engine does not support — dropping the out-of-order frame. "
                               + "Segments will be short rather than wrong.");
                }
                continue;
            }

            channel.LastTime = output.PresentationTimeUs;
            var at = TimeOf(channel);
            channel.Pending.Add(new Pending(at, at, Duration: 0, Composition: 0, output.IsKeyframe, output.Data.ToArray()));
            channel.Emitted++;
        }
    }

    /// <summary>
    /// Cut when the lead track reaches a keyframe past the current segment's end. Returns the segment now
    /// being filled.
    /// </summary>
    private int CutIfDue(List<Channel> tracks, int segment, CancellationToken cancellationToken)
    {
        var lead = tracks[0];
        for (var i = 1; i < lead.Pending.Count; i++)
        {
            var at = lead.Pending[i];
            var seconds = lead.SecondsOf(at.Presentation);
            if (!request.Plan.StartsNewSegment(seconds, at.KeyFrame, segment)) continue;
            if (cancellationToken.IsCancellationRequested) break;

            Flush(tracks, segment, upTo: seconds);
            segment = request.Plan.IndexOf(seconds);
            i = 0;                                   // the list shifted under us
        }
        return segment;
    }

    /// <summary>
    /// Write one fragment: every track's pending samples strictly before <paramref name="upTo"/> SECONDS, or
    /// all of them when it is null (the final segment). ⚠ The boundary arrives in seconds and each channel
    /// converts it into its OWN timescale — comparing one channel's times against another's is how an audio
    /// cut lands nowhere near the video cut it is supposed to match.
    /// </summary>
    private void Flush(List<Channel> tracks, int segment, double? upTo = null)
    {
        var data = new List<Mp4FragmentTrackData>();

        foreach (var channel in tracks)
        {
            var take = channel.Pending.Count;
            if (upTo is not null)
            {
                var limit = channel.TicksAt(upTo.Value);
                var found = channel.Pending.FindIndex(p => p.Presentation >= limit);
                if (found >= 0) take = found;
            }
            if (take <= 0) continue;

            var samples = new List<Mp4FragmentSample>(take);
            var bytes = new List<byte>();
            for (var i = 0; i < take; i++)
            {
                var current = channel.Pending[i];
                // A COPIED sample already knows its duration, measured across the whole track. A CONVERTED
                // one is the gap to the next frame, and the LAST in a fragment borrows the previous gap
                // rather than holding the segment hostage to a frame that has not arrived. Converted audio is
                // a FIXED number of samples per packet, so its duration is arithmetic and exact.
                var next = i + 1 < channel.Pending.Count ? channel.Pending[i + 1].Decode : (long?)null;
                var duration = current.Duration > 0 ? current.Duration
                             : !channel.IsVideo ? channel.SamplesPerPacket
                             : next is not null ? next.Value - current.Decode
                             : samples.Count > 0 ? samples[^1].Duration
                             : DefaultDuration(channel);
                samples.Add(new Mp4FragmentSample(Math.Max(duration, 1), current.Data.Length,
                                                  current.Composition, current.KeyFrame));
                bytes.AddRange(current.Data);
            }

            data.Add(new Mp4FragmentTrackData
            {
                Track = Declare(channel),
                BaseMediaDecodeTime = channel.Pending[0].Decode,
                Samples = samples,
                Data = bytes.ToArray(),
            });
            channel.Pending.RemoveRange(0, take);
        }

        if (data.Count == 0) return;

        // The init segment carries the decoder configuration, which for a CONVERTED track is knowable only
        // once the encoder has produced output — so it is written HERE, beside the first fragment, and a
        // consumer waits for it exactly as it waits for seg0.
        if (!_initWritten)
        {
            WriteInit(data.Select(d => d.Track).ToList());
            _declared = [.. data.Select(d => d.Track.TrackId)];
            _initWritten = true;
        }

        // 🔴 A fragment may only carry a track the init segment DECLARED — the `moov`/`trex` pair there is the
        // only place a track id is defined. They diverge because the init is written beside the FIRST
        // fragment: a COPIED track produces from its first frame while an encoder may hold a whole segment's
        // worth. Dropping the late samples costs that track its opening seconds; writing them puts an
        // undeclared id in the stream, which a MediaSource may reject SILENTLY.
        for (var i = data.Count - 1; i >= 0; i--)
        {
            if (_declared.Contains(data[i].Track.TrackId)) continue;
            var late = data[i].Track;
            if (_reportedLate.Add(late.TrackId))
            {
                owner.Report($"segments: the {(late.IsVideo ? "video" : "audio")} track produced nothing until "
                           + "after the init segment was written, so it is not declared there — dropping its "
                           + "samples rather than writing a fragment nothing can decode");
            }
            data.RemoveAt(i);
        }

        if (data.Count == 0) return;

        var path = Path.Combine(request.Directory, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"seg{segment}{SegmentRunRequest.SegmentExtension}"));
        // Sequence numbers are 1-based and strictly increasing; the segment index is 0-based.
        Publish(path, file => Mp4FragmentWriter.WriteFragment(file, segment + 1, data));
    }

    private void WriteInit(IReadOnlyList<Mp4FragmentTrack> tracks)
    {
        var path = Path.Combine(request.Directory, SegmentRunRequest.InitSegmentName);
        Publish(path, file => Mp4FragmentWriter.WriteInitSegment(file, tracks));
    }

    /// <summary>
    /// Write a part to <c>{path}.part</c> and RENAME it into place, so it becomes visible only once it is
    /// whole (<see cref="SegmentRunRequest.PartialExtension"/>).
    /// <para>
    /// 🔴 <b>The consumer serves a part the moment it exists</b>, so writing in place publishes a truncated
    /// fragment for however long the write takes — which appends without error and plays for a fraction of a
    /// second. Renaming inside one directory is atomic on every platform this ships to.
    /// </para>
    /// <para>
    /// ⚠ A failed write leaves the <c>.part</c> rather than a corrupt final name; the route sweeps those when
    /// it next opens the source. Leaving it is better than deleting it here — a delete that itself fails
    /// during a teardown would be a second exception on the way out of the first.
    /// </para>
    /// </summary>
    private static void Publish(string path, Action<Stream> write)
    {
        var partial = path + SegmentRunRequest.PartialExtension;
        using (var file = File.Create(partial)) write(file);
        File.Move(partial, path, overwrite: true);
    }

    /// <summary>
    /// The track as the init segment must declare it. A COPIED track is declared from the SOURCE, whose
    /// configuration its frames were encoded with; a CONVERTED one from the RUN's output format, because a
    /// decoder may downmix and an encoder may align dimensions up to a macroblock.
    /// </summary>
    private static Mp4FragmentTrack Declare(Channel channel)
    {
        if (channel.Conversion is null)
        {
            return new Mp4FragmentTrack
            {
                TrackId = channel.TrackId,
                Timescale = channel.Timescale,
                IsVideo = channel.IsVideo,
                Width = channel.Track.Width,
                Height = channel.Track.Height,
                SampleEntry = channel.SampleEntry!,
            };
        }

        var format = channel.Conversion.OutputFormat;
        var config = channel.Conversion.OutputConfig.ToArray();

        return new Mp4FragmentTrack
        {
            TrackId = channel.TrackId,
            Timescale = channel.Timescale,
            IsVideo = channel.IsVideo,
            Width = format.Width ?? 0,
            Height = format.Height ?? 0,
            SampleEntry = channel.IsVideo
                ? Mp4Builder.VisualSampleEntry(
                    EntryType(format.Codec), EntryType(format.Codec) == "hvc1" ? "hvcC" : "avcC",
                    format.Width ?? 0, format.Height ?? 0, config)
                : Mp4Builder.AudioSampleEntry(format.Channels ?? 2, format.SampleRate ?? 48_000, config),
        };
    }

    private static string EntryType(string? codec)
        => string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase) ? "hvc1" : "avc1";

    /// <summary>A single-sample CONVERTED fragment's duration, when there is no gap to measure.</summary>
    private static long DefaultDuration(Channel channel)
    {
        if (channel.IsVideo) return 1_000_000 / 30;              // one frame at a conventional rate
        var rate = channel.Conversion!.OutputFormat.SampleRate ?? 48_000;
        var perPacket = Math.Max(channel.Conversion.OutputFramesPerPacket, 1);
        return (long)(perPacket * 1_000_000 / Math.Max(rate, 1));
    }

    public void Dispose()
    {
        // A device has a handful of hardware codecs and a video run holds TWO. Leaking one does not leak
        // memory: it makes the NEXT conversion fail with a resource error that names nothing.
        foreach (var run in _runs)
        {
            try { run.Dispose(); }
            catch (Exception ex) { owner.Report($"segments: a codec would not release ({ex.GetType().Name})"); }
        }
        _runs.Clear();
    }

    /// <summary>One sample waiting to be written, on its own channel's timescale.</summary>
    /// <param name="Decode">
    /// When it is decoded — what <c>tfdt</c> and a duration are measured on. Differs from
    /// <paramref name="Presentation"/> only for a copied track with B-frames.
    /// </param>
    /// <param name="Presentation">When it is SHOWN — what a cut is decided against, a plan's boundaries being
    /// presentation times.</param>
    /// <param name="Duration">Known exactly for a copy; 0 for a conversion, which is measured at flush time.</param>
    /// <param name="Composition">Presentation minus decode, signed. Zero for anything without B-frames.</param>
    /// <param name="KeyFrame">Whether a decoder may start here.</param>
    /// <param name="Data">The sample's bytes, owned by this entry.</param>
    private sealed record Pending(long Decode, long Presentation, long Duration, long Composition,
                                  bool KeyFrame, byte[] Data);

    private sealed class Channel
    {
        public required MatroskaTrack Track { get; init; }

        /// <summary>The codec run, or null when this track is COPIED and needs none.</summary>
        public required IMediaStreamConversionRun? Conversion { get; init; }

        public required bool IsVideo { get; init; }
        public required int TrackId { get; init; }

        /// <summary>Ticks per second on THIS track's timeline — see the type remarks.</summary>
        public required uint Timescale { get; init; }

        /// <summary>Frames per output packet; 0 for a picture and for any copy, whose frames are timed individually.</summary>
        public required int SamplesPerPacket { get; init; }

        /// <summary>A copied track's <c>stsd</c> entry, taken from the source. Null for a conversion.</summary>
        public byte[]? SampleEntry { get; init; }

        /// <summary>A copied track's whole-track timing — see <see cref="Retime"/>. Null for a conversion.</summary>
        public long[]? Presentation { get; set; }

        /// <inheritdoc cref="Presentation" />
        public long[]? Decode { get; set; }

        /// <inheritdoc cref="Presentation" />
        public long[]? Composition { get; set; }

        /// <inheritdoc cref="Presentation" />
        public long[]? Durations { get; set; }

        /// <summary>How many output frames a CONVERTED channel has produced — the audio timeline's whole input.</summary>
        public long Emitted { get; set; }

        /// <summary>Where this run began in the track's samples, so the end-of-run line can say how many were
        /// actually READ — which a seek landing past the end makes zero.</summary>
        public int Start { get; set; }

        /// <summary>
        /// Where this run begins on the OUTPUT timeline, in this channel's own timescale. Zero for every
        /// channel whose frames already carry absolute times (a copy, and a converted picture); non-zero only
        /// for converted SOUND, whose clock is a packet count — see <see cref="TimeOf"/>.
        /// </summary>
        public long StartTime { get; set; }

        public int Next { get; set; }
        public long LastTime { get; set; }
        public bool WarnedReordering { get; set; }
        public List<Pending> Pending { get; } = [];

        /// <summary>This channel's own time, in seconds — the only unit two channels may be compared in.</summary>
        public double SecondsOf(long time) => Timescale == 0 ? 0 : time / (double)Timescale;

        /// <inheritdoc cref="SecondsOf" />
        public long TicksAt(double seconds) => (long)Math.Round(seconds * Timescale);
    }
}

/// <summary>
/// One track a run will produce, and HOW it travels: copied verbatim, or through a codec. ⚠ Chosen once, by
/// the engine, and carried — the plan the manifest was built from depends on the same answer (a copied
/// picture cuts on the source's keyframes, a converted one on the grid), so a second decision can disagree.
/// </summary>
internal sealed record SegmentTrack(MatroskaTrack Track, bool Copy);
