namespace Shenora.Media;

/// <summary>Why a remux did not happen. A code the caller branches on, never prose for a user.</summary>
public enum Mp4RemuxerOutcome
{
    /// <summary>The output was written and every selected stream was copied into it.</summary>
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
public sealed record Mp4RemuxerResult(
    Mp4RemuxerOutcome Outcome,
    string Reason,
    int VideoSamples = 0,
    int AudioSamples = 0,
    TimeSpan Duration = default)
{
    /// <summary>True only for <see cref="Mp4RemuxerOutcome.Succeeded"/>.</summary>
    public bool Succeeded => Outcome == Mp4RemuxerOutcome.Succeeded;
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
public static class Mp4Remuxer
{
    /// <summary>Matroska CodecIDs this can carry into MP4, and the boxes each becomes.</summary>
    private static readonly Dictionary<string, (string Entry, string Config)> VideoCodecs = new(StringComparer.OrdinalIgnoreCase)
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
    /// The kit's DEFAULT <see cref="MediaConversionOptions.Convert"/> — container repair, no codecs.
    ///
    /// <para>
    /// <b>This is what "an app gets working playback with NOTHING supplied" means</b> (D52). Wire it and an
    /// <c>.mkv</c> of ordinary H.264 + AAC becomes a playable <c>.mp4</c> with no engine, no binary and no
    /// licence weight:
    /// </para>
    /// <code>
    /// interceptor.UseMediaConversion(new MediaConversionOptions
    /// {
    ///     Resolve = MyRoute, CacheRoot = cacheDir, AllowedRoots = [libraryDir],
    ///     Convert = Mp4Remuxer.ConvertAsync,   // the kit's default
    /// });
    /// </code>
    /// <para>
    /// ⚠ <b>It THROWS when it cannot help, and that is required rather than unfriendly.</b> The route runs
    /// this inside <c>Files.BeginReplace</c>, which publishes the output only if the delegate returns
    /// without throwing — so a refusal that returned quietly would promote a truncated or empty file into
    /// the cache and serve it forever. A stream MP4 cannot carry (AC-3, DTS, VP9) is exactly such a
    /// refusal, and the page hears <c>FAILED</c> with a reason instead of playing silence.
    /// </para>
    /// <para>
    /// Runs on a worker thread because the remux is synchronous and file-bound, and the caller is a mission
    /// that expects to await.
    /// </para>
    /// </summary>
    public static Task ConvertAsync(MediaConversionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.Run(() =>
        {
            request.Progress.Report(0);
            var result = Remux(request.SourcePath, request.DestinationPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!result.Succeeded)
            {
                // The OUTCOME, not free text: the route turns this into a FAILED event whose reason is a
                // type name, and this kit's error contract is a code plus parameters (`ipc-contracts`).
                throw new InvalidOperationException($"{result.Outcome}: {result.Reason}");
            }

            request.Progress.Report(1);
        }, cancellationToken);
    }

    /// <summary>
    /// Remux <paramref name="sourcePath"/> into <paramref name="destinationPath"/>, overwriting it.
    /// </summary>
    public static Mp4RemuxerResult Remux(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        try
        {
            using var source = File.OpenRead(sourcePath);
            using var destination = File.Create(destinationPath);
            return Remux(source, destination, cancellationToken);
        }
        catch (Exception)
        {
            // No exception text travels from here. A media path is exactly the kind of detail that must not
            // reach a page, and the caller already knows which file it asked about.
            return new Mp4RemuxerResult(Mp4RemuxerOutcome.SourceUnreadable, "source or destination unusable");
        }
    }

    /// <summary>
    /// Remux one open stream into another. <paramref name="source"/> must be seekable — the sample table has
    /// to be built before the media is copied, so the frames are visited twice.
    /// </summary>
    public static Mp4RemuxerResult Remux(Stream source, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        try
        {
            return Run(source, destination, cancellationToken);
        }
        catch (Exception)
        {
            return new Mp4RemuxerResult(Mp4RemuxerOutcome.SourceUnreadable, "malformed source");
        }
    }

    private static Mp4RemuxerResult Run(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        if (!source.CanSeek) return new Mp4RemuxerResult(Mp4RemuxerOutcome.SourceUnreadable, "source is not seekable");

        var reader = new MatroskaSampleReader(source);
        if (!reader.ReadHeader()) return new Mp4RemuxerResult(Mp4RemuxerOutcome.NotMatroska, "no Matroska header or no tracks");

        // ── choose the streams ────────────────────────────────────────────────────────────────────────
        // The first of each kind MP4 can carry. Deliberately not "every track": a film with four dubs would
        // otherwise produce an output four soundtracks wide, and a webview plays one.
        var video = reader.Tracks.FirstOrDefault(t => t.Kind == MediaStreamKind.Video && CanCarryVideo(t));
        var audio = reader.Tracks.FirstOrDefault(t => t.Kind == MediaStreamKind.Audio && CanCarryAudio(t));

        if (video is null && audio is null)
        {
            var codecs = string.Join(" + ", reader.Tracks.Select(t => $"{t.Kind}:{t.CodecId ?? "?"}"));
            return new Mp4RemuxerResult(Mp4RemuxerOutcome.NoCarriableStream,
                $"no stream MP4 can carry without re-encoding: {codecs}");
        }

        var plans = new List<Mp4TrackPlan>();
        if (video is not null)
        {
            var entry = BuildVideoEntry(video);
            if (entry is null) return new Mp4RemuxerResult(Mp4RemuxerOutcome.MissingDecoderConfig,
                $"video track {video.Number} ({video.CodecId}) carries no decoder configuration");
            plans.Add(new PendingTrack(video, IsVideo: true, entry).Placeholder());
        }
        if (audio is not null)
        {
            var entry = BuildAudioEntry(audio);
            if (entry is null) return new Mp4RemuxerResult(Mp4RemuxerOutcome.MissingDecoderConfig,
                $"audio track {audio.Number} ({audio.CodecId}) carries no decoder configuration");
            plans.Add(new PendingTrack(audio, IsVideo: false, entry).Placeholder());
        }

        // ── walk the clusters ─────────────────────────────────────────────────────────────────────────
        var wanted = plans.Select(p => p.Source.Number).ToHashSet();
        if (!reader.ReadSamples(wanted))
        {
            return new Mp4RemuxerResult(Mp4RemuxerOutcome.SourceUnreadable, "malformed or unbounded clusters");
        }

        if (plans.All(p => p.Source.Samples.Count == 0))
        {
            return new Mp4RemuxerResult(Mp4RemuxerOutcome.SourceUnreadable, "the file declares tracks but holds no frames");
        }

        // ── resolve timing ────────────────────────────────────────────────────────────────────────────
        var resolved = new List<Mp4TrackPlan>();
        foreach (var plan in plans)
        {
            if (plan.Source.Samples.Count == 0) continue;   // a declared-but-empty track is dropped, not written empty
            resolved.Add(Resolve(plan, reader.TimestampScaleNs));
        }
        if (resolved.Count == 0) return new Mp4RemuxerResult(Mp4RemuxerOutcome.SourceUnreadable, "no frames for any carriable track");

        var writeOrder = Interleave(resolved);

        // ── write ─────────────────────────────────────────────────────────────────────────────────────
        try
        {
            return Write(source, destination, resolved, writeOrder, cancellationToken);
        }
        catch (Exception)
        {
            return new Mp4RemuxerResult(Mp4RemuxerOutcome.DestinationUnwritable, "the output could not be written");
        }
    }

    private static bool CanCarryVideo(MatroskaTrack track) =>
        track.CodecId is not null && VideoCodecs.ContainsKey(track.CodecId);

    /// <summary>AAC, in any of the profile-qualified spellings Matroska uses (<c>A_AAC/MPEG4/LC</c>).</summary>
    private static bool CanCarryAudio(MatroskaTrack track) =>
        track.CodecId is not null
        && (track.CodecId.Equals("A_AAC", StringComparison.OrdinalIgnoreCase)
            || track.CodecId.StartsWith("A_AAC/", StringComparison.OrdinalIgnoreCase));

    private static byte[]? BuildVideoEntry(MatroskaTrack track)
    {
        if (track.CodecPrivate is not { Length: > 0 } config) return null;
        var (entry, configBox) = VideoCodecs[track.CodecId!];

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
    private static MatroskaSample[] Interleave(List<Mp4TrackPlan> tracks)
    {
        var ordered = tracks
            .SelectMany((track, index) => track.Samples.Select(s => (Track: index, Sample: s)))
            .OrderBy(s => s.Sample.Offset)
            .ToArray();

        var running = 0L;
        var current = -1;
        foreach (var (track, sample) in ordered)
        {
            if (track != current)
            {
                tracks[track].ChunkOffsets.Add(running);
                tracks[track].ChunkSamples.Add(0);
                current = track;
            }

            tracks[track].ChunkSamples[^1]++;
            running += sample.Length;
        }

        return [.. ordered.Select(s => s.Sample)];
    }

    private static Mp4RemuxerResult Write(Stream source, Stream destination,
                                          List<Mp4TrackPlan> tracks, MatroskaSample[] writeOrder,
                                          CancellationToken cancellationToken)
    {
        var ftyp = Mp4Builder.Ftyp();
        var mediaBytes = writeOrder.Sum(s => (long)s.Length);
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
            return new Mp4RemuxerResult(Mp4RemuxerOutcome.DestinationUnwritable,
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

        CopySamples(source, destination, writeOrder, cancellationToken);

        var duration = tracks.Max(t => t.Timescale == 0 ? 0d : (double)t.Duration / t.Timescale);
        return new Mp4RemuxerResult(
            Mp4RemuxerOutcome.Succeeded,
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
    private static void CopySamples(Stream source, Stream destination, MatroskaSample[] writeOrder,
                                    CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        foreach (var sample in writeOrder)
        {
            // Between frames, not mid-frame: a partial frame would leave the output inconsistent with the
            // sample table already written, and the caller discards the whole file on cancellation anyway.
            cancellationToken.ThrowIfCancellationRequested();
            source.Position = sample.Offset;
            var left = sample.Length;
            while (left > 0)
            {
                var take = Math.Min(left, buffer.Length);
                var read = source.ReadAtLeast(buffer.AsSpan(0, take), take, throwOnEndOfStream: false);
                if (read <= 0) throw new EndOfStreamException();
                destination.Write(buffer, 0, read);
                left -= read;
            }
        }
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
