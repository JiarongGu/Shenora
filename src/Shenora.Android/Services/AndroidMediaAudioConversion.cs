using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;

using Android.Media;
using static Shenora.Android.AndroidMediaCodecs;

namespace Shenora.Android;

/// <summary>
/// Android's <see cref="IMediaStreamConversion"/> — the device's own decoder chained into its own AAC
/// encoder, so this ships no codec bytes and carries no licence (D51/D52).
/// <para>
/// ⚠ What it can convert is per DEVICE: <see cref="CanConvert"/> asks <c>MediaCodecList</c> rather than a
/// table, and a device that cannot answers false so the planner says <c>Unsupported</c> instead of
/// starting work that cannot finish. AOSP has no AC-3 decoder at all.
/// </para>
/// </summary>
public static class AndroidMediaAudioConversion
{
    private const string AacMime = "audio/mp4a-latm";

    /// <summary>
    /// Register this platform converter into a conversion pipeline as a MIDDLEWARE, so an app's own
    /// converter can sit in front of it. Dispose to remove it.
    /// </summary>
    /// <param name="pipeline">The pipeline to register into.</param>
    /// <param name="log">
    /// Diagnostics. 🔴 Without it a codec failure here is unexplainable: <c>Mp4Remuxer</c> reports
    /// everything as <c>SourceUnreadable "malformed source"</c>, accusing the FILE.
    /// </param>
    public static IDisposable Use(MediaConversionPipeline pipeline, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return pipeline.Use((source, codecPrivate) => Begin(source, codecPrivate, log), Claims);
    }

    /// <summary>Can this device convert the codec? Exposed so a capability report can ask without starting one.</summary>
    public static bool CanConvert(string codec)
    {
        var mime = MimeOf(codec);
        return mime is not null && HasCodec(mime, encoder: false) && HasCodec(AacMime, encoder: true);
    }

    /// <summary>The middleware body: answer with a run, or null to let the next converter try.</summary>
    private static IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate,
                                                   ILogger? log)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Codec is null || MimeOf(source.Codec) is not { } mime) return null;
        if (!HasCodec(mime, encoder: false) || !HasCodec(AacMime, encoder: true)) return null;

        try
        {
            return new Run(mime, source, codecPrivate, log);
        }
        catch (Exception ex)
        {
            // A codec that will not configure reports as "cannot". No exception text escapes to the
            // CALLER, which may be answering a page; it reaches the app's own log instead.
            Report(log, $"[Shenora.Android] the {mime} converter would not configure "
                      + $"({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    /// <summary>The planner's lowercase names to Android's MIME types. Unknown names are refused, not guessed.</summary>
    private static readonly Dictionary<string, string> Mimes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ac3"] = "audio/ac3",
        ["eac3"] = "audio/eac3",
        ["dts"] = "audio/vnd.dts",
        ["mp3"] = "audio/mpeg",
        ["vorbis"] = "audio/vorbis",
        ["flac"] = "audio/flac",
        ["opus"] = "audio/opus",
        ["alac"] = "audio/alac",
    };

    /// <summary>
    /// What this converter OFFERS, from the same table <see cref="MimeOf"/> reads. ⚠ Computed on access,
    /// never a static initialiser: that would read the table before it exists and claim nothing at all.
    /// </summary>
    public static IReadOnlyList<MediaStreamClaim> Claims =>
        [.. Mimes.Keys.Select(codec => new MediaStreamClaim(MediaStreamKind.Audio, codec))];

    private static string? MimeOf(string codec) => Mimes.TryGetValue(codec, out var mime) ? mime : null;

    /// <summary>One stream's decode-then-encode, driven through MediaCodec's synchronous API.</summary>
    private sealed class Run : IMediaStreamConversionRun
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
        private int OutputSampleRate { get; set; }
        private int OutputChannels { get; set; }

        /// <summary>
        /// The output as a stream description. ⚠ The ENCODER's numbers, not the source's — valid only
        /// after it has produced output, since a decoder may resample and a downmix may change channels.
        /// </summary>
        public MediaStreamInfo OutputFormat => new(MediaStreamKind.Audio, "aac",
            Channels: OutputChannels > 0 ? OutputChannels : null,
            SampleRate: OutputSampleRate > 0 ? OutputSampleRate : null);

        private readonly ILogger? _log;
        private readonly string mimeForDiagnostics;

        public Run(string mime, MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate, ILogger? log = null)
        {
            _log = log;
            mimeForDiagnostics = mime;
            // From the container, never a guess: a wrong rate plays at the wrong speed rather than failing.
            OutputSampleRate = source.SampleRate is > 0 ? source.SampleRate.Value : 48000;
            OutputChannels = source.Channels is > 0 ? Math.Min(source.Channels.Value, 2) : 2;

            var input = MediaFormat.CreateAudioFormat(mime, OutputSampleRate, source.Channels is > 0 ? source.Channels.Value : 2);
            // 🔴 SIZE THE INPUT BUFFERS, or the decoder sizes them itself and gets it wrong. Measured on
            // Android: without this the very first MP3 frame — 314 bytes — threw
            // `Java.Nio.BufferOverflowException` from `buffer.Put`, because MediaCodec had allocated input
            // buffers smaller than one compressed frame. 64 KB exceeds a frame of any codec accepted here.
            input.SetInteger(MediaFormat.KeyMaxInputSize, 64 * 1024);
            if (!codecPrivate.IsEmpty)
            {
                // csd-0 is Android's codec initialisation data: absent for AC-3, required for Vorbis and
                // FLAC — a decoder configured without it produces silence, not an error.
                input.SetByteBuffer("csd-0", Java.Nio.ByteBuffer.Wrap(codecPrivate.ToArray()));
            }

            _decoder = MediaCodec.CreateDecoderByType(mime)!;
            _decoder.Configure(input, null, null, MediaCodecConfigFlags.None);
            _decoder.Start();

            // ⚠ Downmixed to at most STEREO: this tier targets web playback, not fidelity.
            var output = MediaFormat.CreateAudioFormat(AacMime, OutputSampleRate, OutputChannels);
            output.SetInteger(MediaFormat.KeyAacProfile, (int)MediaCodecProfileType.Aacobjectlc);
            output.SetInteger(MediaFormat.KeyBitRate, 128_000);
            _encoder = MediaCodec.CreateEncoderByType(AacMime)!;
            _encoder.Configure(output, null, null, MediaCodecConfigFlags.Encode);
            _encoder.Start();
            _encoderStarted = true;
        }

        /// <summary>⚠ Wrapped so a platform failure names itself in the log; still rethrows.</summary>
        public IReadOnlyList<MediaFrame> Push(MediaFrame frame)
        {
            try { return PushCore(frame); }
            catch (Exception ex)
            {
                Report(_log, $"[Shenora.Android] the decoder failed on a {frame.Data.Length}-byte frame "
                           + $"({ex.GetType().Name}: {ex.Message}).");
                throw;
            }
        }

        private IReadOnlyList<MediaFrame> PushCore(MediaFrame frame)
        {
            var produced = new List<MediaFrame>();
            if (_disposed) return produced;

            var index = _decoder.DequeueInputBuffer(TimeoutUs);
            if (index >= 0)
            {
                var buffer = _decoder.GetInputBuffer(index)!;
                buffer.Clear();
                // ⚠ A frame larger than the buffer is a named REFUSAL — `Put` would throw a
                // `BufferOverflowException` naming neither the codec nor the size.
                if (frame.Data.Length > buffer.Remaining())
                {
                    throw new InvalidOperationException(
                        $"the {mimeForDiagnostics} decoder's input buffer has {buffer.Remaining()} byte(s) free "
                        + $"(capacity {buffer.Capacity()}, position {buffer.Position()}, limit {buffer.Limit()}) "
                        + $"and this frame is {frame.Data.Length}");
                }
                buffer.Put(frame.Data.ToArray());
                _decoder.QueueInputBuffer(index, 0, frame.Data.Length, _presentationUs, MediaCodecBufferFlags.None);
                // Monotonic is all the encoder needs; the caller rebuilds real timing from the frame count.
                _presentationUs += 1_000_000L * OutputFramesPerPacket / Math.Max(OutputSampleRate, 1);
            }
            else
            {
                // A frame never queued is LOST — the soundtrack is one frame shorter and silently so.
                Report(_log, $"[Shenora.Android] the decoder had no input buffer free; "
                           + $"a {frame.Data.Length}-byte input frame was dropped.");
            }

            Pump(produced, endOfStream: false);
            return produced;
        }

        /// <summary>Same guard as <see cref="Push"/>, for the same reason — see its remarks.</summary>
        public IReadOnlyList<MediaFrame> Drain()
        {
            try { return DrainCore(); }
            catch (Exception ex)
            {
                Report(_log, $"[Shenora.Android] the codec failed while draining "
                           + $"({ex.GetType().Name}: {ex.Message}).");
                throw;
            }
        }

        private IReadOnlyList<MediaFrame> DrainCore()
        {
            var produced = new List<MediaFrame>();
            if (_disposed) return produced;

            // The decoder must HEAR the end of stream or the PCM it still holds never surfaces. A buffer
            // can be briefly scarce right after the last Push, so wait a few timeouts before giving up;
            // the only symptom of failing here is a short soundtrack.
            var index = -1;
            for (var i = 0; i < 10 && index < 0; i++) index = _decoder.DequeueInputBuffer(TimeoutUs);
            if (index >= 0)
            {
                _decoder.QueueInputBuffer(index, 0, 0, _presentationUs, MediaCodecBufferFlags.EndOfStream);
            }
            else
            {
                Report(_log, "[Shenora.Android] no input buffer freed to carry the decoder's "
                           + "end-of-stream; audio still inside it is lost.");
            }

            Pump(produced, endOfStream: true);
            return produced;
        }

        /// <summary>
        /// Move everything the decoder has into the encoder, and collect what the encoder gives back.
        /// 🔴 The <c>OutputFormatChanged</c> case is where the AAC <b>csd-0</b> arrives — the file's
        /// AudioSpecificConfig. Miss it and the MP4 opens and plays nothing, with every box valid.
        /// </summary>
        private void Pump(List<MediaFrame> produced, bool endOfStream)
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

        /// <summary>
        /// Hand decoded PCM to the encoder, in as many chunks as its input buffers will take.
        /// 🔴 <b>A DECODER FRAME IS ROUTINELY BIGGER THAN ONE ENCODER INPUT BUFFER</b> — MP3 decodes 1152
        /// samples and AC-3 1536 against the AAC encoder's own 1024 — so a single <c>Put(pcm)</c> throws
        /// <c>BufferOverflowException</c> on the FIRST frame (<c>docs/design/mobile-shells.md</c>).
        /// </summary>
        private void FeedEncoder(byte[] pcm, bool last)
        {
            if (!_encoderStarted) return;

            var offset = 0;
            do
            {
                var index = _encoder.DequeueInputBuffer(TimeoutUs);
                if (index < 0)
                {
                    // Dropped PCM makes a shorter soundtrack that still plays, so say so.
                    Report(_log, $"[Shenora.Android] the encoder had no input buffer free; "
                               + $"{pcm.Length - offset} byte(s) of decoded audio were dropped.");
                    return;
                }

                var buffer = _encoder.GetInputBuffer(index)!;
                buffer.Clear();
                var take = Math.Min(pcm.Length - offset, buffer.Remaining());

                // 🔴 A ZERO-CAPACITY BUFFER WOULD SPIN THIS LOOP FOREVER — `offset` never advances and the
                // encoder is fed empty buffers as fast as it hands them out. An empty PCM buffer with
                // `last` set is legitimate (that is end-of-stream), so the guard is "no room AND still
                // carrying audio", not "no room".
                if (take == 0 && offset < pcm.Length)
                {
                    Report(_log, $"[Shenora.Android] the encoder offered a buffer with no room; "
                               + $"{pcm.Length - offset} byte(s) of decoded audio were dropped.");
                    _encoder.QueueInputBuffer(index, 0, 0, _presentationUs, MediaCodecBufferFlags.None);
                    return;
                }

                if (take > 0) buffer.Put(pcm, offset, take);
                offset += take;

                // EndOfStream on the LAST chunk only — an earlier one truncates the soundtrack rather
                // than failing, because audio still unfed is simply dropped.
                var final = last && offset >= pcm.Length;
                _encoder.QueueInputBuffer(index, 0, take, _presentationUs,
                    final ? MediaCodecBufferFlags.EndOfStream : MediaCodecBufferFlags.None);
            }
            while (offset < pcm.Length);
        }

        private void DrainEncoder(List<MediaFrame> produced, bool endOfStream)
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

                // ⚠ The AudioSpecificConfig arriving as a BUFFER rather than in the format, as some
                // encoders do. Never write it as a frame — but MP4's sample entry needs it.
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
                    // Every AAC frame is a sync sample; the time is the encoder's own for this buffer.
                    produced.Add(new MediaFrame(frame, _info.PresentationTimeUs, IsKeyframe: true));
                }

                var end = (_info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                _encoder.ReleaseOutputBuffer(index, render: false);
                if (end) return;
            }
        }

        /// <summary>
        /// ⚠ Releasing MATTERS: a device has only a handful of codec instances, and leaking one makes the
        /// NEXT conversion fail with a resource error that names nothing.
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
