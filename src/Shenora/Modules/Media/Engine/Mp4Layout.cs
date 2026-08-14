using Shenora.Core.WebView;

namespace Shenora.Modules.Media;

/// <summary>
/// Where one sample lives in the SOURCE and where it lands in the planned OUTPUT.
///
/// <para>
/// 🔴 <b>The length is the same on both sides, and that single fact is what makes a remux servable as a
/// range request.</b> A remux copies frames — it does not decode, pad or re-time them — so a byte range of
/// the output is answerable straight from the source: find the spans the range touches, read those bytes,
/// send them. Nothing has to have been produced first, which is the difference between this path and the
/// segment path (D71).
/// </para>
/// </summary>
/// <param name="SourceOffset">Absolute offset of the frame's bytes in the source stream.</param>
/// <param name="Length">The frame's length in bytes — identical in both, because a remux copies.</param>
/// <param name="OutputOffset">Absolute offset of those bytes in the planned output.</param>
public readonly record struct Mp4SampleSpan(long SourceOffset, int Length, long OutputOffset);

/// <summary>
/// The MP4 a remux WOULD write, computed without writing it.
///
/// <para>
/// 🔴 <b>This is what makes a remux servable as an ordinary URL.</b> The total is known before any work, so
/// a 206 can carry a real <c>Content-Range</c>, and any range maps to source bytes — so a seek to the end is
/// serviceable on a cold start. Measured 2026-08-12 (D71): a 206 with a real total plays on both mobile
/// shells with the whole timeline seekable (<c>seekable=60.02</c> on each) and a cold seek to 80 % landing,
/// while a 200 with no length is refused outright by Android's element. Everything that failed there, failed
/// for want of a SIZE — which is a claim about the HEADER and not about how much has arrived: the separate
/// THROTTLED-body run in the same decision reached <c>seekable=[0–60]</c> at <c>buffered=[0–8.3]</c>.
/// </para>
///
/// <para>
/// 🔴 <b>The one invariant every consumer rests on: this describes the output EXACTLY, to the byte.</b>
/// <see cref="Mp4Remuxer.Plan"/> and the writer run ONE implementation of the preparation and ONE of the
/// header composition, so they cannot drift apart in code. ⚠ What they do not share is the WALK — each
/// reads the source itself — so agreement rests on that walk being deterministic over the same bytes, which
/// is representable, falsifiable, and exactly what <c>Mp4LayoutTests</c> pins (planned length == written
/// length, and the layout rebuilding the written file byte for byte). It also means a layout is only valid
/// for the source it was computed from: cache it against identity+length+mtime, never against a path alone.
/// If a plan and a write ever disagreed, a route would answer a <c>Content-Range</c> total the bytes do not
/// honour — and a media element's failure for that is SILENT: a blank picture, no error, nothing logged.
/// </para>
///
/// <para>
/// ⚠ <b>Cost and memory, stated so nobody discovers them — and the number that matters is the PEAK, not
/// this object.</b> Planning walks the source's clusters once (metadata, never payloads), so time is what
/// the remuxer's first pass costs and no more. Memory is the part to size before trusting it, counting BOTH
/// tracks: a two-hour film is ~216,000 video frames at 30 fps plus ~337,000 AAC frames at 48 kHz, so
/// ~553,000 spans ≈ <b>13 MB</b> for the finished layout. Producing it holds far more live at once — the
/// reader's per-frame records, the timing pass's copy of them, five <c>long[]</c> per track, the write order
/// at 40 bytes an entry plus the sort's buffer, and three whole <c>moov</c> copies — <b>on the order of
/// 110–150 MB</b> for that film, and roughly a GIGABYTE at the reader's four-million-sample ceiling. Those
/// are arithmetic from the struct layouts, not measurements: approximate in multiplier, certain in
/// direction. So keep the layout and pay once: cache it against the source's identity (the conversion
/// route's <c>DerivedCacheKey.For(path, length, mtime)</c> is the key shape already in use), never rebuild
/// it per range request, and do not plan two films concurrently on a phone.
/// </para>
///
/// <para>
/// ⚠ <b>Do not compare two of these for equality.</b> It is a record, so the compiler generated
/// <c>Equals</c>, <c>==</c> and <c>GetHashCode</c> — and they are meaningless here, because
/// <see cref="ReadOnlyMemory{T}"/> and <see cref="IReadOnlyList{T}"/> compare by segment and by reference.
/// Two plans of the SAME stream come out unequal. The record shape is kept for deconstruction and
/// <c>with</c>; a cache keys on the source's identity, never on the layout.
/// </para>
/// </summary>
/// <param name="Header">
/// Every byte before the first sample: <c>ftyp</c>, <c>moov</c>, and the <c>mdat</c> box header — literally
/// the output's first <c>Header.Length</c> bytes.
/// <para>
/// ⚠ <b>The <c>mdat</c> box header is INSIDE this on purpose</b>, even though it is not part of the movie
/// box. It is 8 bytes for an ordinary file and 16 for one past 4 GB, and only the composer knows which — so
/// leaving it out would make every consumer re-derive that choice to find where the samples begin, which is
/// two implementations of one calculation and exactly how they come to disagree. With it in, the contract is
/// arithmetic a caller cannot get wrong: bytes <c>[0, Header.Length)</c> are these, and
/// <c>Samples[0].OutputOffset == Header.Length</c>.
/// </para>
/// </param>
/// <param name="Samples">
/// Every sample, in the order the writer emits them — which is decode-time order across the tracks, not
/// source order. Contiguous by construction: each span begins where the previous one ended.
/// <para>
/// ⚠ A span may be ZERO length. A degenerate laced frame produces one, and it shares its
/// <c>OutputOffset</c> with the span after it — so code answering "which span holds byte X" must skip
/// empties rather than match on the offset alone.
/// </para>
/// </param>
/// <param name="TotalLength">
/// <c>Header.Length</c> plus every sample — the whole file, and the number a <c>Content-Range</c> states.
/// </param>
public sealed record Mp4Layout(
    ReadOnlyMemory<byte> Header,
    IReadOnlyList<Mp4SampleSpan> Samples,
    long TotalLength);

/// <summary>
/// Turns a planned <see cref="Mp4Layout"/> into bytes, reading only the source bytes a requested range
/// actually touches — the read half of D71's computed-remux path, and the piece a 206 route calls once per
/// request.
///
/// <para>
/// 🔴 <b>Why a byte range can be answered for a file that has never been produced.</b> A layout already
/// states, for every byte of the output, whether it is a literal header byte or which source frame it came
/// from. So "give me bytes [start, end]" is arithmetic over that map plus one seek per touched frame —
/// nothing has to be assembled first, which is the entire reason planning exists instead of writing (D71).
/// </para>
///
/// <para>
/// ⚠ <b>This trusts that <c>source</c> is the SAME stream a layout was planned from, unchanged since — and
/// that is deliberately the CALLER's job, not this type's.</b> <see cref="Mp4Layout"/> carries no identity
/// of its own on purpose: <see cref="Mp4Remuxer.Plan"/> takes an open <see cref="Stream"/>, not a path, and
/// a plan of a <see cref="MemoryStream"/> or a network body has no path or mtime to remember in the first
/// place — so a field here could not always answer the question anyway. A route serving a real FILE already
/// has both, and already has the shape to key a cache on them
/// (<c>DerivedCacheKey.For(path, length, mtime)</c>, the same shape <c>SegmentStream</c> and
/// <c>MediaConversion</c> already use), so that check belongs one layer up, where the identity actually
/// lives, rather than duplicated here against a type that cannot always have one. <b>Getting this wrong is
/// silent</b>: every span still resolves to a valid offset in the wrong file, so the bytes that come back
/// are some other file's frames wearing this one's <c>Content-Range</c> — a route must re-plan (or confirm
/// its cached plan still matches) whenever the identity check says the file moved on.
/// </para>
/// </summary>
public static class Mp4LayoutReader
{
    /// <summary>
    /// 64 KiB — the same chunk size <see cref="Mp4Remuxer"/>'s own sample copy uses, so a single very large
    /// span (an uncompressed keyframe can run into megabytes) never has to be buffered whole.
    /// </summary>
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Write exactly the planned output's bytes in <c>[start, endInclusive]</c> to <paramref name="destination"/>
    /// — an INCLUSIVE end, matching HTTP's own <c>Range</c> header, so a route can pass the header's two
    /// numbers straight through with no off-by-one translation either side would otherwise have to remember.
    ///
    /// <para>
    /// 🔴 <b>This is a PUSH-style wrapper over <see cref="Mp4LayoutRangeStream"/>, not a second implementation
    /// of the range→span mapping.</b> Every rule about how a range straddles the header into <c>mdat</c>, how
    /// a request starts and ends mid-sample, and how a zero-length laced span is skipped rather than walked
    /// onto is written ONCE, on the pull reader — see <see cref="Mp4LayoutRangeStream"/>'s own remarks for all
    /// three — and this method drives it into an ordinary buffer. Two implementations of that mapping is
    /// precisely how they come to disagree; this repo has the scar (<c>Mp4Remuxer.Plan</c>/<c>Write</c> were
    /// refactored onto one <c>ComposeHeader</c>/<c>Prepare</c> for the same reason), so driving one FROM the
    /// other makes disagreement structurally impossible rather than merely tested against.
    /// </para>
    /// <para>
    /// ⚠ <b>The direction, and why not the reverse.</b> A pull reader can serve a push caller by looping
    /// <see cref="Mp4LayoutRangeStream.Read"/> into a buffer, exactly as this method does; a push writer
    /// cannot serve a pull caller without buffering its whole output somewhere for <c>Read</c> to hand back
    /// piecemeal — which is the exact materialisation <see cref="ComputedRemuxRoute.Produce"/> exists to stop
    /// doing. So the pull reader has to be the one holding the real mapping state, and this method is the
    /// thin side of the pair.
    /// </para>
    /// </summary>
    /// <param name="layout">What the remux would produce. See the type's own remarks on source identity.</param>
    /// <param name="source">
    /// The exact stream <paramref name="layout"/> was planned from. Its position is moved freely and is
    /// unspecified on return — and, unlike <see cref="ComputedRemuxRoute.Produce"/>'s own use of
    /// <see cref="Mp4LayoutRangeStream"/>, this method never closes it. Every existing caller (every theory in
    /// <c>Mp4LayoutTests</c> included) relies on being able to keep using <paramref name="source"/> afterwards,
    /// so the reader this method drives is built with ownership left OFF.
    /// </param>
    /// <param name="start">First byte of the range, inclusive, zero-based against the planned output.</param>
    /// <param name="endInclusive">Last byte of the range, inclusive — HTTP Range semantics, not exclusive.</param>
    /// <param name="destination">Where the bytes go, in order, with never more than one chunk buffered.</param>
    /// <param name="cancellationToken">
    /// Checked BEFORE each <see cref="BufferSize"/> chunk is pulled from the reader — the same granularity,
    /// and the same ORDER, the old self-contained implementation checked at: cancelling must stop this method
    /// from paying for a read (and a write) it will only throw away, not merely stop it after paying for one.
    /// A range can cover the whole file, so an abandoned request — a page that seeked again before this one
    /// finished — must not pay for I/O nobody will use.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="start"/> is negative, <paramref name="start"/> is past <paramref name="endInclusive"/>,
    /// or <paramref name="endInclusive"/> reaches or passes <see cref="Mp4Layout.TotalLength"/> — raised by
    /// <see cref="Mp4LayoutRangeStream"/>'s own constructor, which this method leans on rather than repeating
    /// the same three checks a second time.
    /// </exception>
    public static void CopyRange(Mp4Layout layout, Stream source, long start, long endInclusive,
                                 Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        // `ownsSource` defaults to false — see the `source` parameter doc above on why this call must leave
        // it exactly as every existing caller expects to find it: open, position merely unspecified.
        using var reader = new Mp4LayoutRangeStream(layout, source, start, endInclusive);

        var buffer = new byte[BufferSize];
        while (true)
        {
            // Checked BEFORE the read, not after — an already-cancelled token must not pay for one chunk of
            // I/O it will only throw away. See the parameter's own doc for why the ordering, not merely the
            // granularity, has to match the old implementation.
            cancellationToken.ThrowIfCancellationRequested();
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            destination.Write(buffer, 0, read);
        }
    }

    /// <summary>
    /// The index of the first span in <paramref name="samples"/> that can hold byte <paramref name="target"/>
    /// — skipping any ZERO-length span, which holds no byte at all and would otherwise look like a match
    /// because it shares its <see cref="Mp4SampleSpan.OutputOffset"/> with the real span that follows it.
    ///
    /// <para>
    /// Binary search for the RIGHTMOST span whose <see cref="Mp4SampleSpan.OutputOffset"/> is at or before
    /// the target: <see cref="Mp4Layout.Samples"/> is contiguous and non-decreasing in that field, several
    /// zero-length spans can share one offset, and the content-bearing span at that offset — if any — is
    /// always the LAST of the run sharing it, never the first, by construction of how the offsets accumulate
    /// (each span begins exactly where the one before it ends; a zero-length one ends where it began).
    /// </para>
    /// <para>
    /// Internal rather than private: <see cref="Mp4LayoutRangeStream"/> needs the exact same lookup to seed
    /// its own starting position, and a second binary search over the same list is precisely the kind of
    /// duplicated mapping this pairing exists to avoid.
    /// </para>
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

        // Defensive rather than load-bearing: contiguity already guarantees the span landed on above is the
        // right one whenever it is nonzero. If it is not — a run of zero-length spans with nothing nonzero
        // sharing that offset, which a well-formed layout never produces — step forward instead of handing
        // back a span that can never satisfy anything, rather than silently answering with the wrong bytes.
        while (candidate >= 0 && candidate < samples.Count && samples[candidate].Length == 0) candidate++;
        return candidate < samples.Count ? candidate : -1;
    }
}

/// <summary>
/// A PULL-based reader over one range of a planned layout: the same range→span mapping
/// <see cref="Mp4LayoutReader.CopyRange"/> walks, expressed as an ordinary forward-only <see cref="Stream"/>
/// instead of a method that writes straight to a destination.
///
/// <para>
/// 🔴 <b>Why this exists at all, and why it is the CANONICAL side of the pair.</b>
/// <see cref="ComputedRemuxRoute.Produce"/> used to answer a range by materialising it whole into a
/// <see cref="MemoryStream"/> — the entire computed output, on every platform, because a faststart file "only
/// ever requests <c>bytes=0-</c>" under <see cref="WebViewRangeDelivery.Sliced"/> and IS the
/// whole output under <see cref="WebViewRangeDelivery.Unsliced"/> (D44/D71). This type is what
/// lets that body be read lazily instead, the same seam <see cref="BoundedBodyStream"/> gave the
/// plain file route: a <see cref="Stream"/> goes in behind it, a <see cref="Stream"/> comes out, and nothing
/// upstream has to change shape.
/// </para>
///
/// <para>
/// ⚠ <b>A pull reader is genuinely harder than the push writer it replaces as the source of truth, and the
/// difference is STATE.</b> <see cref="Mp4LayoutReader.CopyRange"/> never had to remember where it was between
/// two calls — it owned the whole loop from start to finish in one stack frame. <see cref="Read"/> can be
/// called with any <c>count</c> — measured 2 KiB from Android, 32 KiB from iOS — so a span bigger than one
/// buffer, or a header bigger than one buffer, has to be resumed exactly where the PREVIOUS call left off. The
/// state that makes that possible is <c>_headerRemaining</c> (what is left of the header slice, shrinking as
/// it is consumed), <c>_sampleIndex</c> (which span in <see cref="Mp4Layout.Samples"/> is next, advancing
/// monotonically and never revisited), and <c>_currentSpanRemaining</c>/<c>_currentSpanSourceCursor</c> (how
/// much of THAT span's overlap with the requested range is still owed, and where in <c>source</c>
/// to resume reading it from). Every one of those persists across calls on purpose; recomputing any of them
/// from scratch per <see cref="Read"/> call would either re-walk the sample list from the start every time (an
/// O(n²) walk over a two-hour film's ~553,000 spans) or silently re-serve bytes already sent.
/// </para>
///
/// <para>
/// ⚠ <b>Ownership of <c>source</c> is opt-in, and the default is OFF.</b>
/// <see cref="Mp4LayoutReader.CopyRange"/> constructs one of these per call without ever disposing what it
/// wraps — every existing caller (including every theory in <c>Mp4LayoutTests</c>) expects <c>source</c> to
/// stay open and reusable afterwards, so <c>ownsSource</c> defaults to <c>false</c> for exactly
/// that call shape. <see cref="ComputedRemuxRoute.Produce"/> is the one caller that opts in
/// (<c>ownsSource: true</c>): there, <c>source</c> is a <c>FileStream</c> opened once per REQUEST and handed
/// to nobody else, so this reader is the last thing that will ever touch it, and — like
/// <see cref="BoundedBodyStream"/> — it closes <c>source</c> the moment its own bound is
/// satisfied rather than waiting for a platform-initiated close that iOS is measured to never send.
/// </para>
///
/// <para>
/// ⚠ <b>A read failure closes <c>source</c> too, and calls <c>onReadFailure</c> before rethrowing
/// — this is the read-time half of what used to be one synchronous try/catch inside <c>Produce</c>.</b> A
/// lazy body's bytes are read by the platform AFTER the response's status line and <c>Content-Length</c> are
/// already committed (see <see cref="WebViewFiles.Read"/>'s own doc for the same tradeoff on the
/// plain file path), so a source that moved out from under a cached plan can no longer be caught before the
/// headers go out — there is no way around that for a genuinely lazy body. What still has to happen is
/// dropping the poisoned cached layout so the NEXT request re-plans instead of repeating the same broken read
/// forever, and that is exactly what <c>onReadFailure</c> is for: <c>Produce</c> passes a
/// callback that logs and forgets the cache entry, this type calls it at the point the failure is actually
/// discovered, and then the original exception still propagates so the platform sees the read fail loudly.
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

    /// <summary>
    /// The next output-space byte still owed out of <c>mdat</c>. Starts at the first mdat byte this range
    /// touches and advances to the end of each span's overlap as it is fully consumed — see
    /// <see cref="AdvanceToNextSpan"/>.
    /// </summary>
    private long _mdatCursor;

    /// <summary>
    /// The next span in <see cref="Mp4Layout.Samples"/> to examine. Advances monotonically — a span already
    /// stepped past (skipped as zero-length, or fully consumed) is never revisited.
    /// </summary>
    private int _sampleIndex;

    /// <summary>Where in <see cref="_source"/> to resume reading the CURRENT span's overlap from.</summary>
    private long _currentSpanSourceCursor;

    /// <summary>How many bytes of the CURRENT span's overlap with the requested range remain unread.</summary>
    private long _currentSpanRemaining;

    private long _position;
    private bool _finished;

    /// <summary>
    /// Guards <see cref="CloseSourceIfOwned"/> against running twice — 0 or 1, flipped with
    /// <see cref="Interlocked.Exchange(ref int, int)"/> rather than a plain <c>bool</c>, matching
    /// <see cref="BoundedBodyStream"/>'s own <c>_closed</c> guard for the identical shape: a self-close from
    /// inside <see cref="Read"/> (reaching the bound, or a read failure) can race a platform- or
    /// caller-initiated <see cref="Dispose(bool)"/>, and a plain bool read-then-write is not atomic across
    /// that race.
    /// </summary>
    private int _disposed;

    /// <param name="layout">What the remux would produce. See the type's own remarks on source identity.</param>
    /// <param name="source">
    /// The exact stream <paramref name="layout"/> was planned from. Read and seeked freely; see
    /// <paramref name="ownsSource"/> for whether this stream also disposes it.
    /// </param>
    /// <param name="start">First byte of the range, inclusive, zero-based against the planned output.</param>
    /// <param name="endInclusive">Last byte of the range, inclusive — HTTP Range semantics, not exclusive.</param>
    /// <param name="ownsSource">
    /// Whether this stream disposes <paramref name="source"/> when it finishes (naturally, at its own bound,
    /// or because a read failed) or when it is itself disposed. Defaults to <c>false</c> — see the type's own
    /// remarks on why <see cref="Mp4LayoutReader.CopyRange"/> needs the opposite of what
    /// <see cref="ComputedRemuxRoute.Produce"/> needs.
    /// </param>
    /// <param name="onReadFailure">
    /// Called, before the triggering exception is rethrown, when <paramref name="source"/> either throws or
    /// runs dry before a planned span's bytes do. Optional: <see cref="Mp4LayoutReader.CopyRange"/> has no use
    /// for it, since its own caller already sees the exception synchronously.
    /// </param>
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
            // Wholly inside the header: nothing in mdat to add, mirroring CopyRange's own early return. The
            // sentinel below makes AdvanceToNextSpan see an already-exhausted walk with no extra branch needed.
            _sampleIndex = layout.Samples.Count;
        }
        else
        {
            _mdatCursor = Math.Max(start, header.Length);
            var index = Mp4LayoutReader.FindFirstSpanCovering(layout.Samples, _mdatCursor);
            // -1 only for an empty Samples list — unreachable given the checks above (an empty Samples means
            // TotalLength == Header.Length, so endInclusive < TotalLength already forces the header-only
            // branch above to fire first) but kept as a defensive guard rather than assumed.
            _sampleIndex = index < 0 ? layout.Samples.Count : index;
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
    /// Read up to <paramref name="count"/> bytes of the requested range, resuming exactly where the previous
    /// call left off.
    /// <para>
    /// Serves the header remainder first (a pure memory copy, no I/O), then walks <c>mdat</c> span by span —
    /// skipping zero-length ones, matching <see cref="Mp4LayoutReader.CopyRange"/>'s own rule for a degenerate
    /// laced frame — reading at most one span's worth of source bytes per call, exactly like an ordinary
    /// <see cref="Stream.Read(byte[], int, int)"/> is allowed to (a short nonzero read passes straight
    /// through; only a read that comes back <c>0</c> while the span still owes bytes is treated as truncation).
    /// </para>
    /// <para>
    /// Closes the underlying source (when this stream owns it — see the constructor's <c>ownsSource</c>) the
    /// instant its own bound is reached — on the read that delivers the LAST byte, not on a subsequent call
    /// that would merely confirm EOF — because nothing later is guaranteed to ask again. The same close fires,
    /// plus <c>onReadFailure</c>, if the source throws or runs dry first; see the type's own remarks on why
    /// that could not be caught earlier.
    /// </para>
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
    /// Advance past any zero-length span and land on the next one this range actually overlaps, recording
    /// where to resume reading it from — the SAME per-span arithmetic
    /// <see cref="Mp4LayoutReader.CopyRange"/> used to run inline, now split out so <see cref="Read"/> can
    /// call it exactly once per span rather than once per request.
    /// </summary>
    /// <returns>
    /// <c>true</c> with <see cref="_currentSpanSourceCursor"/>/<see cref="_currentSpanRemaining"/> set, or
    /// <c>false</c> when no further span overlaps the requested range (past the end, or the list is
    /// exhausted).
    /// </returns>
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
