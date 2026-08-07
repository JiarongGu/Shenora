namespace Shenora.Media;

/// <summary>
/// Writes media into a container the web can open — the MUXER half of the translation layer, as a seam.
///
/// <para>
/// 🔴 <b>Why this exists when <c>MediaConversionOptions.Convert</c> already accepts a delegate.</b> That
/// delegate is ALL-OR-NOTHING: an app that wants only its own AAC encoder, or only a native muxer, has to
/// reimplement the entire pipeline behind it — demux, timing, sample tables, interleave. The seams here cut
/// the pipeline into the parts a consumer actually wants to replace, and each can be swapped alone:
/// </para>
/// <list type="table">
/// <item><term><see cref="IMediaAudioConversion"/></term><description>the CODEC — one stream in, one out.</description></item>
/// <item><term><see cref="IMediaContainerWriter"/> (this)</term><description>the MUXER — where the streams land.</description></item>
/// <item><term><c>MediaConversionOptions.Convert</c></term><description>the WHOLE JOB, for an app replacing everything.</description></item>
/// </list>
/// <para>
/// So a consumer supplying a native muxer (<c>AVAssetWriter</c>, Android's <c>MediaMuxer</c>) keeps the
/// kit's demuxing and timing; one supplying a codec keeps the kit's muxing. That composability is the point
/// — not throughput.
/// </para>
///
/// <para>
/// ⚠ <b>A writer must not be asked to carry what it cannot.</b> <see cref="CanCarry"/> is asked per stream
/// BEFORE any work starts, so an unsupported codec becomes an honest refusal rather than a file that is
/// written, cached and then plays silent. A writer that answers true and then drops the stream is the worst
/// outcome available here, and it is the shape <c>ISegmentEngine.HasRenderedPicture</c> exists for one
/// layer down.
/// </para>
/// </summary>
public interface IMediaContainerWriter
{
    /// <summary>The container this produces, as a lowercase extension including the dot (<c>.mp4</c>).</summary>
    string Container { get; }

    /// <summary>
    /// Can this writer carry <paramref name="codec"/> for <paramref name="kind"/> WITHOUT re-encoding?
    /// <para>
    /// The question is about the CONTAINER, not the device: MP4 cannot hold AC-3 whatever codecs the
    /// hardware has. Whether the device could re-encode it into something carriable is
    /// <see cref="IMediaCapability"/>'s question, and the two are asked together.
    /// </para>
    /// </summary>
    bool CanCarry(MediaStreamKind kind, string codec);

    /// <summary>
    /// Translate <paramref name="source"/> into this container.
    /// </summary>
    /// <param name="source">The file to translate, positioned at its start. Must be seekable.</param>
    /// <param name="destination">Where the container is written. The caller owns atomicity, not this.</param>
    /// <param name="conversion">
    /// The codec seam, or null. With one, a stream the container cannot carry is re-encoded; without one it
    /// is refused. ⚠ Copying is always preferred where possible — it is faster, lossless, and cannot fail
    /// halfway — so supplying a conversion never causes a carriable stream to be re-encoded.
    /// </param>
    /// <param name="cancellationToken">Honoured between frames, so shutdown is prompt without a torn frame.</param>
    MediaRemuxerResult Write(Stream source, Stream destination, IMediaAudioConversion? conversion,
                             CancellationToken cancellationToken = default);
}
