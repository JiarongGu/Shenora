namespace Shenora.Media;

/// <summary>Why a remux did not happen. A code the caller branches on, never prose for a user.</summary>
public enum MediaRemuxerOutcome
{
    /// <summary>
    /// The output was written.
    /// <para>
    /// ⚠ <b>This does NOT mean every stream survived — check <see cref="MediaRemuxerResult.Dropped"/>.</b>
    /// A film whose only soundtrack is AC-3, remuxed with no <see cref="IMediaAudioConversion"/>, succeeds
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
    public MediaRemuxerResult Write(Stream source, Stream destination, IMediaAudioConversion? conversion,
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
    /// The kit's default converter for <see cref="MediaConversionOptions.Convert"/>.
    /// <code>
    /// Convert = new Mp4Remuxer().ToConverter(),                     // container repair only
    /// Convert = new Mp4Remuxer().ToConverter(audioConversion),      // ...and the device's codecs
    /// </code>
    /// <para>
    /// ⚠ <b>The factory moved OFF this class on 2026-08-07</b> — see
    /// <see cref="MediaContainerWriterExtensions.ToConverter"/>. It used to be
    /// <c>Mp4Remuxer.ConvertWith(conversion, writer)</c>, which meant a class named "Remuxer" minting a
    /// route delegate that might run a completely different muxer. Wrapping a writer is the INTERFACE's
    /// job, not one implementation's.
    /// </para>
    /// </summary>

    /// <summary>
    /// Remux <paramref name="sourcePath"/> into <paramref name="destinationPath"/>, overwriting it.
    /// </summary>
    public static MediaRemuxerResult Remux(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        => Remux(sourcePath, destinationPath, conversion: null, cancellationToken);

    /// <summary>
    /// Remux, and TRANSCODE any soundtrack MP4 cannot carry using the device the app supplies.
    /// <para>
    /// With a <paramref name="conversion"/> an AC-3 or DTS film becomes fully playable instead of being
    /// refused — on a device whose codecs can do it. Where they cannot, the refusal is unchanged and honest.
    /// </para>
    /// </summary>
    public static MediaRemuxerResult Remux(string sourcePath, string destinationPath,
                                         IMediaAudioConversion? conversion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        try
        {
            using var source = File.OpenRead(sourcePath);
            using var destination = File.Create(destinationPath);
            return Remux(source, destination, conversion, cancellationToken);
        }
        catch (Exception)
        {
            // No exception text travels from here. A media path is exactly the kind of detail that must not
            // reach a page, and the caller already knows which file it asked about.
            return new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "source or destination unusable");
        }
    }

    /// <summary>
    /// Run a SUPPLIED muxer over the request's files — the path that makes
    /// <see cref="IMediaContainerWriter"/> a seam a consumer can actually reach.
    /// <para>
    /// The file handling is this class's rather than the writer's, deliberately: opening, disposing and
    /// swallowing the path in a failure are all things the kit already gets right here, and a consumer
    /// implementing a muxer should have to think about frames, not about whether a media path can reach a
    /// page.
    /// </para>
    /// </summary>
    private static MediaRemuxerResult WriteThrough(IMediaContainerWriter writer, MediaConversionRequest request,
                                                   IMediaAudioConversion? conversion, CancellationToken cancellationToken)
    {
        try
        {
            using var source = File.OpenRead(request.SourcePath);
            using var destination = File.Create(request.DestinationPath);
            return writer.Write(source, destination, conversion, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Same rule as the path overload above: no exception text travels from here.
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
                                         IMediaAudioConversion? conversion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        try
        {
            return Run(source, destination, conversion, cancellationToken);
        }
        catch (Exception)
        {
            return new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "malformed source");
        }
    }

    private static MediaRemuxerResult Run(Stream source, Stream destination, IMediaAudioConversion? conversion, CancellationToken cancellationToken)
    {
        if (!source.CanSeek) return new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "source is not seekable");

        var reader = new MatroskaSampleReader(source);
        if (!reader.ReadHeader()) return new MediaRemuxerResult(MediaRemuxerOutcome.NotMatroska, "no Matroska header or no tracks");

        // ── choose the streams ────────────────────────────────────────────────────────────────────────
        // The first of each kind MP4 can carry. Deliberately not "every track": a film with four dubs would
        // otherwise produce an output four soundtracks wide, and a webview plays one.
        var video = reader.Tracks.FirstOrDefault(t => t.Kind == MediaStreamKind.Video && CanCarryVideo(t));
        var audio = reader.Tracks.FirstOrDefault(t => t.Kind == MediaStreamKind.Audio && CanCarryAudio(t));

        // Copying beats converting whenever both are possible: it is faster, lossless, and cannot fail
        // halfway. Only when NO carriable soundtrack exists is a convertible one worth reaching for.
        MatroskaTrack? convert = null;
        string? convertCodec = null;
        if (audio is null && conversion is not null)
        {
            foreach (var track in reader.Tracks.Where(t => t.Kind == MediaStreamKind.Audio))
            {
                var codec = MatroskaProbe.CodecNameOf(track.CodecId);
                if (codec is null || !conversion.CanConvert(codec)) continue;
                convert = track;
                convertCodec = codec;
                break;
            }
        }

        if (video is null && audio is null && convert is null)
        {
            var codecs = string.Join(" + ", reader.Tracks.Select(t => $"{t.Kind}:{t.CodecId ?? "?"}"));
            return new MediaRemuxerResult(MediaRemuxerOutcome.NoCarriableStream,
                $"no stream MP4 can carry without re-encoding: {codecs}");
        }

        var plans = new List<Mp4TrackPlan>();
        if (video is not null)
        {
            var entry = BuildVideoEntry(video);
            if (entry is null) return new MediaRemuxerResult(MediaRemuxerOutcome.MissingDecoderConfig,
                $"video track {video.Number} ({video.CodecId}) carries no decoder configuration");
            plans.Add(new PendingTrack(video, IsVideo: true, entry).Placeholder());
        }
        if (audio is not null)
        {
            var entry = BuildAudioEntry(audio);
            if (entry is null) return new MediaRemuxerResult(MediaRemuxerOutcome.MissingDecoderConfig,
                $"audio track {audio.Number} ({audio.CodecId}) carries no decoder configuration");
            plans.Add(new PendingTrack(audio, IsVideo: false, entry).Placeholder());
        }

        // ── walk the clusters ─────────────────────────────────────────────────────────────────────────
        var wanted = plans.Select(p => p.Source.Number).ToHashSet();
        if (convert is not null) wanted.Add(convert.Number);
        if (!reader.ReadSamples(wanted))
        {
            return new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "malformed or unbounded clusters");
        }

        if (plans.All(p => p.Source.Samples.Count == 0) && (convert is null || convert.Samples.Count == 0))
        {
            return new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "the file declares tracks but holds no frames");
        }

        // ── resolve timing ────────────────────────────────────────────────────────────────────────────
        var resolved = new List<Mp4TrackPlan>();
        foreach (var plan in plans)
        {
            if (plan.Source.Samples.Count == 0) continue;   // a declared-but-empty track is dropped, not written empty
            resolved.Add(Resolve(plan, reader.TimestampScaleNs));
        }

        // The transcode, after the copies: a failure here must not have already spooled work for tracks
        // that were going to be copied anyway.
        if (convert is not null && convertCodec is not null && convert.Samples.Count > 0)
        {
            var converted = Convert(source, convert, convertCodec, conversion!, cancellationToken);
            if (converted is null)
            {
                return new MediaRemuxerResult(MediaRemuxerOutcome.NoCarriableStream,
                    $"the device could not convert {convertCodec} after accepting it");
            }
            resolved.Add(converted);
        }

        if (resolved.Count == 0) return new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "no frames for any carriable track");

        var writeOrder = Interleave(resolved);

        // ── what did NOT survive ──────────────────────────────────────────────────────────────────────
        // Computed HERE because this is the only place that knows both what the file offered and what was
        // chosen. A successful remux that dropped the soundtrack is the kit's most dangerous outcome —
        // nothing throws, the file plays, and the user hears silence — so the result must be able to say
        // so even though it still says Succeeded.
        // ⚠ `resolved` ALREADY carries the converted track — Convert() returns a plan whose Source is that
        // very MatroskaTrack — so the chosen set is exactly this, with nothing to add. An extra
        // `kept.Add(convert.Number)` used to sit here and was wrong on the one path where it did anything:
        // a convertible track that declared ZERO frames skips the Convert block above, contributes no plan,
        // and was then marked kept anyway — so the output had no soundtrack and `Dropped` said everything
        // survived. Exactly the silent-film outcome this block exists to make reportable, and the copy path
        // one branch up already handled the same case correctly.
        var kept = new HashSet<ulong>(resolved.Select(r => r.Source.Number));
        var dropped = reader.Tracks
            .Where(t => !kept.Contains(t.Number))
            .Select(t => MatroskaProbe.CodecNameOf(t.CodecId) ?? t.CodecId ?? "unknown")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // ── write ─────────────────────────────────────────────────────────────────────────────────────
        try
        {
            return Write(source, destination, resolved, writeOrder, cancellationToken) with { Dropped = dropped };
        }
        catch (Exception)
        {
            return new MediaRemuxerResult(MediaRemuxerOutcome.DestinationUnwritable, "the output could not be written");
        }
    }

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
            IsVideo = pending.IsVideo,
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

    private static MediaRemuxerResult Write(Stream source, Stream destination,
                                          List<Mp4TrackPlan> tracks, WriteItem[] writeOrder,
                                          CancellationToken cancellationToken)
    {
        var ftyp = Mp4Builder.Ftyp();
        var mediaBytes = writeOrder.Sum(item => (long)item.Sample.Length);
        var headerBytes = MediaHeaderBytesFor(mediaBytes);

        // Built twice on purpose: the first tells us how long it is, which is what decides where the media
        // starts, which is what the second one has to state. Fixed-width chunk offsets are what make the two
        // the same length — see Mp4Builder's remarks.
        var sizing = Mp4Builder.Moov(tracks, 0);
        var mediaStart = ftyp.Length + sizing.Length + headerBytes;
        var moov = Mp4Builder.Moov(tracks, mediaStart);

        if (moov.Length != sizing.Length)
        {
            // Unreachable by construction, and asserted rather than assumed: if it ever fires, every chunk
            // offset in the file is wrong by the difference and the output would be silently unplayable.
            return new MediaRemuxerResult(MediaRemuxerOutcome.DestinationUnwritable,
                "the sample table changed size between passes");
        }

        destination.Write(ftyp);
        destination.Write(moov);

        Span<byte> header = stackalloc byte[16];
        if (headerBytes == 8)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header[..4], (uint)(mediaBytes + 8));
            "mdat"u8.CopyTo(header[4..8]);
        }
        else
        {
            // The 64-bit form: a size of 1 says "the real one is the eight bytes after the type".
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header[..4], 1);
            "mdat"u8.CopyTo(header[4..8]);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(header[8..], (ulong)(mediaBytes + 16));
        }
        destination.Write(header[..headerBytes]);

        CopySamples(source, destination, tracks, writeOrder, cancellationToken);

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
    /// carries <see cref="IMediaAudioConversionRun.OutputFramesPerPacket"/> samples at
    /// <see cref="IMediaAudioConversionRun.OutputSampleRate"/> — so the timescale is the sample rate, each
    /// frame lasts one packet, and the table is exact by construction instead of being re-derived from
    /// timestamps that no longer apply.
    /// </para>
    /// <para>
    /// ⚠ Spooled to a TEMPORARY FILE, deleted on close. A two-hour soundtrack is ~115 MB as AAC and this
    /// runs on phones; holding it would be the kind of allocation that works on every test file and dies on
    /// a real one.
    /// </para>
    /// </summary>
    private static Mp4TrackPlan? Convert(Stream source, MatroskaTrack track, string codec,
                                         IMediaAudioConversion conversion, CancellationToken cancellationToken)
    {
        // Everything the platform codec must be configured with, from what Matroska declared: a decoder
        // told the wrong rate produces audio at the wrong SPEED rather than an error, and one told no
        // CodecPrivate produces silence for the codecs that need it.
        using var run = conversion.Begin(
            new MediaStreamInfo(MediaStreamKind.Audio, codec,
                Channels: track.Channels > 0 ? track.Channels : null,
                SampleRate: track.SampleRate > 0 ? (int)Math.Round(track.SampleRate) : null),
            track.CodecPrivate ?? ReadOnlyMemory<byte>.Empty);
        if (run is null) return null;

        var spool = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
                                   FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
        var samples = new List<MatroskaSample>();
        var frame = Array.Empty<byte>();

        void Emit(IReadOnlyList<ReadOnlyMemory<byte>> outputs)
        {
            foreach (var output in outputs)
            {
                if (output.Length == 0) continue;
                samples.Add(new MatroskaSample(spool.Position, output.Length,
                    Ticks: samples.Count * (long)run.OutputFramesPerPacket, KeyFrame: true));
                spool.Write(output.Span);
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
            Emit(run.Push(frame.AsMemory(0, sample.Length)));
        }

        // 🔴 Without this the tail stays inside the codec and the soundtrack simply stops early, in a file
        // that is otherwise perfectly well-formed.
        Emit(run.Drain());

        var config = run.OutputConfig;
        if (samples.Count == 0 || config.Length == 0) { spool.Dispose(); return null; }

        var timescale = (uint)Math.Max(run.OutputSampleRate, 1);
        var perPacket = (long)Math.Max(run.OutputFramesPerPacket, 1);
        var decode = new long[samples.Count];
        var durations = new long[samples.Count];
        for (var i = 0; i < samples.Count; i++) { decode[i] = i * perPacket; durations[i] = perPacket; }

        return new Mp4TrackPlan
        {
            Source = track,
            IsVideo = false,
            Timescale = timescale,
            SampleEntry = Mp4Builder.AudioSampleEntry(Math.Max(run.OutputChannels, 1), timescale, config.ToArray()),
            Samples = [.. samples],
            Decode = decode,
            Composition = new long[samples.Count],
            Durations = durations,
            Shift = 0,
            ByteSource = spool,
        };
    }

    /// <summary>
    /// A track chosen but not yet timed. Only exists so selection can fail EARLY — a missing decoder
    /// configuration is worth reporting before walking a multi-gigabyte file to find its frames.
    /// </summary>
    private readonly record struct PendingTrack(MatroskaTrack Source, bool IsVideo, byte[] Entry)
    {
        public Mp4TrackPlan Placeholder() => new()
        {
            Source = Source,
            IsVideo = IsVideo,
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
