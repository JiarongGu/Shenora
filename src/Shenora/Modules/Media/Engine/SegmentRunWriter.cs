namespace Shenora.Modules.Media;

/// <summary>
/// The pump behind <see cref="DefaultSegmentEngine"/>: read source frames, push them through the platform's
/// codecs, and cut the output into numbered fMP4 fragments on the grid.
///
/// <para>
/// Split out of the engine because it is the part with STATE — the open codec runs, the samples accumulated
/// so far, the segment being filled — while the engine itself is a handful of stateless answers. It is also
/// the part a fake <see cref="IMediaStreamConversion"/> can drive end to end, which is what makes the whole
/// loop testable on a machine with no codecs at all.
/// </para>
///
/// <para>
/// 🔴 <b>THE TWO KINDS ARE TIMED DIFFERENTLY, and this paragraph claimed the opposite until a device said
/// so.</b> It read "ONE timescale for both tracks: microseconds", on the reasoning that
/// <see cref="MediaFrame.PresentationTimeUs"/> is what a codec stamps. An AUDIO encoder stamps nothing —
/// <c>IosMediaAudioConversion</c> returns every frame as <c>new MediaFrame(bytes, 0)</c>, because it does
/// not have to: a packet is a fixed number of samples, so its timeline is arithmetic. Timing audio from
/// presentation-time gaps therefore gave every packet a 1 µs duration, and the iOS simulator reported
/// <c>appendInit=ok appendSeg0=ok buffered=0.00-0.00</c> for 234 KB of AAC — accepted, and worth nothing.
/// </para>
/// <para>
/// So a picture is timed in microseconds from what the encoder stamped, and a soundtrack on its own sample
/// rate from the packet count. <see cref="Mp4Remuxer"/> already drew that line and says so; this is the same
/// rule applied to fragments. See <c>TimeOf</c>.
/// </para>
/// </summary>
internal sealed class SegmentRunWriter(
    DefaultSegmentEngine owner, SegmentRunRequest request, long sourceTicksPerSecond) : IDisposable
{
    /// <summary>Microseconds — see the type remarks.</summary>
    private const uint MicrosecondTimescale = 1_000_000;

    private readonly List<IDisposable> _runs = [];
    private bool _initWritten;

    /// <summary>Read, convert and write until the source ends or the token fires.</summary>
    /// <param name="reader">Already past its header, with the sample index read.</param>
    /// <param name="source">The source file, seekable — sample bytes are read by offset.</param>
    /// <param name="video">The picture track, or null for a sound-only run.</param>
    /// <param name="audio">The sound track, or null.</param>
    /// <param name="from">Where to start reading the LEAD track — the seek target, already keyframe-aligned.</param>
    /// <param name="startTicks">The first segment's start on the source timeline.</param>
    /// <param name="conversionOf">The shell's codecs.</param>
    /// <param name="cancellationToken">Checked between frames; disposing the run fires it.</param>
    public void Run(MatroskaSampleReader reader, Stream source, MatroskaTrack? video, MatroskaTrack? audio,
                    int from, long startTicks, IMediaStreamConversion conversionOf,
                    CancellationToken cancellationToken)
    {
        var lead = video ?? audio!;
        var tracks = new List<Channel>();

        if (video is not null && Open(conversionOf, video, MediaStreamKind.Video) is { } v) tracks.Add(v);
        if (audio is not null && Open(conversionOf, audio, MediaStreamKind.Audio) is { } a) tracks.Add(a);
        if (tracks.Count == 0)
        {
            owner.Report("segments: no codec would open for this source");
            return;
        }

        var segment = request.FirstSegment;
        var frame = Array.Empty<byte>();

        foreach (var channel in tracks)
        {
            // Each track seeks independently: their keyframes are their own, and a sound track has one on
            // every frame. Using the picture's index for both would start the sound in the wrong place.
            channel.Next = ReferenceEquals(channel.Track, lead) ? from : SegmentGrid.SeekIndex(channel.Track.Samples, startTicks);
        }

        // Round-robin by source time, so both codecs are fed roughly in step and neither runs far ahead
        // buffering output nobody has cut yet.
        while (!cancellationToken.IsCancellationRequested)
        {
            var channel = tracks
                .Where(c => c.Next < c.Track.Samples.Count)
                .OrderBy(c => c.Track.Samples[c.Next].Ticks)
                .FirstOrDefault();
            if (channel is null) break;

            var sample = channel.Track.Samples[channel.Next++];
            if (frame.Length < sample.Length) frame = new byte[sample.Length];
            source.Position = sample.Offset;
            if (source.ReadAtLeast(frame.AsSpan(0, sample.Length), sample.Length, throwOnEndOfStream: false) != sample.Length)
            {
                owner.Report("segments: the source ended mid-frame — stopping rather than writing a torn sample");
                break;
            }

            // Zero outputs is NORMAL: codecs buffer, and a video encoder holds a GOP. Treating an empty
            // return as failure abandons a working conversion in its opening second.
            var micros = sample.Ticks * 1_000_000L / Math.Max(sourceTicksPerSecond, 1);
            Accept(channel, channel.Conversion.Push(new MediaFrame(frame.AsMemory(0, sample.Length), micros, sample.KeyFrame)));

            segment = CutIfDue(tracks, segment, cancellationToken);
        }

        // 🔴 Without the drain the tail sits inside the codec and the last segment is short, in a file that
        // is otherwise well-formed — playback simply stops early. It costs more for a picture than a
        // soundtrack, because the encoder's window is a GOP rather than a few packets.
        if (!cancellationToken.IsCancellationRequested)
        {
            foreach (var channel in tracks) Accept(channel, channel.Conversion.Drain());
            segment = CutIfDue(tracks, segment, cancellationToken);
            Flush(tracks, segment);                 // whatever is left is the final segment
        }
    }

    /// <summary>Begin one track's conversion, or null when this device declines it after all.</summary>
    private Channel? Open(IMediaStreamConversion conversion, MatroskaTrack track, MediaStreamKind kind)
    {
        var codec = MatroskaProbe.CodecNameOf(track.CodecId, track.CodecPrivate ?? ReadOnlyMemory<byte>.Empty);
        var info = new MediaStreamInfo(kind, codec ?? string.Empty)
        {
            Width = track.Width,
            Height = track.Height,
            // ⚠ The seam states a sample rate as an INTEGER while Matroska stores a double (a rate is
            // declared as a float there and 48000 arrives as 48000.0). Rounded rather than truncated: a
            // codec configured at 47999 Hz does not fail, it plays everything slightly flat.
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
        var isVideo = kind is MediaStreamKind.Video;
        var format = run.OutputFormat;
        return new Channel
        {
            Track = track,
            Conversion = run,
            IsVideo = isVideo,
            TrackId = isVideo ? DefaultSegmentEngine.VideoTrackId : DefaultSegmentEngine.AudioTrackId,
            // Video is timed in microseconds because that is what the encoder stamps; audio is timed on its
            // OWN sample rate because the encoder stamps nothing and the timeline is arithmetic. See TimeOf.
            Timescale = isVideo ? MicrosecondTimescale : (uint)Math.Max(format.SampleRate ?? 48_000, 1),
            SamplesPerPacket = isVideo ? 0 : Math.Max(run.OutputFramesPerPacket, 1),
        };
    }

    /// <summary>
    /// The time of the output frame about to be accepted, on the CHANNEL's own timeline.
    ///
    /// <para>
    /// 🔴 <b>THE TWO KINDS ARE TIMED DIFFERENTLY, AND USING ONE RULE FOR BOTH PRODUCED A STREAM THAT
    /// APPENDED CLEANLY AND BUFFERED NOTHING.</b> Measured on the iOS simulator 2026-08-14:
    /// <c>appendInit=ok appendSeg0=ok buffered=0.00-0.00</c> for 234 KB of AAC. The cause is that
    /// <c>IosMediaAudioConversion</c> emits every frame as <c>new MediaFrame(bytes, 0)</c> — an audio
    /// encoder does not time its output, because it does not have to: a packet is a FIXED number of samples,
    /// so the timeline is arithmetic. Deriving durations from presentation-time gaps therefore gave every
    /// packet a 1 µs duration and the whole segment a length of half a millisecond.
    /// </para>
    /// <para>
    /// <see cref="Mp4Remuxer"/> already had this right and says so in its own comment — "the ONE place the
    /// kinds differ … Audio frames are a fixed number of samples each, so the timeline is arithmetic".
    /// This is that rule, applied to fragments. ⚠ A second implementation of one calculation is how the two
    /// come to disagree, and this one disagreed for exactly as long as it took a device to say so.
    /// </para>
    /// </summary>
    private static long TimeOf(Channel channel) => channel.IsVideo
        // Microseconds, matching what the encoder stamped 1:1.
        ? channel.LastTime
        // Packet index × frames-per-packet, on the track's own sample-rate timeline.
        : channel.Emitted * channel.SamplesPerPacket;

    /// <summary>
    /// Take a codec's outputs into the segment being filled.
    /// <para>
    /// ⚠ <b>Reordered output is REFUSED rather than reordered here.</b> A fragment states sample durations as
    /// the gap to the next sample, so a presentation time that goes backwards would produce a negative
    /// duration — which is unrepresentable, and rounding it to zero writes a segment that plays frames on top
    /// of each other. Both platform encoders are configured not to reorder (iOS sets
    /// <c>AllowFrameReordering</c> false; Android's one-second GOP at these settings does not), so this is a
    /// fail-closed guard on an assumption rather than a case to handle. <see cref="Mp4FragmentWriter"/>
    /// already carries signed composition offsets for whenever a reordering encoder has to be supported.
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
            channel.Pending.Add(new Pending(TimeOf(channel), output.IsKeyframe, output.Data.ToArray()));
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
            if (!SegmentGrid.StartsNewSegment(at.TimeUs, at.KeyFrame, segment, lead.Timescale, request.SegmentSeconds)) continue;
            if (cancellationToken.IsCancellationRequested) break;

            Flush(tracks, segment, upTo: at.TimeUs);
            segment = SegmentGrid.SegmentOf(at.TimeUs, lead.Timescale, request.SegmentSeconds);
            i = 0;                                   // the list shifted under us
        }
        return segment;
    }

    /// <summary>
    /// Write one fragment: every track's pending samples strictly before <paramref name="upTo"/>, or all of
    /// them when it is null (the final segment).
    /// </summary>
    private void Flush(List<Channel> tracks, int segment, long? upTo = null)
    {
        var data = new List<Mp4FragmentTrackData>();

        foreach (var channel in tracks)
        {
            var take = upTo is null
                ? channel.Pending.Count
                : channel.Pending.FindIndex(p => p.TimeUs >= upTo.Value) is var found and >= 0 ? found : channel.Pending.Count;
            if (take <= 0) continue;

            var samples = new List<Mp4FragmentSample>(take);
            var bytes = new List<byte>();
            for (var i = 0; i < take; i++)
            {
                var current = channel.Pending[i];
                // A sample's duration is the gap to the next one. The LAST in a fragment has no next, so it
                // borrows the previous gap — the same approximation `SampleTiming.Durations` makes, and the
                // alternative (waiting for one more frame) would hold every segment hostage to the next.
                // Audio is a FIXED number of samples per packet, so its duration is arithmetic and exact
                // — see TimeOf. Only a picture's duration has to be measured as the gap to the next frame.
                var next = i + 1 < channel.Pending.Count ? channel.Pending[i + 1].TimeUs : (long?)null;
                var duration = !channel.IsVideo ? channel.SamplesPerPacket
                             : next is not null ? next.Value - current.TimeUs
                             : samples.Count > 0 ? samples[^1].Duration
                             : DefaultDuration(channel);
                samples.Add(new Mp4FragmentSample(Math.Max(duration, 1), current.Data.Length, 0, current.KeyFrame));
                bytes.AddRange(current.Data);
            }

            data.Add(new Mp4FragmentTrackData
            {
                Track = Declare(channel),
                BaseMediaDecodeTime = channel.Pending[0].TimeUs,
                Samples = samples,
                Data = bytes.ToArray(),
            });
            channel.Pending.RemoveRange(0, take);
        }

        if (data.Count == 0) return;

        // The init segment carries the decoder configuration, and that is only knowable once the encoder has
        // produced output — so it is written HERE, beside the first fragment, rather than ahead of the run.
        // A consumer therefore waits for it exactly as it waits for seg0.
        if (!_initWritten)
        {
            WriteInit(data.Select(d => d.Track).ToList());
            _initWritten = true;
        }

        var path = Path.Combine(request.Directory, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"seg{segment}{SegmentRunRequest.SegmentExtension}"));
        using var file = File.Create(path);
        // Sequence numbers are 1-based and strictly increasing; the segment index is 0-based.
        Mp4FragmentWriter.WriteFragment(file, segment + 1, data);
    }

    private void WriteInit(IReadOnlyList<Mp4FragmentTrack> tracks)
    {
        var path = Path.Combine(request.Directory, SegmentRunRequest.InitSegmentName);
        using var file = File.Create(path);
        Mp4FragmentWriter.WriteInitSegment(file, tracks);
    }

    /// <summary>
    /// The track as the init segment must declare it — built from the RUN's output format rather than the
    /// source's, because a decoder may downmix and an encoder may align dimensions up to a macroblock.
    /// </summary>
    private static Mp4FragmentTrack Declare(Channel channel)
    {
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

    /// <summary>A single-sample fragment's duration, when there is no gap to measure.</summary>
    private static long DefaultDuration(Channel channel)
    {
        if (channel.IsVideo) return 1_000_000 / 30;              // one frame at a conventional rate
        var rate = channel.Conversion.OutputFormat.SampleRate ?? 48_000;
        var perPacket = Math.Max(channel.Conversion.OutputFramesPerPacket, 1);
        return (long)(perPacket * 1_000_000 / Math.Max(rate, 1));
    }

    public void Dispose()
    {
        // Every platform hands out a hardware codec and a device has a handful — a video run holds TWO.
        // Leaking one does not leak memory, it makes the NEXT conversion fail with a resource error that
        // names nothing.
        foreach (var run in _runs)
        {
            try { run.Dispose(); }
            catch (Exception ex) { owner.Report($"segments: a codec would not release ({ex.GetType().Name})"); }
        }
        _runs.Clear();
    }

    private sealed record Pending(long TimeUs, bool KeyFrame, byte[] Data);

    private sealed class Channel
    {
        public required MatroskaTrack Track { get; init; }
        public required IMediaStreamConversionRun Conversion { get; init; }
        public required bool IsVideo { get; init; }
        public required int TrackId { get; init; }

        /// <summary>Ticks per second on THIS track's timeline — see <see cref="TimeOf"/>.</summary>
        public required uint Timescale { get; init; }

        /// <summary>Frames per output packet; 0 for a picture, whose frames are timed individually.</summary>
        public required int SamplesPerPacket { get; init; }

        /// <summary>How many output frames this channel has produced — the audio timeline's whole input.</summary>
        public long Emitted { get; set; }

        public int Next { get; set; }
        public long LastTime { get; set; }
        public bool WarnedReordering { get; set; }
        public List<Pending> Pending { get; } = [];
    }
}
