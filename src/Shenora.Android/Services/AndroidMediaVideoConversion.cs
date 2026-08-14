using Shenora.Modules.Media;

using Android.Media;
using Android.Views;
using static Shenora.Android.AndroidMediaCodecs;

namespace Shenora.Android;

/// <summary>
/// Android's PICTURE converter — a platform decoder rendering into a platform H.264 encoder.
///
/// <para>
/// <b>The gap it closes is measured, not assumed</b> (API 36 / WebView 133, 2026-08-10): the device decodes
/// <c>mpeg4</c> to a real 480×270 frame while its own webview answers <c>""</c> for
/// <c>video/mp4; codecs="mp4v.20.8"</c> — and a page pointed at such a file reaches <c>readyState = 4</c>
/// with <c>error</c> null and a <c>0×0</c> picture. Sound over a blank rectangle, with nothing raised.
/// This is the same job the audio converter does, on the same seam, for the other stream kind.
/// </para>
///
/// <para>
/// 🔴 <b>SURFACE-TO-SURFACE, and that choice is the whole reason this is short.</b> The obvious path —
/// decode into a <c>ByteBuffer</c>, feed the encoder — means handling whatever YUV layout the device
/// returns (<c>COLOR_FormatYUV420Flexible</c> covers planar, semi-planar and every stride/slice-height
/// quirk a vendor ships). Handing the decoder the ENCODER'S OWN INPUT SURFACE makes the pixels the
/// platform's problem end to end: no colour conversion, no stride arithmetic, and nothing device-specific
/// to get wrong.
/// </para>
///
/// <para>
/// ⚠ <b>It is a SOFTWARE decoder for the case that motivated it</b> (<c>c2.android.mpeg4.decoder</c>), so
/// the work is real per frame — unlike the audio tier, which rides hardware. The encoder is normally
/// hardware. That asymmetry is a fact about the codec, not a defect here, and it is why the planner still
/// prefers a COPY wherever the container can carry the stream.
/// </para>
/// </summary>
public static class AndroidMediaVideoConversion
{
    private const string AvcMime = "video/avc";

    /// <summary>
    /// Register this platform converter into a conversion pipeline. Dispose to remove it.
    /// <para>
    /// A MIDDLEWARE, like its audio peer: it DECLINES anything that is not a video stream it can handle, so
    /// one pipeline carries both kinds and nothing can be registered into the wrong one.
    /// </para>
    /// </summary>
    public static IDisposable Use(MediaConversionPipeline pipeline, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return pipeline.Use((source, codecPrivate) => Begin(source, codecPrivate, log), Claims);
    }

    /// <summary>
    /// What this converter OFFERS to attempt — the declaration the pipeline answers <c>CanConvert</c> from
    /// before any codec is built.
    /// <para>
    /// ⚠ Derived from the SAME table <see cref="MimeOf"/> reads, so the claim and the behaviour cannot
    /// drift. ⚠ Computed on access, not cached: a static initialiser runs in declaration order and would
    /// read that table before it exists, producing a converter that silently claims nothing.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MediaStreamClaim> Claims =>
        [.. Mimes.Keys.Select(codec => new MediaStreamClaim(MediaStreamKind.Video, codec))];

    /// <summary>Can this device convert the codec? Exposed so a capability report can ask without starting one.</summary>
    public static bool CanConvert(string codec)
    {
        var mime = MimeOf(codec);
        return mime is not null && HasCodec(mime, encoder: false) && HasCodec(AvcMime, encoder: true);
    }

    /// <summary>The middleware body: answer with a run, or null to let the next converter try.</summary>
    private static IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate,
                                                   Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Kind first: this converter answers for pictures and nothing else, and saying so here is what lets
        // one pipeline serve every kind.
        if (source.Kind is not MediaStreamKind.Video) return null;
        if (source.Codec is null || MimeOf(source.Codec) is not { } mime) return null;
        if (!HasCodec(mime, encoder: false) || !HasCodec(AvcMime, encoder: true)) return null;

        // A codec cannot be configured without dimensions, and a probe that omits them is asking whether the
        // device COULD rather than starting one — answer yes without building anything.
        if (source.Width is not > 0 || source.Height is not > 0) return null;

        try
        {
            return new Run(mime, source, codecPrivate, log);
        }
        catch (Exception ex)
        {
            Report(log, $"[Shenora.Android] the {mime} video converter would not configure "
                      + $"({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    // `Report` and `HasCodec` live in `AndroidMediaCodecs` — see the audio converter.

    /// <summary>
    /// The planner's lowercase names to Android's MIME types.
    /// <para>
    /// ⚠ <b>H.264 and HEVC are deliberately ABSENT.</b> MP4 already carries both, so the remuxer COPIES
    /// them — offering to re-encode what can be copied would turn a lossless, gigabyte-per-second operation
    /// into a lossy one that takes minutes.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> Mimes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mpeg4"] = "video/mp4v-es",
        ["mpeg2video"] = "video/mpeg2",
        ["vp8"] = "video/x-vnd.on2.vp8",
        ["vp9"] = "video/x-vnd.on2.vp9",
        ["av1"] = "video/av01",
    };

    /// <summary>
    /// ⚠ ONE table, read by both <see cref="Claims"/> and this — so what the converter DECLARES and what it
    /// ACCEPTS cannot drift.
    /// </summary>
    private static string? MimeOf(string codec) => Mimes.TryGetValue(codec, out var mime) ? mime : null;

    /// <summary>
    /// One picture's decode-then-encode, driven synchronously through a shared Surface.
    /// </summary>
    private sealed class Run : IMediaStreamConversionRun
    {
        private const int TimeoutUs = 10_000;

        private readonly MediaCodec _decoder;
        private readonly MediaCodec _encoder;
        private readonly Surface _bridge;
        private readonly MediaCodec.BufferInfo _info = new();
        private readonly Action<string>? _log;
        private readonly int _width;
        private readonly int _height;
        private bool _disposed;

        public ReadOnlyMemory<byte> OutputConfig { get; private set; }

        /// <summary>⚠ Zero: a picture times every frame individually, so the muxer reads those instead.</summary>
        public int OutputFramesPerPacket => 0;

        public MediaStreamInfo OutputFormat => new(MediaStreamKind.Video, "h264", Width: _width, Height: _height);

        public Run(string mime, MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate, Action<string>? log)
        {
            _log = log;
            _width = source.Width!.Value;
            _height = source.Height!.Value;

            // ── the encoder first, because the decoder needs its input surface ────────────────────────────
            var output = MediaFormat.CreateVideoFormat(AvcMime, _width, _height)!;
            output.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
            // A bitrate proportional to the picture rather than a constant: 0.15 bits per pixel per frame is
            // the usual rule of thumb for H.264, and the alternative — one number for every resolution —
            // is either wasteful on a thumbnail or visibly bad on a film.
            output.SetInteger(MediaFormat.KeyBitRate, (int)Math.Clamp(_width * (long)_height * 3 / 10, 400_000, 12_000_000));
            output.SetInteger(MediaFormat.KeyFrameRate, (int)Math.Clamp(source.FrameRate ?? 30, 1, 120));
            // One keyframe a second. ⚠ This is a SEEKING decision, not a quality one: the sync sample table
            // is what a player seeks to, so a long GOP produces a file that scrubs badly however good it looks.
            output.SetInteger(MediaFormat.KeyIFrameInterval, 1);

            _encoder = MediaCodec.CreateEncoderByType(AvcMime)!;
            _encoder.Configure(output, null, null, MediaCodecConfigFlags.Encode);
            _bridge = _encoder.CreateInputSurface()!;
            _encoder.Start();

            // ── the decoder, and the MINIMAL format is a measured trap rather than tidiness ───────────────
            // 🔴 `MediaCodecList.FindDecoderForFormat` REFUSES the format MediaExtractor hands you: it
            // carries `profile`, `level`, `max-bitrate`, `frame-count` and `sar-*`, and nothing matches all
            // of them — so a working decoder looks ABSENT. Measured 2026-08-10: the extractor's own format
            // found nothing, while mime + dimensions found `c2.android.mpeg4.decoder` on the same device.
            // Configuring is the same story, so the format built here carries only what a decoder needs.
            var input = MediaFormat.CreateVideoFormat(mime, _width, _height)!;
            if (!codecPrivate.IsEmpty)
            {
                // The container's initialisation data. Empty is legal for some codecs and fatal for others —
                // an MPEG-4 Part 2 decoder without its VOL header produces green frames rather than an error.
                input.SetByteBuffer("csd-0", Java.Nio.ByteBuffer.Wrap(codecPrivate.ToArray())!);
            }

            _decoder = MediaCodec.CreateDecoderByType(mime)!;
            _decoder.Configure(input, _bridge, null, MediaCodecConfigFlags.None);
            _decoder.Start();
        }

        /// <inheritdoc />
        public IReadOnlyList<MediaFrame> Push(MediaFrame frame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (frame.Data.IsEmpty) return [];

            var index = _decoder.DequeueInputBuffer(TimeoutUs);
            if (index >= 0)
            {
                var buffer = _decoder.GetInputBuffer(index)!;
                buffer.Clear();
                if (frame.Data.Length > buffer.Remaining())
                {
                    Report(_log, $"[Shenora.Android] a {frame.Data.Length}-byte frame exceeds the decoder's "
                               + $"{buffer.Remaining()}-byte input buffer.");
                    return [];
                }
                buffer.Put(frame.Data.ToArray());
                _decoder.QueueInputBuffer(index, 0, frame.Data.Length, frame.PresentationTimeUs, MediaCodecBufferFlags.None);
            }

            var produced = new List<MediaFrame>();
            PumpDecoder(produced, endOfStream: false);
            DrainEncoder(produced, endOfStream: false);
            return produced;
        }

        /// <inheritdoc />
        public IReadOnlyList<MediaFrame> Drain()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var produced = new List<MediaFrame>();

            var index = _decoder.DequeueInputBuffer(TimeoutUs);
            if (index >= 0) _decoder.QueueInputBuffer(index, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);

            PumpDecoder(produced, endOfStream: true);

            // 🔴 The encoder holds a GOP, and only this tells it no more pictures are coming. Without it the
            // file ends up to a second short, well-formed, with nothing reporting the loss.
            try { _encoder.SignalEndOfInputStream(); } catch (Exception) { /* already signalled */ }
            DrainEncoder(produced, endOfStream: true);
            return produced;
        }

        /// <summary>
        /// Move decoded pictures onto the shared surface. ⚠ <c>render: true</c> is what hands the frame to
        /// the encoder — releasing without it silently discards the picture, and the output is simply short.
        /// </summary>
        private void PumpDecoder(List<MediaFrame> produced, bool endOfStream)
        {
            while (true)
            {
                var index = _decoder.DequeueOutputBuffer(_info, endOfStream ? TimeoutUs : 0);
                if (index == (int)MediaCodecInfoState.TryAgainLater) return;
                if (index == (int)MediaCodecInfoState.OutputFormatChanged) continue;
                if (index < 0) continue;

                var end = (_info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                _decoder.ReleaseOutputBuffer(index, render: _info.Size > 0);

                // Keep the encoder moving while the decoder runs, or a long clip fills its output queue.
                DrainEncoder(produced, endOfStream: false);
                if (end) return;
            }
        }

        /// <summary>Collect encoded H.264, converting it into the form MP4 carries.</summary>
        private void DrainEncoder(List<MediaFrame> produced, bool endOfStream)
        {
            while (true)
            {
                var index = _encoder.DequeueOutputBuffer(_info, endOfStream ? TimeoutUs : 0);
                if (index == (int)MediaCodecInfoState.TryAgainLater) return;
                if (index == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    // The authoritative csd, once the encoder has settled on one.
                    var format = _encoder.OutputFormat;
                    if (format is not null && OutputConfig.IsEmpty) OutputConfig = AvcCFrom(format);
                    continue;
                }
                if (index < 0) continue;

                var buffer = _encoder.GetOutputBuffer(index);
                if (buffer is not null && _info.Size > 0)
                {
                    var bytes = new byte[_info.Size];
                    buffer.Position(_info.Offset);
                    buffer.Get(bytes);

                    if ((_info.Flags & MediaCodecBufferFlags.CodecConfig) != 0)
                    {
                        // 🔴 SPS and PPS arrive as ONE Annex-B blob and MP4 wants an `avcC` box. Writing the
                        // blob straight into the sample entry produces a file that opens and shows nothing.
                        if (OutputConfig.IsEmpty) OutputConfig = AvcCFromAnnexB(bytes);
                    }
                    else
                    {
                        // 🔴 And the FRAMES are Annex-B too — start codes, where MP4 stores 4-byte lengths.
                        // Copying them across unchanged is the classic "plays in VLC, black in a browser".
                        produced.Add(new MediaFrame(LengthPrefixed(bytes), _info.PresentationTimeUs,
                            (_info.Flags & MediaCodecBufferFlags.KeyFrame) != 0));
                    }
                }

                var end = (_info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                _encoder.ReleaseOutputBuffer(index, render: false);
                if (end) return;
            }
        }

        /// <summary>Build an <c>avcC</c> from the encoder's own csd buffers, when it reported them separately.</summary>
        private static ReadOnlyMemory<byte> AvcCFrom(MediaFormat format)
        {
            try
            {
                var sps = format.GetByteBuffer("csd-0");
                var pps = format.GetByteBuffer("csd-1");
                if (sps is null || pps is null) return ReadOnlyMemory<byte>.Empty;
                return AvcC(Bytes(sps), Bytes(pps));
            }
            catch (Exception) { return ReadOnlyMemory<byte>.Empty; }

            static byte[] Bytes(Java.Nio.ByteBuffer buffer)
            {
                var bytes = new byte[buffer.Remaining()];
                buffer.Get(bytes);
                buffer.Rewind();
                return StripStartCode(bytes);
            }
        }

        /// <summary>Split one Annex-B blob into its SPS and PPS and build an <c>avcC</c>.</summary>
        private static ReadOnlyMemory<byte> AvcCFromAnnexB(byte[] blob)
        {
            var units = SplitAnnexB(blob);
            byte[]? sps = null, pps = null;
            foreach (var unit in units)
            {
                if (unit.Length == 0) continue;
                var type = unit[0] & 0x1F;
                if (type == 7) sps ??= unit;
                else if (type == 8) pps ??= unit;
            }
            return sps is null || pps is null ? ReadOnlyMemory<byte>.Empty : AvcC(sps, pps);
        }

        /// <summary>
        /// The <c>AVCDecoderConfigurationRecord</c> MP4 stores in its sample entry: a version, the three
        /// profile bytes copied out of the SPS, then the parameter sets with 16-bit lengths.
        /// </summary>
        private static byte[] AvcC(byte[] sps, byte[] pps)
        {
            if (sps.Length < 4) return [];
            var record = new List<byte>(sps.Length + pps.Length + 16)
            {
                1, sps[1], sps[2], sps[3],
                0xFF,                       // 6 bits reserved + 2 bits: 4-byte NAL lengths, matching the frames
                0xE1,                       // 3 bits reserved + 5 bits: one SPS
                (byte)(sps.Length >> 8), (byte)sps.Length,
            };
            record.AddRange(sps);
            record.Add(1);                  // one PPS
            record.Add((byte)(pps.Length >> 8));
            record.Add((byte)pps.Length);
            record.AddRange(pps);
            return [.. record];
        }

        /// <summary>Annex-B start codes to 4-byte big-endian lengths, which is what MP4 carries.</summary>
        private static byte[] LengthPrefixed(byte[] annexB)
        {
            var units = SplitAnnexB(annexB);
            if (units.Count == 0) return annexB;   // no start code found: already length-prefixed, pass it on

            var total = 0;
            foreach (var unit in units) total += unit.Length + 4;
            var output = new byte[total];
            var at = 0;
            foreach (var unit in units)
            {
                output[at] = (byte)(unit.Length >> 24);
                output[at + 1] = (byte)(unit.Length >> 16);
                output[at + 2] = (byte)(unit.Length >> 8);
                output[at + 3] = (byte)unit.Length;
                Array.Copy(unit, 0, output, at + 4, unit.Length);
                at += unit.Length + 4;
            }
            return output;
        }

        /// <summary>Every NAL unit in an Annex-B buffer, start codes removed. Handles 3- and 4-byte codes.</summary>
        private static List<byte[]> SplitAnnexB(byte[] data)
        {
            var units = new List<byte[]>();
            var start = -1;
            for (var i = 0; i + 2 < data.Length; i++)
            {
                if (data[i] != 0 || data[i + 1] != 0) continue;
                var codeLength = data[i + 2] == 1 ? 3 : (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1 ? 4 : 0);
                if (codeLength == 0) continue;

                if (start >= 0) units.Add(data[start..i]);
                start = i + codeLength;
                i = start - 1;
            }
            if (start >= 0 && start < data.Length) units.Add(data[start..]);
            return units;
        }

        private static byte[] StripStartCode(byte[] unit)
        {
            var units = SplitAnnexB(unit);
            return units.Count > 0 ? units[0] : unit;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // ⚠ Two hardware codecs AND a Surface. A device has only a handful of each, and leaking them
            // does not leak memory — it makes the NEXT conversion fail with a resource error naming nothing.
            foreach (var codec in new[] { _decoder, _encoder })
            {
                try { codec.Stop(); } catch (Exception) { }
                try { codec.Release(); } catch (Exception) { }
                codec.Dispose();
            }
            try { _bridge.Release(); } catch (Exception) { }
            _bridge.Dispose();
        }
    }
}
