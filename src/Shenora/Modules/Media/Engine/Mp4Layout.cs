using Shenora.Core.WebView;

namespace Shenora.Modules.Media;

/// <summary>Where one sample lives in the SOURCE and where it lands in the planned OUTPUT.</summary>
/// <param name="SourceOffset">Absolute offset of the frame's bytes in the source stream.</param>
/// <param name="Length">The frame's length in bytes — identical in both, because a remux copies.</param>
/// <param name="OutputOffset">Absolute offset of those bytes in the planned output.</param>
internal readonly record struct Mp4SampleSpan(long SourceOffset, int Length, long OutputOffset);

/// <summary>
/// The MP4 a remux WOULD write, computed without writing it, EXACTLY to the byte. 🔴 A plan and a write that
/// disagree answer a <c>Content-Range</c> total the bytes do not honour, and a media element fails that
/// SILENTLY: blank picture, no error, nothing logged. ⚠ Valid only for the source it was computed from.
/// <para>
/// ⚠ <b>Never compare two of these for equality</b>: <see cref="ReadOnlyMemory{T}"/> compares by segment and
/// <see cref="IReadOnlyList{T}"/> by reference, so the generated <c>Equals</c>/<c>==</c> is quietly always
/// false, even for two plans of the same stream.
/// </para>
/// Design: <c>docs/design/media.md</c>, <c>docs/design/mobile-shells.md</c>.
/// </summary>
/// <param name="Header">
/// Every byte before the first sample — <c>ftyp</c>, <c>moov</c>, and the <c>mdat</c> box header (8 bytes, or
/// 16 past 4 GB) — so <c>Samples[0].OutputOffset == Header.Length</c>.
/// </param>
/// <param name="Samples">
/// Every sample in the writer's emit order — decode-time order across the tracks, not source order — and
/// contiguous: each span begins where the previous one ended.
/// ⚠ A span may be ZERO length (a degenerate laced frame) and shares its <c>OutputOffset</c> with the span
/// after it, so code answering "which span holds byte X" must skip empties rather than match on the offset.
/// </param>
/// <param name="TotalLength"><c>Header.Length</c> plus every sample — the whole file, and the number a
/// <c>Content-Range</c> states.</param>
internal sealed record Mp4Layout(
    ReadOnlyMemory<byte> Header,
    IReadOnlyList<Mp4SampleSpan> Samples,
    long TotalLength);

/// <summary>
/// Turns a planned <see cref="Mp4Layout"/> into bytes, reading only the source bytes a requested range
/// actually touches.
/// <para>
/// 🔴 <b>The CALLER, not this type, must ensure <c>source</c> is the SAME stream the layout was planned
/// from, unchanged since</b> — a layout carries no identity, so the check lives one layer up, keyed on
/// <c>DerivedCacheKey.For(path, length, mtime)</c>. <b>Getting it wrong is SILENT</b>: every span still
/// resolves to a valid offset in the wrong file, so the bytes returned are another file's frames wearing
/// this one's <c>Content-Range</c>.
/// </para>
/// </summary>
internal static class Mp4LayoutReader
{
    /// <summary>64 KiB, matching <see cref="Mp4Remuxer"/>'s sample copy.</summary>
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Write the planned output's bytes in <c>[start, endInclusive]</c> to <paramref name="destination"/> —
    /// an INCLUSIVE end, matching HTTP's <c>Range</c> header. A PUSH-style wrapper over
    /// <see cref="Mp4LayoutRangeStream"/>, which owns the range→span mapping. ⚠ <paramref name="source"/>'s
    /// position is unspecified on return, and this method never closes it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> is negative or past
    /// <paramref name="endInclusive"/>, or <paramref name="endInclusive"/> reaches
    /// <see cref="Mp4Layout.TotalLength"/>.</exception>
    public static void CopyRange(Mp4Layout layout, Stream source, long start, long endInclusive,
                                 Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        using var reader = new Mp4LayoutRangeStream(layout, source, start, endInclusive);   // ownsSource: false

        var buffer = new byte[BufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            destination.Write(buffer, 0, read);
        }
    }

    /// <summary>
    /// The index of the first span in <paramref name="samples"/> that can hold byte <paramref name="target"/>,
    /// skipping any ZERO-length span. Binary search for the RIGHTMOST span at or before the target: offsets
    /// are non-decreasing, and the content-bearing span at an offset is always the LAST of that run.
    /// </summary>
    /// <returns>An index into <paramref name="samples"/>, or -1 when the list is empty.</returns>
    internal static int FindFirstSpanCovering(IReadOnlyList<Mp4SampleSpan> samples, long target)
    {
        var lo = 0;
        var hi = samples.Count - 1;
        var candidate = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (samples[mid].OutputOffset <= target) { candidate = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        // Defensive: a well-formed layout never ends a run of zero-length spans with nothing nonzero after it.
        while (candidate >= 0 && candidate < samples.Count && samples[candidate].Length == 0) candidate++;
        return candidate < samples.Count ? candidate : -1;
    }
}

/// <summary>
/// A PULL-based reader over one range of a planned layout, as a forward-only <see cref="Stream"/>.
/// <para>
/// 🔴 <b><see cref="Read"/> can be called with any <c>count</c></b> (2 KiB from Android, 32 KiB from iOS), so
/// a span or header larger than one buffer must RESUME where the previous call stopped — hence the cursor
/// fields below persist across calls. ⚠ Recomputing any per call either re-walks the sample list from the
/// start (O(n²) over hundreds of thousands of spans) or silently re-serves bytes already sent.
/// </para>
/// <para>
/// ⚠ <b>Ownership of <c>source</c> is opt-in and defaults to OFF</b>, since
/// <see cref="Mp4LayoutReader.CopyRange"/> needs it open afterwards. When owned, this closes it at its own
/// bound — like <see cref="BoundedBodyStream"/> — not on a platform-initiated close iOS never sends.
/// </para>
/// <para>
/// ⚠ <b>A read failure also closes <c>source</c>, and calls <c>onReadFailure</c> before rethrowing.</b> The
/// bytes are read AFTER the status line and <c>Content-Length</c> are committed, so a source that moved out
/// from under a cached plan can only be caught here; <c>onReadFailure</c> drops the poisoned cache entry.
/// </para>
/// </summary>
internal sealed class Mp4LayoutRangeStream : Stream
{
    private readonly Mp4Layout _layout;
    private readonly Stream _source;
    private readonly bool _ownsSource;
    private readonly Action<Exception>? _onReadFailure;
    private readonly long _endInclusive;
    private readonly long _length;

    /// <summary>What is left of the header slice for this range. Empty once the header portion is served.</summary>
    private ReadOnlyMemory<byte> _headerRemaining;

    /// <summary>The next output-space byte still owed out of <c>mdat</c> (see <see cref="AdvanceToNextSpan"/>).</summary>
    private long _mdatCursor;

    /// <summary>The next span to examine. Monotonic: a span stepped past is never revisited.</summary>
    private int _sampleIndex;

    /// <summary>Where in <see cref="_source"/> to resume reading the CURRENT span's overlap from.</summary>
    private long _currentSpanSourceCursor;

    /// <summary>How many bytes of the CURRENT span's overlap with the requested range remain unread.</summary>
    private long _currentSpanRemaining;

    private long _position;
    private bool _finished;

    /// <summary>Guards <see cref="CloseSourceIfOwned"/> against running twice: a self-close from inside
    /// <see cref="Read"/> can race a caller's <see cref="Dispose(bool)"/>.</summary>
    private int _disposed;

    /// <param name="layout">What the remux would produce.</param>
    /// <param name="source">The exact stream <paramref name="layout"/> was planned from. Read and seeked freely.</param>
    /// <param name="start">First byte of the range, inclusive, zero-based against the planned output.</param>
    /// <param name="endInclusive">Last byte of the range, inclusive — HTTP Range semantics, not exclusive.</param>
    /// <param name="ownsSource">Whether this stream disposes <paramref name="source"/>. Defaults to <c>false</c>.</param>
    /// <param name="onReadFailure">Called, before the triggering exception is rethrown, when
    /// <paramref name="source"/> throws or runs dry before a planned span's bytes do. Optional.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="start"/> is negative, <paramref name="start"/> is past <paramref name="endInclusive"/>,
    /// or <paramref name="endInclusive"/> reaches or passes <see cref="Mp4Layout.TotalLength"/>.
    /// </exception>
    public Mp4LayoutRangeStream(Mp4Layout layout, Stream source, long start, long endInclusive,
                                bool ownsSource = false, Action<Exception>? onReadFailure = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(source);
        if (start < 0)
            throw new ArgumentOutOfRangeException(nameof(start), start, "a range start must not be negative");
        if (endInclusive >= layout.TotalLength)
            throw new ArgumentOutOfRangeException(nameof(endInclusive), endInclusive,
                $"a range end must be before the layout's total length ({layout.TotalLength})");
        if (start > endInclusive)
            throw new ArgumentOutOfRangeException(nameof(start), start, "a range start must not be past its own end");

        _layout = layout;
        _source = source;
        _ownsSource = ownsSource;
        _onReadFailure = onReadFailure;
        _endInclusive = endInclusive;
        _length = endInclusive - start + 1;

        var header = layout.Header;
        _headerRemaining = start < header.Length
            ? header[(int)start..(int)Math.Min(endInclusive + 1, header.Length)]
            : ReadOnlyMemory<byte>.Empty;

        if (endInclusive < header.Length)
        {
            // Wholly inside the header: the sentinel makes AdvanceToNextSpan see an exhausted walk.
            _sampleIndex = layout.Samples.Count;
        }
        else
        {
            _mdatCursor = Math.Max(start, header.Length);
            var index = Mp4LayoutReader.FindFirstSpanCovering(layout.Samples, _mdatCursor);
            _sampleIndex = index < 0 ? layout.Samples.Count : index;   // -1 only for an empty Samples list
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException("Mp4LayoutRangeStream is forward-only; it cannot seek.");
    }

    /// <summary>
    /// Read up to <paramref name="count"/> bytes of the range, resuming where the previous call left off: the
    /// header remainder first, then <c>mdat</c> span by span, at most one span per call. A short nonzero read
    /// passes through; only a <c>0</c> while the span still owes bytes is truncation.
    /// ⚠ When this stream owns the source it closes it on the read that delivers the LAST byte, not on a
    /// later call that would merely confirm EOF — nothing is guaranteed to ask again.
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count <= 0) return 0;

        if (!_headerRemaining.IsEmpty)
        {
            var take = Math.Min(count, _headerRemaining.Length);
            _headerRemaining.Span[..take].CopyTo(buffer.AsSpan(offset, take));
            _headerRemaining = _headerRemaining[take..];
            _position += take;
            if (_position >= _length) CloseSourceIfOwned();
            return take;
        }

        if (_finished) return 0;

        if (_currentSpanRemaining <= 0 && !AdvanceToNextSpan())
        {
            _finished = true;
            CloseSourceIfOwned();
            return 0;
        }

        var toRead = (int)Math.Min(count, _currentSpanRemaining);
        int read;
        try
        {
            _source.Position = _currentSpanSourceCursor;
            read = _source.Read(buffer, offset, toRead);
        }
        catch (Exception ex)
        {
            _onReadFailure?.Invoke(ex);
            CloseSourceIfOwned();
            throw;
        }

        if (read <= 0)
        {
            var eof = new EndOfStreamException("the source ended before a planned span's bytes did");
            _onReadFailure?.Invoke(eof);
            CloseSourceIfOwned();
            throw eof;
        }

        _currentSpanSourceCursor += read;
        _currentSpanRemaining -= read;
        _position += read;
        if (_position >= _length) CloseSourceIfOwned();
        return read;
    }

    /// <summary>
    /// Advance past any zero-length span and land on the next one this range overlaps, recording where to
    /// resume reading it from. Called once per span, never once per <see cref="Read"/>.
    /// </summary>
    /// <returns><c>true</c> with the current-span cursors set, <c>false</c> when no further span overlaps.</returns>
    private bool AdvanceToNextSpan()
    {
        var samples = _layout.Samples;
        while (_sampleIndex < samples.Count)
        {
            var span = samples[_sampleIndex++];
            if (span.Length == 0) continue;                        // no bytes to give, and no position to advance
            if (span.OutputOffset > _endInclusive)                  // past the request — every later span is further still
            {
                _sampleIndex = samples.Count;
                return false;
            }

            var overlapStart = Math.Max(_mdatCursor, span.OutputOffset);
            var overlapEndExclusive = Math.Min(_endInclusive + 1, span.OutputOffset + span.Length);
            if (overlapEndExclusive <= overlapStart) continue;

            _currentSpanSourceCursor = span.SourceOffset + (overlapStart - span.OutputOffset);
            _currentSpanRemaining = overlapEndExclusive - overlapStart;
            _mdatCursor = overlapEndExclusive;
            return true;
        }

        return false;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("Mp4LayoutRangeStream is forward-only; it cannot seek.");

    public override void SetLength(long value) =>
        throw new NotSupportedException("Mp4LayoutRangeStream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Mp4LayoutRangeStream is read-only.");

    /// <summary>Idempotent, and a no-op unless this stream was constructed with <c>ownsSource: true</c>.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) CloseSourceIfOwned();
        base.Dispose(disposing);
    }

    private void CloseSourceIfOwned()
    {
        if (!_ownsSource) return;
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;   // already closed — the racing second call
        _source.Dispose();
    }
}
