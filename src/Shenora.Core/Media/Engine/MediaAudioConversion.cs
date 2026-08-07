namespace Shenora.Media;

/// <summary>
/// Turning ONE stream into something the web accepts, using the codecs the DEVICE already has.
///
/// <para>
/// <b>Tier 2 of D52's engine tiers, and the narrowest thing that closes the gap tier 1 leaves.</b>
/// <see cref="Mp4Remuxer"/> repairs the CONTAINER for nothing; what it cannot repair is the SOUNDTRACK —
/// AC-3, E-AC-3 and DTS are routine inside an <c>.mkv</c> and play in no browser. Repairing that means
/// decoding one stream and encoding it as AAC, which is two platform calls: zero bytes shipped, zero
/// licence weight, no codec written (D51/D52).
/// </para>
///
/// <para>
/// 🔴 <b>Why this is per-FRAME and not "convert this file".</b> A two-hour soundtrack is hundreds of
/// megabytes compressed and gigabytes as PCM, so a whole-stream call cannot run on a phone at all. It is
/// also the shape both platforms already have — <c>MediaCodec</c> and <c>AudioConverter</c> are both fed
/// buffers and drained — so a file-shaped seam would force every implementation to build a queue behind it.
/// </para>
///
/// <para>
/// ⚠ <b>What the kit still does not do:</b> pick codecs, choose bitrates, or ship an encoder. This asks the
/// platform for the one conversion the web needs and refuses anything it cannot do — a device that cannot
/// decode AC-3 answers false from <see cref="CanConvert"/> and the planner says
/// <see cref="MediaPlaybackAction.Unsupported"/> rather than starting work that cannot finish. Measured
/// 2026-08-07: an iPhone decodes AC-3, an AOSP Android does not, so both answers are real.
/// </para>
/// </summary>
public interface IMediaAudioConversion
{
    /// <summary>
    /// Can this device turn <paramref name="codec"/> into something MP4 carries?
    /// <para>
    /// Asked BEFORE any work starts, because the honest answer to "no" is
    /// <see cref="MediaPlaybackAction.Unsupported"/> — a refusal the app can act on — rather than a
    /// conversion that fails halfway and leaves a partial file.
    /// </para>
    /// </summary>
    bool CanConvert(string codec);

    /// <summary>
    /// Begin converting one stream, or null when this device cannot.
    /// <para>
    /// ⚠ Null rather than an exception: "this device lacks that codec" is an ordinary answer here, and it
    /// is the SAME answer <see cref="CanConvert"/> gives — a caller that checked first should not have to
    /// catch as well.
    /// </para>
    /// </summary>
    /// <param name="source">
    /// What the stream IS. <see cref="MediaStreamInfo.SampleRate"/> and <see cref="MediaStreamInfo.Channels"/>
    /// are not decoration here — a platform codec is CONFIGURED with them before it is fed anything, and a
    /// wrong rate produces audio at the wrong speed rather than an error.
    /// </param>
    /// <param name="codecPrivate">
    /// The codec's initialisation data as the container stored it — Matroska's <c>CodecPrivate</c>, which is
    /// an <c>avcC</c>, an AudioSpecificConfig, or a Vorbis header set depending on the codec.
    /// <para>
    /// ⚠ <b>Empty is legal and common</b> (AC-3 needs none), but for the codecs that DO need it a decoder
    /// configured without it produces silence or refuses — so it is passed rather than left for the
    /// implementation to hunt for.
    /// </para>
    /// </param>
    IMediaAudioConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate);
}

/// <summary>
/// One stream's conversion in progress. Dispose to release the platform codec.
///
/// <para>
/// ⚠ <b>Disposing MATTERS here in a way it does not for a managed object.</b> Both platforms hand out a
/// hardware or system codec instance, and there are only a handful per device — leaking one does not leak
/// memory, it makes the NEXT conversion in the app fail with a resource error that names nothing.
/// </para>
/// </summary>
public interface IMediaAudioConversionRun : IDisposable
{
    /// <summary>
    /// The decoder configuration the OUTPUT needs — an AudioSpecificConfig for AAC — which MP4 must carry
    /// in its sample entry before the first frame.
    /// <para>
    /// ⚠ It is only knowable after the encoder has been fed, so it is a property read at the END rather
    /// than a constructor argument. Reading it early is legal and returns empty; writing that into a file
    /// produces an MP4 that opens and plays nothing, which is why <see cref="Mp4Remuxer"/> reads it after
    /// <see cref="Drain"/>.
    /// </para>
    /// </summary>
    ReadOnlyMemory<byte> OutputConfig { get; }

    /// <summary>How many samples one output frame represents, for the timing table (1024 for AAC).</summary>
    int OutputFramesPerPacket { get; }

    /// <summary>The output's sample rate, which a decoder may resample away from the input's.</summary>
    int OutputSampleRate { get; }

    /// <summary>The output's channel count, which a decoder may downmix (5.1 AC-3 to stereo AAC).</summary>
    int OutputChannels { get; }

    /// <summary>
    /// Feed one compressed input frame; returns whatever came out, which is often nothing.
    /// <para>
    /// ⚠ <b>Zero outputs is NORMAL and not an error.</b> Codecs buffer: a decoder needs several frames
    /// before it emits, and an encoder holds a window. A caller that treats an empty return as failure will
    /// abandon a perfectly good conversion in its first few frames.
    /// </para>
    /// </summary>
    IReadOnlyList<ReadOnlyMemory<byte>> Push(ReadOnlyMemory<byte> frame);

    /// <summary>
    /// End of input: return everything still buffered.
    /// <para>
    /// 🔴 <b>Skipping this truncates the soundtrack and nothing reports it.</b> The tail sits inside the
    /// codec, the file is well-formed, and the audio simply stops early — the same "exit 0 is not evidence"
    /// shape <see cref="ISegmentEngine.HasRenderedPicture"/> exists for.
    /// </para>
    /// </summary>
    IReadOnlyList<ReadOnlyMemory<byte>> Drain();
}
