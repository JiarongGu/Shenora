using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;

using Android.Media;
using Android.Views;
using static Shenora.Android.AndroidMediaCodecs;

namespace Shenora.Android;

/// <summary>
/// Android's PICTURE converter — a platform decoder rendering into a platform H.264 encoder, for a codec
/// the device decodes but its webview refuses. Measured: <c>mpeg4</c>, which reaches <c>readyState = 4</c>
/// with <c>error</c> null and a <c>0×0</c> picture (<c>.claude/knowledge/mobile-shells.md</c>).
/// <para>
/// 🔴 <b>SURFACE-TO-SURFACE:</b> the decoder renders into the ENCODER'S OWN INPUT SURFACE and no frame is
/// ever read back — no colour conversion, no stride arithmetic, nothing device-specific to get wrong.
/// </para>
/// <para>
/// ⚠ The decoder for the motivating case (<c>c2.android.mpeg4.decoder</c>) is SOFTWARE, so the work is
/// real per frame; the planner still prefers a COPY wherever the container can carry the stream.
/// </para>
/// </summary>
public static class AndroidMediaVideoConversion
{
    private const string AvcMime = "video/avc";

    /// <summary>
    /// Register this platform converter into a conversion pipeline as a MIDDLEWARE — it DECLINES anything
    /// that is not a video stream it can handle, so one pipeline carries both kinds. Dispose to remove it.
    /// </summary>
    public static IDisposable Use(MediaConversionPipeline pipeline, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return pipeline.Use((source, codecPrivate) => Begin(source, codecPrivate, log), Claims);
    }

    /// <summary>
    /// What this converter OFFERS, from the same table <see cref="MimeOf"/> reads. ⚠ Computed on access,
    /// never a static initialiser: that would read the table before it exists and claim nothing at all.
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
                                                   ILogger? log)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Pictures and nothing else, which is what lets one pipeline serve every kind.
        if (source.Kind is not MediaStreamKind.Video) return null;
        if (source.Codec is null || MimeOf(source.Codec) is not { } mime) return null;
        if (!HasCodec(mime, encoder: false) || !HasCodec(AvcMime, encoder: true)) return null;

        // A codec cannot be configured without dimensions; a probe that omits them is not starting a run.
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

    /// <summary>
    /// The planner's lowercase names to Android's MIME types. ⚠ H.264 and HEVC are ABSENT: MP4 carries
    /// both, so the remuxer COPIES them losslessly instead of re-encoding.
    /// </summary>
    private static readonly Dictionary<string, string> Mimes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mpeg4"] = "video/mp4v-es",
        ["mpeg2video"] = "video/mpeg2",
        ["vp8"] = "video/x-vnd.on2.vp8",
        ["vp9"] = "video/x-vnd.on2.vp9",
        ["av1"] = "video/av01",
    };

    private static string? MimeOf(string codec) => Mimes.TryGetValue(codec, out var mime) ? mime : null;

    /// <summary>One picture's decode-then-encode, driven synchronously through a shared Surface.</summary>
    private sealed class Run : IMediaStreamConversionRun
    {
        private const int TimeoutUs = 10_000;

        private readonly MediaCodec _decoder;
        private readonly MediaCodec _encoder;
        private readonly Surface _bridge;
        private readonly MediaCodec.BufferInfo _info = new();
        private readonly ILogger? _log;
        private readonly int _width;
        private readonly int _height;
        private bool _disposed;

        public ReadOnlyMemory<byte> OutputConfig { get; private set; }

        /// <summary>⚠ Zero: a picture times every frame individually, so the muxer reads those instead.</summary>
        public int OutputFramesPerPacket => 0;

        public MediaStreamInfo OutputFormat => new(MediaStreamKind.Video, "h264", Width: _width, Height: _height);

        public Run(string mime, MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate, ILogger? log)
        {
            _log = log;
            _width = source.Width!.Value;
            _height = source.Height!.Value;

            // ── the encoder first, because the decoder needs its input surface ────────────────────────────
            var output = MediaFormat.CreateVideoFormat(AvcMime, _width, _height)!;
            output.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
            var frameRate = (int)Math.Clamp(source.FrameRate ?? 30, 1, 120);
            // ⚠ Bits per pixel per FRAME, so the rate has to carry the frame rate — 0.15 bpp/frame is
            // ordinary for H.264. Without the frame-rate factor 720p lands on the 400 kbps floor and the
            // 12 Mbps ceiling needs a 40-megapixel picture to reach, which is how the omission was spotted.
            var bitRate = (int)Math.Clamp(_width * (long)_height * frameRate * 15 / 100, 400_000, 12_000_000);
            output.SetInteger(MediaFormat.KeyBitRate, bitRate);
            output.SetInteger(MediaFormat.KeyFrameRate, frameRate);
            // 🔴 One keyframe a SECOND, which `SegmentGrid` cuts its segments on — changing this makes the
            // media tier's grid illegal. Also a seeking decision: a long GOP scrubs badly however good it looks.
            output.SetInteger(MediaFormat.KeyIFrameInterval, 1);

            _encoder = MediaCodec.CreateEncoderByType(AvcMime)!;
            _encoder.Configure(output, null, null, MediaCodecConfigFlags.Encode);
            _bridge = _encoder.CreateInputSurface()!;
            _encoder.Start();

            // ⚠ SAID OUT LOUD, because the REQUEST is the only half of this the kit owns and the only half
            // observable from outside — and an encoder need not honour it. Measured on an API 36 emulator:
            // the same source came out at ~1.7 Mbps whether this asked for 4.1 Mbps or the 400 kbps floor,
            // so an "the output is the wrong size" report can only be attributed with this line in hand.
            AppCallback.Log(_log, () => $"[Shenora.Android] video encoder: {_width}x{_height}@{frameRate} "
                                        + $"-> requested {bitRate / 1000} kbps");

            // ── the decoder, whose MINIMAL format is a measured trap rather than tidiness ─────────────────
            // 🔴 `MediaCodecList.FindDecoderForFormat` REFUSES the format `MediaExtractor` hands you — it
            // carries `profile`/`level`/`sar-*` and nothing matches — so a working decoder looks ABSENT,
            // and configuring is the same story (`.claude/knowledge/mobile-shells.md`).
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
            else
            {
                // A picture never queued is a visible glitch with nothing else saying why.
                Report(_log, $"[Shenora.Android] the decoder had no input buffer free; "
                           + $"a {frame.Data.Length}-byte picture frame was dropped.");
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

            // The decoder must HEAR the end of stream or pictures it is still reordering never flush. A
            // buffer can be briefly scarce right after the last Push, so wait a few timeouts.
            var index = -1;
            for (var i = 0; i < 10 && index < 0; i++) index = _decoder.DequeueInputBuffer(TimeoutUs);
            if (index >= 0) _decoder.QueueInputBuffer(index, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
            else Report(_log, "[Shenora.Android] no input buffer freed to carry the decoder's "
                            + "end-of-stream; pictures still inside it are lost.");

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
