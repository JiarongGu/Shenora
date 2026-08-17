using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;

using Android.Media;
using static Shenora.Android.AndroidMediaCodecs;

namespace Shenora.Android;

/// <summary>
/// Android's <see cref="IMediaStreamConversion"/> — a platform decoder feeding a platform AAC encoder.
///
/// <para>
/// <b>Two codecs chained, because MediaCodec has no compressed-to-compressed mode.</b> The soundtrack is
/// decoded to PCM and re-encoded as AAC; both are the device's own codecs, so this ships no bytes and
/// carries no licence (D51/D52).
/// </para>
///
/// <para>
/// ⚠ <b>What it can do is per DEVICE.</b> AOSP has no AC-3 decoder at all — measured on an API
/// 36 emulator — while a handset may well have one, because Android codec support is vendor-declared.
/// <see cref="CanConvert"/> therefore asks <c>MediaCodecList</c> rather than consulting a table, and a
/// device that cannot answers false so the planner says <c>Unsupported</c> instead of starting work that
/// cannot finish.
/// </para>
/// </summary>
public static class AndroidMediaAudioConversion
{
    private const string AacMime = "audio/mp4a-latm";

    /// <summary>
    /// Register this platform converter into a conversion pipeline. Dispose to remove it.
    /// <para>
    /// A MIDDLEWARE rather than an implementation of the whole contract: an app that adds its own converter
    /// keeps this one behind it, so it only has to handle what it actually wants to improve on.
    /// </para>
    /// </summary>
    /// <param name="pipeline">The pipeline to register into.</param>
    /// <param name="log">
    /// Diagnostics, and it is not optional in spirit even though it is in signature.
    /// <para>
    /// 🔴 <b>Without it this converter cannot explain itself, and its caller is guaranteed not to.</b>
    /// <c>Mp4Remuxer</c> catches everything and reports <c>SourceUnreadable "malformed source"</c> — correct
    /// for a shipped path, because a media path must never reach a page — so a codec failure here surfaces
    /// as an accusation against the FILE. Measured: a device whose <c>CanConvert</c> and
    /// <c>Begin</c> both said yes then converted nothing, and there was no way to ask why.
    /// </para>
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
            // A codec that will not configure reports as "cannot", which the caller already handles as a
            // refusal. No exception text escapes TO THE CALLER — this runs on behalf of logic that may
            // answer a page — but it does reach the log, which is the app's own sink and not the page's.
            Report(log, $"[Shenora.Android] the {mime} converter would not configure "
                      + $"({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    // `Report` and `HasCodec` live in `AndroidMediaCodecs` — neither is converter-specific, and a copy
    // here would be byte-identical to the picture converter's.

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
    /// What this converter OFFERS — read from the SAME table <see cref="MimeOf"/> uses, so the declaration
    /// and the behaviour cannot drift. ⚠ Computed on access, never a static initialiser: that runs in
    /// declaration order and would read the table before it exists, claiming nothing at all.
    /// </summary>
    public static IReadOnlyList<MediaStreamClaim> Claims =>
        [.. Mimes.Keys.Select(codec => new MediaStreamClaim(MediaStreamKind.Audio, codec))];

    private static string? MimeOf(string codec) => Mimes.TryGetValue(codec, out var mime) ? mime : null;

    /// <summary>
    /// One stream's decode-then-encode, driven synchronously.
    ///
    /// <para>
    /// ⚠ <b>Synchronous MediaCodec on purpose.</b> The async callback mode is the modern API and it is the
    /// wrong shape here: this seam is pull-based (<c>Push</c> in, frames out), so a callback would need a
    /// queue and a lock behind it to be re-serialised into exactly what the synchronous API already gives.
    /// </para>
    /// </summary>
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
        /// The output as a stream description — the unified contract's single answer for either kind.
        /// <para>
        /// ⚠ Read AFTER the encoder has produced output: a decoder may resample and a downmix may change the
        /// channel count, so these are the ENCODER's numbers rather than the source's.
        /// </para>
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
            // The rate and channel count are what the DECODER is configured with; a wrong rate plays at the
            // wrong speed rather than failing, which is why they come from the container rather than a guess.
            OutputSampleRate = source.SampleRate is > 0 ? source.SampleRate.Value : 48000;
            OutputChannels = source.Channels is > 0 ? Math.Min(source.Channels.Value, 2) : 2;

            var input = MediaFormat.CreateAudioFormat(mime, OutputSampleRate, source.Channels is > 0 ? source.Channels.Value : 2);
            // 🔴 SIZE THE INPUT BUFFERS, or the decoder sizes them itself and gets it wrong.
            // Measured on Android: without this, the very first MP3 frame — 314 bytes — threw
            // `Java.Nio.BufferOverflowException` from `buffer.Put`, because MediaCodec had allocated input
            // buffers smaller than one compressed frame. `CanConvert` said yes, `Begin` configured and
            // started both codecs, and the failure landed one call later, where `Mp4Remuxer` turns anything
            // thrown into `SourceUnreadable "malformed source"` — the file blamed for the codec's fault.
            // 64 KB comfortably exceeds a frame of any audio codec this converter accepts.
            input.SetInteger(MediaFormat.KeyMaxInputSize, 64 * 1024);
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

        /// <summary>
        /// ⚠ <b>Wrapped so a platform failure NAMES ITSELF before it is swallowed.</b> It still rethrows —
        /// the caller's handling is unchanged and this is not a place to invent recovery — but
        /// <c>Mp4Remuxer</c> converts anything thrown here into <c>SourceUnreadable "malformed source"</c>,
        /// which accuses the FILE of a fault belonging to the codec. Without this line the only evidence a
        /// device leaves is that accusation.
        /// </summary>
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
                // ⚠ A frame larger than the codec's buffer is a REFUSAL, not an overflow. `Put` throws
                // `BufferOverflowException`, which reaches the caller as a malformed-source verdict and
                // names neither the codec nor the size — so the honest failure is stated here instead.
                if (frame.Data.Length > buffer.Remaining())
                {
                    throw new InvalidOperationException(
                        $"the {mimeForDiagnostics} decoder's input buffer has {buffer.Remaining()} byte(s) free "
                        + $"(capacity {buffer.Capacity()}, position {buffer.Position()}, limit {buffer.Limit()}) "
                        + $"and this frame is {frame.Data.Length}");
                }
                buffer.Put(frame.Data.ToArray());
                _decoder.QueueInputBuffer(index, 0, frame.Data.Length, _presentationUs, MediaCodecBufferFlags.None);
                // The presentation clock only has to be MONOTONIC for the encoder; the real timing is
                // rebuilt from the output frame count by the caller, which is exact.
                _presentationUs += 1_000_000L * OutputFramesPerPacket / Math.Max(OutputSampleRate, 1);
            }
            else
            {
                // Same honesty as FeedEncoder's identical case: a frame never queued is LOST, the
                // soundtrack is one frame shorter, and nothing else would say so.
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

            // The decoder must HEAR the end of stream or the PCM it is still holding never surfaces. An
            // input buffer can be briefly scarce right after the last Push, so wait a few timeouts
            // before giving up — and say so on failure, because the only symptom is a short soundtrack.
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
        /// <para>
        /// 🔴 The <c>OutputFormatChanged</c> case is where the AAC <b>csd-0</b> arrives, and it is the file's
        /// AudioSpecificConfig. Miss it and the MP4 carries an empty audio configuration — a file that opens
        /// and plays nothing, with every box valid.
        /// </para>
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
        /// <para>
        /// 🔴 <b>A DECODER FRAME IS ROUTINELY BIGGER THAN ONE ENCODER INPUT BUFFER, and assuming otherwise
        /// broke every conversion on this platform.</b> This used to be a single `Put(pcm)`. MP3 decodes
        /// 1152 samples per frame and AC-3 decodes 1536, while the AAC encoder's buffers are sized for its
        /// own 1024-sample frame — so the very first frame threw `Java.Nio.BufferOverflowException`.
        /// Measured: a 314-byte MP3 frame, on a device where `CanConvert` and `Begin` had both
        /// answered yes. The pairing that happens to fit is the exception, not the rule.
        /// </para>
        /// <para>
        /// ⚠ The failure was invisible from outside: it surfaced through `Mp4Remuxer` as
        /// `SourceUnreadable "malformed source"`, blaming the FILE. That is why this converter now takes a
        /// log — see <see cref="Use"/>.
        /// </para>
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
                    // No buffer free. Dropping PCM silently would produce a shorter, subtly wrong soundtrack
                    // — the kind of defect nobody reports because it still plays — so say so.
                    Report(_log, $"[Shenora.Android] the encoder had no input buffer free; "
                               + $"{pcm.Length - offset} byte(s) of decoded audio were dropped.");
                    return;
                }

                var buffer = _encoder.GetInputBuffer(index)!;
                buffer.Clear();
                var take = Math.Min(pcm.Length - offset, buffer.Remaining());

                // 🔴 A ZERO-CAPACITY BUFFER WOULD SPIN THIS LOOP FOREVER — `offset` never advances, the
                // condition never ends, and the encoder is fed empty buffers as fast as it hands them out.
                // Found reviewing this method the day it was written, and worth the four lines precisely
                // because the bug it replaced came from assuming `Clear()` leaves the full capacity free.
                // An empty PCM buffer with `last` set is legitimate, though — that is how end-of-stream is
                // signalled — so the guard is "no room AND still carrying audio", not "no room".
                if (take == 0 && offset < pcm.Length)
                {
                    Report(_log, $"[Shenora.Android] the encoder offered a buffer with no room; "
                               + $"{pcm.Length - offset} byte(s) of decoded audio were dropped.");
                    _encoder.QueueInputBuffer(index, 0, 0, _presentationUs, MediaCodecBufferFlags.None);
                    return;
                }

                if (take > 0) buffer.Put(pcm, offset, take);
                offset += take;

                // EndOfStream goes on the LAST chunk only — flagging an earlier one ends the stream with
                // audio still unfed, which truncates the soundtrack instead of failing.
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
                    // Every AAC frame is a sync sample; the time is the encoder's own for this buffer.
                    produced.Add(new MediaFrame(frame, _info.PresentationTimeUs, IsKeyframe: true));
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
