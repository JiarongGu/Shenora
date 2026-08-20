namespace Shenora.Modules.Media;

/// <summary>Why a remux did not happen. A code the caller branches on, never prose for a user.</summary>
public enum MediaRemuxerOutcome
{
    /// <summary>
    /// The output was written.
    /// <para>
    /// ⚠ <b>This does NOT mean every stream survived — check <see cref="MediaRemuxerResult.Dropped"/>.</b> A
    /// film whose only soundtrack is AC-3, remuxed with no <see cref="IMediaStreamConversion"/>, succeeds and
    /// plays SILENTLY (D63).
    /// </para>
    /// </summary>
    Succeeded,

    /// <summary>Not a Matroska file, or one declaring no track at all.</summary>
    NotMatroska,

    /// <summary>
    /// Matroska, with tracks, but none MP4 can carry without re-encoding — the planner's
    /// <see cref="MediaPlaybackAction.Transcode"/> case.
    /// </summary>
    NoCarriableStream,

    /// <summary>
    /// The codec is one MP4 carries, but the track shipped no usable decoder configuration and none could be
    /// derived. Writing the file anyway produces one that opens and shows nothing.
    /// </summary>
    MissingDecoderConfig,

    /// <summary>The source is malformed, truncated, or larger than this will walk.</summary>
    SourceUnreadable,

    /// <summary>The output could not be written.</summary>
    DestinationUnwritable,
}

/// <summary>What a remux did, or did not do.</summary>
/// <param name="Outcome">The verdict.</param>
/// <param name="Reason">
/// A short, non-localised explanation for the host LOG. Never for a user and never for the wire, whose error
/// contract is a code plus parameters (`ipc-contracts`).
/// </param>
/// <param name="VideoSamples">Frames copied into the picture track. 0 when there is none.</param>
/// <param name="AudioSamples">Frames copied into the sound track. 0 when there is none.</param>
/// <param name="Duration">The longest track's duration, as written into the output.</param>
public sealed record MediaRemuxerResult(
    MediaRemuxerOutcome Outcome,
    string Reason,
    int VideoSamples = 0,
    int AudioSamples = 0,
    TimeSpan Duration = default)
{
    /// <summary>True only for <see cref="MediaRemuxerOutcome.Succeeded"/>.</summary>
    public bool Succeeded => Outcome == MediaRemuxerOutcome.Succeeded;

    /// <summary>
    /// Codecs present in the source that did NOT make it into the output — <c>["ac3"]</c> for a film whose
    /// only soundtrack MP4 cannot carry and no conversion could rescue.
    /// <para>
    /// 🔴 <b>A successful remux that dropped the audio is the kit's most dangerous outcome: nothing throws,
    /// the file plays, and the user hears nothing.</b> The conversion route also puts this on the
    /// <see cref="MediaConversionEvents.Ready"/> event. ⚠ Empty is the normal case, and this is not a failure
    /// channel — the outcome says whether the file is usable, this what it cost.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Dropped { get; init; } = [];
}

/// <summary>
/// Rewrites a Matroska file as MP4, copying every frame untouched: no decode, no encode, no shipped binary
/// (D52's tier-1 engine). The video inside an ordinary <c>.mkv</c> is almost always H.264 or HEVC and the
/// device already decodes both — what the webview refuses is the BOX.
/// <para>
/// ⚠ <b>TWO PASSES over the source, forced by the output format.</b> A player needs the sample table
/// (<c>moov</c>) before it can seek and that table cannot be written until every frame's size and position
/// are known; streaming the output as it reads would put <c>moov</c> at the END, where the file plays from
/// the start and cannot seek until fetched whole. Roughly <b>1.2–1.4 GB/s</b> in memory (31.3 MB / 4000
/// frames in 22–26 ms), excluding file I/O.
/// </para>
/// <para>
/// It re-encodes nothing, so a stream MP4 cannot carry — AC-3, DTS, VP9 — is REPORTED rather than converted;
/// it converts no Annex-B start codes (Matroska already stores H.264 length-prefixed) and carries no
/// subtitles. It owns no atomicity: <c>UseMediaConversion</c> writes to a temporary path swapped into place
/// only on success, so a failed remux cannot leave a half-written file to be served as a cache hit.
/// </para>
/// </summary>
public sealed class Mp4Remuxer : IMediaContainerWriter
{
    /// <inheritdoc />
    public string Container => ".mp4";

    /// <inheritdoc />
    /// <remarks>
    /// What MP4 can hold WITHOUT re-encoding: H.264 and HEVC video (already length-prefixed in Matroska),
    /// AAC audio. Everything else is a refusal the transcode tier may then repair.
    /// </remarks>
    public bool CanCarry(MediaStreamKind kind, string codec) => kind switch
    {
        MediaStreamKind.Video => codec is "h264" or "hevc",
        MediaStreamKind.Audio => codec is "aac",
        _ => false,
    };

    /// <inheritdoc />
    public MediaRemuxerResult Write(Stream source, Stream destination, IMediaStreamConversion? conversion,
                                    CancellationToken cancellationToken = default)
        => Remux(source, destination, conversion, cancellationToken);

    /// <summary>
    /// How many bytes the media box's own header needs: the ordinary 8, or 16 for the 64-bit form. The total
    /// media size is known before the sample table is built, so the choice is never circular.
    /// </summary>
    internal static int MediaHeaderBytesFor(long mediaBytes) =>
        mediaBytes + 8 <= uint.MaxValue ? 8 : 16;

    /// <summary>
    /// Remux <paramref name="sourcePath"/> into <paramref name="destinationPath"/>, overwriting it, and
    /// TRANSCODE anything MP4 cannot carry using the codecs the app supplies. On a device whose codecs
    /// cannot, the refusal is unchanged.
    /// </summary>
    /// <param name="sourcePath">The Matroska file to read.</param>
    /// <param name="destinationPath">The MP4 to write, overwritten if it exists.</param>
    /// <param name="conversion">
    /// The shell's codecs. ⚠ <b><c>null</c> means a stream MP4 cannot carry is DROPPED, not converted</b>
    /// — the result still says <see cref="MediaRemuxerOutcome.Succeeded"/> and names the loss in
    /// <see cref="MediaRemuxerResult.Dropped"/>, so a film whose only soundtrack was AC-3 plays silently.
    /// Pass it explicitly (D59): there is deliberately no overload that omits it.
    /// </param>
    /// <param name="cancellationToken">Checked between frames; cancelling leaves no output.</param>
    public static MediaRemuxerResult Remux(string sourcePath, string destinationPath,
                                         IMediaStreamConversion? conversion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        try
        {
            // 🔴 THE DEFAULT 4 KiB BUFFER IS LOAD-BEARING — DO NOT RAISE IT. The walk SEEKS past every frame
            // payload and reads only block headers, so a bigger buffer drags in the very bytes it is skipping.
            // Measured on a 5 min / 166 MB / 4.6 Mbps MKV: at 4 KiB the walk makes the OS fetch 34 % of the
            // file, at 64 KiB 96 %, at 1 MiB 99 % — and buys NO time back (66 ms vs 60 ms warm). The 7,201
            // syscalls look like something to optimise and are not: unbuffered cuts the fetch to 20 % but
            // costs 4x the time (268 ms). 4 KiB is the knee.
            using var source = File.OpenRead(sourcePath);
            using var destination = File.Create(destinationPath);
            return Remux(source, destination, conversion, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 🔴 BEFORE THE GENERAL CATCH. Cancellation is not "source or destination unusable": answering
            // `SourceUnreadable` makes the caller's next move telling the user their video is corrupt, and
            // this overload is public — an app writing its own `Convert` delegate reaches it, so a wrong
            // answer travels all the way to a page as a FAILED event.
            throw;
        }
        catch (Exception)
        {
            // No exception text travels from here: a media path must not reach a page, and the caller already
            // knows which file it asked about.
            return new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "source or destination unusable");
        }
    }

    /// <summary>
    /// Remux one open stream into another, transcoding an uncarriable stream when it can.
    /// <paramref name="source"/> must be seekable — the sample table has to be built before the media is
    /// copied, so the frames are visited twice.
    /// </summary>
    /// <param name="source">The Matroska stream to read. Must be seekable.</param>
    /// <param name="destination">The stream the MP4 is written to.</param>
    /// <param name="conversion">
    /// The shell's codecs. ⚠ <c>null</c> DROPS what MP4 cannot carry rather than converting it — see the
    /// path overload above. Pass it explicitly (D59).
    /// </param>
    /// <param name="cancellationToken">Checked between frames.</param>
    public static MediaRemuxerResult Remux(Stream source, Stream destination,
                                         IMediaStreamConversion? conversion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        try
        {
            return Run(source, destination, conversion, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 🔴 CANCELLATION IS NOT A MALFORMED FILE. There is no `Canceled` outcome — cancellation is an
            // exception and the caller already holds the token. ⚠ BOTH entry points must rethrow, or a
            // cancelled remux reported as success hands back a truncated file that reads as a corrupt one.
            throw;
        }
        catch (Exception)
        {
            return new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "malformed source");
        }
    }

    /// <summary>
    /// What this remuxer WOULD write for <paramref name="source"/>, without writing it — the output's exact
    /// length and every byte's provenance, after one metadata pass and no copying at all.
    /// <para>
    /// 🔴 <b>A plannable source is a PURE COPY, whatever <see cref="IMediaStreamConversion"/> the writer
    /// carries</b> — planning refuses anything not carried untouched. ⚠ Weaken that to "only refuse what
    /// needs re-encoding" and an H.264 + AC-3 film plans at video-only length and is then WRITTEN longer: the
    /// silent <c>Content-Range</c> failure this type exists to prevent.
    /// </para>
    /// <para>
    /// ⚠ <b><c>null</c> is a ROUTING ANSWER, not an error</b> — including anything the output would LOSE (a
    /// re-encode, a second dub, a track holding no frames). A layout is a length and a byte map with no
    /// channel to report a loss, so a plan that dropped the soundtrack would serve a perfect
    /// <c>Content-Range</c> and no way to explain the silence.
    /// </para>
    /// <para>
    /// ⚠ <b>Peaks at 110–150 MB for a two-hour film, roughly a GIGABYTE at the reader's four-million-sample
    /// ceiling.</b> Cache against the source's identity and do not plan two films at once on a phone.
    /// </para>
    /// </summary>
    /// <param name="source">The Matroska source. Must be seekable — the frame index comes from a full walk.</param>
    /// <param name="cancellationToken">
    /// Observed once per cluster, inside the walk. ⚠ Cancelling THROWS rather than answering <c>null</c>,
    /// which would send the film to segments because someone navigated away.
    /// </param>
    /// <returns>The output's layout, or <c>null</c> when this source cannot be described this way.</returns>
    internal static Mp4Layout? Plan(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        PreparedRemux? prepared;
        try
        {
            // `lossless: true` is the whole difference from a write: nothing the source offered may be left
            // behind, because a plan describes an output and has no channel for what it cost.
            (prepared, _) = Prepare(source, conversion: null, lossless: true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not "this file cannot be planned": answering null would tell a route to fall
            // back to segments because someone navigated away.
            throw;
        }
        catch (Exception)
        {
            // Malformed is unplannable, and no exception text travels from here.
            return null;
        }

        if (prepared is null) return null;

        try
        {
            // Every span below claims to address the SOURCE, and a converted track's bytes live in a temp
            // file — so a layout covering one points a route at offsets in a file about to be deleted.
            if (prepared.Tracks.Exists(track => track.ByteSource is not null)) return null;

            var composed = ComposeHeader(prepared.Tracks, prepared.WriteOrder);
            if (composed is null) return null;
            var (header, mediaBytes) = composed.Value;

            // The SAME order the copy loop uses. Contiguous by construction: `mdat` is the frames back to
            // back, so the running total IS the next frame's offset.
            var samples = new Mp4SampleSpan[prepared.WriteOrder.Length];
            var at = (long)header.Length;
            for (var i = 0; i < prepared.WriteOrder.Length; i++)
            {
                var sample = prepared.WriteOrder[i].Sample;
                samples[i] = new Mp4SampleSpan(sample.Offset, sample.Length, at);
                at += sample.Length;
            }

            return new Mp4Layout(header, samples, header.Length + mediaBytes);
        }
        finally
        {
            prepared.Dispose();
        }
    }

    private static MediaRemuxerResult Run(Stream source, Stream destination, IMediaStreamConversion? conversion, CancellationToken cancellationToken)
    {
        var (prepared, refused) = Prepare(source, conversion, lossless: false, cancellationToken);
        // Every refusal inside `Prepare` supplies a result, so the fallback is unreachable; it is here rather
        // than a `!` because the alternative is a NullReferenceException far from the branch that forgot one.
        if (prepared is null) return refused ?? new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "the source could not be prepared");

        try
        {
            try
            {
                return Write(source, destination, prepared, cancellationToken) with { Dropped = prepared.Dropped };
            }
            catch (OperationCanceledException)
            {
                // 🔴 BEFORE THE GENERAL CATCH, or a CANCELLED remux is answered with "the output could not be
                // written" — telling the caller their disk failed when they pressed stop.
                throw;
            }
            catch (Exception)
            {
                return new MediaRemuxerResult(MediaRemuxerOutcome.DestinationUnwritable, "the output could not be written");
            }
        }
        finally
        {
            // 🔴 The converted track's spool, on every path out of here. A COPIED track has none — those read
            // from `source`, which this method must not close.
            prepared.Dispose();
        }
    }

    /// <summary>
    /// Everything that must be known before a byte can be written: which streams survive, each one's sample
    /// table, and the exact order the frames go out in.
    /// <para>
    /// 🔴 <b>Shared by the write path and by <see cref="Plan"/>.</b> A plan that selected its tracks,
    /// resolved its timing or interleaved its chunks even slightly differently from the write would describe
    /// a file the write does not produce — and the symptom is not a crash but a <c>Content-Range</c> total
    /// the bytes do not honour, which a media element fails on SILENTLY: a blank picture, no error.
    /// </para>
    /// </summary>
    /// <param name="source">The Matroska source, seekable — the frame index comes from walking it.</param>
    /// <param name="conversion">The app's codecs, or null for a pure copy.</param>
    /// <param name="lossless">
    /// When true, ANY stream the source offered that the output would not carry is a REFUSAL rather than a
    /// drop — checked cheaply on carriability before the clusters are walked and authoritatively on the
    /// <c>Dropped</c> set after, since only the walk reveals a track that holds no frames. <see cref="Plan"/>'s
    /// rule, not the writer's. ⚠ Only ever passed <c>true</c> with <paramref name="conversion"/> null.
    /// </param>
    /// <param name="cancellationToken">Observed per cluster in the walk, and between frames in a conversion.</param>
    private static (PreparedRemux? Ready, MediaRemuxerResult? Refused) Prepare(
        Stream source, IMediaStreamConversion? conversion, bool lossless, CancellationToken cancellationToken)
    {
        if (!source.CanSeek) return (null, new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "source is not seekable"));

        var reader = new MatroskaSampleReader(source);
        if (!reader.ReadHeader()) return (null, new MediaRemuxerResult(MediaRemuxerOutcome.NotMatroska, "no Matroska header or no tracks"));

        // ⚠ THE CHEAP PRE-FILTER, NOT THE RULE — the authoritative check is the `dropped` one further down,
        // and anything this misses that one catches. ⚠ It sweeps `reader.Tracks`, which holds only picture
        // and sound (the reader drops subtitles at parse time). Load-bearing: a subtitle can never be
        // carried, so counting one here would make almost every real film unplannable.
        if (lossless && reader.Tracks.Any(track => !Carriable(track)))
        {
            var uncarriable = string.Join(" + ", reader.Tracks.Where(t => !Carriable(t)).Select(t => $"{t.Kind}:{t.CodecId ?? "?"}"));
            return (null, new MediaRemuxerResult(MediaRemuxerOutcome.NoCarriableStream,
                $"a computed output needs every stream carriable; these would need re-encoding: {uncarriable}"));
        }

        // ── choose the streams ────────────────────────────────────────────────────────────────────────
        // The first of each kind MP4 can carry, not "every track": a film with four dubs would otherwise
        // produce an output four soundtracks wide, and a webview plays one.
        var video = reader.Tracks.FirstOrDefault(t => t.Kind == MediaStreamKind.Video && CanCarryVideo(t));
        var audio = reader.Tracks.FirstOrDefault(t => t.Kind == MediaStreamKind.Audio && CanCarryAudio(t));

        // Copying beats converting whenever both are possible: faster, lossless, and it cannot fail halfway.
        // ONE selection for both kinds. The video case is `mpeg4`: the device decodes it and its own webview
        // refuses it, so the page gets sound and a blank picture with NO error at all.
        (MatroskaTrack? Track, string? Codec) Choose(MediaStreamKind kind, MatroskaTrack? carriable)
        {
            if (carriable is not null || conversion is null) return (null, null);
            foreach (var track in reader.Tracks.Where(t => t.Kind == kind))
            {
                // Through the VfW wrapper: an h263 track has no native Matroska id, so without the private
                // data its codec name is "vfw" and a converter declines a codec it offers.
                var codec = MatroskaProbe.CodecNameOf(track.CodecId, track.CodecPrivate ?? ReadOnlyMemory<byte>.Empty);
                if (codec is null || !conversion.CanConvert(kind, codec)) continue;
                return (track, codec);
            }
            return (null, null);
        }

        var (convert, convertCodec) = Choose(MediaStreamKind.Audio, audio);
        var (convertVideo, convertVideoCodec) = Choose(MediaStreamKind.Video, video);

        if (video is null && audio is null && convert is null && convertVideo is null)
        {
            var codecs = string.Join(" + ", reader.Tracks.Select(t => $"{t.Kind}:{t.CodecId ?? "?"}"));
            return (null, new MediaRemuxerResult(MediaRemuxerOutcome.NoCarriableStream,
                $"no stream MP4 can carry without re-encoding: {codecs}"));
        }

        var plans = new List<Mp4TrackPlan>();
        if (video is not null)
        {
            var entry = BuildVideoEntry(video);
            if (entry is null) return (null, new MediaRemuxerResult(MediaRemuxerOutcome.MissingDecoderConfig,
                $"video track {video.Number} ({video.CodecId}) carries no decoder configuration"));
            plans.Add(new PendingTrack(video, entry).Placeholder());
        }
        if (audio is not null)
        {
            var entry = BuildAudioEntry(audio);
            if (entry is null) return (null, new MediaRemuxerResult(MediaRemuxerOutcome.MissingDecoderConfig,
                $"audio track {audio.Number} ({audio.CodecId}) carries no decoder configuration"));
            plans.Add(new PendingTrack(audio, entry).Placeholder());
        }

        // ── walk the clusters ─────────────────────────────────────────────────────────────────────────
        var wanted = plans.Select(p => p.Source.Number).ToHashSet();
        if (convert is not null) wanted.Add(convert.Number);
        if (convertVideo is not null) wanted.Add(convertVideo.Number);
        // ⚠ The token reaches the WALK, not just the conversion below: the walk is the long part of a
        // copy-only remux, and a `Plan` from a range route runs it inside a web request.
        if (!reader.ReadSamples(wanted, cancellationToken))
        {
            return (null, new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "malformed or unbounded clusters"));
        }

        if (plans.All(p => p.Source.Samples.Count == 0) && (convert is null || convert.Samples.Count == 0)
            && (convertVideo is null || convertVideo.Samples.Count == 0))
        {
            return (null, new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "the file declares tracks but holds no frames"));
        }

        // ⚠ THE SCOPE OF THIS `try` IS THE SPOOL'S LIFETIME, and `handedOver` is what makes an early RETURN
        // leak nothing either. 🔴 It must START HERE, before the first Convert: starting it after leaks the
        // audio spool whenever the audio converts and the video conversion then fails.
        var resolved = new List<Mp4TrackPlan>();
        var handedOver = false;
        try
        {
            // ── resolve timing ────────────────────────────────────────────────────────────────────────
            foreach (var plan in plans)
            {
                if (plan.Source.Samples.Count == 0) continue;   // a declared-but-empty track is dropped, not written empty
                resolved.Add(Resolve(plan, reader.TimestampScaleNs));
            }

            // The transcode, after the copies: a failure here must not have already spooled work for tracks
            // that were going to be copied anyway.
            if (convert is not null && convertCodec is not null && convert.Samples.Count > 0)
            {
                var converted = Convert(source, convert, convertCodec, MediaStreamKind.Audio, conversion!,
                                        reader.TimestampScaleNs, cancellationToken);
                if (converted is null)
                {
                    return (null, new MediaRemuxerResult(MediaRemuxerOutcome.NoCarriableStream,
                        $"the device could not convert {convertCodec} after accepting it"));
                }
                resolved.Add(converted);
            }

            if (convertVideo is not null && convertVideoCodec is not null && convertVideo.Samples.Count > 0)
            {
                var converted = Convert(source, convertVideo, convertVideoCodec, MediaStreamKind.Video, conversion!,
                                        reader.TimestampScaleNs, cancellationToken);
                if (converted is null)
                {
                    // 🔴 A REFUSAL, never a silent drop: a file whose picture we accepted and then failed to
                    // produce is the wrong file, so the route emits FAILED rather than caching a black one.
                    return (null, new MediaRemuxerResult(MediaRemuxerOutcome.NoCarriableStream,
                        $"the device could not convert video {convertVideoCodec} after accepting it"));
                }
                resolved.Add(converted);
            }

            if (resolved.Count == 0) return (null, new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "no frames for any carriable track"));

            var writeOrder = Interleave(resolved);

            // ── what did NOT survive ──────────────────────────────────────────────────────────────────
            // ⚠ `resolved` ALREADY carries the converted track, so the chosen set is exactly this. Marking
            // `convert.Number` kept separately marks a ZERO-frame convertible track as survived, and
            // `Dropped` then claims nothing was lost from a file with no soundtrack.
            var kept = new HashSet<ulong>(resolved.Select(r => r.Source.Number));
            var dropped = reader.Tracks
                .Where(t => !kept.Contains(t.Number))
                // ⚠ Through the wrapper HERE TOO — this is the half a page reads, and `dropped:["vfw"]` names
                // a container convention rather than a codec, which no app can act on.
                .Select(t => MatroskaProbe.CodecNameOf(t.CodecId, t.CodecPrivate ?? ReadOnlyMemory<byte>.Empty)
                             ?? t.CodecId ?? "unknown")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // 🔴 THE AUTHORITATIVE HALF OF THE LOSSLESS RULE. Carriability is not the whole question: the
            // first-of-each-kind selection leaves a SECOND dub behind, and a track that declares itself and
            // holds no frames drops itself. Neither needs re-encoding, both leave an output MISSING a stream
            // the source offered, and an `Mp4Layout` has no channel to say so — a computed remux that lost
            // the soundtrack is served as a 200 with a perfect `Content-Range`.
            if (lossless && dropped.Length > 0)
            {
                return (null, new MediaRemuxerResult(MediaRemuxerOutcome.NoCarriableStream,
                    $"a computed output must lose nothing; these did not survive: {string.Join(" + ", dropped)}"));
            }

            handedOver = true;
            return (new PreparedRemux { Tracks = resolved, WriteOrder = writeOrder, Dropped = dropped }, null);
        }
        finally
        {
            if (!handedOver) foreach (var plan in resolved) plan.ByteSource?.Dispose();
        }
    }

    /// <summary>
    /// A remux decided but not yet performed: the tracks with their finished sample tables, the frame order
    /// both the chunk table and the copy loop use, and what the source offered that did not survive.
    /// ⚠ <b>It OWNS any conversion spool</b> — a <c>DeleteOnClose</c> temp file, so whoever holds one of these
    /// must dispose it or leak a whole soundtrack onto a phone's disk with no name anyone can find.
    /// </summary>
    private sealed class PreparedRemux
    {
        public required List<Mp4TrackPlan> Tracks { get; init; }

        /// <summary>The frames in emission order — computed ONCE, and shared rather than re-derived.</summary>
        public required WriteItem[] WriteOrder { get; init; }

        public required IReadOnlyList<string> Dropped { get; init; }

        /// <summary>Releases every converted track's spool. A copied track has none and reads from the source.</summary>
        public void Dispose()
        {
            foreach (var track in Tracks) track.ByteSource?.Dispose();
        }
    }

    /// <summary>
    /// Whether the output could carry this track's frames UNTOUCHED, asked of the raw Matroska CodecID and
    /// through <see cref="Mp4Carriage"/> rather than answered here: the segment writer asks the same question
    /// about the same file, and a second spelling of it is how the plan and the write disagree.
    /// </summary>
    private static bool Carriable(MatroskaTrack track) => Mp4Carriage.CanCarry(track);

    private static bool CanCarryVideo(MatroskaTrack track) => Mp4Carriage.CanCarryVideo(track);

    private static bool CanCarryAudio(MatroskaTrack track) => Mp4Carriage.CanCarryAudio(track);

    private static byte[]? BuildVideoEntry(MatroskaTrack track) => Mp4Carriage.EntryFor(track);

    private static byte[]? BuildAudioEntry(MatroskaTrack track) => Mp4Carriage.EntryFor(track);

    /// <summary>Turn one track's frame list into a decode timeline on a timescale MP4 can hold.</summary>
    private static Mp4TrackPlan Resolve(Mp4TrackPlan pending, long timestampScaleNs)
    {
        var samples = pending.Source.Samples.ToArray();

        // Prefer the timescale that expresses the source's own ticks EXACTLY — a clean 1000 for the 1 ms
        // scale every real file uses. Only an unusual scale falls back to milliseconds, where rounding drifts
        // picture against sound over an hour.
        uint timescale;
        long[] times;
        if (timestampScaleNs > 0 && 1_000_000_000L % timestampScaleNs == 0)
        {
            timescale = (uint)(1_000_000_000L / timestampScaleNs);
            times = samples.Select(s => s.Ticks).ToArray();
        }
        else
        {
            timescale = 1000;
            times = samples.Select(s => s.Ticks * timestampScaleNs / 1_000_000).ToArray();
        }

        var step = pending.Source.DefaultDurationNs > 0
            ? pending.Source.DefaultDurationNs * timescale / 1_000_000_000L
            : 0;

        // Ties only ever arise from lacing, which is an audio shape; on a picture track this is a no-op.
        var presentation = SampleTiming.SpreadTies(times, step);
        var (decode, composition, shift) = SampleTiming.Derive(presentation);
        var durations = SampleTiming.Durations(decode, step);

        return new Mp4TrackPlan
        {
            Source = pending.Source,
            Timescale = timescale,
            SampleEntry = pending.SampleEntry,
            Samples = samples,
            Decode = decode,
            Composition = composition,
            Durations = durations,
            Shift = shift,
        };
    }

    /// <summary>
    /// Decide the order frames are written in, fill in the chunk tables, and RETURN that order. Source order
    /// is kept, which is already interleaved, and a chunk is one unbroken run of the same track.
    /// <para>
    /// 🔴 <b>Computed ONCE and handed on, never recomputed where the bytes are copied.</b> Deriving it twice
    /// — even from the same rule — makes any later edit to one ordering silently desynchronise the file from
    /// its own index: it parses perfectly and decodes garbage.
    /// </para>
    /// </summary>
    private static WriteItem[] Interleave(List<Mp4TrackPlan> tracks)
    {
        // Ordered by DECODE TIME, not by position in the source: a CONVERTED track's bytes live in a spool
        // with offsets of its own, so comparing those against the source's compares two unrelated numbers and
        // the chunks end up ordered by nothing.
        // Normalised to seconds first: two tracks can have different timescales, and comparing raw ticks
        // across them silently interleaves by the wrong ratio.
        var ordered = tracks
            .SelectMany((track, index) => track.Samples.Select((sample, i) => new WriteItem(
                index, i, sample, track.Timescale == 0 ? 0d : (double)track.Decode[i] / track.Timescale)))
            .OrderBy(item => item.Seconds)
            .ThenBy(item => item.Track)
            .ToArray();

        var running = 0L;
        var current = -1;
        foreach (var item in ordered)
        {
            if (item.Track != current)
            {
                tracks[item.Track].ChunkOffsets.Add(running);
                tracks[item.Track].ChunkSamples.Add(0);
                current = item.Track;
            }

            tracks[item.Track].ChunkSamples[^1]++;
            running += item.Sample.Length;
        }

        return ordered;
    }

    /// <summary>One frame in the order it will be written, and which track's byte source it comes from.</summary>
    private readonly record struct WriteItem(int Track, int Index, MatroskaSample Sample, double Seconds);

    /// <summary>
    /// Everything before the first sample byte — <c>ftyp</c>, <c>moov</c> and the <c>mdat</c> box header —
    /// plus how many media bytes follow it. <c>null</c> when the two <c>moov</c> passes disagree on size.
    /// <para>
    /// 🔴 <b>THE CIRCULARITY, AND THE ONE PLACE IT IS RESOLVED.</b> The chunk-offset table holds ABSOLUTE
    /// positions, so the sample table's CONTENTS depend on where the media starts, which depends on how long
    /// the header is, which depends on the sample table. The way out is a fixed-WIDTH table — Mp4Builder
    /// writes <c>co64</c> always and never <c>stco</c> for this reason — so building once with a media start
    /// of zero learns the length, and building again with the real value produces something as long.
    /// 🔴 <b>Both the write path and <see cref="Plan"/> call THIS, and nothing may re-derive it:</b> a planned
    /// header one byte off from the written one moves every chunk offset after it, and the file parses
    /// perfectly and decodes garbage while its total LENGTH — all a range request advertises — still matches.
    /// </para>
    /// </summary>
    private static (byte[] Bytes, long MediaBytes)? ComposeHeader(IReadOnlyList<Mp4TrackPlan> tracks, WriteItem[] writeOrder)
    {
        var ftyp = Mp4Builder.Ftyp();
        var mediaBytes = writeOrder.Sum(item => (long)item.Sample.Length);
        var mediaHeaderBytes = MediaHeaderBytesFor(mediaBytes);

        // Built twice: the first tells us how long it is, which decides where the media starts, which the
        // second has to state.
        var sizing = Mp4Builder.Moov(tracks, 0);
        var mediaStart = ftyp.Length + sizing.Length + mediaHeaderBytes;
        var moov = Mp4Builder.Moov(tracks, mediaStart);

        // Unreachable by construction, and checked: if it fires, every chunk offset in the file is wrong by
        // the difference and the output is silently unplayable.
        if (moov.Length != sizing.Length) return null;

        var bytes = new byte[mediaStart];
        ftyp.CopyTo(bytes.AsSpan());
        moov.CopyTo(bytes.AsSpan(ftyp.Length));

        var mediaHeader = bytes.AsSpan(ftyp.Length + moov.Length);
        if (mediaHeaderBytes == 8)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mediaHeader[..4], (uint)(mediaBytes + 8));
            "mdat"u8.CopyTo(mediaHeader[4..8]);
        }
        else
        {
            // The 64-bit form: a size of 1 says "the real one is the eight bytes after the type".
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mediaHeader[..4], 1);
            "mdat"u8.CopyTo(mediaHeader[4..8]);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(mediaHeader[8..16], (ulong)(mediaBytes + 16));
        }

        return (bytes, mediaBytes);
    }

    private static MediaRemuxerResult Write(Stream source, Stream destination, PreparedRemux prepared,
                                          CancellationToken cancellationToken)
    {
        var tracks = prepared.Tracks;
        var composed = ComposeHeader(tracks, prepared.WriteOrder);
        if (composed is null)
        {
            return new MediaRemuxerResult(MediaRemuxerOutcome.DestinationUnwritable,
                "the sample table changed size between passes");
        }

        var (header, mediaBytes) = composed.Value;
        destination.Write(header);

        CopySamples(source, destination, tracks, prepared.WriteOrder, cancellationToken);

        var duration = tracks.Max(t => t.Timescale == 0 ? 0d : (double)t.Duration / t.Timescale);
        return new MediaRemuxerResult(
            MediaRemuxerOutcome.Succeeded,
            $"remuxed {tracks.Count} stream(s), {mediaBytes} media byte(s) copied",
            tracks.FirstOrDefault(t => t.IsVideo)?.Samples.Length ?? 0,
            tracks.FirstOrDefault(t => !t.IsVideo)?.Samples.Length ?? 0,
            TimeSpan.FromSeconds(duration));
    }

    /// <summary>
    /// Copy every frame in the order the chunk table promised — <paramref name="writeOrder"/> IS that order,
    /// handed over rather than re-derived (see <see cref="Interleave"/>). Ascending source position, so the
    /// read is sequential however the tracks are interleaved.
    /// </summary>
    private static void CopySamples(Stream source, Stream destination, List<Mp4TrackPlan> tracks,
                                    WriteItem[] writeOrder, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        foreach (var item in writeOrder)
        {
            // Each track says where ITS bytes are: the source file for a copy, a spool for a conversion.
            // Reading everything from `source` produces a file full of the wrong bytes at plausible offsets.
            var from = tracks[item.Track].ByteSource ?? source;
            var sample = item.Sample;
            // Between frames, not mid-frame: a partial frame leaves the output inconsistent with the sample
            // table already written.
            cancellationToken.ThrowIfCancellationRequested();
            from.Position = sample.Offset;
            var left = sample.Length;
            while (left > 0)
            {
                var take = Math.Min(left, buffer.Length);
                var read = from.ReadAtLeast(buffer.AsSpan(0, take), take, throwOnEndOfStream: false);
                if (read <= 0) throw new EndOfStreamException();
                destination.Write(buffer, 0, read);
                left -= read;
            }
        }
    }

    /// <summary>
    /// Run one track through the device's codecs and spool the result, returning a plan whose bytes live in
    /// the spool rather than in the source.
    /// <para>
    /// <b>AUDIO timing comes from the ENCODER, not from the source.</b> A decoder may resample and a downmix
    /// may change the channel count, so the output's frames do not line up with the input's — but every
    /// output frame carries <see cref="IMediaStreamConversionRun.OutputFramesPerPacket"/> samples at
    /// <see cref="IMediaStreamConversionRun.OutputFormat"/>'s rate, so the timescale is the sample rate and
    /// the table is exact by construction. ⚠ <b>AUDIO's alone</b>: a picture's frames are timed individually,
    /// so the video branch derives its timeline from what the encoder stamped.
    /// </para>
    /// <para>⚠ Spooled to a TEMPORARY FILE, deleted on close — a two-hour soundtrack is ~115 MB as AAC.</para>
    /// </summary>
    private static Mp4TrackPlan? Convert(Stream source, MatroskaTrack track, string codec, MediaStreamKind kind,
                                         IMediaStreamConversion conversion, long timestampScaleNs,
                                         CancellationToken cancellationToken)
    {
        // A codec told the wrong values does not fail: audio plays at the wrong SPEED, a picture comes out
        // stretched or green, and one told no CodecPrivate produces silence. ONE call for both kinds; fields
        // that do not apply to a kind are null, since MediaStreamInfo is best-effort.
        using var run = conversion.Begin(
            new MediaStreamInfo(kind, codec,
                Channels: track.Channels > 0 ? track.Channels : null,
                SampleRate: track.SampleRate > 0 ? (int)Math.Round(track.SampleRate) : null,
                Width: track.Width > 0 ? track.Width : null,
                Height: track.Height > 0 ? track.Height : null,
                FrameRate: track.DefaultDurationNs > 0 ? 1_000_000_000d / track.DefaultDurationNs : null),
            track.CodecPrivate ?? ReadOnlyMemory<byte>.Empty);
        if (run is null) return null;

        // 🔴 THE SPOOL OUTLIVES THIS METHOD ON THE SUCCESS PATH — it becomes the plan's `ByteSource`, so it
        // cannot be a `using`. ⚠ Every OTHER path must close it BY HAND, and missing one fails nothing: a
        // leaked handle is invisible until the device runs out.
        var spool = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
                                   FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
        var samples = new List<MatroskaSample>();
        var presentation = new List<long>();
        var frame = Array.Empty<byte>();

        try
        {
            void Emit(IReadOnlyList<MediaFrame> outputs)
            {
                foreach (var output in outputs)
                {
                    if (output.Data.Length == 0) continue;
                    // Ticks carries the PRESENTATION time for both kinds; the decode timeline is derived
                    // below. The keyframe flag passes through — inventing it makes a seek land on a smear.
                    samples.Add(new MatroskaSample(spool.Position, output.Data.Length,
                        Ticks: output.PresentationTimeUs, KeyFrame: output.IsKeyframe));
                    presentation.Add(output.PresentationTimeUs);
                    spool.Write(output.Data.Span);
                }
            }

            foreach (var sample in track.Samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frame.Length < sample.Length) frame = new byte[sample.Length];
                source.Position = sample.Offset;
                if (source.ReadAtLeast(frame.AsSpan(0, sample.Length), sample.Length, throwOnEndOfStream: false) != sample.Length) break;
                // Zero outputs is NORMAL — codecs buffer, and treating an empty return as failure abandons
                // every conversion in its first frames. The source's ticks are the file's own scale; the seam
                // speaks microseconds.
                Emit(run.Push(new MediaFrame(frame.AsMemory(0, sample.Length),
                                             sample.Ticks * timestampScaleNs / 1_000, sample.KeyFrame)));
            }

            // 🔴 Without this the tail stays inside the codec and the soundtrack simply stops early, in a file
            // that is otherwise perfectly well-formed.
            Emit(run.Drain());

            var config = run.OutputConfig;
            if (samples.Count == 0 || config.Length == 0) { spool.Dispose(); return null; }

            // ── the ONE place the kinds differ: the codec's own model of time. Audio frames are a fixed
            // number of samples each, so the timeline is arithmetic and every frame is a sync sample. Video
            // frames are timed individually, so the timeline is DERIVED from what the encoder stamped.
            var format = run.OutputFormat;
            uint timescale;
            long[] decode, composition, durations;
            long shift;
            byte[] entry;

            if (kind is MediaStreamKind.Video)
            {
                // Microseconds, matching MediaFrame 1:1, so nothing rounds between the encoder and the file.
                timescale = 1_000_000;
                // 🔴 The SAME derivation the copy path uses. Hand-rolled here it produces composition offsets
                // that go NEGATIVE — which `ctts` version 0 stores UNSIGNED, so the file carries a vast
                // positive offset and plays wrong while parsing perfectly.
                (decode, composition, shift) = SampleTiming.Derive(presentation);
                durations = SampleTiming.Durations(decode, track.DefaultDurationNs > 0 ? track.DefaultDurationNs / 1_000 : 0);

                var entryType = (format.Codec ?? "h264").Equals("hevc", StringComparison.OrdinalIgnoreCase) ? "hvc1" : "avc1";
                entry = Mp4Builder.VisualSampleEntry(entryType, entryType == "hvc1" ? "hvcC" : "avcC",
                    Math.Max(format.Width ?? 0, 1), Math.Max(format.Height ?? 0, 1), config.ToArray());
            }
            else
            {
                timescale = (uint)Math.Max(format.SampleRate ?? 0, 1);
                var perPacket = (long)Math.Max(run.OutputFramesPerPacket, 1);
                decode = new long[samples.Count];
                durations = new long[samples.Count];
                for (var i = 0; i < samples.Count; i++) { decode[i] = i * perPacket; durations[i] = perPacket; }
                composition = new long[samples.Count];
                shift = 0;
                entry = Mp4Builder.AudioSampleEntry(Math.Max(format.Channels ?? 0, 1), timescale, config.ToArray());
            }

            return new Mp4TrackPlan
            {
                Source = track,
                Timescale = timescale,
                SampleEntry = entry,
                Samples = [.. samples],
                Decode = decode,
                Composition = composition,
                Durations = durations,
                Shift = shift,
                ByteSource = spool,
            };
        }
        catch
        {
            // CANCELLATION LANDS HERE, and it is much the likeliest of these paths — a transcode is the
            // longest thing this kit does. Leaking the handle leaks a FILE, once per cancelled conversion.
            spool.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A track chosen but not yet timed. Exists so selection can fail EARLY — a missing decoder configuration
    /// is worth reporting before walking a multi-gigabyte file to find its frames.
    /// </summary>
    private readonly record struct PendingTrack(MatroskaTrack Source, byte[] Entry)
    {
        public Mp4TrackPlan Placeholder() => new()
        {
            Source = Source,
            Timescale = 0,
            SampleEntry = Entry,
            Samples = [],
            Decode = [],
            Composition = [],
            Durations = [],
            Shift = 0,
        };
    }
}
