namespace Shenora.Modules.Media;

/// <summary>
/// The pump behind <see cref="DefaultSegmentEngine"/>: take source frames, COPY the ones MP4 can carry and
/// push the rest through the platform's codecs (D76), and cut the output into numbered fMP4 fragments on the
/// plan. Split out of the engine because it is the part with STATE, and the part a fake
/// <see cref="IMediaStreamConversion"/> can drive end to end on a machine with no codecs at all.
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

    /// <summary>
    /// How many BYTES the lead track may hold without finding a cut before one is FORCED.
    /// <para>
    /// Bytes, not samples: what runs a phone out of memory is the payload, and a 4K frame is twenty times
    /// a 480p one, so a sample count bounds the wrong quantity. Generous on purpose — an ordinary long GOP
    /// must never trip it, so this is an out-of-memory guard rather than a segmentation policy.
    /// </para>
    /// </summary>
    /// <remarks>
    /// ⚠ A FIELD rather than a const so a test can lower it — the same reason <c>IFileOperations</c> is an
    /// internal seam. Proving this guard at the real 64 MB would mean allocating ~150 MB inside a unit
    /// test; proving it at 64 KB proves the same branch. Not an app knob: it is internal and undocumented
    /// outside this file.
    /// </remarks>
    internal static long MaxPendingBytes = 64L * 1024 * 1024;

    /// <summary>Said once per run: a forced cut repeats every segment afterwards and reads as a storm.</summary>
    private bool _reportedUncutCap;

    /// <summary>Read, copy or convert, and write until the source ends or the token fires.</summary>
    /// <param name="source">The source file, seekable — sample bytes are read by offset.</param>
    /// <param name="video">The picture track and how it travels, or null for a sound-only run.</param>
    /// <param name="audio">The sound track and how it travels, or null.</param>
    /// <param name="from">Where to start reading the LEAD track — the seek target, already keyframe-aligned.</param>
    /// <param name="startSeconds">Where the first segment begins on the media timeline.</param>
    /// <param name="conversionOf">The shell's codecs, or null when this shell registered none.</param>
    /// <param name="cancellationToken">Checked between frames; disposing the run fires it.</param>
    /// <param name="extend">
    /// Index more of the source, up to the given time in SECONDS; false when there is no more.
    /// 🔴 <b>Called BEFORE the pump consumes its last known sample, not after it runs out</b>: a copied
    /// sample's duration is the gap to its SUCCESSOR, so a frame taken while it is the last one known gets
    /// the track's declared duration instead of its real gap.
    /// </param>
    public void Run(Stream source, SegmentTrack? video, SegmentTrack? audio,
                    int from, double startSeconds, IMediaStreamConversion? conversionOf,
                    Func<double, bool> extend,
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
            // Each track seeks independently: using the picture's index for both starts the sound in the
            // wrong place.
            channel.Next = ReferenceEquals(channel.Track, lead)
                ? from
                : SegmentGrid.SeekIndex(channel.Track.Samples, startTicks);
            channel.Start = channel.Next;
            // Only a converted soundtrack needs an origin — see TimeOf.
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

            // 🔴 THE CHANNEL ABOUT TO BE TAKEN FROM is within one sample of its known end: index more
            // before taking, so the frame about to be written still has a successor to be timed against.
            // See `extend`.
            // ⚠ This asked `tracks.All(…)`, which is a different question and the wrong one. Two tracks
            // rarely run out together — a soundtrack has many more frames than a picture track — so the
            // FIRST to run out was consumed while the other still had plenty, `All` stayed false, no
            // extension happened, and that frame took the track's DECLARED duration instead of its real
            // gap. The comment above already described the per-frame rationale; the condition did not
            // implement it.
            if (channel is not null && channel.Next + 1 >= channel.Track.Samples.Count)
            {
                var reach = channel is not null
                    ? channel.Track.Samples[^1].Ticks * timeline.Factor / (double)timeline.Timescale
                    : startSeconds;
                if (extend(reach))
                {
                    foreach (var each in tracks.Where(c => c.Conversion is null)) Retime(each);
                    continue;
                }
            }

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
                Take(channel, index, frame.AsSpan(0, sample.Length).ToArray());
            }
            else
            {
                // ⚠ Zero outputs is NORMAL: codecs buffer, and a video encoder holds a GOP. Treating an empty
                // return as failure abandons a working conversion in its opening second.
                var micros = (long)Math.Round(timeline.SecondsOf(sample.Ticks) * 1_000_000);
                Accept(channel, channel.Conversion.Push(new MediaFrame(frame.AsMemory(0, sample.Length), micros, sample.KeyFrame)));
            }

            segment = CutIfDue(tracks, segment, cancellationToken);
        }

        // 🔴 Without the drain the tail sits inside the codec and the last segment is short, in a file that is
        // otherwise well-formed — playback simply stops early. A copied channel has no tail.
        if (!cancellationToken.IsCancellationRequested)
        {
            foreach (var channel in tracks.Where(c => c.Conversion is not null))
            {
                Accept(channel, channel.Conversion!.Drain());
            }
            segment = CutIfDue(tracks, segment, cancellationToken);
            Flush(tracks, segment);                 // whatever is left is the final segment
        }

        // 🔴 What each track actually CONTRIBUTED. A short track is silent by construction — the fragments
        // are well-formed and every append succeeds — so `of`, `from` and `emitted` are the only way to tell
        // a missing sample from a late seek.
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
            // Asked again rather than trusted: the selection that chose this track is far enough from here
            // to disagree about a malformed file.
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
    /// Build a COPIED track's decode timeline once, for the whole track, through
    /// <see cref="SampleTiming.Derive"/> — the same derivation <see cref="Mp4Remuxer"/> uses, so a remux and
    /// a fragment run cannot disagree about one file's timing.
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
        var from = channel.Retimed;
        if (from >= samples.Count) return;

        if (from == 0)
        {
            // The track's declared frame duration on this timeline — what the LAST sample falls back to,
            // having no next frame to measure against.
            channel.Step = channel.Track.DefaultDurationNs > 0 && timeline.Timescale > 0
                ? channel.Track.DefaultDurationNs * timeline.Timescale / 1_000_000_000L
                : 0;
        }

        var count = samples.Count - from;
        var presentation = new long[count];
        for (var i = 0; i < count; i++) presentation[i] = samples[from + i].Ticks * timeline.Factor;

        // 🔴 SPREAD THE TIES FIRST, exactly as `Mp4Remuxer` does before its own `Derive`. Lacing packs
        // several audio frames into one Matroska block and they arrive sharing a timestamp; tied times
        // become zero-length `stts` entries, so a soundtrack whose frames all claim to last no time plays
        // as a fraction of a second of noise while every box in the file still validates. This path had
        // the same producer and skipped the same call — a no-op on a picture track, where ties cannot arise.
        presentation = SampleTiming.SpreadTies(presentation, channel.Step);

        var (decode, _, chunkShift) = SampleTiming.Derive(presentation);

        // 🔴 The shift belongs to the RUN, not to the chunk. See Channel.Shift.
        if (!channel.Shifted)
        {
            channel.Shift = chunkShift;
            channel.Shifted = true;
        }

        // ⚠ THE SEAM. Sorting inside a chunk matches sorting the whole track only while reordering stays
        // INSIDE the chunk. Checked rather than assumed: a chunk whose first frame decodes before the
        // previous chunk's last puts the fragments out of order, which appends without error and plays wrongly.
        if (channel.Decode.Count > 0 && decode[0] < channel.Decode[^1] && !channel.WarnedSeam)
        {
            channel.WarnedSeam = true;
            owner.Report($"segments: the {(channel.IsVideo ? "picture" : "sound")} reorders across an index "
                       + "boundary, which this run indexes in chunks — timing is clamped at the seam");
        }

        for (var i = 0; i < count; i++)
        {
            var at = decode[i];
            // Monotonic by construction: a decode time may never go backwards past what is already written.
            if (channel.Decode.Count > 0 && at < channel.Decode[^1]) at = channel.Decode[^1];

            var composition = presentation[i] + channel.Shift - at;
            // The run's shift was taken from the first chunk; a later frame needing more would give a
            // NEGATIVE offset, which asks a player to show a frame it has not decoded.
            if (composition < 0) composition = 0;

            channel.Presentation.Add(presentation[i]);
            channel.Decode.Add(at);
            channel.Composition.Add(composition);
            channel.Durations.Add(0);              // filled below, once the successor is known
        }

        // Durations are the gap to the NEXT frame, so the previous chunk's last entry is only knowable now.
        for (var i = Math.Max(from - 1, 0); i < channel.Decode.Count - 1; i++)
        {
            channel.Durations[i] = Math.Max(0, channel.Decode[i + 1] - channel.Decode[i]);
        }

        // The final entry has no successor yet, and a fallback beats a zero, which reads as a truncated file.
        var last = channel.Decode.Count - 1;
        channel.Durations[last] = channel.Step > 0 ? channel.Step
            : last > 0 ? channel.Durations[last - 1]
            : 1;
        if (channel.Durations[last] <= 0) channel.Durations[last] = 1;

        channel.Retimed = samples.Count;
    }

    /// <summary>Take one COPIED sample, with the timing derived for the whole track.</summary>
    private static void Take(Channel channel, int index, byte[] data)
    {
        channel.Emitted++;      // the end-of-run line only; a copy's TIMES never come from it
        channel.Pending.Add(new Pending(
            Decode: channel.Decode[index],
            Presentation: channel.Presentation[index],
            Duration: channel.Durations[index],
            Composition: channel.Composition[index],
            KeyFrame: channel.Track.Samples[index].KeyFrame,
            Data: data));
        channel.PendingBytes += data.Length;
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
    /// ⚠ <b>Reordered output is REFUSED rather than reordered here</b>, and the frame is DROPPED — segments
    /// come out short rather than wrong. A converted channel states sample durations as the gap to the next
    /// sample, so a backwards presentation time gives a negative duration, and rounding it to zero writes a
    /// segment that plays frames on top of each other. A COPIED channel needs none of it,
    /// <see cref="Retime"/> expressing its reordering exactly.
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
            var converted = output.Data.ToArray();
            channel.Pending.Add(new Pending(at, at, Duration: 0, Composition: 0, output.IsKeyframe, converted));
            channel.PendingBytes += converted.Length;
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

        // 🔴 A LEAD TRACK THAT NEVER REACHES A KEYFRAME WOULD OTHERWISE BUFFER THE WHOLE SOURCE. The cut
        // above needs a keyframe PAST the segment end; a long-GOP or damaged stream may not offer one for
        // minutes, and `Pending` holds every sample's BYTES until it does — whole-source memory, then
        // doubled by the final flush. Measured shape: one 600-frame run produced a single segment.
        // ⚠ Cutting here lands on a non-keyframe, so that segment cannot be decoded from cold. That is a
        // real cost and it is still the better one: the alternative is one segment the size of the film,
        // which cannot be seeked into either AND runs a phone out of memory. Said out loud, once.
        if (lead.PendingBytes > MaxPendingBytes)
        {
            var at = lead.Pending[^1];
            var seconds = lead.SecondsOf(at.Presentation);
            if (!_reportedUncutCap)
            {
                _reportedUncutCap = true;
                owner.Report($"segments: the lead track has held {lead.PendingBytes / (1024 * 1024)} MB without "
                           + "reaching a keyframe past the segment end, so this segment is being cut on a "
                           + "non-keyframe to bound memory — seeking INTO it may not work");
            }
            Flush(tracks, segment, upTo: seconds);
            segment = Math.Max(segment, request.Plan.IndexOf(seconds));
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
                // A COPIED sample already knows its duration. A CONVERTED one is the gap to the next frame,
                // and the LAST in a fragment borrows the previous gap rather than holding the segment hostage
                // to a frame that has not arrived; converted audio is a fixed number of samples per packet.
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
            channel.PendingBytes -= bytes.Count;
            channel.Pending.RemoveRange(0, take);
        }

        if (data.Count == 0) return;

        // The init segment's decoder configuration is knowable only once an encoder has produced output, so
        // it is written HERE, beside the first fragment — see SegmentRunRequest.Directory.
        if (!_initWritten)
        {
            // 🔴 EVERY OPENED CHANNEL, not just the ones with samples in THIS fragment. The init segment is
            // the only place a track id is defined, so a track that had produced nothing by the first flush
            // used to go undeclared — and the loop below then dropped its samples for the WHOLE RUN. A
            // copied track produces from its first frame while an encoder may hold a whole segment, and a
            // soundtrack that simply starts a few seconds in does it without any encoder at all. Nothing
            // downstream notices, because `VerifyPicture` only looks for picture: a film silent end to end.
            // The channel list is known before any sample is read, so declaring from it is always possible.
            WriteInit([.. tracks.Select(Declare)]);
            _declared = [.. tracks.Select(c => c.TrackId)];
            _initWritten = true;
        }

        // 🔴 A fragment may only carry a track the init segment DECLARED — the `moov`/`trex` pair there is the
        // only place a track id is defined, and they diverge because a COPIED track produces from its first
        // frame while an encoder may hold a whole segment's worth. Dropping the late samples costs that track
        // its opening seconds; writing them puts an undeclared id in the stream, which a MediaSource may
        // reject SILENTLY.
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
    /// whole (<see cref="SegmentRunRequest.Directory"/> states the contract). ⚠ A failed write leaves the
    /// <c>.part</c> rather than a corrupt final name; the route sweeps those when it next opens the source.
    /// </summary>
    private static void Publish(string path, Action<Stream> write)
    {
        var partial = path + SegmentRunRequest.PartialExtension;
        using (var file = File.Create(partial)) write(file);
        File.Move(partial, path, overwrite: true);
    }

    /// <summary>
    /// The track as the init segment must declare it. ⚠ A COPIED track is declared from the SOURCE, whose
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
        // 🔴 A device has a handful of hardware codecs and a video run holds TWO. Leaking one makes the NEXT
        // conversion fail with a resource error that names nothing.
        foreach (var run in _runs)
        {
            try { run.Dispose(); }
            catch (Exception ex) { owner.Report($"segments: a codec would not release ({ex.GetType().Name})"); }
        }
        _runs.Clear();
    }

    /// <summary>One sample waiting to be written, on its own channel's timescale.</summary>
    /// <param name="Decode">When it is decoded — what <c>tfdt</c> and a duration are measured on.</param>
    /// <param name="Presentation">When it is SHOWN — what a cut is decided against.</param>
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

        /// <summary>Frames per output packet; 0 for a picture and for any copy.</summary>
        public required int SamplesPerPacket { get; init; }

        /// <summary>A copied track's <c>stsd</c> entry, taken from the source. Null for a conversion.</summary>
        public byte[]? SampleEntry { get; init; }

        /// <summary>
        /// A copied track's derived timing, GROWING as the source is indexed — see <see cref="Retime"/>.
        /// Empty for a conversion, whose frames carry their own times.
        /// </summary>
        public List<long> Presentation { get; } = [];

        /// <inheritdoc cref="Presentation" />
        public List<long> Decode { get; } = [];

        /// <inheritdoc cref="Presentation" />
        public List<long> Composition { get; } = [];

        /// <inheritdoc cref="Presentation" />
        public List<long> Durations { get; } = [];

        /// <summary>How many of the track's samples have been given a decode time.</summary>
        public int Retimed { get; set; }

        /// <summary>The track's declared frame duration on this timeline; the last sample's fallback.</summary>
        public long Step { get; set; }

        /// <summary>
        /// The presentation shift that makes every frame decodable before it is shown.
        /// 🔴 <b>Taken from the FIRST chunk and never changed</b> — a shift that moved between chunks would
        /// put neighbouring fragments on different timelines.
        /// </summary>
        public long Shift { get; set; }

        /// <inheritdoc cref="Shift" />
        public bool Shifted { get; set; }

        /// <summary>Reported once per channel, so a pathological stream is one line rather than thousands.</summary>
        public bool WarnedSeam { get; set; }

        /// <summary>How many output frames a CONVERTED channel has produced — the audio timeline's whole input.</summary>
        public long Emitted { get; set; }

        /// <summary>Where this run began in the track's samples, for the end-of-run line.</summary>
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

        /// <summary>
        /// Bytes currently held in <see cref="Pending"/>, maintained INCREMENTALLY.
        /// <para>
        /// ⚠ Summing the list instead would be O(n) inside a per-sample loop, i.e. O(n²) over a run — a
        /// memory guard that costs quadratic time is not a fix.
        /// </para>
        /// </summary>
        public long PendingBytes { get; set; }

        /// <summary>This channel's own time, in seconds — the only unit two channels may be compared in.</summary>
        public double SecondsOf(long time) => Timescale == 0 ? 0 : time / (double)Timescale;

        /// <inheritdoc cref="SecondsOf" />
        public long TicksAt(double seconds) => (long)Math.Round(seconds * Timescale);
    }
}

/// <summary>
/// One track a run will produce, and HOW it travels: copied verbatim, or through a codec. ⚠ Chosen once, by
/// the engine, and carried — the plan the manifest was built from depends on the same answer, so a second
/// decision can disagree with it.
/// </summary>
internal sealed record SegmentTrack(MatroskaTrack Track, bool Copy);
