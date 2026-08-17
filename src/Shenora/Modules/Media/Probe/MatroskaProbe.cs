using System.Buffers.Binary;

namespace Shenora.Modules.Media;

/// <summary>
/// Reads what is INSIDE a Matroska file — its tracks, their codecs and its duration — without decoding a
/// frame and without any external tool, so a <see cref="MediaProbeResult"/> costs no media toolchain (D51).
/// <para>
/// ⚠ <b>It reads the HEADER, never the content.</b> No frames, no decoding, no walking clusters: cheap
/// enough for a scan, and never the thing that makes a file play or not. What it produces is an OPINION
/// for the planner, which still checks container and streams together because both can lie. Scoped to
/// Matroska because that is the container that stops ordinary video playing in a webview — the H.264
/// inside a <c>.mkv</c> is usually perfectly playable, the box is not. WebM parses identically.
/// </para>
/// </summary>
public static class MatroskaProbe
{
    // EBML element ids as they appear on the wire, INCLUDING their length-descriptor bits — which is what
    // makes the comparisons below plain equality rather than bit-twiddling.
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
    private const ulong IdSamplingFrequency = 0xB5;

    /// <summary>Matroska's track-type numbers. Only the three the planner can act on are named.</summary>
    private const ulong TrackTypeVideo = 1;
    private const ulong TrackTypeAudio = 2;
    private const ulong TrackTypeSubtitle = 17;

    /// <summary>
    /// How far into the file the header may be before this gives up. ⚠ A bound rather than "read until
    /// Tracks is found": this parses a file the PAGE can point at, so a malformed or hostile file must
    /// cost a bounded read, not a walk to EOF.
    /// </summary>
    private const long HeaderBudgetBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Probe <paramref name="path"/>, or null when it is not Matroska or cannot be read. Null rather than
    /// an exception or a partial result: "I could not tell" is an ordinary answer, and the planner already
    /// treats an absent probe as "assume nothing and check the extension".
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
            // Unreadable, gone, or locked. No exception text travels from here — a media path must never
            // reach a page.
            return null;
        }
    }

    /// <summary>Probe an open stream. The stream is read from its current position and is NOT disposed.</summary>
    /// <param name="stream">A readable stream positioned at the start of the file.</param>
    /// <param name="container">
    /// The container to report, as a lowercase extension with the dot. Defaults to <c>.mkv</c>; pass
    /// <c>.webm</c> when that is what the file is called — the planner decides on the extension the PAGE
    /// sees, not on the format underneath.
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

            // The Segment's direct children only — Info and Tracks matter; Clusters are the content and
            // are never entered.
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
            int? sampleRate = null;

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
                        ReadAudio(entry.Nested(fieldSize), ref channels, ref sampleRate);
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
            streams.Add(new MediaStreamInfo(kind, CodecNameOf(codecId), Channels: channels, SampleRate: sampleRate));
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

    /// <summary>
    /// The audio track's shape.
    /// <para>
    /// 🔴 <b><c>SamplingFrequency</c> must be read here, not left null.</b> It configures a decoder, and
    /// guessing 48 kHz for a 44.1 kHz track produces audio that plays at the WRONG SPEED rather than
    /// failing — so a fixture at 48 kHz cannot catch its absence. ⚠ It is an EBML float (4 or 8 bytes),
    /// not an integer, and is ROUNDED into the <c>int?</c>: truncating 44099.999… misconfigures the very
    /// decoder this exists to configure.
    /// </para>
    /// </summary>
    private static void ReadAudio(EbmlReader audio, ref int? channels, ref int? sampleRate)
    {
        while (audio.TryReadElement(out var id, out var size))
        {
            if (id == IdChannels && size <= 8) channels = (int)audio.ReadUnsigned(size);
            else if (id == IdSamplingFrequency && size is 4 or 8)
            {
                var hz = audio.ReadFloat(size);
                // A non-positive or absurd rate is not a rate. Null says "unknown", which every consumer
                // handles; inventing one would be the wrong-speed failure.
                if (hz > 0 && hz < int.MaxValue) sampleRate = (int)Math.Round(hz);
            }
            else audio.Skip(size);
        }
    }

    /// <summary>
    /// The codec name, reading through Matroska's <c>V_MS/VFW/FOURCC</c> wrapper when the track carries the
    /// private data that names what is actually inside it.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>WITHOUT THIS, A WHOLE FAMILY OF REAL FILES REPORTS ITS CODEC AS "vfw".</b> Matroska has native
    /// ids for h264, HEVC, MPEG-2, MPEG-4 Part 2, VP8/9 and AV1; <b>everything else</b> uses the
    /// Video-for-Windows wrapper, with the true codec as a FourCC inside a <c>BITMAPINFOHEADER</c>. An h263
    /// track has no native id at all, so it arrives named <c>vfw</c>: the converter declines a codec it
    /// offers, and the page is told <c>dropped:["vfw"]</c> — a CONTAINER CONVENTION no app can act on.
    /// <para>
    /// ⚠ The FourCC sits at offset 16 of the header (after <c>biSize</c>, width, height, planes and bit
    /// count) and its case is not dependable — <c>H263</c>, <c>h263</c> and <c>U263</c> all occur — so it
    /// is upper-cased before lookup. A header too short to hold one falls back to the wrapper's own name.
    /// </para>
    /// </remarks>
    internal static string? CodecNameOf(string? codecId, ReadOnlyMemory<byte> codecPrivate)
    {
        var name = CodecNameOf(codecId);
        if (name is not "vfw") return name;

        return FourCcCodec(codecPrivate) ?? name;
    }

    /// <summary>The codec a <c>BITMAPINFOHEADER</c>'s FourCC names, or null when it holds none this kit knows.</summary>
    private static string? FourCcCodec(ReadOnlyMemory<byte> codecPrivate)
    {
        const int FourCcOffset = 16;
        if (codecPrivate.Length < FourCcOffset + 4) return null;

        var fourCc = System.Text.Encoding.ASCII
            .GetString(codecPrivate.Slice(FourCcOffset, 4).Span)
            .Trim()
            .ToUpperInvariant();

        return fourCc switch
        {
            "H263" or "U263" or "S263" or "M263" => "h263",
            // ⚠ The MPEG-4 Part 2 family ALSO arrives this way from many encoders even though Matroska has
            // a native id for it, so both spellings must answer `mpeg4` or the planner sees two codecs.
            "DIVX" or "DX50" or "XVID" or "MP4V" or "FMP4" or "DIV3" => "mpeg4",
            "H264" or "AVC1" or "X264" => "h264",
            "HEVC" or "HVC1" or "H265" => "hevc",
            "MPG2" or "MP2V" => "mpeg2video",
            "VP80" => "vp8",
            "VP90" => "vp9",
            _ => null,
        };
    }

    /// <summary>
    /// Matroska's <c>CodecID</c> to the lowercase names the planner and every policy speak (<c>h264</c>,
    /// <c>aac</c>, <c>ac3</c>…). ⚠ Translated rather than passed through — Matroska's names are its own
    /// (<c>V_MPEG4/ISO/AVC</c>, not <c>h264</c>), and a policy written against probe output would
    /// otherwise need two vocabularies for the same codec. An unknown id comes back as a lowercased tail
    /// rather than null, so a name the policy does not recognise is treated as unplayable: the safe way.
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
            // ⚠ Handled by the overload that can see CodecPrivate (`FourCcCodec`). Reaching HERE means the
            // caller had no private data, and "vfw" is then all the container said.
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
    /// The EBML primitives Matroska is built from: variable-length ids and sizes, big-endian integers.
    /// Every read is against a remaining budget, so a malformed length cannot walk off the end or spin.
    /// </summary>
    private sealed class EbmlReader(Stream stream, long budget = HeaderBudgetBytes)
    {
        private long _remaining = budget;

        /// <summary>
        /// A reader over the next <paramref name="size"/> bytes — one element's children.
        /// <para>
        /// 🔴 <b>The payload is COPIED.</b> Handing the child the same stream leaves the parent's position
        /// advanced by the child's reads and its BUDGET untouched, so the parent reads past the element it
        /// delegated: a video track carrying a nested <c>Video</c> element swallows the AUDIO track after
        /// it, and the file reports one stream instead of two. Copying advances both position and budget
        /// by exactly <paramref name="size"/>, whatever the child reads. Bounded too — this parses a file
        /// a page can point at, so an over-large declared size is skipped rather than allocated.
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
            // An "unknown size" element (all value bits set) means "to the end of the parent" — for a
            // header walk, everything left.
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
        /// spans. An ID keeps that marker; a SIZE drops it — conflating the two is the classic way an
        /// EBML parser reads every element wrong.
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

            // "Unknown size" is every value bit set; a sentinel the caller turns into "the rest of the
            // parent".
            if (!keepMarker && allBitsSet) value = ulong.MaxValue;
            return true;
        }
    }
}
