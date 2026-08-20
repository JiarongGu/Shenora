using Microsoft.Extensions.Logging;

namespace Shenora.Modules.Media;

/// <summary>
/// The kit's own <see cref="ISegmentEngine"/> — the platform's codecs behind
/// <see cref="IMediaStreamConversion"/>, plus <see cref="Mp4FragmentWriter"/>, and nothing else. It ships no
/// engine bytes and inherits no licence (D42/D51). Segments are the TRANSCODE path, and which route a source
/// takes is the APP's decision (D71/D72).
/// <para>
/// 🔴 <b>IT COPIES EVERY STREAM MP4 CAN CARRY AND RE-ENCODES ONLY WHAT IT CANNOT</b> (D76). The platform
/// video encoders offer h263/mpeg4/mpeg2video, none of which a webview decodes, so re-encoding everything
/// leaves an EMPTY intersection with what the page can play and returns sound-only segments for essentially
/// every real film.
/// </para>
/// <para>
/// ⚠ <see cref="IsAvailable"/> is a REGISTRATION test, not a platform one: it is false on the desktop only
/// because <c>Shenora.Windows</c> ships no converter.
/// </para>
/// </summary>
internal sealed class DefaultSegmentEngine : ISegmentEngine
{
    // Explicit fields, not primary-constructor captures: the nested run class cannot reach a captured parameter.
    private readonly IMediaStreamConversion? _conversion;
    private readonly ILogger? _log;

    /// <param name="conversion">
    /// The shell's codecs. Null means <see cref="IsAvailable"/> is false and nothing else here is ever called.
    /// </param>
    /// <param name="log">Optional diagnostics. Guarded: a throwing sink must not kill a production run.</param>
    public DefaultSegmentEngine(IMediaStreamConversion? conversion, ILogger? log = null)
    {
        _conversion = conversion;
        _log = log;
    }

    /// <summary>
    /// The track numbers this engine writes. Fixed because <see cref="HasRenderedPicture"/> is handed a PATH
    /// and must know which track to measure.
    /// </summary>
    internal const int VideoTrackId = 1;
    internal const int AudioTrackId = 2;

    /// <summary>
    /// The longest segment this engine will COPY a track into, in seconds; past it the track is re-encoded.
    /// ⚠ <b>A memory bound, not a media one</b>: a fragment's bytes are held in one buffer, so a source whose
    /// keyframes are a minute apart would put hundreds of megabytes into one fragment on a phone.
    /// </summary>
    internal const double MaxCopiedSegmentSeconds = 30.0;

    /// <inheritdoc />
    public bool IsAvailable => _conversion is not null;

    /// <inheritdoc />
    public string Describe() => _conversion is null
        ? "no segment engine: this shell registered no IMediaStreamConversion"
        : $"the kit's default segment engine (fMP4 fragments over {_conversion.GetType().Name})";

    /// <inheritdoc />
    public TimeSpan? DurationOf(MediaByteSource source) => Probe(source)?.Duration;

    /// <inheritdoc />
    /// <remarks>
    /// 🔴 <b>The dimensions are read off the RESULT, not off the stream</b> — <c>MatroskaProbe</c> fills
    /// <see cref="MediaProbeResult.Width"/>/<see cref="MediaProbeResult.Height"/> and leaves
    /// <see cref="MediaStreamInfo.Width"/> null on every stream it reports, so asking the stream answers
    /// FALSE for every source alive and the engine builds no video encoder, producing sound-only segments
    /// that play perfectly. A test pins it: the wrong version compiles and looks right.
    /// </remarks>
    public bool HasPicture(MediaByteSource source)
    {
        var probe = Probe(source);
        return probe is not null
            && probe.Streams.Any(s => s.Kind is MediaStreamKind.Video)
            && probe.Width is > 0 && probe.Height is > 0;
    }

    /// <inheritdoc />
    /// <remarks>Answered by SUBTRACTION, not by structure — <see cref="Mp4FragmentReader"/> says why.</remarks>
    public bool HasRenderedPicture(string segment) => Mp4FragmentReader.SampleBytes(segment, VideoTrackId) > 0;

    /// <inheritdoc />
    /// <remarks>
    /// Answers null — "I will hit your grid" — for every source whose PICTURE this run would re-encode, and a
    /// derived plan only for one it will COPY (D76).
    /// </remarks>
    public SegmentPlan? PlanSegments(MediaByteSource source, SegmentLengths lengths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(lengths);
        if (_conversion is null || lengths.Seconds <= 0) return null;
        if (Probe(source, cancellationToken)?.Duration is not { } duration || duration <= TimeSpan.Zero) return null;

        try
        {
            using var file = source.Open(cancellationToken);
            if (!IsIndexable(file, source)) return null;
            var reader = new MatroskaSampleReader(file);
            if (!reader.ReadHeader()) return null;

            // Only the LEAD track decides the boundaries, and only when it is copied. A soundtrack has a
            // keyframe on every frame, so any boundary suits it.
            if (Pick(reader, MediaStreamKind.Video) is not { Copy: true } lead) return null;

            var timeline = SourceTimeline.For(reader.TimestampScaleNs);

            // 🔴 THE FILE'S OWN INDEX FIRST; the walk below is the expensive fallback. ⚠ Null is ORDINARY —
            // Cues are optional — and the boundaries are identical either way.
            var keyFrames = reader.KeyFrameTicksFromCues(lead.Track.Number, cancellationToken);
            if (keyFrames is null)
            {
                Report($"segments: '{source.Label}' has no usable keyframe index — walking its clusters");
                // ⚠ The long walk, inside a web request — which is why the token reaches it.
                if (!reader.ReadSamples(new HashSet<ulong> { lead.Track.Number }, cancellationToken)) return null;
                keyFrames = [.. lead.Track.Samples.Where(s => s.KeyFrame).Select(s => s.Ticks)];
            }

            var starts = SegmentGrid.KeyFrameStarts(keyFrames, timeline, lengths);
            if (SegmentPlan.Cuts(starts, duration) is not { } plan)
            {
                Report("segments: the source's keyframes do not describe a playlist — falling back to the grid");
                return null;
            }

            // 🔴 A fragment is held whole in memory, so keyframes minutes apart cannot be COPIED. Declining
            // sends the run back to the grid and a re-encode.
            if (plan.LongestSeconds > MaxCopiedSegmentSeconds)
            {
                Report($"segments: the source's keyframes are up to {plan.LongestSeconds:0.#}s apart, past the "
                     + $"{MaxCopiedSegmentSeconds:0}s a copied fragment may be — re-encoding instead");
                return null;
            }

            Report($"segments: {plan}");
            return plan;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A source that cannot be planned is one the grid still serves; the contract's answer is null.
            Report($"segments: could not plan '{source.Label}' ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>
    /// Can this stream be INDEXED — seekable, and able to say how long it is? ⚠ Refused BY NAME: Matroska is
    /// read by offset, so a forward-only stream otherwise reads as a malformed container and every
    /// diagnostic downstream says "not readable Matroska" about a file that is perfectly fine.
    /// </summary>
    private bool IsIndexable(Stream stream, MediaByteSource source)
    {
        if (stream is { CanSeek: true, CanRead: true }) return true;
        Report($"segments: '{source.Label}' opened a stream that cannot seek, and Matroska is read by offset "
             + "— a ranged source needs a seekable adapter over its transport");
        return false;
    }

    /// <inheritdoc />
    public ISegmentRun? Start(SegmentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        if (_conversion is null) return null;

        // ONE candidate: this engine has no second encoder path, so null tells the caller to stop asking.
        if (request.Attempt > 0)
        {
            Report($"segments: no second candidate — this engine has one encoder path (attempt {request.Attempt})");
            return null;
        }

        // ⚠ Only a GRID can be unhittable — boundaries taken from the source's own keyframes are real by
        // construction (SegmentGrid).
        if (request.Plan.GridSeconds is { } grid && !SegmentGrid.IsUsable(grid, out var reason))
        {
            Report($"segments: {reason}");
            return null;
        }

        var run = new Run(this, request);
        run.Begin();
        return run;
    }

    /// <summary>
    /// The first track of a kind this run can produce, and HOW it will travel — copy first, convert only what
    /// cannot be copied (D76). The fallback is asked of the CONVERSION rather than of the container, so a
    /// codec the encoder declines is reported instead of being fed for nothing.
    /// </summary>
    /// <param name="reader">Past its header, so the tracks are known.</param>
    /// <param name="kind">Picture or sound.</param>
    /// <param name="allowCopy">
    /// 🔴 <b>False when the run must hit a GRID, because a copied track can only be cut where the SOURCE has
    /// a keyframe.</b> A copy that cannot land on the plan's cuts produces one enormous segment while the
    /// manifest goes on naming the rest — a stream that plays for a few seconds and then 503s for ever.
    /// </param>
    private SegmentTrack? Pick(MatroskaSampleReader reader, MediaStreamKind kind, bool allowCopy = true)
    {
        foreach (var track in reader.Tracks)
        {
            if (!allowCopy) break;
            if (track.Kind != kind) continue;
            if (Mp4Carriage.EntryFor(track) is not null) return new SegmentTrack(track, Copy: true);
        }

        foreach (var track in reader.Tracks)
        {
            if (track.Kind != kind) continue;
            var codec = MatroskaProbe.CodecNameOf(track.CodecId, track.CodecPrivate ?? ReadOnlyMemory<byte>.Empty);
            if (codec is null) continue;
            if (_conversion!.CanConvert(kind, codec)) return new SegmentTrack(track, Copy: false);
            Report($"segments: no {kind} converter for '{codec}' on this device, and MP4 cannot carry it as it is");
        }

        return null;
    }

    /// <summary>
    /// The source's header, or null when it cannot be read. ⚠ Opens its OWN stream and closes it — holding
    /// one open across a whole run would pin a ranged transport's connection for the duration.
    /// </summary>
    private MediaProbeResult? Probe(MediaByteSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            using var stream = source.Open(cancellationToken);
            return IsIndexable(stream, source) ? MatroskaProbe.Read(stream) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The contract's answer for both DurationOf and HasPicture is the absent one, never a throw.
            Report($"segments: could not probe '{source.Label}' ({ex.GetType().Name})");
            return null;
        }
    }

    internal void Report(string message) => AppCallback.Log(_log, () => $"[Shenora.Modules.Media] {message}");

    /// <summary>
    /// One production run: a background pump that writes numbered fragments until it reaches the end of the
    /// source or is disposed. 🔴 <b>Disposing must KILL it</b> — a producer that outlives its consumer holds
    /// a hardware codec, of which a device has a handful, plus a file handle and a CPU, invisibly.
    /// </summary>
    private sealed class Run(DefaultSegmentEngine owner, SegmentRunRequest request) : ISegmentRun
    {
        private readonly CancellationTokenSource _stopping = new();
        private Task? _pump;

        /// <inheritdoc />
        public bool HasExited => _pump is null or { IsCompleted: true };

        public void Begin() => _pump = Task.Run(() => Pump(_stopping.Token), CancellationToken.None);

        private void Pump(CancellationToken cancellationToken)
        {
            try
            {
                Produce(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Disposed — the caller asked for this.
            }
            catch (Exception ex)
            {
                // 🔴 Started with Task.Run and never awaited, so an escaping exception is an unobserved fault
                // whose only symptom is segments that stop appearing. This line is the cause; the consumer
                // reports only the effect ("seg{k} did not arrive").
                owner.Report($"segments: the production run failed ({ex.GetType().Name}: {ex.Message})");
            }
        }

        private void Produce(CancellationToken cancellationToken)
        {
            using var source = request.Source.Open(cancellationToken);
            if (!owner.IsIndexable(source, request.Source)) return;

            var reader = new MatroskaSampleReader(source);
            if (!reader.ReadHeader())
            {
                owner.Report($"segments: '{request.Source.Label}' is not readable Matroska");
                return;
            }

            // 🔴 The PLAN says whether a copy is legal, and getting this wrong is silent: on any other origin
            // the picture is re-encoded even where it could have been copied, because a copy slips every cut
            // to the first source keyframe past the boundary and nothing reports it.
            // ⚠ Sound is exempt: every audio frame is a sync sample, so any boundary suits it.
            var copyable = request.Plan.Origin is SegmentBoundaries.SourceKeyFrames;
            var video = request.HasPicture
                ? owner.Pick(reader, MediaStreamKind.Video, allowCopy: copyable)
                : null;
            var audio = owner.Pick(reader, MediaStreamKind.Audio);
            if (video is null && audio is null)
            {
                owner.Report("segments: the source carries nothing this engine can copy or convert");
                return;
            }

            var wanted = new HashSet<ulong>();
            if (video is not null) wanted.Add(video.Track.Number);
            if (audio is not null) wanted.Add(audio.Track.Number);
            var timeline = SourceTimeline.For(reader.TimestampScaleNs);
            var startSeconds = request.Plan.StartOf(request.FirstSegment);

            // INDEX ONLY WHAT THIS RUN NEEDS TO BEGIN and let the pump ask for the rest as it writes —
            // indexing the whole file first walks every cluster on the request that gates first paint.
            // ⚠ Returns whether it made PROGRESS, not whether it succeeded: a reader that has reached the end
            // answers "no more" and the pump must not ask again.
            var counted = 0;
            bool Extend(double untilSeconds)
            {
                var before = Counted();
                reader.ReadSamplesUntil(wanted, timeline.TicksAt(untilSeconds), cancellationToken);
                var after = Counted();
                counted = after;
                return after > before;
            }

            int Counted()
            {
                var total = 0;
                foreach (var track in reader.Tracks)
                {
                    if (wanted.Contains(track.Number)) total += track.Samples.Count;
                }
                return total;
            }

            // Far enough to open the first segment and still hold a sample of lookahead past it.
            if (!Extend(startSeconds + (request.Plan.LengthOf(request.FirstSegment) * 2) + 1) && counted == 0)
            {
                owner.Report("segments: the source's sample index could not be read");
                return;
            }

            using var writer = new SegmentRunWriter(owner, request, timeline);
            var lead = (video ?? audio)!;
            var from = SegmentGrid.SeekIndex(lead.Track.Samples, timeline.TicksAt(startSeconds));

            var how = string.Join(" + ", new[] { How(video, "picture"), How(audio, "sound") }
                                         .Where(part => part.Length > 0));
            owner.Report($"segments: producing from seg{request.FirstSegment} "
                       + $"(sample {from} of {lead.Track.Samples.Count}, {how})");

            writer.Run(source, video, audio, from, startSeconds, conversionOf: owner._conversion,
                       extend: Extend, cancellationToken);
        }

        /// <summary>How one track travels, for the log — whether a codec is being spent.</summary>
        private static string How(SegmentTrack? track, string name) => track is null
            ? string.Empty
            : $"{name} ({(track.Copy ? "copied" : "converted")})";

        /// <inheritdoc />
        public void Dispose()
        {
            _stopping.Cancel();
            try
            {
                // Bounded: a pump blocked inside a platform codec must not hold up the caller's teardown.
                _pump?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Wait surfaces the pump's own fault, which Pump has already reported.
            }
            _stopping.Dispose();
        }
    }
}
