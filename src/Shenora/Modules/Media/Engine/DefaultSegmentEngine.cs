namespace Shenora.Modules.Media;

/// <summary>
/// The kit's own <see cref="ISegmentEngine"/> — the platform's codecs behind
/// <see cref="IMediaStreamConversion"/>, plus <see cref="Mp4FragmentWriter"/>, and nothing else.
///
/// <para>
/// 🔴 <b>It ships no engine bytes and inherits no licence, which is what makes a DEFAULT defensible at
/// all</b> (D42's objection was megabytes every consumer pays for; D51's was a licence every consumer
/// inherits). Everything here is composition: the demuxer this kit already had for remuxing, the codecs the
/// shell already registers for conversion, and the fragment writer. An app past the platform's reach still
/// supplies its own engine through the same seam — this is the escape hatch's default, not its replacement.
/// </para>
///
/// <para>
/// <b>What it is for.</b> Segments are the TRANSCODE path. A source whose streams the container can already
/// carry is better served by the computed-remux route — one file, one plain <c>&lt;video src&gt;</c>, no
/// MediaSource and no re-encode (D71/D72). Which route a source takes is the APP's decision, expressed by
/// which route it registers for that URL; this engine does not second-guess it, because a route that
/// declined work it was explicitly given would be undebuggable.
/// </para>
///
/// <para>
/// ⚠ <b>Mobile only, honestly rather than silently.</b> <see cref="IMediaStreamConversion"/> is implemented
/// on Android and iOS; <c>Shenora.Windows</c> has no codec, so on the desktop <see cref="IsAvailable"/> is
/// false and the app's answer is the computed-remux route — which is the right answer there anyway, since
/// WebView2 serves byte ranges properly.
/// </para>
/// </summary>
internal sealed class DefaultSegmentEngine : ISegmentEngine
{
    // Explicit fields rather than primary-constructor captures: the nested run class reads both, and a
    // captured parameter is not a member a nested type can reach.
    private readonly IMediaStreamConversion? _conversion;
    private readonly Action<string>? _log;

    /// <param name="conversion">
    /// The shell's codecs. Null — or a shell that registered none — means <see cref="IsAvailable"/> is false
    /// and nothing else here is ever called.
    /// </param>
    /// <param name="log">Optional diagnostics. Guarded: a throwing sink must not kill a production run.</param>
    public DefaultSegmentEngine(IMediaStreamConversion? conversion, Action<string>? log = null)
    {
        _conversion = conversion;
        _log = log;
    }

    /// <summary>
    /// The track numbers this engine writes. Fixed rather than derived because
    /// <see cref="HasRenderedPicture"/> is handed a PATH and nothing else — it has to know which track to
    /// measure, and the only way it can is by having chosen the number itself.
    /// </summary>
    internal const int VideoTrackId = 1;
    internal const int AudioTrackId = 2;

    /// <inheritdoc />
    public bool IsAvailable => _conversion is not null;

    /// <inheritdoc />
    public string Describe() => _conversion is null
        ? "no segment engine: this shell registered no IMediaStreamConversion"
        : $"the kit's default segment engine (fMP4 fragments over {_conversion.GetType().Name})";

    /// <inheritdoc />
    public TimeSpan? DurationOf(string source) => Probe(source)?.Duration;

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ A video STREAM is not a picture: an attached cover image is carried as one, and building a video
    /// encoder for a soundtrack with album art wastes a hardware codec and produces a segment whose "picture"
    /// is one frame repeated. Dimensions are what separate them.
    /// <para>
    /// 🔴 <b>And the dimensions are on the RESULT, not on the stream</b> — <c>MatroskaProbe</c> fills
    /// <see cref="MediaProbeResult.Width"/>/<see cref="MediaProbeResult.Height"/> and leaves
    /// <see cref="MediaStreamInfo.Width"/> null on every stream it reports. This method asked the stream
    /// first and therefore answered FALSE for every source alive — so the engine would have built no video
    /// encoder at all and produced sound-only segments that played perfectly. Caught by a test, not by
    /// reading; the wrong version compiles and looks right.
    /// </para>
    /// </remarks>
    public bool HasPicture(string source)
    {
        var probe = Probe(source);
        return probe is not null
            && probe.Streams.Any(s => s.Kind is MediaStreamKind.Video)
            && probe.Width is > 0 && probe.Height is > 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The question the whole feature turns on, answered by SUBTRACTION rather than by structure — see
    /// <see cref="Mp4FragmentReader"/> for the measured bug it exists to catch.
    /// </remarks>
    public bool HasRenderedPicture(string segment) => Mp4FragmentReader.SampleBytes(segment, VideoTrackId) > 0;

    /// <inheritdoc />
    public ISegmentRun? Start(SegmentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_conversion is null) return null;

        // ONE candidate. The `Attempt` ladder exists for an engine that can offer a software encoder after a
        // hardware one failed; this engine has whatever the platform gave it and no second answer, so a
        // retry would re-run the identical work and fail identically. Null tells the caller to stop asking.
        if (request.Attempt > 0)
        {
            Report($"segments: no second candidate — this engine has one encoder path (attempt {request.Attempt})");
            return null;
        }

        // Refused at composition time rather than discovered by a seek. See SegmentGrid: a fractional grid
        // produces segments that PLAY and only misbehave when somebody seeks into them.
        if (!SegmentGrid.IsUsable(request.SegmentSeconds, out var reason))
        {
            Report($"segments: {reason}");
            return null;
        }

        var run = new Run(this, request);
        run.Begin();
        return run;
    }

    private MediaProbeResult? Probe(string source)
    {
        try
        {
            return MatroskaProbe.Read(source);
        }
        catch (Exception ex)
        {
            // A source that cannot be probed is one this engine cannot serve, and the contract's answer for
            // both DurationOf and HasPicture is the absent one rather than a throw.
            Report($"segments: could not probe '{Path.GetFileName(source)}' ({ex.GetType().Name})");
            return null;
        }
    }

    internal void Report(string message) => AppCallback.Log(_log, () => $"[Shenora.Modules.Media] {message}");

    /// <summary>
    /// One production run: a background pump that writes numbered fragments until it reaches the end of the
    /// source or is disposed.
    /// <para>
    /// 🔴 <b>Disposing must KILL it, and that is not a tidiness point.</b> A rolling window whose producer
    /// outlives its consumer holds a hardware codec — of which a device has a handful — plus a file handle
    /// and a CPU, invisibly, on a phone. The contract says so; this honours it with a token the pump checks
    /// between frames and a wait on the way out.
    /// </para>
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
                // Disposed. The caller asked for this, so it is not a fault and not worth a line.
            }
            catch (Exception ex)
            {
                // 🔴 A run dies with NO caller on the stack — it was started with Task.Run and nobody awaits
                // it — so an escaping exception is an unobserved fault and the only symptom is segments that
                // stop appearing. The consumer's own wait budget then reports "seg{k} did not arrive", which
                // names the effect and not the cause. This line is the cause.
                owner.Report($"segments: the production run failed ({ex.GetType().Name}: {ex.Message})");
            }
        }

        private void Produce(CancellationToken cancellationToken)
        {
            using var source = File.OpenRead(request.SourcePath);
            var reader = new MatroskaSampleReader(source);
            if (!reader.ReadHeader())
            {
                owner.Report($"segments: '{Path.GetFileName(request.SourcePath)}' is not readable Matroska");
                return;
            }

            var video = request.HasPicture ? Pick(reader, MediaStreamKind.Video) : null;
            var audio = Pick(reader, MediaStreamKind.Audio);
            if (video is null && audio is null)
            {
                owner.Report("segments: the source carries nothing this engine can convert");
                return;
            }

            var wanted = new HashSet<ulong>();
            if (video is not null) wanted.Add(video.Number);
            if (audio is not null) wanted.Add(audio.Number);
            if (!reader.ReadSamples(wanted, cancellationToken))
            {
                owner.Report("segments: the source's sample index could not be read");
                return;
            }

            // The clock the grid is measured on. Both tracks are cut against the SAME timeline, because the
            // manifest declares one grid for the whole presentation.
            var ticksPerSecond = reader.TimestampScaleNs > 0 ? 1_000_000_000L / reader.TimestampScaleNs : 1_000L;
            var startTicks = SegmentGrid.StartTicks(request.FirstSegment, ticksPerSecond, request.SegmentSeconds);

            using var writer = new SegmentRunWriter(owner, request, ticksPerSecond);
            var lead = video ?? audio!;
            var from = SegmentGrid.SeekIndex(lead.Samples, startTicks);

            owner.Report($"segments: producing from seg{request.FirstSegment} "
                       + $"(sample {from} of {lead.Samples.Count}, {(video is null ? "sound only" : "picture + sound")})");

            writer.Run(reader, source, video, audio, from, startTicks, conversionOf: owner._conversion!, cancellationToken);
        }

        /// <summary>
        /// The first track of a kind that this device can actually convert. ⚠ <b>Asked of the CONVERSION and
        /// not of the container</b>: a track whose codec the encoder declines is one the run would feed for
        /// nothing, producing a segment missing that stream — which reads as a broken engine rather than as
        /// an unsupported codec.
        /// </summary>
        private MatroskaTrack? Pick(MatroskaSampleReader reader, MediaStreamKind kind)
        {
            foreach (var track in reader.Tracks)
            {
                if (track.Kind != kind) continue;
                var codec = MatroskaProbe.CodecNameOf(track.CodecId, track.CodecPrivate ?? ReadOnlyMemory<byte>.Empty);
                if (codec is null) continue;
                if (owner._conversion!.CanConvert(kind, codec)) return track;
                owner.Report($"segments: no {kind} converter for '{codec}' on this device");
            }
            return null;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _stopping.Cancel();
            try
            {
                // Bounded: a pump blocked inside a platform codec must not hold up the caller's teardown, and
                // the token has already told it to stop. Everything it owns is disposed by its own finally.
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
