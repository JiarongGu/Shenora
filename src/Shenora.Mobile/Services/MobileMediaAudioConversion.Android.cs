#if ANDROID
using Android.Media;
using Shenora.Media;

namespace Shenora.Mobile;

/// <summary>
/// Android's <see cref="IMediaAudioConversion"/> — a platform decoder feeding a platform AAC encoder.
///
/// <para>
/// <b>Two codecs chained, because MediaCodec has no compressed-to-compressed mode.</b> The soundtrack is
/// decoded to PCM and re-encoded as AAC; both are the device's own codecs, so this ships no bytes and
/// carries no licence (D51/D52).
/// </para>
///
/// <para>
/// ⚠ <b>What it can do is per DEVICE.</b> AOSP has no AC-3 decoder at all — measured 2026-08-07 on an API
/// 36 emulator — while a handset may well have one, because Android codec support is vendor-declared.
/// <see cref="CanConvert"/> therefore asks <c>MediaCodecList</c> rather than consulting a table, and a
/// device that cannot answers false so the planner says <c>Unsupported</c> instead of starting work that
/// cannot finish.
/// </para>
/// </summary>
public sealed class MobileMediaAudioConversion : IMediaAudioConversion
{
    private const string AacMime = "audio/mp4a-latm";

    /// <inheritdoc />
    public bool CanConvert(string codec)
    {
        var mime = MimeOf(codec);
        return mime is not null && HasCodec(mime, encoder: false) && HasCodec(AacMime, encoder: true);
    }

    /// <inheritdoc />
    public IMediaAudioConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Codec is null || MimeOf(source.Codec) is not { } mime) return null;
        if (!HasCodec(mime, encoder: false) || !HasCodec(AacMime, encoder: true)) return null;

        try
        {
            return new Run(mime, source, codecPrivate);
        }
        catch (Exception)
        {
            // A codec that will not configure reports as "cannot", which the caller already handles as a
            // refusal. No exception text escapes — this runs on behalf of logic that may answer a page.
            return null;
        }
    }

    /// <summary>Is a codec for this MIME actually instantiable here? RegularCodecs, so a hidden one does not count.</summary>
    private static bool HasCodec(string mime, bool encoder)
    {
        try
        {
            var list = new MediaCodecList(MediaCodecListKind.RegularCodecs);
            foreach (var info in list.GetCodecInfos() ?? [])
            {
                if (info.IsEncoder != encoder) continue;
                foreach (var type in info.GetSupportedTypes() ?? [])
                {
                    if (string.Equals(type, mime, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>The planner's lowercase names to Android's MIME types. Unknown names are refused, not guessed.</summary>
    private static string? MimeOf(string codec) => codec.ToLowerInvariant() switch
    {
        "ac3" => "audio/ac3",
        "eac3" => "audio/eac3",
        "dts" => "audio/vnd.dts",
        "mp3" => "audio/mpeg",
        "vorbis" => "audio/vorbis",
        "flac" => "audio/flac",
        "opus" => "audio/opus",
        "alac" => "audio/alac",
        _ => null,
    };

    /// <summary>
    /// One stream's decode-then-encode, driven synchronously.
    ///
    /// <para>
    /// ⚠ <b>Synchronous MediaCodec on purpose.</b> The async callback mode is the modern API and it is the
    /// wrong shape here: this seam is pull-based (<c>Push</c> in, frames out), so a callback would need a
    /// queue and a lock behind it to be re-serialised into exactly what the synchronous API already gives.
    /// </para>
    /// </summary>
    private sealed class Run : IMediaAudioConversionRun
    {
        private const int TimeoutUs = 10_000;

        private readonly MediaCodec _decoder;
        private readonly MediaCodec _encoder;
        private readonly MediaCodec.BufferInfo _info = new();
        private long _presentationUs;
        private bool _encoderStarted;
        private bool _disposed;

        public ReadOnlyMemory<byte> OutputConfig { get; private set; }
        public int OutputFramesPerPacket => 1024;   // AAC-LC, always
        public int OutputSampleRate { get; private set; }
        public int OutputChannels { get; private set; }

        public Run(string mime, MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate)
        {
            // The rate and channel count are what the DECODER is configured with; a wrong rate plays at the
            // wrong speed rather than failing, which is why they come from the container rather than a guess.
            OutputSampleRate = source.SampleRate is > 0 ? source.SampleRate.Value : 48000;
            OutputChannels = source.Channels is > 0 ? Math.Min(source.Channels.Value, 2) : 2;

            var input = MediaFormat.CreateAudioFormat(mime, OutputSampleRate, source.Channels is > 0 ? source.Channels.Value : 2);
            if (!codecPrivate.IsEmpty)
            {
                // csd-0 is how Android is handed a codec's initialisation data. Absent for AC-3, required
                // for Vorbis and FLAC — and a decoder configured without it produces silence, not an error.
                input.SetByteBuffer("csd-0", Java.Nio.ByteBuffer.Wrap(codecPrivate.ToArray()));
            }

            _decoder = MediaCodec.CreateDecoderByType(mime)!;
            _decoder.Configure(input, null, null, MediaCodecConfigFlags.None);
            _decoder.Start();

            // ⚠ Downmixed to at most STEREO. A 5.1 AAC track is legal but every browser that plays AAC plays
            // stereo, and the point of this tier is web playback rather than fidelity.
            var output = MediaFormat.CreateAudioFormat(AacMime, OutputSampleRate, OutputChannels);
            output.SetInteger(MediaFormat.KeyAacProfile, (int)MediaCodecProfileType.Aacobjectlc);
            output.SetInteger(MediaFormat.KeyBitRate, 128_000);
            _encoder = MediaCodec.CreateEncoderByType(AacMime)!;
            _encoder.Configure(output, null, null, MediaCodecConfigFlags.Encode);
            _encoder.Start();
            _encoderStarted = true;
        }

        public IReadOnlyList<ReadOnlyMemory<byte>> Push(ReadOnlyMemory<byte> frame)
        {
            var produced = new List<ReadOnlyMemory<byte>>();
            if (_disposed) return produced;

            var index = _decoder.DequeueInputBuffer(TimeoutUs);
            if (index >= 0)
            {
                var buffer = _decoder.GetInputBuffer(index)!;
                buffer.Clear();
                buffer.Put(frame.ToArray());
                _decoder.QueueInputBuffer(index, 0, frame.Length, _presentationUs, MediaCodecBufferFlags.None);
                // The presentation clock only has to be MONOTONIC for the encoder; the real timing is
                // rebuilt from the output frame count by the caller, which is exact.
                _presentationUs += 1_000_000L * OutputFramesPerPacket / Math.Max(OutputSampleRate, 1);
            }

            Pump(produced, endOfStream: false);
            return produced;
        }

        public IReadOnlyList<ReadOnlyMemory<byte>> Drain()
        {
            var produced = new List<ReadOnlyMemory<byte>>();
            if (_disposed) return produced;

            var index = _decoder.DequeueInputBuffer(TimeoutUs);
            if (index >= 0)
            {
                _decoder.QueueInputBuffer(index, 0, 0, _presentationUs, MediaCodecBufferFlags.EndOfStream);
            }

            Pump(produced, endOfStream: true);
            return produced;
        }

        /// <summary>
        /// Move everything the decoder has into the encoder, and collect what the encoder gives back.
        /// <para>
        /// 🔴 The <c>OutputFormatChanged</c> case is where the AAC <b>csd-0</b> arrives, and it is the file's
        /// AudioSpecificConfig. Miss it and the MP4 carries an empty audio configuration — a file that opens
        /// and plays nothing, with every box valid.
        /// </para>
        /// </summary>
        private void Pump(List<ReadOnlyMemory<byte>> produced, bool endOfStream)
        {
            while (true)
            {
                var decoded = _decoder.DequeueOutputBuffer(_info, TimeoutUs);
                if (decoded == (int)MediaCodecInfoState.TryAgainLater) { if (!endOfStream) break; }
                else if (decoded == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    var format = _decoder.OutputFormat!;
                    if (format.ContainsKey(MediaFormat.KeySampleRate)) OutputSampleRate = format.GetInteger(MediaFormat.KeySampleRate);
                    continue;
                }
                else if (decoded >= 0)
                {
                    var pcm = _decoder.GetOutputBuffer(decoded)!;
                    var bytes = new byte[_info.Size];
                    pcm.Position(_info.Offset);
                    pcm.Get(bytes);
                    _decoder.ReleaseOutputBuffer(decoded, render: false);
                    FeedEncoder(bytes, last: false);
                }

                DrainEncoder(produced, endOfStream: false);

                if (!endOfStream) break;
                if (decoded >= 0 && (_info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) break;
                if (decoded == (int)MediaCodecInfoState.TryAgainLater) break;
            }

            if (!endOfStream) return;

            FeedEncoder([], last: true);
            DrainEncoder(produced, endOfStream: true);
        }

        private void FeedEncoder(byte[] pcm, bool last)
        {
            if (!_encoderStarted) return;
            var index = _encoder.DequeueInputBuffer(TimeoutUs);
            if (index < 0) return;

            var buffer = _encoder.GetInputBuffer(index)!;
            buffer.Clear();
            if (pcm.Length > 0) buffer.Put(pcm);
            _encoder.QueueInputBuffer(index, 0, pcm.Length, _presentationUs,
                last ? MediaCodecBufferFlags.EndOfStream : MediaCodecBufferFlags.None);
        }

        private void DrainEncoder(List<ReadOnlyMemory<byte>> produced, bool endOfStream)
        {
            while (true)
            {
                var index = _encoder.DequeueOutputBuffer(_info, endOfStream ? TimeoutUs : 0);
                if (index == (int)MediaCodecInfoState.TryAgainLater) return;
                if (index == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    var format = _encoder.OutputFormat!;
                    if (format.ContainsKey("csd-0"))
                    {
                        var csd = format.GetByteBuffer("csd-0")!;
                        var config = new byte[csd.Remaining()];
                        csd.Get(config);
                        OutputConfig = config;
                    }
                    if (format.ContainsKey(MediaFormat.KeySampleRate)) OutputSampleRate = format.GetInteger(MediaFormat.KeySampleRate);
                    if (format.ContainsKey(MediaFormat.KeyChannelCount)) OutputChannels = format.GetInteger(MediaFormat.KeyChannelCount);
                    continue;
                }
                if (index < 0) return;

                // ⚠ A CODEC-CONFIG buffer is the AudioSpecificConfig arriving as a BUFFER rather than in the
                // format — some encoders do it this way. It is not audio and must never be written as a
                // frame, but it must be KEPT: it is what MP4's sample entry needs.
                if ((_info.Flags & MediaCodecBufferFlags.CodecConfig) != 0)
                {
                    var buffer = _encoder.GetOutputBuffer(index)!;
                    var config = new byte[_info.Size];
                    buffer.Position(_info.Offset);
                    buffer.Get(config);
                    if (OutputConfig.IsEmpty) OutputConfig = config;
                    _encoder.ReleaseOutputBuffer(index, render: false);
                    continue;
                }

                if (_info.Size > 0)
                {
                    var buffer = _encoder.GetOutputBuffer(index)!;
                    var frame = new byte[_info.Size];
                    buffer.Position(_info.Offset);
                    buffer.Get(frame);
                    produced.Add(frame);
                }

                var end = (_info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                _encoder.ReleaseOutputBuffer(index, render: false);
                if (end) return;
            }
        }

        /// <summary>
        /// ⚠ Releasing MATTERS: these are hardware or system codec instances and a device has only a handful.
        /// Leaking one does not leak memory — it makes the NEXT conversion fail with a resource error that
        /// names nothing.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _decoder.Stop(); } catch (Exception) { }
            try { _decoder.Release(); } catch (Exception) { }
            try { _encoder.Stop(); } catch (Exception) { }
            try { _encoder.Release(); } catch (Exception) { }
            _decoder.Dispose();
            _encoder.Dispose();
            _info.Dispose();
        }
    }
}
#endif
