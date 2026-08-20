namespace Shenora.Modules.Media;

/// <summary>
/// Writes media into a container the web can open — the MUXER seam. A consumer supplying a native muxer
/// (<c>AVAssetWriter</c>, Android's <c>MediaMuxer</c>) keeps the kit's demuxing and timing, where
/// <c>MediaConversionOptions.Convert</c> replaces the whole pipeline (<c>docs/design/media.md</c>, "The four
/// seams").
/// <para>
/// ⚠ <b>A writer must not be asked to carry what it cannot.</b> <see cref="IMediaContainerWriter.CanCarry"/>
/// is asked per stream BEFORE any work starts, so an unsupported codec becomes an honest refusal rather than
/// a file that is written, cached and then plays silent. A writer that answers true and then drops the
/// stream is the worst outcome available here.
/// </para>
/// </summary>
public interface IMediaContainerWriter
{
    /// <summary>The container this produces, as a lowercase extension including the dot (<c>.mp4</c>).</summary>
    string Container { get; }

    /// <summary>
    /// Can this writer carry <paramref name="codec"/> for <paramref name="kind"/> WITHOUT re-encoding? ⚠ The
    /// question is about the CONTAINER, not the device — MP4 cannot hold AC-3 whatever codecs the hardware
    /// has; whether the device could re-encode it is <see cref="IMediaCapability"/>'s question.
    /// </summary>
    bool CanCarry(MediaStreamKind kind, string codec);

    /// <summary>Translate <paramref name="source"/> into this container.</summary>
    /// <param name="source">The file to translate, positioned at its start. Must be seekable.</param>
    /// <param name="destination">Where the container is written. The caller owns atomicity, not this.</param>
    /// <param name="conversion">
    /// The codec seam, or null. With one, a stream the container cannot carry is re-encoded whatever its
    /// KIND. Without it, an unplayable soundtrack is dropped and REPORTED, while a picture the container
    /// cannot carry means the file is refused. ⚠ Supplying a conversion never causes a CARRIABLE stream to be
    /// re-encoded.
    /// </param>
    /// <param name="cancellationToken">Honoured between frames, so shutdown is prompt without a torn frame.</param>
    MediaRemuxerResult Write(Stream source, Stream destination, IMediaStreamConversion? conversion,
                             CancellationToken cancellationToken = default);
}

/// <summary>
/// Turning a container writer into the delegate the conversion route runs.
/// </summary>
public static class MediaContainerWriterExtensions
{
    /// <summary>
    /// Wrap this writer as a <see cref="MediaConversionOptions.Convert"/> delegate.
    /// <code>
    /// Convert = new Mp4Remuxer().ToConverter(conversion),        // the kit's muxer
    /// Convert = myNativeMuxer.ToConverter(conversion),           // yours
    /// </code>
    /// <para>
    /// The kit keeps the FILE handling — opening, disposing, and swallowing the path on failure — and
    /// forwards <see cref="MediaRemuxerResult.Dropped"/> onto the request, which is what lets the route tell
    /// a page WHY a film is silent.
    /// </para>
    /// <para>
    /// ⚠ <b>It THROWS on refusal.</b> The route runs this inside <c>Files.BeginReplace</c>, which publishes
    /// the output only if the delegate returns without throwing — so a refusal that returned quietly would
    /// promote a truncated file into the cache and serve it forever.
    /// </para>
    /// </summary>
    /// <param name="writer">The muxer.</param>
    /// <param name="conversion">The codec seam, or null for container repair only. ONE seam answers for every stream kind.</param>
    public static Func<MediaConversionRequest, CancellationToken, Task> ToConverter(
        this IMediaContainerWriter writer, IMediaStreamConversion? conversion = null)
    {
        ArgumentNullException.ThrowIfNull(writer);

        return (request, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(request);

            return Task.Run(() =>
            {
                request.Progress.Report(0);

                MediaRemuxerResult result;
                try
                {
                    using var source = File.OpenRead(request.SourcePath);
                    using var destination = File.Create(request.DestinationPath);
                    result = writer.Write(source, destination, conversion, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 🔴 No exception text travels from here — a media path must not reach a page.
                    result = new MediaRemuxerResult(MediaRemuxerOutcome.SourceUnreadable, "source or destination unusable");
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (!result.Succeeded)
                {
                    // The OUTCOME, not free text — this kit's error contract is a code plus parameters
                    // (`ipc-contracts`).
                    throw new InvalidOperationException($"{result.Outcome}: {result.Reason}");
                }

                foreach (var codec in result.Dropped) request.Dropped.Add(codec);
                request.Progress.Report(1);
            }, cancellationToken);
        };
    }
}
