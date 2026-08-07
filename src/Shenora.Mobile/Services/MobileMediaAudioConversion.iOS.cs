#if IOS || MACCATALYST
using System.Runtime.InteropServices;
using Shenora;
using Shenora.Media;

namespace Shenora.Mobile;

/// <summary>
/// iOS's <see cref="IMediaAudioConversion"/> — AudioToolbox's <c>AudioConverter</c>, chained decoder →
/// PCM → AAC encoder. The soundtrack half of the translation layer on this platform (D59).
/// <para>
/// <b>Why it is TWO converters and not one.</b> <c>AudioConverterNew</c> will happily build a
/// compressed→compressed converter for some pairs, but not reliably across the formats that matter here,
/// and a chain is what the Android side already does (MediaCodec decoder → AAC encoder). Two converters
/// with PCM in the middle is the shape both platforms can actually satisfy.
/// </para>
/// <para>
/// ⚠ <b>It converts what the DEVICE decodes and MP4 can carry — nothing wider.</b> Ask
/// <see cref="MobileMediaCapability"/> first; the middleware DECLINES (returns null) for anything else
/// rather than producing a file that opens and plays silence.
/// </para>
/// <para>
/// 🔴 <b>Registering it is what closes a gap that was ANNOUNCED but not fixed.</b> iOS reports AC-3 as
/// decodable and AAC as encodable, so the planner says <c>Transcode</c> — and until this existed, no
/// conversion was registered on iOS, so <c>Mp4Remuxer</c> dropped the soundtrack and reported it in
/// <see cref="MediaRemuxerResult.Dropped"/>. Honest, but silent. This makes the film play.
/// </para>
/// </summary>
public static class MobileMediaAudioConversion
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    // FourCCs, big-endian in an int. 'aac ' really does carry a trailing space (kAudioFormatMPEG4AAC).
    private const uint FormatPcm = 0x6C70636D;    // 'lpcm'
    private const uint FormatAac = 0x61616320;    // 'aac '
    private const uint FormatAc3 = 0x61632D33;    // 'ac-3'
    private const uint FormatEac3 = 0x65632D33;   // 'ec-3'

    /// <summary>Signed, packed, host-endian 16-bit PCM — kAudioFormatFlagIsSignedInteger | IsPacked.</summary>
    private const uint PcmFlags = 0x4 | 0x8;

    /// <summary>What this can take as INPUT. AAC is absent on purpose: MP4 carries it already, so
    /// converting would be a lossy round-trip for nothing (the remuxer copies it instead).</summary>
    private static readonly Dictionary<string, uint> Inputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ac3"] = FormatAc3,
        ["eac3"] = FormatEac3,
    };

    /// <summary>
    /// Add this platform's converter to a pipeline, behind anything the app registered itself.
    /// <para>
    /// Registered rather than returned, so an adopter's own encoder can sit in FRONT of it with
    /// <c>pipeline.Use(...)</c> and this stays as the fallback — the composability D59 rests on.
    /// </para>
    /// </summary>
    public static void Use(MediaAudioPipeline pipeline, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        pipeline.Use((source, _) =>
        {
            if (source.Codec is not { } codec || !Inputs.TryGetValue(codec, out var inputFormat)) return null;
            // Defaults matter here: a wrong rate produces audio at the wrong SPEED rather than an error,
            // which is the trap MediaAudioMiddleware's own docs call out.
            var channels = source.Channels is > 0 ? source.Channels.Value : 2;
            var rate = source.SampleRate is > 0 ? source.SampleRate.Value : 48000;
            return AudioConverterRun.TryStart(inputFormat, rate, channels, log);
        });
    }

    /// <summary>One in-flight conversion: an AC-3 (or E-AC-3) stream in, AAC packets out.</summary>
    private sealed class AudioConverterRun : IMediaAudioConversionRun
    {
        private readonly Action<string>? _log;
        private readonly IntPtr _decoder;
        private readonly IntPtr _encoder;
        private readonly List<byte> _pcm = [];
        private bool _disposed;

        private AudioConverterRun(IntPtr decoder, IntPtr encoder, int rate, int channels, Action<string>? log)
        {
            _decoder = decoder;
            _encoder = encoder;
            OutputSampleRate = rate;
            OutputChannels = channels;
            _log = log;
            OutputConfig = AacConfig(rate, channels);
        }

        /// <summary>
        /// The 2-byte AudioSpecificConfig an MP4 <c>esds</c> needs: 5 bits object type (2 = AAC-LC), 4 bits
        /// sample-rate index, 4 bits channel configuration.
        /// <para>
        /// <b>Synthesised rather than read from the encoder</b>, and the alternative is worth naming: iOS
        /// exposes <c>kAudioConverterCompressionMagicCookie</c>, but for AAC that cookie is an
        /// ESDS-wrapped blob, so using it means parsing a descriptor tree to recover the same two bytes.
        /// Android does read its config from the encoder because <c>csd-0</c> IS the raw ASC there — the
        /// platforms differ, and matching each one's shape beats forcing a single path.
        /// </para>
        /// <para>
        /// ⚠ An unlisted sample rate falls back to the 48 kHz index. That is wrong-but-playable rather than
        /// broken, and every rate AC-3 actually uses is in the table.
        /// </para>
        /// </summary>
        private static byte[] AacConfig(int rate, int channels)
        {
            int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
            var index = Array.IndexOf(rates, rate);
            if (index < 0) index = 3;                                  // 48 kHz
            var config = Math.Clamp(channels, 1, 7);
            return
            [
                (byte)((2 << 3) | (index >> 1)),
                (byte)(((index & 1) << 7) | (config << 3)),
            ];
        }

        /// <inheritdoc />
        public ReadOnlyMemory<byte> OutputConfig { get; }

        /// <inheritdoc />
        /// <remarks>AAC's frame is 1024 samples. The timing table is built from this, so a wrong value
        /// produces audio that drifts against the picture rather than an error.</remarks>
        public int OutputFramesPerPacket => 1024;

        /// <inheritdoc />
        public int OutputSampleRate { get; }

        /// <inheritdoc />
        public int OutputChannels { get; }

        /// <summary>
        /// Build both converters, or answer null. ⚠ Null means "this device cannot", which the pipeline
        /// treats as a DECLINE and passes to the next middleware — never as a failure.
        /// </summary>
        public static AudioConverterRun? TryStart(uint inputFormat, int rate, int channels, Action<string>? log)
        {
            var compressedIn = Compressed(inputFormat, rate, channels);
            var pcm = Pcm(rate, channels);
            var aac = Compressed(FormatAac, rate, channels);

            IntPtr decoder = IntPtr.Zero, encoder = IntPtr.Zero;
            try
            {
                if (AudioConverterNew(ref compressedIn, ref pcm, out decoder) != 0) return Fail(decoder, encoder);
                if (AudioConverterNew(ref pcm, ref aac, out encoder) != 0) return Fail(decoder, encoder);
                return new AudioConverterRun(decoder, encoder, rate, channels, log);
            }
            catch (Exception)
            {
                // A missing framework or a bad descriptor reads as "cannot convert", which is the safe
                // direction: the remuxer then drops the track and SAYS SO rather than writing a broken file.
                return Fail(decoder, encoder);
            }

            static AudioConverterRun? Fail(IntPtr a, IntPtr b)
            {
                if (a != IntPtr.Zero) AudioConverterDispose(a);
                if (b != IntPtr.Zero) AudioConverterDispose(b);
                return null;
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<ReadOnlyMemory<byte>> Push(ReadOnlyMemory<byte> frame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (frame.IsEmpty) return [];

            var decoded = Convert(_decoder, frame, compressedInput: true);
            if (decoded.Length == 0) return [];

            _pcm.AddRange(decoded);
            return Encode(drain: false);
        }

        /// <inheritdoc />
        public IReadOnlyList<ReadOnlyMemory<byte>> Drain()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // ⚠ Everything still buffered, or the soundtrack ends early and NOTHING reports it — the file is
            // well-formed and simply stops. That is the failure this method exists to prevent.
            return Encode(drain: true);
        }

        /// <summary>
        /// Encode whole AAC frames out of the PCM buffer. A partial frame stays buffered unless draining,
        /// where it is padded — dropping it would clip the last few milliseconds of every conversion.
        /// </summary>
        private IReadOnlyList<ReadOnlyMemory<byte>> Encode(bool drain)
        {
            var bytesPerFrame = OutputChannels * 2 * OutputFramesPerPacket;
            var packets = new List<ReadOnlyMemory<byte>>();

            while (_pcm.Count >= bytesPerFrame)
            {
                var chunk = _pcm.GetRange(0, bytesPerFrame).ToArray();
                _pcm.RemoveRange(0, bytesPerFrame);
                var encoded = Convert(_encoder, chunk, compressedInput: false);
                if (encoded.Length > 0) packets.Add(encoded);
            }

            if (drain && _pcm.Count > 0)
            {
                var tail = new byte[bytesPerFrame];
                _pcm.CopyTo(tail, 0);
                _pcm.Clear();
                var encoded = Convert(_encoder, tail, compressedInput: false);
                if (encoded.Length > 0) packets.Add(encoded);
            }

            return packets;
        }

        /// <summary>
        /// Run one buffer through a converter.
        /// <para>
        /// ⚠ <b><c>AudioConverterConvertBuffer</c>, not <c>FillComplexBuffer</c>, and that is a real
        /// limitation rather than a shortcut.</b> The complex form needs a native callback the converter
        /// invokes to pull input, which means a function pointer into managed code and a pinned context
        /// across a P/Invoke boundary — correct but far more machinery. The simple form works for the
        /// fixed-size, whole-packet steps this chain does. **If a format needs variable packet sizes, this
        /// returns nothing for it and the track is dropped and reported — not silently mangled.**
        /// </para>
        /// </summary>
        private byte[] Convert(IntPtr converter, ReadOnlyMemory<byte> input, bool compressedInput)
        {
            // Generous: decoded PCM is far larger than its compressed source, and an undersized buffer is
            // reported by the converter rather than overrunning.
            var capacity = compressedInput ? Math.Max(input.Length * 64, 65536) : Math.Max(input.Length, 8192);
            var output = new byte[capacity];
            var outputSize = (uint)output.Length;

            var status = AudioConverterConvertBuffer(
                converter, (uint)input.Length, input.ToArray(), ref outputSize, output);

            if (status != 0 || outputSize == 0)
            {
                if (status != 0) Log(() => $"[Shenora.Mobile] AudioConverter returned {status}.");
                return [];
            }
            return output[..(int)outputSize];
        }

        private void Log(Func<string> message) => AppCallback.Log(_log, message);

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // BOTH, and neither throw: a leaked converter holds a hardware codec slot, and a device has few.
            if (_decoder != IntPtr.Zero) AudioConverterDispose(_decoder);
            if (_encoder != IntPtr.Zero) AudioConverterDispose(_encoder);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [DllImport(AudioToolbox)]
    private static extern int AudioConverterNew(ref StreamDescription source, ref StreamDescription destination, out IntPtr converter);

    [DllImport(AudioToolbox)]
    private static extern int AudioConverterDispose(IntPtr converter);

    [DllImport(AudioToolbox)]
    private static extern int AudioConverterConvertBuffer(
        IntPtr converter, uint inputSize, byte[] input, ref uint outputSize, byte[] output);

    private static StreamDescription Compressed(uint formatId, int rate, int channels) => new()
    {
        SampleRate = rate,
        FormatId = formatId,
        ChannelsPerFrame = (uint)channels,
        FramesPerPacket = formatId == FormatAac ? 1024u : 1536u,
    };

    private static StreamDescription Pcm(int rate, int channels) => new()
    {
        SampleRate = rate,
        FormatId = FormatPcm,
        FormatFlags = PcmFlags,
        ChannelsPerFrame = (uint)channels,
        FramesPerPacket = 1,
        BitsPerChannel = 16,
        BytesPerFrame = (uint)(channels * 2),
        BytesPerPacket = (uint)(channels * 2),
    };
}
#endif
