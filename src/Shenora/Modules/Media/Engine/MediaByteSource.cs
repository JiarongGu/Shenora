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
