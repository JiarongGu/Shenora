namespace Shenora.Modules.Media;

/// <summary>
/// One media source's BYTES, and the name diagnostics may call it by — <b>independent of where they live</b>.
/// A local file, a LAN share and a remote url differ only in how the stream is opened, so the tier takes the
/// OPENER rather than a path.
/// <para>
/// 🔴 <b>It carries a <see cref="Label"/> and no address.</b> A remote media url routinely carries the
/// caller's credentials, and every diagnostic in this tier prints the source it is working on. An opener
/// closes over its own address, which keeps the secret out of the kit rather than trusting each log line.
/// </para>
/// </summary>
public sealed class MediaByteSource
{
    /// <summary>
    /// What diagnostics call this source — a title, a track id, a file name. ⚠ It is PRINTED, so do not build
    /// it from a url: <c>Path.GetFileName</c> splits on separators and a query string has none, so
    /// <c>?sig=…</c> survives it whole.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Open a fresh stream over the source. Called once per read pass; the caller disposes what it gets, and
    /// may call this again while an earlier stream is still open.
    /// <para>
    /// 🔴 <b>The stream MUST be seekable and MUST report <see cref="Stream.Length"/>.</b> Matroska is read by
    /// offset, so a forward-only stream cannot be indexed at all and a ranged remote source needs a seekable
    /// adapter over its transport.
    /// </para>
    /// </summary>
    /// <remarks>
    /// ⚠ Throwing is an ordinary answer — a source that has gone away, a fetch that failed. Every
    /// <see cref="ISegmentEngine"/> member promises an absent answer rather than a throw.
    /// </remarks>
    public required Func<CancellationToken, Stream> Open { get; init; }

    /// <summary>How much of a source is held in memory to serve reads, when the caller states no preference.</summary>
    public const int DefaultWindowBytes = 256 * 1024;

    /// <summary>
    /// A source read in RANGES — the shape a remote or ranged-HTTP file has. The caller supplies only
    /// <paramref name="fetch"/>; the kit supplies the seekable, buffered <see cref="Stream"/> over it.
    /// <para>
    /// 🔴 <b>The buffering is not optional and is why this ships here.</b> Matroska is parsed by EBML varint,
    /// one <c>ReadByte</c> at a time, so the obvious adapter — a fetch per read — issues a round trip per
    /// BYTE. A local <c>FileStream</c> buffers for free, so an app porting from <see cref="ForFile"/> has no
    /// warning that the naive version is unusable rather than merely slower.
    /// </para>
    /// </summary>
    /// <param name="label">What diagnostics print. See <see cref="Label"/> — never build it from a url.</param>
    /// <param name="length">
    /// The source's total size, which must be known up front: Matroska is read by offset from the END
    /// (SeekHead, then Cues), so a source that cannot state its length cannot be indexed. Over HTTP this is
    /// <c>Content-Length</c>, from a HEAD or from the <c>Content-Range</c> of any one ranged response.
    /// </param>
    /// <param name="fetch">
    /// <c>(offset, count, token)</c> ⇒ a body holding at most <c>count</c> bytes starting at <c>offset</c>.
    /// Returning FEWER is legal — it is asked again for the rest. Ownership of the body passes to the kit.
    /// <para>
    /// ⚠ <b>The address, the credentials and the retry policy stay on YOUR side</b> — the kit never sees
    /// them, which is what keeps a url out of a kit diagnostic by construction. Throwing is an ordinary
    /// answer for a source that has gone away. Called on a POOL thread and waited on.
    /// </para>
    /// </param>
    /// <param name="windowBytes">Read-ahead per fetch. Larger trades memory for round trips.</param>
    public static MediaByteSource ForRanges(string label, long length,
                                            Func<long, int, CancellationToken, Task<Stream>> fetch,
                                            int windowBytes = DefaultWindowBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowBytes);

        return new MediaByteSource
        {
            Label = label,
            Open = token => new RangeFetchStream(length, fetch, windowBytes, token),
        };
    }

    /// <summary>A file on this machine — a local source or a mounted LAN share.</summary>
    public static MediaByteSource ForFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new MediaByteSource
        {
            Label = Path.GetFileName(path),
            Open = _ => File.OpenRead(path),
        };
    }

    /// <summary>The label — never an address. See the type's remarks.</summary>
    public override string ToString() => Label;
}
