using System.Buffers.Binary;

namespace Shenora.Modules.Media;

/// <summary>
/// Reads what is INSIDE a Matroska file — its tracks, their codecs and its duration — without decoding a
/// frame and without any external tool.
///
/// <para>
/// <b>Why this is the first piece of the translation layer.</b> The planner already decides what must happen
/// to a file (<see cref="MediaPlaybackPlanner"/>), but it can only decide from a
/// <see cref="MediaProbeResult"/> — and until now the only thing that could produce one was an external
/// probe. That made "can this play?" depend on shipping a media toolchain, for a question that is answered
/// by reading a few hundred bytes of header. This answers it in managed code, under the kit's own licence
/// (D51).
/// </para>
///
/// <para>
/// ⚠ <b>It reads the HEADER, never the content.</b> No frames, no decoding, no seeking through clusters —
/// so it is cheap enough for a scan and it can never be the thing that makes a file play or not. What it
/// produces is an OPINION for the planner, exactly like an external probe's, and the planner still checks
/// container and streams together because both can lie.
/// </para>
///
/// <para>
/// Scoped to Matroska because that is the container that actually stops ordinary video playing in a webview
/// — the H.264 inside a <c>.mkv</c> is usually perfectly playable, it is the box that is not. WebM is the
/// same format and parses identically; it is already playable, so probing one is harmless rather than useful.
/// </para>
/// </summary>
public static class MatroskaProbe
{
    // EBML element ids, as they appear on the wire INCLUDING their length-descriptor bits. Reading them as
    // whole numbers is what makes the comparisons below plain equality rather than bit-twiddling.
    private const ulong IdEbmlHeader = 0x1A45DFA3;
    private const ulong IdSegment = 0x18538067;
    private const ulong IdInfo = 0x1549A966;
    private const ulong IdTimestampScale = 0x2AD7B1;
    private const ulong IdDuration = 0x4489;
    private const ulong IdTracks = 0x1654AE6B;
    private const ulong IdTrackEntry = 0xAE;
    private const ulong IdTrackType = 0x83;
    private const ulong IdCodecId = 0x86;
    private const ulong IdVideo = 0xE0;
    private const ulong IdAudio = 0xE1;
    private const ulong IdPixelWidth = 0xB0;
    private const ulong IdPixelHeight = 0xBA;
    private const ulong IdChannels = 0x9F;

    /// <summary>Matroska's track-type numbers. Only the three the planner can act on are named.</summary>
    private const ulong TrackTypeVideo = 1;
    private const ulong TrackTypeAudio = 2;
    private const ulong TrackTypeSubtitle = 17;

    /// <summary>
    /// How far into the file the header may be before this gives up.
    /// <para>
    /// ⚠ A bound rather than "read until Tracks is found", because this parses a file the PAGE can point at.
    /// A malformed or hostile file must cost a bounded read, not a walk to EOF — the same fail-closed
    /// instinct the extraction limits and the path containment follow.
    /// </para>
    /// </summary>
    private const long HeaderBudgetBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Probe <paramref name="path"/>, or null when it is not Matroska or cannot be read.
    /// <para>
    /// Null rather than an exception or a partial result: "I could not tell" is an ordinary answer here, and
    /// the planner already treats an absent probe as "assume nothing and check the extension".
    /// </para>
    /// </summary>
    public static MediaProbeResult? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var file = File.OpenRead(path);
            return Read(file, Path.GetExtension(path).ToLowerInvariant());
        }
        catch (Exception)
        {
            // Unreadable, gone, or locked. No exception text travels from here — a media path is exactly
            // the kind of detail that must not reach a page.
            return null;
        }
    }

    /// <summary>
    /// Probe an open stream. The stream is read from its current position and is NOT disposed.
    /// </summary>
    /// <param name="stream">A readable stream positioned at the start of the file.</param>
    /// <param name="container">
    /// The container to report, as a lowercase extension with the dot. Defaults to <c>.mkv</c>; pass
    /// <c>.webm</c> when that is what the file is called, because the planner decides on the extension the
    /// PAGE will see rather than on the format underneath.
    /// </param>
    public static MediaProbeResult? Read(Stream stream, string container = ".mkv")
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            var reader = new EbmlReader(stream);

            // An EBML header first, or this is not Matroska at all and nothing below would mean anything.
            if (!reader.TryReadElement(out var id, out var size) || id != IdEbmlHeader) return null;
            reader.Skip(size);

            if (!reader.TryReadElement(out id, out size) || id != IdSegment) return null;

            var streams = new List<MediaStreamInfo>();
            double? durationTicks = null;
            double timestampScale = 1_000_000;   // Matroska's default: nanoseconds per tick.
            int? width = null;
            int? height = null;

            // Walk the Segment's direct children only. Info and Tracks are what matter; Clusters are the
            // content and are deliberately never entered.
            while (reader.TryReadElement(out var childId, out var childSize))
            {
                switch (childId)
                {
                    case IdInfo:
                        ReadInfo(reader.Nested(childSize), ref durationTicks, ref timestampScale);
                        break;
                    case IdTracks:
                        ReadTracks(reader.Nested(childSize), streams, ref width, ref height);
                        break;
                    default:
                        reader.Skip(childSize);
                        break;
                }

                // Everything the planner needs lives before the first Cluster in a well-formed file.
                if (streams.Count > 0 && durationTicks is not null) break;
            }

            if (streams.Count == 0 && durationTicks is null) return null;

            return new MediaProbeResult
            {
                Container = container,
                Streams = streams,
                Duration = durationTicks is { } ticks && ticks > 0
                    ? TimeSpan.FromSeconds(ticks * timestampScale / 1_000_000_000d)
                    : null,
                Width = width,
                Height = height,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ReadInfo(EbmlReader info, ref double? duration, ref double timestampScale)
    {
        while (info.TryReadElement(out var id, out var size))
        {
            switch (id)
            {
                case IdTimestampScale when size <= 8:
                    timestampScale = info.ReadUnsigned(size);
                    break;
                case IdDuration when size is 4 or 8:
                    duration = info.ReadFloat(size);
                    break;
                default:
                    info.Skip(size);
                    break;
            }
        }
    }

    private static void ReadTracks(EbmlReader tracks, List<MediaStreamInfo> streams,
                                   ref int? width, ref int? height)
    {
        while (tracks.TryReadElement(out var id, out var size))
        {
            if (id != IdTrackEntry) { tracks.Skip(size); continue; }

            var entry = tracks.Nested(size);
            ulong type = 0;
            string? codecId = null;
            int? channels = null;

            while (entry.TryReadElement(out var fieldId, out var fieldSize))
            {
                switch (fieldId)
                {
                    case IdTrackType when fieldSize <= 8:
                        type = entry.ReadUnsigned(fieldSize);
                        break;
                    case IdCodecId:
                        codecId = entry.ReadAscii(fieldSize);
                        break;
                    case IdVideo:
                        ReadVideo(entry.Nested(fieldSize), ref width, ref height);
                        break;
                    case IdAudio:
                        ReadAudio(entry.Nested(fieldSize), ref channels);
                        break;
                    default:
                        entry.Skip(fieldSize);
                        break;
                }
            }

            var kind = type switch
            {
                TrackTypeVideo => MediaStreamKind.Video,
                TrackTypeAudio => MediaStreamKind.Audio,
                TrackTypeSubtitle => MediaStreamKind.Subtitle,
                _ => MediaStreamKind.Unknown,
            };
            streams.Add(new MediaStreamInfo(kind, CodecNameOf(codecId), Channels: channels));
        }
    }

    private static void ReadVideo(EbmlReader video, ref int? width, ref int? height)
    {
        while (video.TryReadElement(out var id, out var size))
        {
            switch (id)
            {
                case IdPixelWidth when size <= 8: width ??= (int)video.ReadUnsigned(size); break;
                case IdPixelHeight when size <= 8: height ??= (int)video.ReadUnsigned(size); break;
                default: video.Skip(size); break;
            }
        }
    }

    private static void ReadAudio(EbmlReader audio, ref int? channels)
    {
        while (audio.TryReadElement(out var id, out var size))
        {
            if (id == IdChannels && size <= 8) channels = (int)audio.ReadUnsigned(size);
            else audio.Skip(size);
        }
    }

    /// <summary>
    /// Matroska's <c>CodecID</c> to the lowercase names the planner and every policy speak
    /// (<c>h264</c>, <c>aac</c>, <c>ac3</c>…).
    /// <para>
    /// ⚠ Translated rather than passed through, because Matroska's names are its own
    /// (<c>V_MPEG4/ISO/AVC</c>, not <c>h264</c>) and a policy written against probe output would otherwise
    /// have to know two vocabularies for the same codec. Unknown ids come back as a lowercased tail rather
    /// than null — a name the policy does not recognise is correctly treated as unplayable, which is the
    /// safe direction.
    /// </para>
    /// </summary>
    internal static string? CodecNameOf(string? codecId)
    {
        if (string.IsNullOrWhiteSpace(codecId)) return null;
        var id = codecId.Trim().ToUpperInvariant();

        return id switch
        {
            "V_MPEG4/ISO/AVC" => "h264",
            "V_MPEGH/ISO/HEVC" => "hevc",
            "V_VP8" => "vp8",
            "V_VP9" => "vp9",
            "V_AV1" => "av1",
            "V_MPEG4/ISO/ASP" or "V_MPEG4/ISO/SP" or "V_MPEG4/ISO/AP" => "mpeg4",
            "V_MPEG2" => "mpeg2video",
            "V_MS/VFW/FOURCC" => "vfw",
            "A_AAC" => "aac",
            "A_AC3" => "ac3",
            "A_EAC3" => "eac3",
            "A_DTS" => "dts",
            "A_FLAC" => "flac",
            "A_OPUS" => "opus",
            "A_VORBIS" => "vorbis",
            "A_MPEG/L3" => "mp3",
            "A_MPEG/L2" => "mp2",
            "A_TRUEHD" => "truehd",
            "A_ALAC" => "alac",
            "S_TEXT/UTF8" => "subrip",
            "S_TEXT/ASS" or "S_TEXT/SSA" => "ass",
            _ when id.StartsWith("A_AAC/", StringComparison.Ordinal) => "aac",
            _ when id.StartsWith("A_PCM/", StringComparison.Ordinal) => "pcm",
            _ => id.ToLowerInvariant(),
        };
    }

    /// <summary>
    /// The EBML primitives Matroska is built from: variable-length ids, variable-length sizes, and
    /// big-endian integers. Bounded by construction — every read is against a remaining budget, so a
    /// malformed length cannot walk off the end of the file or spin.
    /// </summary>
    private sealed class EbmlReader(Stream stream, long budget = HeaderBudgetBytes)
    {
        private long _remaining = budget;

        /// <summary>
        /// A reader over the next <paramref name="size"/> bytes — one element's children.
        /// <para>
        /// 🔴 <b>The payload is COPIED, and that is a fix rather than a convenience.</b> The first version
        /// handed the child the same stream with its own budget, which left the parent's position advanced
        /// by the child's reads and its BUDGET untouched — so a parent went on reading past the element it
        /// had delegated and consumed whatever followed. The visible symptom: a video track carrying a
        /// nested <c>Video</c> element swallowed the AUDIO track after it, and a file reported one stream
        /// instead of two. Copying makes both the position and the budget advance by exactly
        /// <paramref name="size"/>, whatever the child does or does not read.
        /// </para>
        /// <para>
        /// Bounded, because this is parsing a file a page can point at: header elements are tiny, and a
        /// declared size that is not gets skipped rather than allocated.
        /// </para>
        /// </summary>
        public EbmlReader Nested(long size)
        {
            var take = (int)Math.Min(size, Math.Min(_remaining, MaxNestedBytes));
            if (take <= 0) { Skip(size); return new EbmlReader(new MemoryStream([]), 0); }

            var buffer = new byte[take];
            var read = stream.ReadAtLeast(buffer, take, throwOnEndOfStream: false);
            _remaining -= read;
            if (size > read) Skip(size - read);
            return new EbmlReader(new MemoryStream(buffer, 0, read), read);
        }

        /// <summary>The most one nested element may be buffered to. Matroska headers are far below this.</summary>
        private const int MaxNestedBytes = 1024 * 1024;

        /// <summary>Read one element's id and payload size, or false at the end of this reader's budget.</summary>
        public bool TryReadElement(out ulong id, out long size)
        {
            id = 0;
            size = 0;
            if (_remaining <= 0) return false;

            if (!TryReadVariableInt(keepMarker: true, out var rawId)) return false;
            if (!TryReadVariableInt(keepMarker: false, out var rawSize)) return false;

            id = rawId;
            // An "unknown size" element (all value bits set) means "to the end of the parent", which for a
            // header walk is the same as "everything left".
            size = rawSize >= long.MaxValue ? _remaining : Math.Min((long)rawSize, _remaining);
            return true;
        }

        /// <summary>Skip a payload, clamped to what is left so a bogus length cannot escape the budget.</summary>
        public void Skip(long size)
        {
            var take = Math.Min(size, _remaining);
            for (var i = 0L; i < take; i++)
            {
                if (stream.ReadByte() < 0) { _remaining = 0; return; }
            }
            _remaining -= take;
        }

        public ulong ReadUnsigned(long size)
        {
            ulong value = 0;
            var take = Math.Min(size, _remaining);
            for (var i = 0L; i < take; i++)
            {
                var b = stream.ReadByte();
                if (b < 0) { _remaining = 0; return value; }
                value = (value << 8) | (byte)b;
            }
            _remaining -= take;
            return value;
        }

        public double ReadFloat(long size)
        {
            Span<byte> buffer = stackalloc byte[8];
            var take = (int)Math.Min(size, _remaining);
            if (take is not (4 or 8)) { Skip(size); return 0; }
            if (stream.ReadAtLeast(buffer[..take], take, throwOnEndOfStream: false) != take)
            {
                _remaining = 0;
                return 0;
            }
            _remaining -= take;
            return take == 4
                ? BinaryPrimitives.ReadSingleBigEndian(buffer[..4])
                : BinaryPrimitives.ReadDoubleBigEndian(buffer[..8]);
        }

        public string? ReadAscii(long size)
        {
            var take = (int)Math.Min(size, Math.Min(_remaining, 256));
            if (take <= 0) { Skip(size); return null; }
            var buffer = new byte[take];
            var read = stream.ReadAtLeast(buffer, take, throwOnEndOfStream: false);
            _remaining -= read;
            if (size > take) Skip(size - take);
            // Trim the trailing NULs Matroska pads short strings with.
            return System.Text.Encoding.ASCII.GetString(buffer, 0, read).TrimEnd('\0');
        }

        /// <summary>
        /// EBML's variable-length integer: the first set bit of the first byte says how many bytes it
        /// spans. An ID keeps that marker (it is part of the id); a SIZE drops it (it is not part of the
        /// value) — conflating the two is the classic way an EBML parser reads every element wrong.
        /// </summary>
        private bool TryReadVariableInt(bool keepMarker, out ulong value)
        {
            value = 0;
            if (_remaining <= 0) return false;

            var first = stream.ReadByte();
            if (first < 0) { _remaining = 0; return false; }
            _remaining--;

            var length = 1;
            var mask = 0x80;
            while (length <= 8 && (first & mask) == 0)
            {
                length++;
                mask >>= 1;
            }
            if (length > 8) return false;   // a leading zero byte is not a legal length descriptor

            var allBitsSet = (first & (mask - 1)) == mask - 1;
            value = keepMarker ? (ulong)first : (ulong)(first & (mask - 1));

            for (var i = 1; i < length; i++)
            {
                if (_remaining <= 0) return false;
                var next = stream.ReadByte();
                if (next < 0) { _remaining = 0; return false; }
                _remaining--;
                value = (value << 8) | (byte)next;
                allBitsSet &= next == 0xFF;
            }

            // "Unknown size" is every value bit set; report it as a sentinel the caller turns into
            // "the rest of the parent".
            if (!keepMarker && allBitsSet) value = ulong.MaxValue;
            return true;
        }
    }
}
