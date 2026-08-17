using CoreFoundation;
using CoreMedia;
using CoreVideo;
using Foundation;
using Microsoft.Extensions.Logging;
using Shenora;
using Shenora.Modules.Media;
using VideoToolbox;

namespace Shenora.iOS;

/// <summary>
/// The PICTURE half of iOS's codec seam: decode a track this device can read but its webview refuses, and
/// re-encode it as H.264 — the peer of <c>AndroidMediaVideoConversion</c>, so the tier is not Android-shaped.
///
/// <para>
/// 🔴 <b>The gap it closes is measured, not theoretical, and the deferral that used to sit in `TASKS.md`
/// was refuted by its own evidence.</b> That entry read "no measured gap justifies it — iOS decodes what its
/// webview accepts". The opposite is what was measured: this device decodes <c>mpeg4</c> (MPEG-4 Part 2)
/// perfectly and <b>its own webview refuses it</b>, so a page gets sound and a blank picture with NO error at
/// all — the failure mode <see cref="Mp4Remuxer"/>'s track-selection comment records independently. Owner,
/// 2026-08-13: <i>"its not a question it should/shouldn't we building the pipeline, so it should support
/// range of conversion logic too."</i>
/// </para>
/// <para>
/// ⚠ <b>It is SHORTER than the Android peer for one reason worth knowing before comparing them:</b>
/// VideoToolbox hands back compressed samples already length-prefixed (AVCC), which is exactly what MP4
/// stores. The Android port had to split Annex-B start codes and re-prefix every NAL unit; there is no such
/// step here, and adding one would corrupt the output.
/// </para>
/// <para>
/// ⚠ <b>The two sessions are joined by a CALLBACK, not by a surface.</b> Android bridges its decoder to its
/// encoder with a shared <c>Surface</c> so pixels never enter managed memory; VideoToolbox has no equivalent,
/// so a decoded <see cref="CVImageBuffer"/> is handed straight to the compression session from inside the
/// decompression callback. The buffer is NOT copied — it is fed while the callback still owns it, which is
/// the one ordering this class cannot get wrong without producing a green picture.
/// </para>
/// </summary>
public static class IosMediaVideoConversion
{
    /// <summary>
    /// Register the picture converter on the app's pipeline. Dispose to remove it.
    /// </summary>
    /// <remarks>
    /// ⚠ Returns a registration rather than <c>void</c> — unlike <see cref="IosMediaAudioConversion.Use"/>,
    /// which predates the chain being removable. The shell registers both; an app that wants its own picture
    /// path registers after and wins, because the chain is asked last-first.
    /// </remarks>
    public static IDisposable Use(MediaConversionPipeline pipeline, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return pipeline.Use((source, codecPrivate) => Begin(source, codecPrivate, log), Claims);
    }

    /// <summary>
    /// What this converter OFFERS to attempt — the declaration the pipeline answers <c>CanConvert</c> from
    /// before any codec is built.
    /// <para>
    /// ⚠ Derived from the same table <see cref="CodecTypeOf"/> switches on, so the claim and the behaviour
    /// cannot drift. A second hand-written list would be a second thing to keep true.
    /// </para>
    /// </summary>
    /// <remarks>
    /// ⚠ Computed on access rather than cached in a static initialiser: a cached one runs in DECLARATION
    /// order, so it read <c>Offered</c> before that table existed and produced an empty claim list — a
    /// converter that silently claimed nothing, which is the failure mode this whole file has already been
    /// bitten by twice.
    /// </remarks>
    public static IReadOnlyList<MediaStreamClaim> Claims =>
        [.. Offered.Keys.Select(codec => new MediaStreamClaim(MediaStreamKind.Video, codec))];

    /// <summary>
    /// Which picture codecs this converter offers.
    /// <para>
    /// ⚠ <b>H.264 and HEVC are deliberately ABSENT, exactly as on Android.</b> MP4 already carries both, so
    /// <see cref="Mp4Remuxer"/> COPIES them — lossless, instant, and it cannot fail halfway. Offering to
    /// convert them would make the kit re-encode a film it could have remuxed.
    /// </para>
    /// </summary>
    public static bool CanConvert(string codec) => CodecTypeOf(codec) is not null;

    private static IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate,
                                                    ILogger? log)
    {
        // ⚠ Declining a KIND is silent on purpose — the audio converter shares this chain and every
        // soundtrack would otherwise log a line about a picture converter that correctly ignored it.
        if (source.Kind is not MediaStreamKind.Video) return null;

        // 🔴 EVERY OTHER DECLINE REPORTS, and that is not tidiness. This class returned null from four
        // places without a word, and the first device run could not tell "declined" from "broken" — the
        // exact failure D63 names, produced by the very file added to answer it. A picture that is dropped
        // says only `dropped:["mpeg4"]`, which names the codec and nothing about WHY.
        if (source.Codec is not { } codec)
        {
            Report(log, "[Shenora.iOS] picture conversion declined a video track with NO codec name");
            return null;
        }

        if (CodecTypeOf(codec) is not { } codecType)
        {
            Report(log, $"[Shenora.iOS] picture conversion does not offer {codec} — h264/hevc are copied by "
                      + "the remuxer, and anything else is not claimed by this kit");
            return null;
        }

        // A platform video encoder REFUSES to configure without real dimensions, so a source that reached
        // here without them cannot be converted — and saying so is better than configuring at 0x0 and
        // failing later inside the first frame.
        if (source.Width is not > 0 || source.Height is not > 0)
        {
            Report(log, $"[Shenora.iOS] picture conversion declined {codec}: no dimensions on the source, "
                      + "and VideoToolbox cannot configure an encoder without them");
            return null;
        }

        try
        {
            return Run.TryStart(codecType, source.Width.Value, source.Height.Value, codecPrivate, log);
        }
        catch (Exception ex)
        {
            // Declining is an ordinary answer on this seam — the caller then reports UNSUPPORTED_CODEC and
            // names it — so a platform that throws must not take the conversion out with it.
            Report(log, $"[Shenora.iOS] picture conversion could not start for {codec} ({ex.GetType().Name})");
            return null;
        }
    }

    private static void Report(ILogger? log, string message) => AppCallback.Log(log, () => message);

    /// <summary>
    /// The codecs worth OFFERING, as VideoToolbox's own four-character codes.
    /// <para>
    /// ⚠ Deliberately the same short list the Android peer offers, so a film that converts on one shell
    /// converts on the other. A codec absent here is not "unsupported by iOS" — it is one the kit does not
    /// claim, which is the honest difference.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, CMVideoCodecType> Offered =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["mpeg4"] = CMVideoCodecType.Mpeg4Video,
            ["mpeg2video"] = CMVideoCodecType.Mpeg2Video,
            ["h263"] = CMVideoCodecType.H263,
        };

    /// <summary>
    /// ⚠ ONE table, read by both <see cref="Claims"/> and this — so what the converter DECLARES and what it
    /// ACCEPTS cannot drift. A hand-written second list is a second thing to keep true.
    /// </summary>
    private static CMVideoCodecType? CodecTypeOf(string codec) =>
        Offered.TryGetValue(codec, out var type) ? type : null;

    private sealed class Run : IMediaStreamConversionRun
    {
        private readonly VTCompressionSession _encoder;
        private readonly CMVideoFormatDescription _sourceFormat;
        private readonly ILogger? _log;
        private readonly CMVideoCodecType _codecType;
        private readonly int _width;
        private readonly int _height;
        private readonly ReadOnlyMemory<byte> _codecPrivate;

        /// <summary>
        /// Always present — see the construction site for why a DEFERRED one was tried and reverted.
        /// </summary>
        private readonly VTDecompressionSession _decoder;

        /// <summary>
        /// Where both callbacks deposit their output. ⚠ Guarded: VideoToolbox may call back on its own
        /// threads, and both sessions are pumped to completion inside <see cref="Push"/> and
        /// <see cref="Drain"/>, so the list is read on the caller's thread between those pumps.
        /// </summary>
        private readonly List<MediaFrame> _produced = [];
        private readonly object _gate = new();

        private bool _disposed;

        public ReadOnlyMemory<byte> OutputConfig { get; private set; }

        /// <summary>⚠ Zero: a picture times every frame individually, so the muxer reads those instead.</summary>
        public int OutputFramesPerPacket => 0;

        public MediaStreamInfo OutputFormat => new(MediaStreamKind.Video, "h264", Width: _width, Height: _height);

        private Run(VTDecompressionSession decoder, VTCompressionSession encoder,
                    CMVideoFormatDescription sourceFormat, CMVideoCodecType codecType, int width, int height,
                    ReadOnlyMemory<byte> codecPrivate, ILogger? log)
        {
            _decoder = decoder;
            _encoder = encoder;
            _sourceFormat = sourceFormat;
            _codecType = codecType;
            _width = width;
            _height = height;
            _codecPrivate = codecPrivate;
            _log = log;
        }

        /// <summary>
        /// One place builds the decompression session, so the eager path and the deferred one cannot drift.
        /// </summary>
        private static VTDecompressionSession? CreateDecoder(CMVideoFormatDescription sourceFormat,
            Func<Run?> owner, ILogger? log, CMVideoCodecType codecType, int width, int height,
            ReadOnlyMemory<byte> codecPrivate)
        {
            var session = VTDecompressionSession.Create(
                (sourceFrame, status, flags, image, presentation, duration) =>
                    owner()?.OnDecoded(status, image, presentation, duration),
                sourceFormat);

            if (session is null)
            {
                AppCallback.Log(log, () => $"[Shenora.iOS] picture conversion: no DECODER for {codecType} at "
                                         + $"{width}x{height} (codecPrivate "
                                         + $"{(codecPrivate.IsEmpty ? "ABSENT" : $"{codecPrivate.Length}B")})");
            }

            return session;
        }

        public static Run? TryStart(CMVideoCodecType codecType, int width, int height,
                                    ReadOnlyMemory<byte> codecPrivate, ILogger? log)
        {
            var sourceFormat = SourceFormat(codecType, width, height, codecPrivate);
            if (sourceFormat is null)
            {
                AppCallback.Log(log, () => "[Shenora.iOS] picture conversion could not describe the SOURCE "
                                         + "format, so the decoder was never created");
                return null;
            }

            VTDecompressionSession? decoder = null;
            VTCompressionSession? encoder = null;
            Run? run = null;

            try
            {
                encoder = VTCompressionSession.Create(width, height, CMVideoCodecType.H264,
                    (sourceFrame, status, flags, buffer) => run?.OnEncoded(status, buffer));
                if (encoder is null)
                {
                    AppCallback.Log(log, () => $"[Shenora.iOS] picture conversion: no H.264 ENCODER at "
                                             + $"{width}x{height}, so {codecType} cannot be re-encoded");
                    return null;
                }

                // One keyframe a second. ⚠ A SEEKING decision rather than a quality one, and the same one
                // the Android peer makes: the sync-sample table is built from these, so a long interval
                // makes every seek land far from where the user asked.
                encoder.SetProperty(VTCompressionPropertyKey.MaxKeyFrameIntervalDuration, new NSNumber(1));
                encoder.SetProperty(VTCompressionPropertyKey.RealTime, NSNumber.FromBoolean(false));
                encoder.SetProperty(VTCompressionPropertyKey.AllowFrameReordering, NSNumber.FromBoolean(false));

                // 🔴 THE DECODER IS REQUIRED HERE, AND A DEFERRED ONE WAS A MISTAKE THIS FILE ALREADY MADE.
                // Creating it is the ONLY honest answer to "can this device convert that codec" — the
                // encoder proves nothing about the source. Measured on an iPhone 17 Pro:
                //
                //   picture conversion: no DECODER for Mpeg4Video at 480x270 (codecPrivate 47B)
                //
                // 47 bytes of ESDS present and VideoToolbox still refuses. So the earlier reading — "it
                // needs its ESDS, and the capability probe cannot supply one" — was WRONG: this device has
                // no MPEG-4 Part 2 decoder at all, and `h263` answered true beside it by HAVING a decoder,
                // not by needing no extensions.
                //
                // ⚠ Deferring it made `CanConvert` answer TRUE from the encoder alone, so the kit promised a
                // conversion it could not perform — exactly the `accepts && !repairable → BROKEN` case
                // `CodecProbe` warns about — and the muxer then failed with `NoCarriableStream` after
                // accepting the track. It also broke `CONVERT-REFUSAL`, which asks for a codec that must be
                // REFUSED. An over-claim here is worse than a narrow answer: a refusal is a routing
                // decision the app can act on, while a promise that fails mid-mux is a transcode spent on
                // nothing.
                decoder = CreateDecoder(sourceFormat, () => run, log, codecType, width, height, codecPrivate);
                if (decoder is null) return null;

                run = new Run(decoder, encoder, sourceFormat, codecType, width, height, codecPrivate, log);
                AppCallback.Log(log, () => $"[Shenora.iOS] picture conversion ready: {codecType} -> h264 at "
                                         + $"{width}x{height}");
                return run;
            }
            catch
            {
                decoder?.Dispose();
                encoder?.Dispose();
                sourceFormat.Dispose();
                throw;
            }
        }

        public IReadOnlyList<MediaFrame> Push(MediaFrame frame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using var sample = SampleFrom(frame);
            if (sample is null) return [];

            _decoder.DecodeFrame(sample, VTDecodeFrameFlags.EnableTemporalProcessing, IntPtr.Zero, out _);
            return Take();
        }

        public IReadOnlyList<MediaFrame> Drain()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // 🔴 THE DECODER FIRST, and the order is not symmetry — it holds real pictures back. Its
            // remaining frames must reach the ENCODER before the encoder is told there is no more input,
            // or the tail is encoded by nobody and the file simply stops early with no error anywhere.
            _decoder.FinishDelayedFrames();
            _decoder.WaitForAsynchronousFrames();

            // 🔴 And only THEN the encoder, which holds a GOP. Without this the last second or so of every
            // converted film is missing — well-formed, playable, and short.
            _encoder.CompleteFrames(CMTime.PositiveInfinity);
            return Take();
        }

        /// <summary>
        /// The decompression callback. ⚠ It feeds the encoder from INSIDE the callback, while VideoToolbox
        /// still owns the pixel buffer — copying it out first would be the obvious shape and would cost a
        /// full-frame copy per picture for nothing.
        /// </summary>
        private void OnDecoded(VTStatus status, CVImageBuffer? image, CMTime presentation, CMTime duration)
        {
            if (status != VTStatus.Ok || image is null) return;
            _encoder.EncodeFrame(image, presentation, duration, null, IntPtr.Zero, out _);
        }

        /// <summary>
        /// The compression callback: one encoded picture, already length-prefixed.
        /// </summary>
        private void OnEncoded(VTStatus status, CMSampleBuffer? buffer)
        {
            if (status != VTStatus.Ok || buffer is null) return;

            // 🔴 The `avcC` comes from the sample's own format description rather than from anything this
            // class assembles, and it is read on EVERY sample until it is known: the first sample is not
            // guaranteed to carry a format description, and writing an empty config into a file produces
            // one that opens and plays nothing.
            if (OutputConfig.IsEmpty && buffer.GetVideoFormatDescription() is { } described)
            {
                OutputConfig = AvcCFrom(described);
            }

            using var block = buffer.GetDataBuffer();
            if (block is null) return;

            var data = new byte[block.DataLength];
            unsafe
            {
                fixed (byte* into = data)
                {
                    if (block.CopyDataBytes((nuint)0, (nuint)data.Length, (IntPtr)into) != CMBlockBufferError.None) return;
                }
            }

            lock (_gate)
            {
                _produced.Add(new MediaFrame(data, TimeToMicroseconds(buffer.PresentationTimeStamp),
                                             IsKeyframe(buffer)));
            }
        }

        private IReadOnlyList<MediaFrame> Take()
        {
            lock (_gate)
            {
                if (_produced.Count == 0) return [];
                var taken = _produced.ToArray();
                _produced.Clear();
                return taken;
            }
        }

        /// <summary>
        /// ⚠ <b>NOT-depends-on-others, and the polarity is the trap.</b> VideoToolbox marks a sample with
        /// <c>DependsOnOthers</c>; a keyframe is one where that is FALSE. Reading the attachment as if it
        /// meant "is a keyframe" claims every frame is one, and a seek then lands on a green smear.
        /// Absent attachments mean a sync sample, which is why the default is true.
        /// </summary>
        private static bool IsKeyframe(CMSampleBuffer buffer)
        {
            var first = buffer.GetSampleAttachments(createIfNecessary: false)?.FirstOrDefault();
            // `NotSync` is `bool?` — absent means the attachment was never set, which means a sync sample.
            return first?.NotSync is not true;
        }

        private static long TimeToMicroseconds(CMTime time) =>
            time.IsInvalid ? 0 : (long)(time.Seconds * 1_000_000d);

        private CMSampleBuffer? SampleFrom(MediaFrame frame)
        {
            var data = frame.Data.ToArray();
            var block = CMBlockBuffer.FromMemoryBlock(data, (nuint)0, CMBlockBufferFlags.AssureMemoryNow, out var error);
            if (block is null || error != CMBlockBufferError.None) return null;
            using var owned = block;

            var timing = new CMSampleTimingInfo
            {
                PresentationTimeStamp = CMTime.FromSeconds(frame.PresentationTimeUs / 1_000_000d, 1_000_000),
                DecodeTimeStamp = CMTime.Invalid,
                Duration = CMTime.Invalid,
            };

            var sample = CMSampleBuffer.CreateReady(block, _sourceFormat, 1, [timing], [(nuint)data.Length],
                                                    out var sampleError);
            return sampleError == CMSampleBufferError.None ? sample : null;
        }

        /// <summary>
        /// Rebuild an <c>avcC</c> from the encoder's parameter sets. ⚠ MP4 stores the SPS and PPS in this
        /// box, NOT in the frames — VideoToolbox keeps them out of the sample data entirely, so a file
        /// written without this decodes nothing.
        /// </summary>
        private static ReadOnlyMemory<byte> AvcCFrom(CMVideoFormatDescription description)
        {
            var sets = new List<byte[]>();
            for (nuint i = 0; ; i++)
            {
                var set = description.GetH264ParameterSet(i, out _, out _, out var status);
                if (status != CMFormatDescriptionError.None || set is null || set.Length == 0) break;
                sets.Add(set);
                if (sets.Count >= 2) break;
            }

            return sets.Count >= 2 ? AvcC(sets[0], sets[1]) : ReadOnlyMemory<byte>.Empty;
        }

        /// <summary>The `avcC` box body — the same layout the Android peer builds, from the same two sets.</summary>
        private static byte[] AvcC(byte[] sps, byte[] pps)
        {
            var box = new List<byte>
            {
                1,             // configurationVersion
                sps[1],        // AVCProfileIndication  — from the SPS itself, never assumed
                sps[2],        // profile_compatibility
                sps[3],        // AVCLevelIndication
                0xFF,          // 6 bits reserved + lengthSizeMinusOne = 3 (4-byte lengths)
                0xE1,          // 3 bits reserved + numOfSequenceParameterSets = 1
            };
            box.Add((byte)(sps.Length >> 8));
            box.Add((byte)(sps.Length & 0xFF));
            box.AddRange(sps);
            box.Add(1);        // numOfPictureParameterSets
            box.Add((byte)(pps.Length >> 8));
            box.Add((byte)(pps.Length & 0xFF));
            box.AddRange(pps);
            return [.. box];
        }

        /// <summary>
        /// Describe the SOURCE stream so the decoder knows what it is being fed.
        /// <para>
        /// ⚠ <b>The container's codec-private bytes are the decoder configuration</b>, and for MPEG-4 Part 2
        /// that is the ESDS descriptor Matroska stores verbatim. A decoder created without it produces a
        /// green picture rather than an error, which is the failure this method exists to prevent.
        /// </para>
        /// </summary>
        private static CMVideoFormatDescription? SourceFormat(CMVideoCodecType codecType, int width, int height,
                                                              ReadOnlyMemory<byte> codecPrivate)
        {
            NSDictionary? extensions = null;
            if (!codecPrivate.IsEmpty)
            {
                var atoms = NSDictionary.FromObjectAndKey(
                    NSData.FromArray(codecPrivate.ToArray()), new NSString("esds"));
                extensions = NSDictionary.FromObjectAndKey(atoms,
                    new NSString("SampleDescriptionExtensionAtoms"));
            }

            // ⚠ The CONSTRUCTOR, not a `Create` — the bindings expose `Create` only for H.264/HEVC
            // parameter sets and for an image buffer, none of which can carry a source codec's extensions.
            return extensions is null
                ? new CMVideoFormatDescription(codecType, new CMVideoDimensions(width, height))
                : new CMVideoFormatDescription(codecType, new CMVideoDimensions(width, height), extensions);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // ⚠ TWO hardware sessions. A device has only a handful, and leaking one does not leak memory —
            // it makes the NEXT conversion in the app fail with a resource error that names nothing.
            try { _encoder.Dispose(); } catch (Exception) { /* releasing a codec must not throw onward */ }
            try { _decoder.Dispose(); } catch (Exception) { /* same */ }
            try { _sourceFormat.Dispose(); } catch (Exception) { /* same */ }
        }
    }
}
