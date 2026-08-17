using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;

using System.Runtime.InteropServices;
using Shenora;

namespace Shenora.iOS;

/// <summary>
/// iOS's <see cref="IMediaStreamConversion"/> — AudioToolbox's <c>AudioConverter</c>, chained decoder →
/// PCM → AAC encoder. The soundtrack half of the translation layer on this platform (D59).
/// <para>
/// <b>Why it is TWO converters and not one.</b> <c>AudioConverterNew</c> will happily build a
/// compressed→compressed converter for some pairs, but not reliably across the formats that matter here,
/// and a chain is what the Android side already does (MediaCodec decoder → AAC encoder). Two converters
/// with PCM in the middle is the shape both platforms can actually satisfy.
/// </para>
/// <para>
/// ⚠ <b>It converts what the DEVICE decodes and MP4 can carry — nothing wider.</b> Ask
/// <see cref="IosMediaCapability"/> first; the middleware DECLINES (returns null) for anything else
/// rather than producing a file that opens and plays silence.
/// </para>
/// <para>
/// 🔴 <b>Registering it is what closes a gap that was ANNOUNCED but not fixed.</b> iOS reports AC-3 as
/// decodable and AAC as encodable, so the planner says <c>Transcode</c> — and until this existed, no
/// conversion was registered on iOS, so <c>Mp4Remuxer</c> dropped the soundtrack and reported it in
/// <see cref="MediaRemuxerResult.Dropped"/>. Honest, but silent. This makes the film play.
/// </para>
/// </summary>
public static class IosMediaAudioConversion
{
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    // FourCCs, big-endian in an int. 'aac ' really does carry a trailing space (kAudioFormatMPEG4AAC).
    private const uint FormatPcm = 0x6C70636D;    // 'lpcm'
    private const uint FormatAac = 0x61616320;    // 'aac '
    private const uint FormatAc3 = 0x61632D33;    // 'ac-3'
    private const uint FormatEac3 = 0x65632D33;   // 'ec-3'

    /// <summary>Signed, packed, host-endian 16-bit PCM — kAudioFormatFlagIsSignedInteger | IsPacked.</summary>
    private const uint PcmFlags = 0x4 | 0x8;

    /// <summary>
    /// The kit's own <c>OSStatus</c>, returned by the input callback to mean <b>"no more input right
    /// now"</b> — as distinct from "the stream has ended", which is what zero packets with <c>noErr</c>
    /// means and which the converter LATCHES permanently.
    /// <para>
    /// 🔴 It exists because this distinction is not optional and there is no API for it: a converter fed
    /// one frame per call runs out of input on every single call, and saying so the wrong way kills it
    /// after the FIRST frame. <c>AudioConverterFillComplexBuffer</c> returns whatever the callback returns,
    /// so the caller gets this value back and reads it as success.
    /// </para>
    /// <para>'shnr', so a log line naming it is unmistakably ours rather than CoreAudio's.</para>
    /// </summary>
    private const int NoMoreInput = 0x73686E72;

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
    public static void Use(MediaConversionPipeline pipeline, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        pipeline.Use((source, _) =>
        {
            if (source.Codec is not { } codec || !Inputs.TryGetValue(codec, out var inputFormat)) return null;
            // Defaults matter here: a wrong rate produces audio at the wrong SPEED rather than an error,
            // which is the trap MediaConversionMiddleware's own docs call out.
            var channels = source.Channels is > 0 ? source.Channels.Value : 2;
            var rate = source.SampleRate is > 0 ? source.SampleRate.Value : 48000;
            return AudioConverterRun.TryStart(inputFormat, rate, channels, log);
        }, Claims);
    }

    /// <summary>
    /// What this converter OFFERS — read from the SAME <see cref="Inputs"/> table it converts from, so the
    /// declaration and the behaviour cannot drift.
    /// <para>
    /// 🔴 <b>Declaring it is what removes a WILDCARD from the chain, and the wildcard was not harmless.</b>
    /// A converter registered without claims means "ask me about anything", so ONE of them made every other
    /// converter's claim moot and left the DEVICE as the only gate — which reported <c>h264</c> as
    /// convertible on a phone, because every phone decodes it and nothing here offers to convert it.
    /// Measured. ⚠ Computed on access, never a static initialiser: that runs in declaration order
    /// and would read the table before it exists, claiming nothing at all.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MediaStreamClaim> Claims =>
        [.. Inputs.Keys.Select(codec => new MediaStreamClaim(MediaStreamKind.Audio, codec))];

    /// <summary>One in-flight conversion: an AC-3 (or E-AC-3) stream in, AAC packets out.</summary>
    private sealed class AudioConverterRun : IMediaStreamConversionRun
    {
        private readonly ILogger? _log;
        private readonly IntPtr _decoder;
        private readonly IntPtr _encoder;
        private readonly List<byte> _pcm = [];
        private bool _disposed;

        // ── the tally, because the SEGMENT writer times this track by COUNTING packets ───────────────
        // 🔴 A soundtrack that emits too few packets is silently SHORT rather than broken: every fragment
        // is well-formed, every append succeeds, and the stream stalls because `buffered` is the
        // intersection of the tracks. These four numbers separate the three candidates — the decoder
        // returning nothing, the encoder returning nothing, and the arithmetic that turns packets into
        // time — none of which the pipeline could otherwise tell apart.
        private int _pushes;
        private int _packets;
        private long _inputBytes;
        private long _decodedBytes;
        private bool _summarised;

        // How many calls of each leg still report in full — see Trace. Decode gets more because it is the
        // leg under investigation and its first frame is where the answer is.
        private int _decodeTraces = 4;
        private int _encodeTraces = 2;

        private AudioConverterRun(IntPtr decoder, IntPtr encoder, int rate, int channels, ILogger? log)
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

        /// <summary>The output as a stream description — one answer for either kind.</summary>
        public MediaStreamInfo OutputFormat => new(MediaStreamKind.Audio, "aac",
            Channels: OutputChannels > 0 ? OutputChannels : null,
            SampleRate: OutputSampleRate > 0 ? OutputSampleRate : null);
        private int OutputSampleRate { get; }

        /// <inheritdoc />
        private int OutputChannels { get; }

        /// <summary>
        /// Build both converters, or answer null. ⚠ Null means "this device cannot", which the pipeline
        /// treats as a DECLINE and passes to the next middleware — never as a failure.
        /// </summary>
        public static AudioConverterRun? TryStart(uint inputFormat, int rate, int channels, ILogger? log)
        {
            var declared = Compressed(inputFormat, rate, channels);
            var compressedIn = Complete(declared);
            var pcm = Pcm(rate, channels);
            var aac = Compressed(FormatAac, rate, channels);

            IntPtr decoder = IntPtr.Zero, encoder = IntPtr.Zero;
            try
            {
                if (AudioConverterNew(ref compressedIn, ref pcm, out decoder) != 0) return Fail(decoder, encoder);
                if (AudioConverterNew(ref pcm, ref aac, out encoder) != 0) return Fail(decoder, encoder);

                // The three descriptions that can disagree, printed together because only their DIFFERENCE
                // is informative: what the kit asserted, what CoreAudio says the format really is, and what
                // the converter settled on. A decoder handed an under-specified ASBD is the leading
                // explanation for a conversion that consumes input and emits nothing.
                var built = decoder;
                AppCallback.Log(log, () =>
                    $"[Shenora.iOS] decoder in: declared {Describe(declared)} | completed {Describe(compressedIn)} "
                    + $"| current {Describe(Current(built, PropCurrentInput))}");
                AppCallback.Log(log, () =>
                    $"[Shenora.iOS] decoder out: declared {Describe(pcm)} "
                    + $"| current {Describe(Current(built, PropCurrentOutput))}");

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
        public IReadOnlyList<MediaFrame> Push(MediaFrame frame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (frame.Data.IsEmpty) return [];

            _pushes++;
            _inputBytes += frame.Data.Length;

            var decoded = Convert(_decoder, frame.Data, compressedInput: true);
            if (decoded.Length == 0) return [];
            _decodedBytes += decoded.Length;

            _pcm.AddRange(decoded);
            // Every AAC frame is a sync sample; the muxer derives audio timing from the packet count, so the
            // presentation time is a formality here and stated rather than invented.
            var out_ = Encode(drain: false).Select(b => new MediaFrame(b, 0)).ToArray();
            _packets += out_.Length;
            return out_;
        }

        /// <inheritdoc />
        public IReadOnlyList<MediaFrame> Drain()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // 🔴 THE DECODER FIRST, and it is not symmetry — it holds real audio back. A decoder runs a
            // priming latency: the first AC-3 packet measured here yielded 1248 PCM frames rather than
            // 1536, and those 288 frames come out only when the stream is declared over. Draining just the
            // PCM buffer would end every soundtrack a fraction early, well-formed and unreported — the
            // exact failure this method exists to prevent.
            var tail = Convert(_decoder, ReadOnlyMemory<byte>.Empty, compressedInput: true, final: true);
            if (tail.Length > 0) { _pcm.AddRange(tail); _decodedBytes += tail.Length; }

            // ⚠ Everything still buffered, or the soundtrack ends early and NOTHING reports it — the file is
            // well-formed and simply stops. That is the failure this method exists to prevent.
            var out_ = Encode(drain: true).Select(b => new MediaFrame(b, 0)).ToArray();
            _packets += out_.Length;
            Summarise("drain");
            return out_;
        }

        /// <summary>
        /// One line saying where the soundtrack went — emitted at drain AND at dispose, because a run that
        /// is killed mid-segment never drains and that is exactly when the answer matters.
        /// </summary>
        private void Summarise(string at)
        {
            if (_summarised) return;
            _summarised = true;
            var seconds = OutputSampleRate > 0 ? _packets * (double)OutputFramesPerPacket / OutputSampleRate : 0;
            Shenora.AppCallback.Log(_log, () =>
                $"[AUDIO] {at}: pushes={_pushes} in={_inputBytes}B decodedPcm={_decodedBytes}B "
              + $"packets={_packets} rate={OutputSampleRate} ch={OutputChannels} "
              + $"bytesPerPacket={OutputChannels * 2 * OutputFramesPerPacket} -> {seconds:0.###}s of sound");
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

            if (drain)
            {
                // The encoder's own tail, for the same reason the decoder has one — it is told the stream
                // is over exactly once, here, and answers with anything it was still holding.
                var flushed = Convert(_encoder, ReadOnlyMemory<byte>.Empty, compressedInput: false, final: true);
                if (flushed.Length > 0) packets.Add(flushed);
            }

            return packets;
        }

        /// <summary>
        /// Run one buffer through a converter, whose input it PULLS through <see cref="InputProc"/>.
        /// <para>
        /// ⚠ <b><c>AudioConverterFillComplexBuffer</c>, because the simple form cannot do this.</b>
        /// <c>AudioConverterConvertBuffer</c> refuses any conversion needing a complex converter, and
        /// compressed→PCM always is. Measured: the simulator answered <c>'op??'</c> and an
        /// iPhone 17 Pro answered status 0 with ZERO BYTES — the same wrong API failing two different ways.
        /// </para>
        /// </summary>
        private unsafe byte[] Convert(IntPtr converter, ReadOnlyMemory<byte> input, bool compressedInput,
                                      bool final = false)
        {
            // Generous: decoded PCM is far larger than its compressed source, and an undersized buffer is
            // reported by the converter rather than overrunning.
            var capacity = compressedInput ? Math.Max(input.Length * 64, 65536) : Math.Max(input.Length, 8192);
            var output = new byte[capacity];
            var source = input.ToArray();
            var bytesPerPcmFrame = Math.Max(OutputChannels * 2, 1);
            uint outputSize;
            int status;
            // ⚠ Reported on failure, because "produced 0 bytes" alone cannot distinguish "the converter
            // refused" from "it wanted more input than one frame carries" — and guessing between those is
            // how the previous implementation stayed broken.
            uint produced = 0, asked = 0;
            // The callback's own tally, copied back out. 🔴 It is the single most discriminating fact
            // available here: "the converter never pulled" and "the converter pulled, took the frame and
            // emitted nothing" are opposite diagnoses that look identical from the return values alone.
            InputContext tally;

            fixed (byte* inputPtr = source)
            fixed (byte* outputPtr = output)
            {
                var context = new InputContext
                {
                    Data = (IntPtr)inputPtr,
                    Size = (uint)source.Length,
                    Channels = (uint)OutputChannels,
                    Compressed = compressedInput ? 1 : 0,
                    // A compressed frame is ONE packet; PCM is one packet per frame, which is what tells the
                    // encoder how much audio it was handed rather than merely how many bytes.
                    Packets = compressedInput ? (source.Length > 0 ? 1u : 0u)
                                              : (uint)(source.Length / bytesPerPcmFrame),
                    Final = final ? 1 : 0,
                    Description = new AudioStreamPacketDescription { DataByteSize = (uint)source.Length },
                };

                var list = new AudioBufferList
                {
                    NumberBuffers = 1,
                    Buffer = new AudioBuffer
                    {
                        NumberChannels = (uint)OutputChannels,
                        DataByteSize = (uint)capacity,
                        Data = (IntPtr)outputPtr,
                    },
                };

                // How many packets to ASK for: as much PCM as the buffer holds when decoding, and exactly
                // one AAC packet when encoding — the caller already chunks PCM to one frame per call.
                var wanted = compressedInput ? (uint)(capacity / bytesPerPcmFrame) : 1u;

                asked = wanted;
                status = AudioConverterFillComplexBuffer(
                    converter, &InputProc, (IntPtr)(&context), ref wanted, ref list, IntPtr.Zero);
                produced = wanted;

                // ⚠ The converter reports what it WROTE here, not in `wanted`. Reading the packet count and
                // multiplying would be right for PCM and wrong for AAC, whose packets vary in size.
                outputSize = list.Buffer.DataByteSize;
                tally = context;
            }

            // Our own starvation code is a SUCCESS: the converter ran out of input part-way through a call
            // and stopped there, which is the normal shape of a stream fed one frame at a time. Everything
            // it managed to write before that is in the buffer and must not be thrown away.
            var starved = status == NoMoreInput;
            if (starved) status = 0;

            Trace(compressedInput, input.Length, source, status, asked, produced, outputSize, tally, starved);

            // ⚠ An EMPTY final call is how the decoder is flushed, and producing nothing is its correct
            // answer when it held nothing back. Reporting that as a failure would cry wolf on every
            // successful conversion.
            if (status == 0 && outputSize == 0 && final && input.IsEmpty) return [];

            if (status != 0 || outputSize == 0)
            {
                // 🔴 BOTH BRANCHES REPORT, and the silent one is the one that mattered. Measured
                // 2026-08-09: the SIMULATOR fails loudly here with 'op??', while a real iPhone 17 Pro
                // returns status 0 and ZERO BYTES for the same file — success that converted nothing. Only
                // the error branch logged, so on the device this produced no diagnostic whatsoever and the
                // sole evidence was the remuxer's refusal. "Accepted every frame and wrote nothing" is the
                // exact failure this repo has a rule about; it should never be the quiet path.
                Log(() => status != 0
                    ? $"[Shenora.iOS] AudioConverter returned {StatusName(status)}."
                    : $"[Shenora.iOS] AudioConverter reported SUCCESS and produced 0 bytes from a "
                      + $"{input.Length}-byte input ({(compressedInput ? "decode" : "encode")}: asked for "
                      + $"{asked} packet(s), got {produced}) — the conversion is not happening.");
                return [];
            }
            return output[..(int)outputSize];
        }

        /// <summary>
        /// Report what one conversion call actually did — asked, produced, and what the input pump was
        /// pulled for. Budgeted to the first few calls of each leg: the diagnosis is in the FIRST ones, and
        /// an unbudgeted line here is one per audio frame of a whole film.
        /// <para>
        /// 🔴 <b>It reports on SUCCESS as well as failure</b>, because "worked" and "worked for the wrong
        /// reason" are the pair this subsystem keeps confusing — a leg that emits a couple of packets and
        /// then nothing reads as working right up until the file is silent.
        /// </para>
        /// </summary>
        private void Trace(bool compressedInput, int inputLength, byte[] source, int status,
                           uint asked, uint produced, uint outputSize, InputContext tally, bool starved)
        {
            if (_log is null) return;
            ref var budget = ref (compressedInput ? ref _decodeTraces : ref _encodeTraces);
            if (budget <= 0) return;
            budget--;

            var leg = compressedInput ? "decode" : "encode";
            // The first bytes only for a COMPRESSED input: an AC-3 syncframe opens 0B 77, so this settles
            // "is the frame we were handed even the thing we told the converter it was" without a second run.
            var head = compressedInput ? $" head={Hex(source, 8)}" : "";
            Log(() =>
                $"[Shenora.iOS] {leg}: in={inputLength}B{head} status={StatusName(status)}"
                + $"{(starved ? " starved" : "")} asked={asked} produced={produced} out={outputSize}B "
                + $"| pump calls={tally.Calls} lastRequest={tally.Requested} "
                + $"served={tally.ServedPackets}pkt/{tally.ServedBytes}B");
        }

        /// <summary>The first bytes of a buffer, so a frame can be recognised rather than assumed.</summary>
        /// <remarks>⚠ <c>System.Convert</c> spelled out: this class has its own <c>Convert</c> method, which
        /// shadows the BCL type inside it.</remarks>
        private static string Hex(byte[] data, int count)
            => System.Convert.ToHexString(data, 0, Math.Min(count, data.Length));

        private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_log, message, exception: failure);

        /// <summary>
        /// An <c>OSStatus</c> as CoreAudio actually means it: a FOURCC, plus the name where the kit knows one.
        /// <para>
        /// 🔴 <b>Never print the raw integer — that is a diagnostic which reads as evidence and carries
        /// none.</b> `AudioConverter returned 1869627199` is <c>0x6F703F3F</c>, <c>'op??'</c>,
        /// <c>kAudioConverterErr_OperationNotSupported</c>, and nobody reads that off a decimal. The repo
        /// has the same rule for bare exception messages (`probe-diagnostics.md`); a naked error CODE is
        /// the same failure wearing a number.
        /// </para>
        /// </summary>
        private static string StatusName(int status)
        {
            var chars = new[]
            {
                (char)((status >> 24) & 0xFF), (char)((status >> 16) & 0xFF),
                (char)((status >> 8) & 0xFF), (char)(status & 0xFF),
            };
            // Only render a FourCC when every byte is printable — otherwise it is an ordinary negative
            // OSStatus and the decimal is the honest form.
            var printable = Array.TrueForAll(chars, c => c is >= ' ' and < (char)127);
            var code = printable ? $"'{new string(chars)}'" : status.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var known = status switch
            {
                0x666D743F => " (kAudioConverterErr_FormatNotSupported — this device will not decode that codec)",
                0x6F703F3F => " (kAudioConverterErr_OperationNotSupported — the converter exists but refuses this conversion)",
                0x21706B64 => " (kAudioConverterErr_InvalidInputSize)",
                0x21627566 => " (kAudioConverterErr_InvalidOutputSize)",
                _ => "",
            };
            return $"{code}{known}";
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Before the handles go: a run killed mid-segment never drains, and that is precisely the case
            // where "where did the sound go" needs an answer. Summarise() is idempotent.
            Summarise("dispose");
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

    // kAudioConverterCurrentInputStreamDescription / …OutputStreamDescription — what the converter SETTLED
    // on, which is not necessarily what it was handed.
    private const uint PropCurrentInput = 0x63697364;   // 'cisd'
    private const uint PropCurrentOutput = 0x636F7364;  // 'cosd'

    /// <summary>kAudioFormatProperty_FormatInfo — CoreAudio completing a partially-filled description.</summary>
    private const uint PropFormatInfo = 0x666D7469;     // 'fmti'

    [DllImport(AudioToolbox)]
    private static extern int AudioConverterGetProperty(
        IntPtr converter, uint propertyId, ref uint size, ref StreamDescription data);

    [DllImport(AudioToolbox)]
    private static extern int AudioFormatGetProperty(
        uint propertyId, uint specifierSize, IntPtr specifier, ref uint size, ref StreamDescription data);

    /// <summary>One buffer of an <see cref="AudioBufferList"/>. Sequential layout mirrors <c>AudioBuffer</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioBuffer
    {
        public uint NumberChannels;
        public uint DataByteSize;
        public IntPtr Data;
    }

    /// <summary>
    /// An <c>AudioBufferList</c> carrying exactly ONE buffer.
    /// <para>
    /// ⚠ The real struct is variable-length — a count followed by that many buffers — so this shape is only
    /// valid while `NumberBuffers` is 1. That holds here because both legs are INTERLEAVED
    /// (`kAudioFormatFlagIsPacked`, no `NonInterleaved` flag), and interleaved audio is one buffer whatever
    /// the channel count. A non-interleaved format would need one buffer per channel and this would
    /// silently read past the end.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioBufferList
    {
        public uint NumberBuffers;
        public AudioBuffer Buffer;
    }

    /// <summary>Where one packet sits inside a buffer, for formats whose packets vary in size.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamPacketDescription
    {
        public long StartOffset;
        public uint VariableFramesInPacket;
        public uint DataByteSize;
    }

    /// <summary>
    /// What the input callback is handed. Lives in the CALLER'S stack frame for the duration of one
    /// <c>FillComplexBuffer</c> call and is passed as an opaque pointer — the converter never outlives it.
    /// <para>
    /// The last four fields are the callback's TALLY, written by it and read back by the caller once the
    /// call returns. ⚠ They are counted here rather than logged from the callback deliberately: a managed
    /// diagnostic sink invoked from inside an <see cref="UnmanagedCallersOnlyAttribute"/> frame is app code
    /// running on the converter's own stack, and the kit does not run app code there.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct InputContext
    {
        public IntPtr Data;
        public uint Size;
        public uint Packets;
        public uint Channels;
        public int Compressed;
        public int Served;

        /// <summary>
        /// Non-zero only on the LAST call of a stream. It is what licenses the callback to answer
        /// "0 packets, noErr" — the one answer that ends the conversion for good.
        /// </summary>
        public int Final;

        public AudioStreamPacketDescription Description;

        /// <summary>How many times the converter pulled. ZERO is a whole diagnosis on its own.</summary>
        public uint Calls;

        /// <summary>What the converter asked for on the LAST pull, before the callback overwrote it.</summary>
        public uint Requested;

        public uint ServedPackets;
        public uint ServedBytes;
    }

    /// <summary>
    /// The converter's input pump: it calls this to PULL, rather than being pushed a buffer.
    ///
    /// <para>
    /// 🔴 <b>This callback is the whole reason for the rewrite.</b> `AudioConverterConvertBuffer` cannot
    /// perform a conversion that needs a complex converter — and compressed→PCM always does, because the
    /// converter must be free to ask for input at its own rhythm rather than being handed one buffer.
    /// Measured on hardware: the simple form answered `'op??'` on a simulator and SUCCEEDED WITH ZERO BYTES
    /// on an iPhone.
    /// </para>
    ///
    /// <para>
    /// ⚠ <b>Serving the data ONCE and then answering "0 packets" is the contract</b>, not a shortcut. Zero
    /// with a success status is how a caller says "no more input"; returning the same buffer again would
    /// loop the converter forever, and returning an error would abort a conversion that had in fact
    /// finished.
    /// </para>
    /// </summary>
    [UnmanagedCallersOnly]
    private static unsafe int InputProc(IntPtr converter, uint* packets, AudioBufferList* data,
                                        AudioStreamPacketDescription** description, IntPtr userData)
    {
        var context = (InputContext*)userData;
        context->Calls++;
        context->Requested = *packets;
        if (context->Served != 0)
        {
            *packets = 0;
            if (description is not null) *description = null;
            // 🔴 THE WHOLE BUG WAS RETURNING 0 HERE. Zero packets with noErr does not mean "nothing more
            // in this call" — it means THE STREAM HAS ENDED, and the converter latches that permanently.
            // Measured on the iOS simulator: the first frame decoded (1248 PCM frames out of one
            // 834-byte AC-3 packet, after priming), and every frame after it returned 0 with `pump calls=0`
            // — the converter never asked again, because it had been told the stream was over. A non-zero
            // status is how a starved pump says "not yet"; the converter stays alive and the caller sees
            // its own code come back.
            return context->Final != 0 ? 0 : NoMoreInput;
        }

        context->Served = 1;
        context->ServedPackets = context->Packets;
        context->ServedBytes = context->Size;
        *packets = context->Packets;
        data->NumberBuffers = 1;
        data->Buffer.NumberChannels = context->Channels;
        data->Buffer.DataByteSize = context->Size;
        data->Buffer.Data = context->Data;

        // Packet descriptions are for VARIABLE-size packets only. PCM has none — a fixed frame size is
        // implied by the format — and handing one over anyway is rejected.
        if (description is not null)
            *description = context->Compressed != 0 ? &context->Description : null;
        return 0;
    }

    [DllImport(AudioToolbox)]
    private static extern unsafe int AudioConverterFillComplexBuffer(
        IntPtr converter,
        delegate* unmanaged<IntPtr, uint*, AudioBufferList*, AudioStreamPacketDescription**, IntPtr, int> inputProc,
        IntPtr userData,
        ref uint outputPackets,
        ref AudioBufferList outputData,
        IntPtr packetDescriptions);

    private static StreamDescription Compressed(uint formatId, int rate, int channels) => new()
    {
        SampleRate = rate,
        FormatId = formatId,
        ChannelsPerFrame = (uint)channels,
        FramesPerPacket = formatId == FormatAac ? 1024u : 1536u,
    };

    /// <summary>
    /// Ask CoreAudio to FILL IN a compressed description rather than asserting one.
    /// <para>
    /// 🔴 <b>An <c>AudioStreamBasicDescription</c> for a compressed format has fields only the codec
    /// knows</b> — flags, bytes per packet, the real frames per packet — and the kit can supply only the
    /// three that come from the container (format, rate, channels). <c>kAudioFormatProperty_FormatInfo</c>
    /// completes the rest in place. A converter built from an under-specified description can still be
    /// CREATED, which is why <see cref="IosMediaCapability"/> answers yes, and can then consume input and
    /// emit nothing — the failure this is here to remove.
    /// </para>
    /// <para>
    /// ⚠ Falls back to what it was given. A refusal means "CoreAudio does not describe this format", which
    /// the converter is about to say for itself in a way the caller already handles.
    /// </para>
    /// </summary>
    private static StreamDescription Complete(StreamDescription declared)
    {
        var completed = declared;
        var size = (uint)Marshal.SizeOf<StreamDescription>();
        try
        {
            return AudioFormatGetProperty(PropFormatInfo, 0, IntPtr.Zero, ref size, ref completed) == 0
                ? completed
                : declared;
        }
        catch (Exception)
        {
            return declared;
        }
    }

    /// <summary>One of a converter's live stream descriptions, or null if it will not say.</summary>
    private static StreamDescription? Current(IntPtr converter, uint property)
    {
        var description = default(StreamDescription);
        var size = (uint)Marshal.SizeOf<StreamDescription>();
        try
        {
            return AudioConverterGetProperty(converter, property, ref size, ref description) == 0
                ? description
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A stream description in one line. Every field, because the missing one is the point — a diagnostic
    /// that prints the fields it expects to matter cannot show you the one that did.
    /// </summary>
    private static string Describe(StreamDescription? description)
    {
        if (description is not { } d) return "(unavailable)";
        return $"{FourCc(d.FormatId)} {d.SampleRate:0.###}Hz ch={d.ChannelsPerFrame} fpp={d.FramesPerPacket} "
             + $"bpp={d.BytesPerPacket} bpf={d.BytesPerFrame} bits={d.BitsPerChannel} flags=0x{d.FormatFlags:X}";
    }

    private static string FourCc(uint value)
    {
        var chars = new[]
        {
            (char)((value >> 24) & 0xFF), (char)((value >> 16) & 0xFF),
            (char)((value >> 8) & 0xFF), (char)(value & 0xFF),
        };
        return Array.TrueForAll(chars, c => c is >= ' ' and < (char)127)
            ? $"'{new string(chars)}'"
            : $"0x{value:X8}";
    }

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
