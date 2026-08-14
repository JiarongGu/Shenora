using Shenora.Core.Ipc;

namespace Shenora.Modules.Media;

/// <summary>Why a remux did not happen. A code the caller branches on, never prose for a user.</summary>
public enum MediaRemuxerOutcome
{
    /// <summary>
    /// The output was written.
    /// <para>
    /// ⚠ <b>This does NOT mean every stream survived — check <see cref="MediaRemuxerResult.Dropped"/>.</b>
    /// A film whose only soundtrack is AC-3, remuxed with no <see cref="IMediaStreamConversion"/>, succeeds
    /// and plays SILENTLY: the picture is carriable, the audio is not, and dropping it is the only way to
    /// produce a playable file at all. This member used to say "every selected stream was copied", which
    /// was false in exactly that case and is how the silence went unnoticed (D63's failure mode: the
    /// degraded result was indistinguishable from the intended one).
    /// </para>
    /// </summary>
    Succeeded,

    /// <summary>Not a Matroska file, or one declaring no track at all.</summary>
    NotMatroska,

    /// <summary>
    /// Matroska, with tracks, but none MP4 can carry without re-encoding. The honest verdict for the file
    /// this layer cannot help: the planner's <see cref="MediaPlaybackAction.Transcode"/> case.
    /// </summary>
    NoCarriableStream,

    /// <summary>
    /// The codec is one MP4 carries, but the track shipped no usable decoder configuration and none could
    /// be derived. A player needs it before the first frame, so writing the file anyway produces one that
    /// opens and shows nothing.
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
/// A short, non-localised explanation for the host LOG. Not for a user and not for the wire — it names
/// codecs, and this kit's error contract is a code plus parameters, never English prose (`ipc-contracts`).
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
    /// 🔴 <b>This is the difference between a silent film and a silent film you can explain.</b> A
    /// successful remux that dropped the audio is the kit's most dangerous outcome: nothing throws, the
    /// file plays, and the user hears nothing. An app that reads this can say *"this file's AC-3
    /// soundtrack cannot play on this device"* instead of leaving them to wonder — and the conversion route
    /// puts it on the <see cref="MediaConversionEvents.Ready"/> event so a page can too.
    /// </para>
    /// <para>
    /// ⚠ Empty is the normal case and means nothing was lost. It is NOT a failure channel — the outcome
    /// says whether the file is usable; this says what it cost.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Dropped { get; init; } = [];
}

/// <summary>
/// Rewrites a Matroska file as MP4, copying every frame untouched — the cheap half of the translation
/// layer, and the one that fixes the most common failure there is.
///
/// <para>
/// 🔴 <b>What this is for, and why it needs no codec.</b> The video inside an ordinary <c>.mkv</c> is
/// almost always H.264 or HEVC, and the device already decodes both in hardware. What the webview refuses
/// is the BOX. So the repair is to write a different box around the same bytes: no decoding, no encoding,
/// no patents, no shipped binary — the tier-1 engine of D52, and the reason a remuxer is worth writing in
/// managed code while a codec library is not.
/// </para>
///
/// <para>
/// ⚠ <b>It is a TWO-PASS job over the source, and that is forced by the output format rather than chosen.</b>
/// A player needs the sample table (<c>moov</c>) before it can seek, and a sample table cannot be written
/// until every frame's size and position are known — so the whole source is walked for positions before a
/// single byte is written. Streaming a remux out as it reads would put <c>moov</c> at the END, which is a
/// file that plays from the start and cannot seek until it has been fetched whole.
/// </para>
///
/// <para>
/// <b>MEASURED, because "fast" deserves a number</b> (2026-08-07, Release, in-memory, 31.3 MB / 4000
/// frames): <b>22–26 ms steady state — roughly 1.2–1.4 GB/s</b>, with 64 ms on the first run including
/// JIT. A gigabyte film is therefore ~1 s of CPU, and real runs are dominated by disk rather than by this.
/// That is the D52 thesis paying off in one number: it is a COPY, not a decode, so the work is proportional
/// to bytes moved and nothing else. ⚠ The figure is in-memory and excludes file I/O — it measures parsing,
/// table building and the copy, which is the part this class controls.
/// </para>
///
/// <para>
/// <b>What it deliberately does NOT do.</b> It re-encodes nothing, so a stream MP4 cannot carry — AC-3,
/// DTS, VP9 — is reported rather than converted; that is the transcode tier's job and this refuses instead
/// of half-doing it. It does not convert Annex-B start codes, because Matroska already stores H.264 in the
/// length-prefixed form MP4 uses. It carries no subtitles: a text track is a format conversion, not a
/// container rewrite, and the planner already treats them as droppable.
/// </para>
///
/// <para>
/// It writes wherever it is pointed and owns no atomicity — the caller does. Through
/// <c>UseMediaConversion</c> that is already handled: the destination is a temporary path swapped into
/// place only on success, so a failed remux can never leave a half-written file to be served as a cache hit.
/// </para>
/// </summary>
public sealed class Mp4Remuxer : IMediaContainerWriter
{
    /// <inheritdoc />
    public string Container => ".mp4";

    /// <inheritdoc />
    /// <remarks>
    /// What MP4 can hold WITHOUT re-encoding. Video is H.264 and HEVC (their Matroska form is already the
    /// length-prefixed one MP4 uses); audio is AAC. Everything else is a refusal, which the transcode tier
    /// may then repair.
    /// </remarks>
    public bool CanCarry(MediaStreamKind kind, string codec) => kind switch
    {
        MediaStreamKind.Video => codec is "h264" or "hevc",
        MediaStreamKind.Audio => codec is "aac",
        // Subtitles are a FORMAT conversion rather than a container rewrite, and the planner already treats
        // them as droppable — so this carries none and says so rather than dropping them silently.
        _ => false,
    };

    /// <inheritdoc />
    public MediaRemuxerResult Write(Stream source, Stream destination, IMediaStreamConversion? conversion,
                                    CancellationToken cancellationToken = default)
        => Remux(source, destination, conversion, cancellationToken);

    /// <summary>Matroska CodecIDs this can carry into MP4, and the boxes each becomes.</summary>
    private static readonly Dictionary<string, (string Entry, string Config)> CarriableVideo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["V_MPEG4/ISO/AVC"] = ("avc1", "avcC"),
        ["V_MPEGH/ISO/HEVC"] = ("hvc1", "hvcC"),
    };

    /// <summary>
    /// How many bytes the media box's own header needs: the ordinary 8, or 16 for the 64-bit form.
    /// <para>
    /// ⚠ <b>Not a fixed choice, and not circular either — which is the point worth stating.</b> The obvious
    /// simplification is to always write the 64-bit form so the header's length never depends on the size it
    /// announces. But the total media size is known before the sample table is built, so the conditional
    /// costs nothing, and it means an ordinary file gets exactly the header every other muxer writes. A
    /// devkit's output is opened by whatever webview the adopter's user has, and being byte-conventional
    /// where it is free is worth more than the symmetry.
    /// </para>
    /// </summary>
    internal static int MediaHeaderBytesFor(long mediaBytes) =>
        mediaBytes + 8 <= uint.MaxValue ? 8 : 16;

    /// <summary>
    /// Remux <paramref name="sourcePath"/> into <paramref name="destinationPath"/>, overwriting it.
    /// </summary>
    public static MediaRemuxerResult Remux(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        => Remux(sourcePath, destinationPath, conversion: null, cancellationToken);

    /// <summary>
    /// Remux, and TRANSCODE anything MP4 cannot carry using the codecs the app supplies.
    /// <para>
    /// With <paramref name="conversion"/> an AC-3 or DTS film becomes fully playable instead of being refused,
    /// and a picture the webview will not decode becomes H.264 — on a device whose codecs can do it. Where
    /// they cannot, the refusal is unchanged and honest.
    /// </para>
    /// </summary>
    public static MediaRemuxerResult Remux(string sourcePath, string destinationPath,
                                         IMediaStreamConversion? conversion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        try
        {
            using var source = File.OpenRead(sourcePath);
            using var destination = File.Create(destinationPath);
            return Remux(source, destination, conversion, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 🔴 BEFORE THE GENERAL CATCH, and this is the LAST entry point that was missing it (fixed
            // 2026-08-12). Cancellation is not "source or destination unusable": the caller pressed stop, and
            // answering `SourceUnreadable` makes their next move telling the user the video is corrupt. This
            // overload is public and an app that writes its OWN `Convert` delegate reaches it, so a wrong
            // answer here travels all the way to a page as a FAILED event.
            // ⚠ Not on the kit's own default path, and saying otherwise overstated it: `ToConverter` — what
            // `MediaPlayerExtensions` resolves when an app supplies no delegate — always had its own rethrow.
            // That is also why no kit test caught this: the exposure was only ever the app-written delegate.
            // ⚠ It survived the 2026-08-10 pass that fixed the stream overload because the WINDOW was small —
            // the token then reached only a conversion's frame loop, so a copy-only remux could barely be
            // cancelled at all. Making `MatroskaSampleReader.ReadSamples` cancellable widened it to the whole
            // metadata walk, which is the long part of a copy: the same edit that made the feature honest made
            // this defect reachable. `Cancelling_the_PATH_overload_THROWS_rather_than_reporting_an_unusable_file`
            // pins it.
            throw;
        }
        catch (Exception)
        {
            // No exception text travels from here. A media path is exactly the kind of detail that must not
            // reach a page, and the caller already knows which file it asked about.
            return new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "source or destination unusable");
        }
    }

    /// <summary>
    /// Remux one open stream into another. <paramref name="source"/> must be seekable — the sample table has
    /// to be built before the media is copied, so the frames are visited twice.
    /// </summary>
    public static MediaRemuxerResult Remux(Stream source, Stream destination, CancellationToken cancellationToken = default)
        => Remux(source, destination, conversion: null, cancellationToken);

    /// <summary>Remux one open stream into another, transcoding an uncarriable soundtrack when it can.</summary>
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
            // 🔴 CANCELLATION IS NOT A MALFORMED FILE, and this catch reported it as one — the single most
            // misleading answer available, because the caller's next move is to tell the user their video is
            // corrupt. There is deliberately no `Canceled` outcome: cancellation is an EXCEPTION in .NET, the
            // caller already has the token, and inventing an enum member would make every caller handle the
            // same thing twice.
            //
            // ⚠ It was a DEFECT rather than a design, because a neighbouring entry point ALREADY rethrew: the
            // answer you got depended on which one you called. Found by review 2026-08-10.
            // 🔴 And this note USED TO SAY "the file-path overload preserved cancellation", which was true only
            // of a since-deleted PRIVATE write-through helper — the PUBLIC `Remux(string, string, …)` went on
            // swallowing it until 2026-08-12, and a comment asserting the neighbour was already correct is
            // exactly why nobody looked. Both sites in this file rethrow it now, and the correct copy the note
            // was really describing lives on in `IMediaContainerWriter.ToConverter`, which is the path a
            // supplied muxer actually travels.
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
    ///
    /// <para>
    /// 🔴 <b>WHICH write, precisely: the PURE COPY — and for a source this accepts, that is the only write
    /// there is, whatever <see cref="IMediaStreamConversion"/> the caller's writer was configured with.</b>
    /// This is not a caveat, it is the load-bearing consequence of the refusal rule below, so it belongs in
    /// the contract rather than in a comment. A source is only plannable when EVERY stream is carried
    /// untouched; a carriable stream is never offered to a converter (copying beats converting whenever both
    /// are possible); so the conversion cannot reach a plannable source and the bytes are identical with or
    /// without one. ⚠ Weaken the refusal rule to "only refuse what needs re-encoding" and this breaks
    /// immediately: an H.264 + AC-3 film would plan at video-only length and then be WRITTEN longer once a
    /// device transcoded the AC-3 — the silent <c>Content-Range</c> failure this whole type exists to
    /// prevent. There is a test asserting the identity holds with a conversion supplied.
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>Why this exists: it is what makes a repaired container servable as ONE ORDINARY
    /// <c>&lt;video src&gt;</c>.</b> A remux copies frames into a different box, so the output is fully
    /// determined by the source's frame index — sizes, timestamps and keyframe flags — and therefore
    /// knowable before any work. A route holding one of these can answer a 206 with a real
    /// <c>Content-Range</c> total, and can serve a seek to the END of the film cold, without having produced
    /// the byte before it. That is the D71 line between this path and segments, and every mobile failure
    /// measured that day failed for want of a SIZE.
    /// ⚠ <b>"on the very first request" was true of the route until 2026-08-13 and is not any more</b>, which
    /// changes nothing here and everything for a page: <see cref="ComputedRemuxExtensions.UseComputedRemux"/>
    /// runs THIS METHOD in a mission — the walk below is far too expensive for a webview's resource thread —
    /// and answers <c>503 Retry-After: 1</c> until it lands. The total is knowable before any work; it is not
    /// knowable before the walk.
    /// </para>
    ///
    /// <para>
    /// ⚠ <b><c>null</c> is a ROUTING ANSWER, not an error, and it is the reason this returns a nullable
    /// instead of throwing.</b> It means "this source does not belong on the computed path": unreadable,
    /// not Matroska, no stream MP4 can carry, a carriable stream with no decoder configuration, or — the
    /// interesting case — <b>anything the output would LOSE</b>. A stream needing a re-encode, a second dub
    /// the first-of-each-kind selection leaves behind, a track that declares itself and holds no frames:
    /// all three are refusals here, and none of them is a refusal for the writer, whose job is the best
    /// playable file it can make and which REPORTS what it dropped. A layout has no such channel — it is a
    /// length and a byte map — so a plan that lost the soundtrack would be served as a 200 with a perfect
    /// <c>Content-Range</c> and no way to explain the silence. Declining sends the source to segments,
    /// where a re-encoder can help and the loss is still reportable.
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>COST, and the number that matters is the PEAK rather than the result.</b> One walk of the
    /// source's clusters (metadata, never payloads). What comes BACK is ~24 bytes per sample across both
    /// tracks — a two-hour film is ~216,000 video frames at 30 fps plus ~337,000 AAC frames at 48 kHz, so
    /// ~553,000 spans ≈ <b>13 MB</b>. What is LIVE AT ONCE while computing it is far more: the reader's own
    /// per-frame records, the timing pass's copy of them (the original stays reachable through the track),
    /// five <c>long[]</c> per track, the write order at 40 bytes an entry plus the sort's buffer, the spans,
    /// and three whole <c>moov</c> copies — <b>on the order of 110–150 MB</b> for that same film. That is
    /// arithmetic from the struct layouts rather than a measurement, so treat the multiplier as approximate
    /// and the direction as certain.
    /// </para>
    ///
    /// <para>
    /// ⚠ <b>Which makes <c>MatroskaSampleReader</c>'s four-million-sample ceiling the real budget, and it is
    /// roughly a GIGABYTE through here.</b> That bound was written for a remux an app asks for; this is the
    /// method a ROUTE calls on a file a PAGE named. Cache the result against the source's identity, never
    /// rebuild it per range request, and do not plan two films at once on a phone.
    /// See <see cref="Mp4Layout"/>.
    /// </para>
    /// </summary>
    /// <param name="source">The Matroska source. Must be seekable — the frame index comes from a full walk.</param>
    /// <param name="cancellationToken">
    /// Observed once per cluster, inside the walk — so a plan of a multi-gigabyte source stops when the
    /// caller does. ⚠ Cancelling THROWS; it never comes back as <c>null</c>, because null means "send this
    /// source to the segment path" and a route must not reroute a film because someone navigated away.
    /// </param>
    /// <returns>The output's layout, or <c>null</c> when this source cannot be described this way.</returns>
    public static Mp4Layout? Plan(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        PreparedRemux? prepared;
        try
        {
            // `lossless: true` is the whole difference from a write: nothing the source offered may be left
            // behind, because a plan describes an output and has no channel for what it cost.
            // ⚠ The refusal REASON is discarded here and is the one thing this signature cannot carry. It is
            // built anyway, and kept specific, because the route that will consult this wants it in the host
            // log — "why did this film go to segments?" is the first question a slow playback raises.
            (prepared, _) = Prepare(source, conversion: null, lossless: true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not "this file cannot be planned" — the caller pressed stop and already holds
            // the token. Answering null here would tell a route to fall back to segments because someone
            // navigated away. The same distinction `Remux` draws, for the same reason.
            throw;
        }
        catch (Exception)
        {
            // A malformed source is unplannable exactly as it is unremuxable, and no exception text travels
            // from here: a media path is the kind of detail that must not reach a page.
            return null;
        }

        if (prepared is null) return null;

        try
        {
            // Unreachable by construction — `conversion: null` means nothing was spooled — and checked
            // rather than assumed, because every span below claims to address the SOURCE. A converted
            // track's bytes live in a temp file this method neither owns nor could hand out, so a layout
            // covering one would point a route at offsets in a file that is about to be deleted.
            if (prepared.Tracks.Exists(track => track.ByteSource is not null)) return null;

            var composed = ComposeHeader(prepared.Tracks, prepared.WriteOrder);
            if (composed is null) return null;
            var (header, mediaBytes) = composed.Value;

            // The SAME order the copy loop uses, walked once to turn each frame's source position into an
            // output position. Contiguous by construction: `mdat` is the frames back to back, so the running
            // total IS the next frame's offset, and the last one lands exactly on the total length.
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
        // Every refusal inside `Prepare` supplies a result, so the fallback is unreachable — it is here
        // rather than a `!` because the alternative failure is a NullReferenceException at the caller, far
        // from the branch that forgot one.
        if (prepared is null) return refused ?? new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "the source could not be prepared");

        try
        {
            try
            {
                return Write(source, destination, prepared, cancellationToken) with { Dropped = prepared.Dropped };
            }
            catch (OperationCanceledException)
            {
                // 🔴 BEFORE THE GENERAL CATCH, or the kit answers a CANCELLED remux with "the output could
                // not be written" — telling the caller their disk failed when in fact they pressed stop. The
                // write loop checks the token between frames precisely so cancellation is clean; swallowing
                // it here threw that away and replaced it with a diagnosis that is not merely useless but
                // WRONG.
                throw;
            }
            catch (Exception)
            {
                return new MediaRemuxerResult(MediaRemuxerOutcome.DestinationUnwritable, "the output could not be written");
            }
        }
        finally
        {
            // 🔴 THE CONVERTED TRACK'S SPOOL, on every path out of here. It is a temp file opened
            // `DeleteOnClose`, so until this runs the bytes of a whole soundtrack sit on the device's disk
            // with no name anyone can find. A COPIED track has none — those read from `source`, which this
            // method does not own and must not close.
            prepared.Dispose();
        }
    }

    /// <summary>
    /// Everything that must be known before a byte can be written: which streams survive, each one's sample
    /// table, and the exact order the frames go out in.
    ///
    /// <para>
    /// 🔴 <b>Shared by the write path and by <see cref="Plan"/>, which is the entire reason it is a method
    /// rather than the top of <c>Run</c>.</b> A plan that selected its tracks, resolved its timing or
    /// interleaved its chunks even slightly differently from the write would describe a file the write does
    /// not produce. The symptom of that is not a crash: it is a <c>Content-Range</c> total the bytes do not
    /// honour, and a media element's failure for THAT is silent — a blank picture with no error. One
    /// pipeline, two consumers, no room for them to agree only by coincidence.
    /// </para>
    /// </summary>
    /// <param name="source">The Matroska source, seekable — the frame index comes from walking it.</param>
    /// <param name="conversion">The app's codecs, or null for a pure copy.</param>
    /// <param name="lossless">
    /// When true, ANY stream the source offered that the output would not carry is a REFUSAL rather than a
    /// drop — checked twice, cheaply on carriability before the clusters are walked and authoritatively on
    /// the <c>Dropped</c> set afterwards, since only the walk reveals a track that holds no frames.
    /// <para>
    /// That is <see cref="Plan"/>'s rule and deliberately not the writer's. The writer's job is to produce
    /// the best playable file it can, so it carries the picture and REPORTS the AC-3 soundtrack it had to
    /// leave behind. A plan has no such channel — it is a length and a byte map — so the same output served
    /// off a layout is a silent film nobody can explain. ⚠ Only ever passed <c>true</c> with
    /// <paramref name="conversion"/> null: with a converter in play "carriable" is the wrong question, since
    /// the converter is exactly what would rescue the stream.
    /// </para>
    /// </param>
    /// <param name="cancellationToken">
    /// Observed in two places, and both are needed: once per cluster inside the metadata walk (the long part
    /// of a COPY-only remux) and between frames inside a conversion (the long part of everything else).
    /// </param>
    private static (PreparedRemux? Ready, MediaRemuxerResult? Refused) Prepare(
        Stream source, IMediaStreamConversion? conversion, bool lossless, CancellationToken cancellationToken)
    {
        if (!source.CanSeek) return (null, new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "source is not seekable"));

        var reader = new MatroskaSampleReader(source);
        if (!reader.ReadHeader()) return (null, new MediaRemuxerResult(MediaRemuxerOutcome.NotMatroska, "no Matroska header or no tracks"));

        // ⚠ THE CHEAP PRE-FILTER, NOT THE RULE — the authoritative check is the `dropped` one further down,
        // and this exists only so the COMMON refusal costs a header parse instead of a full metadata pass
        // over a multi-gigabyte source. The streaming route asks this question of every file it serves, and
        // an AC-3 film is the answer it gets most often. Anything this misses, the one below catches.
        // ⚠ It sweeps `reader.Tracks`, which holds only picture and sound — the reader drops subtitles at
        // parse time, on the planner's own droppable rule. That is load-bearing rather than incidental: a
        // subtitle track can never be carried, so counting one here would make almost every real film
        // unplannable.
        if (lossless && reader.Tracks.Any(track => !Carriable(track)))
        {
            var uncarriable = string.Join(" + ", reader.Tracks.Where(t => !Carriable(t)).Select(t => $"{t.Kind}:{t.CodecId ?? "?"}"));
            return (null, new MediaRemuxerResult(MediaRemuxerOutcome.NoCarriableStream,
                $"a computed output needs every stream carriable; these would need re-encoding: {uncarriable}"));
        }

        // ── choose the streams ────────────────────────────────────────────────────────────────────────
        // The first of each kind MP4 can carry. Deliberately not "every track": a film with four dubs would
        // otherwise produce an output four soundtracks wide, and a webview plays one.
        var video = reader.Tracks.FirstOrDefault(t => t.Kind == MediaStreamKind.Video && CanCarryVideo(t));
        var audio = reader.Tracks.FirstOrDefault(t => t.Kind == MediaStreamKind.Audio && CanCarryAudio(t));

        // Copying beats converting whenever both are possible: it is faster, lossless, and cannot fail
        // halfway. Only when NO carriable soundtrack exists is a convertible one worth reaching for.
        // ONE selection for both kinds — the picture is chosen on exactly the terms the soundtrack is, which
        // is what "the same path" means in practice. Measured 2026-08-10, the video case is `mpeg4`: the
        // device decodes it and its own webview refuses it, so the page gets sound and a blank picture with
        // NO error at all.
        (MatroskaTrack? Track, string? Codec) Choose(MediaStreamKind kind, MatroskaTrack? carriable)
        {
            if (carriable is not null || conversion is null) return (null, null);
            foreach (var track in reader.Tracks.Where(t => t.Kind == kind))
            {
                // Through the VfW wrapper: an h263 track has no native Matroska id, so without the
                // private data its codec name is "vfw" and this converter declines a codec it offers.
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
        // ⚠ The token reaches the WALK, not just the conversion below. The walk is the long part of a
        // copy-only remux — every cluster in the file — and a `Plan` from a range route runs it inside a web
        // request, so a client that disconnects must stop paying for it.
        if (!reader.ReadSamples(wanted, cancellationToken))
        {
            return (null, new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "malformed or unbounded clusters"));
        }

        if (plans.All(p => p.Source.Samples.Count == 0) && (convert is null || convert.Samples.Count == 0)
            && (convertVideo is null || convertVideo.Samples.Count == 0))
        {
            return (null, new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "the file declares tracks but holds no frames"));
        }

        // ⚠ THE SCOPE OF THIS `try` IS THE SPOOL'S LIFETIME — everything from the first Convert to the
        // moment the prepared set is handed over. Every exit that is not that hand-over closes the spools,
        // which is what `handedOver` buys over a plain `catch`: an early RETURN leaks nothing either.
        // 🔴 That was a live defect until 2026-08-12. The guard used to start AFTER both Convert blocks, so
        // "the audio converted, then the video conversion failed" returned a refusal with the audio spool
        // still open — a temp file holding a whole soundtrack, `DeleteOnClose`, with no name anyone can
        // find. It survived because the path needs two conversions in one file and the second to fail.
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
                    // 🔴 A REFUSAL, never a silent drop. A file whose picture we accepted and then failed to
                    // produce is not "audio only" — it is the wrong file, and the route turns this into a FAILED
                    // event naming the codec rather than caching something that plays black.
                    return (null, new MediaRemuxerResult(MediaRemuxerOutcome.NoCarriableStream,
                        $"the device could not convert video {convertVideoCodec} after accepting it"));
                }
                resolved.Add(converted);
            }

            if (resolved.Count == 0) return (null, new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "no frames for any carriable track"));

            var writeOrder = Interleave(resolved);

            // ── what did NOT survive ──────────────────────────────────────────────────────────────────
            // Computed HERE because this is the only place that knows both what the file offered and what
            // was chosen. A successful remux that dropped the soundtrack is the kit's most dangerous
            // outcome — nothing throws, the file plays, and the user hears silence — so the result must be
            // able to say so even though it still says Succeeded.
            // ⚠ `resolved` ALREADY carries the converted track — Convert() returns a plan whose Source is
            // that very MatroskaTrack — so the chosen set is exactly this, with nothing to add. An extra
            // `kept.Add(convert.Number)` used to sit here and was wrong on the one path where it did
            // anything: a convertible track that declared ZERO frames skips the Convert block above,
            // contributes no plan, and was then marked kept anyway — so the output had no soundtrack and
            // `Dropped` said everything survived. Exactly the silent-film outcome this block exists to make
            // reportable, and the copy path one branch up already handled the same case correctly.
            var kept = new HashSet<ulong>(resolved.Select(r => r.Source.Number));
            var dropped = reader.Tracks
                .Where(t => !kept.Contains(t.Number))
                // ⚠ Through the wrapper HERE TOO, and this is the half a page reads: `dropped:["vfw"]`
                // named a container convention rather than a codec, so no app could act on it.
                .Select(t => MatroskaProbe.CodecNameOf(t.CodecId, t.CodecPrivate ?? ReadOnlyMemory<byte>.Empty)
                             ?? t.CodecId ?? "unknown")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // 🔴 THE AUTHORITATIVE HALF OF THE LOSSLESS RULE, and the only one that is complete.
            // Carriability is not the whole question: the first-of-each-kind selection leaves a SECOND dub
            // behind, and a track that declares itself and holds no frames drops itself. Neither needs
            // re-encoding and both leave an output MISSING a stream the source offered — and unlike
            // `MediaRemuxerResult.Dropped`, an `Mp4Layout` has no channel to say so. A computed remux that
            // quietly lost the soundtrack would be served as a 200 with a perfect `Content-Range`: the
            // silent film this file keeps warning about, now cached and unexplainable.
            // ⚠ Refusing costs the fast path and buys an honest one — the segment route can still produce
            // the file AND report what it could not carry. It also matches the policy one layer up, where a
            // conversion that drops a stream already reports FAILED and caches nothing rather than serving
            // silence.
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
    /// <para>
    /// ⚠ <b>It OWNS any conversion spool</b> — a converted track's bytes live in a temp file opened
    /// <c>DeleteOnClose</c>, so whoever holds one of these must dispose it or leak a whole soundtrack onto a
    /// phone's disk with no name anyone can find.
    /// </para>
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
    /// Whether the output could carry this track's frames UNTOUCHED — the question a computed output rests
    /// on, since a re-encode has no derivable size.
    /// <para>
    /// Deliberately asked of the raw Matroska CodecID through the same two predicates the selection uses,
    /// rather than of <see cref="CanCarry"/>'s translated codec name: those are what actually decide whether
    /// a track is copied, and a second spelling of the same question is how the plan and the write come to
    /// disagree about one file.
    /// </para>
    /// </summary>
    private static bool Carriable(MatroskaTrack track) => track.Kind switch
    {
        MediaStreamKind.Video => CanCarryVideo(track),
        MediaStreamKind.Audio => CanCarryAudio(track),
        _ => false,
    };

    private static bool CanCarryVideo(MatroskaTrack track) =>
        track.CodecId is not null && CarriableVideo.ContainsKey(track.CodecId);

    /// <summary>AAC, in any of the profile-qualified spellings Matroska uses (<c>A_AAC/MPEG4/LC</c>).</summary>
    private static bool CanCarryAudio(MatroskaTrack track) =>
        track.CodecId is not null
        && (track.CodecId.Equals("A_AAC", StringComparison.OrdinalIgnoreCase)
            || track.CodecId.StartsWith("A_AAC/", StringComparison.OrdinalIgnoreCase));

    private static byte[]? BuildVideoEntry(MatroskaTrack track)
    {
        if (track.CodecPrivate is not { Length: > 0 } config) return null;
        var (entry, configBox) = CarriableVideo[track.CodecId!];

        // A zero dimension makes a track a player lays out as nothing. Fall back to a sane frame rather
        // than writing a file that decodes into a window with no area.
        var width = track.Width > 0 ? track.Width : 0;
        var height = track.Height > 0 ? track.Height : 0;
        if (width == 0 || height == 0) return null;

        return Mp4Builder.VisualSampleEntry(entry, configBox, width, height, config);
    }

    private static byte[]? BuildAudioEntry(MatroskaTrack track)
    {
        var channels = track.Channels > 0 ? track.Channels : 2;
        var rate = track.SampleRate > 0 ? track.SampleRate : 48000;

        // A real file ships its own AudioSpecificConfig and it is copied untouched; synthesising one is the
        // fallback for a track that shipped none, and it refuses rather than guess a rate AAC cannot index.
        var config = track.CodecPrivate is { Length: > 0 } shipped
            ? shipped
            : Mp4Builder.SynthesiseAacConfig(rate, channels);
        if (config is null) return null;

        return Mp4Builder.AudioSampleEntry(channels, rate, config);
    }

    /// <summary>
    /// Turn one track's frame list into a decode timeline on a timescale MP4 can hold.
    /// </summary>
    private static Mp4TrackPlan Resolve(Mp4TrackPlan pending, long timestampScaleNs)
    {
        var samples = pending.Source.Samples.ToArray();

        // Prefer the timescale that expresses the source's own ticks EXACTLY — for the 1 ms scale every real
        // file uses, that is a clean 1000. Only an unusual scale falls back to milliseconds, and rounding
        // there is what would otherwise drift picture against sound over an hour.
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
    /// Decide the order frames are written in, fill in the chunk tables, and RETURN that order.
    ///
    /// <para>
    /// Source order is kept, which is already interleaved — Matroska clusters carry picture and sound
    /// together for exactly the reason MP4 wants them together, so a player reading forward finds both
    /// without seeking. A chunk is one unbroken run of the same track, which is what the interleaving
    /// already produces.
    /// </para>
    /// <para>
    /// 🔴 <b>The order is computed ONCE and handed on, rather than recomputed where the bytes are copied,
    /// and that is the point of returning it.</b> The chunk table says where each run of frames will be;
    /// the copy loop puts them there. Those are the same list, and deriving it twice — even from the same
    /// rule — makes them two lists that merely agree today. Any later edit to one ordering (a different
    /// interleave, a stable-sort tie-break, a filter) silently desynchronises the file from its own index,
    /// and the result is not a crash: it is a file that parses perfectly and decodes garbage.
    /// </para>
    /// </summary>
    private static WriteItem[] Interleave(List<Mp4TrackPlan> tracks)
    {
        // Ordered by DECODE TIME, not by position in the source.
        //
        // ⚠ It used to sort on the source offset, which worked only because every track's bytes came from
        // the same file. A CONVERTED track's bytes live in a spool with offsets of its own, so comparing
        // them against the source's is comparing two unrelated numbers — and the result is a file whose
        // chunks are ordered by nothing. Time is what interleaving actually means, it is what a player
        // reading forward needs, and for a copy-only remux it produces the same order as before because
        // Matroska clusters are already time-ordered.
        //
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
    ///
    /// <para>
    /// 🔴 <b>THE CIRCULARITY, AND THE ONE PLACE IT IS RESOLVED.</b> The chunk-offset table holds ABSOLUTE
    /// positions, so the sample table's CONTENTS depend on where the media starts, which depends on how long
    /// the header is, which depends on the sample table. The way out is a fixed-WIDTH table — Mp4Builder
    /// writes <c>co64</c> always and never <c>stco</c> for exactly this reason, see its remarks — so
    /// building once with a media start of zero learns the length, and building again with the real value
    /// produces something byte-for-byte as long.
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>Both the write path and <see cref="Plan"/> call THIS, and nothing may re-derive it.</b> A
    /// planned header one byte longer or shorter than the written one moves every chunk offset after it, and
    /// the file that results parses perfectly and decodes garbage — while its total LENGTH, the one number a
    /// range request advertises, can still match. Two implementations of one calculation is how they come to
    /// disagree; there is deliberately only one.
    /// </para>
    /// </summary>
    private static (byte[] Bytes, long MediaBytes)? ComposeHeader(IReadOnlyList<Mp4TrackPlan> tracks, WriteItem[] writeOrder)
    {
        var ftyp = Mp4Builder.Ftyp();
        var mediaBytes = writeOrder.Sum(item => (long)item.Sample.Length);
        var mediaHeaderBytes = MediaHeaderBytesFor(mediaBytes);

        // Built twice on purpose: the first tells us how long it is, which is what decides where the media
        // starts, which is what the second one has to state.
        var sizing = Mp4Builder.Moov(tracks, 0);
        var mediaStart = ftyp.Length + sizing.Length + mediaHeaderBytes;
        var moov = Mp4Builder.Moov(tracks, mediaStart);

        // Unreachable by construction, and checked rather than assumed: if it ever fires, every chunk
        // offset in the file is wrong by the difference and the output would be silently unplayable.
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
    /// Copy every frame in the order the chunk table promised — <paramref name="writeOrder"/> IS that
    /// order, handed over rather than re-derived (see <see cref="Interleave"/>).
    /// <para>
    /// Ascending source position, so the read is sequential across the whole file however the tracks are
    /// interleaved — which matters on a phone, where a seek per frame is the difference between a remux
    /// that keeps up with playback and one that does not.
    /// </para>
    /// </summary>
    private static void CopySamples(Stream source, Stream destination, List<Mp4TrackPlan> tracks,
                                    WriteItem[] writeOrder, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        foreach (var item in writeOrder)
        {
            // Each track says where ITS bytes are: the source file for a copied track, a spool for a
            // converted one. Reading everything from `source` is the bug this indirection exists to stop,
            // and it would produce a file full of the wrong bytes at plausible-looking offsets.
            var from = tracks[item.Track].ByteSource ?? source;
            var sample = item.Sample;
            // Between frames, not mid-frame: a partial frame would leave the output inconsistent with the
            // sample table already written, and the caller discards the whole file on cancellation anyway.
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
    /// Run one audio track through the device's codecs and spool the result, returning a plan whose bytes
    /// live in the spool rather than in the source.
    ///
    /// <para>
    /// <b>Timing is taken from the ENCODER, not from the source, and that is the whole reason this is not
    /// just "convert the bytes".</b> A decoder may resample and a downmix may change the channel count, so
    /// the output's frames do not line up with the input's at all. What IS exact is that every output frame
    /// carries <see cref="IMediaStreamConversionRun.OutputFramesPerPacket"/> samples at the rate on
    /// <see cref="IMediaStreamConversionRun.OutputFormat"/> — so the timescale is the sample rate, each
    /// frame lasts one packet, and the table is exact by construction instead of being re-derived from
    /// timestamps that no longer apply. ⚠ <b>That reasoning is AUDIO's alone</b>: a picture's frames are
    /// timed individually, so the video branch derives its timeline from what the encoder stamped instead.
    /// </para>
    /// <para>
    /// ⚠ Spooled to a TEMPORARY FILE, deleted on close. A two-hour soundtrack is ~115 MB as AAC and this
    /// runs on phones; holding it would be the kind of allocation that works on every test file and dies on
    /// a real one.
    /// </para>
    /// </summary>
    private static Mp4TrackPlan? Convert(Stream source, MatroskaTrack track, string codec, MediaStreamKind kind,
                                         IMediaStreamConversion conversion, long timestampScaleNs,
                                         CancellationToken cancellationToken)
    {
        // Everything the platform codec must be configured with, from what Matroska declared. A codec told
        // the wrong values does not fail: audio plays at the wrong SPEED, a picture comes out stretched or
        // green, and one told no CodecPrivate produces silence or nothing for the codecs that need it.
        //
        // 🔴 ONE call for both kinds, which is the whole point of the unified seam (owner, 2026-08-12:
        // "both audio and video should take the same path, the difference is encoding and decoding logic").
        // The fields that do not apply to a kind are simply null — MediaStreamInfo has always been
        // best-effort, so a video stream with no channel count is an ordinary value rather than a special case.
        using var run = conversion.Begin(
            new MediaStreamInfo(kind, codec,
                Channels: track.Channels > 0 ? track.Channels : null,
                SampleRate: track.SampleRate > 0 ? (int)Math.Round(track.SampleRate) : null,
                Width: track.Width > 0 ? track.Width : null,
                Height: track.Height > 0 ? track.Height : null,
                FrameRate: track.DefaultDurationNs > 0 ? 1_000_000_000d / track.DefaultDurationNs : null),
            track.CodecPrivate ?? ReadOnlyMemory<byte>.Empty);
        if (run is null) return null;

        // 🔴 THE SPOOL OUTLIVES THIS METHOD ON THE SUCCESS PATH — it becomes the plan's `ByteSource`, which
        // is why it cannot be a `using`. Every OTHER path has to close it by hand, and until 2026-08-10
        // exactly one did (the "produced nothing" return below). That was survivable only because the
        // transcode had NEVER PRODUCED BYTES: with `samples` always empty, the disposing path was the only
        // one that ever ran. Fixing the transcode (da01f1e) is what made every other path reachable, so a
        // dormant leak became a live one in the commit that made the feature work — and nothing failed,
        // because a leaked handle is invisible until the device runs out.
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
                    // below, per kind. The keyframe flag is passed straight through — inventing it is what
                    // makes a seek land on a green smear.
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
                // Zero outputs is NORMAL — codecs buffer. Treating an empty return as failure would abandon
                // every conversion in its first few frames.
                // The source's ticks are the file's own scale; the seam speaks microseconds on both sides.
                Emit(run.Push(new MediaFrame(frame.AsMemory(0, sample.Length),
                                             sample.Ticks * timestampScaleNs / 1_000, sample.KeyFrame)));
            }

            // 🔴 Without this the tail stays inside the codec and the soundtrack simply stops early, in a file
            // that is otherwise perfectly well-formed.
            Emit(run.Drain());

            var config = run.OutputConfig;
            if (samples.Count == 0 || config.Length == 0) { spool.Dispose(); return null; }

            // ── the ONE place the kinds differ, and it is the difference the owner named: the codec's own
            // model of time. Audio frames are a fixed number of samples each, so the timeline is arithmetic
            // and every frame is a sync sample. Video frames are timed individually and only some can be
            // decoded alone, so the timeline is DERIVED from what the encoder stamped on them.
            var format = run.OutputFormat;
            uint timescale;
            long[] decode, composition, durations;
            long shift;
            byte[] entry;

            if (kind is MediaStreamKind.Video)
            {
                // Microseconds, matching MediaFrame 1:1, so nothing rounds between the encoder and the file.
                timescale = 1_000_000;
                // 🔴 The SAME derivation the copy path uses. Hand-rolling it here first produced composition
                // offsets that went NEGATIVE — which `ctts` version 0 stores UNSIGNED, so the file would have
                // carried a vast positive offset and played wrong while parsing perfectly. `SampleTiming`
                // already solves it by shifting the presentation and reporting the shift, which the builder
                // cancels with an edit list. Two implementations of one calculation is how they come to
                // disagree; this one lasted about ten minutes.
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
            // CANCELLATION LANDS HERE, and it is much the likeliest of these paths: a transcode is the
            // longest thing this kit does, so it is the thing a user actually cancels. A device codec
            // throwing lands here too. `DeleteOnClose` means the temp file lives exactly as long as the
            // handle, so leaking the handle leaks a FILE — on a phone, once per cancelled conversion.
            spool.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A track chosen but not yet timed. Only exists so selection can fail EARLY — a missing decoder
    /// configuration is worth reporting before walking a multi-gigabyte file to find its frames.
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
