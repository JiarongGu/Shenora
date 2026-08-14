using Shenora;

namespace Shenora.Core.WebView;

/// <summary>
/// A response body that yields at most <see cref="Length"/> bytes from an inner stream, closes the inner
/// stream the moment the last byte is read, and tolerates being disposed again afterwards.
/// <para>
/// <b>Why this exists at all:</b> <see cref="WebViewResourceResponse.Content"/> is read by the platform
/// AFTER the handler that produced it has returned, and its own doc says not to wrap it in a
/// <c>using</c> — so the seam that hands a body to the platform cannot dispose anything itself. Every
/// body handed over today is a <see cref="MemoryStream"/> for exactly that reason: it needs nobody to
/// close it, because there is nothing to close. That is also its cost — <c>WebViewFiles.Read</c> used to
/// do <c>new byte[count]</c> then <c>ReadExactly</c>, and under
/// <see cref="WebViewRangeDelivery.Unsliced"/> the requested window IS the whole file (D44), so every
/// <c>UseFiles</c> response on Android allocated the entire file. This type is the seam a lazily-read
/// body can sit behind without WebViewFiles or ServeRange changing shape — a Stream went in, a Stream
/// still comes out.
/// </para>
/// <para>
/// 🔴 <b>The two measurements this whole design turns on</b> (real devices, 2026-08-12; the instrument was a
/// THROWAWAY probe — a stream logging its own reads and its own <c>Dispose</c>, handed back as a live 200 body
/// — deleted the moment it had answered, so the numbers are the record and they live in
/// <c>.claude/knowledge/mobile-shells.md</c>): <b>Android disposes a response's <c>Content</c>
/// after reading it to EOF</b> (<c>reads=128 @ 2 KiB, eof=True, DISPOSED=True</c>); <b>iOS never does</b>
/// (<c>reads=8 @ 32 KiB, eof=True, DISPOSED=False</c>, re-checked over a 12-minute window). The desktop
/// host disposes a response's content only when handing it over FAILED, which is a third case again —
/// nothing on the success path there closes it either. So a lazy body cannot rely on the platform in
/// EITHER direction: it must close itself the instant its own bound is satisfied, and it must survive
/// being closed a second time by whichever platform still bothers to. Getting only one of those right
/// leaks the underlying handle on whichever shell was skipped — and iOS is the shell that issues
/// hundreds of range requests for one clip (AVFoundation's container reads, D71), so it is the one that
/// would exhaust a handle budget fastest if this got the disposal backwards.
/// </para>
/// </summary>
internal sealed class BoundedBodyStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private readonly Action<string>? _log;
    private long _position;
    private int _closed;

    /// <param name="inner">
    /// The source of bytes. Ownership passes to this stream: nobody else must read it, seek it or
    /// dispose it once construction succeeds — the failure path in a caller like <c>WebViewFiles.Read</c>
    /// is the one place that still has to, because THIS constructor never throws past field assignment.
    /// </param>
    /// <param name="length">
    /// The bound. Never negative; zero is legal (an empty range) and closes <paramref name="inner"/>
    /// immediately, because a zero-byte body never takes the read that would otherwise trigger the
    /// close — there is no "last byte" for <see cref="Read"/> to notice.
    /// </param>
    /// <param name="log">
    /// Optional diagnostic sink, guarded and lazy exactly like the rest of the kit's callbacks
    /// (<see cref="AppCallback.Log"/>). Used only for the one path that is not simply "reached EOF" or
    /// "disposed twice" — a source that runs dry before the bound does, which is a caller telling this
    /// type a length it cannot actually deliver.
    /// </param>
    public BoundedBodyStream(Stream inner, long length, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        _inner = inner;
        _length = length;
        _log = log;

        // Nothing will ever call Read() for a zero-byte body — Read short-circuits on `remaining <= 0`
        // before touching `_inner` at all — so the close that normally happens on the LAST successful
        // read has no read to hang off. Do it here instead, or an empty file/range leaks its handle for
        // as long as whichever platform happens to hold this response, which on iOS is indefinite.
        if (length == 0) CloseInner();
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException("BoundedBodyStream is forward-only; it cannot seek.");
    }

    /// <summary>
    /// Read up to <paramref name="count"/> bytes, clamped to what the bound has left.
    /// <para>
    /// Closes <c>_inner</c> the moment <see cref="Position"/> reaches <see cref="Length"/> — INSIDE this
    /// call, before returning — because that is the only reliable hook this type has: iOS never disposes
    /// the response body (measured above), so a body that waited for a platform-initiated close would
    /// simply never close there.
    /// </para>
    /// <para>
    /// ⚠ Truncation is judged on EXACTLY ZERO, not on "short". <c>_inner.Read</c> handing back fewer
    /// bytes than <paramref name="count"/> while it still has more to give is ordinary, legal
    /// <see cref="Stream.Read(byte[], int, int)"/> behaviour — this method makes exactly one call to
    /// <c>_inner.Read</c> per call of its own and never loops to "fill the buffer", so an ordinary nonzero
    /// short read passes straight through untouched; a caller wanting the whole window loops itself
    /// (<see cref="Stream.ReadExactly(byte[], int, int)"/> is exactly that loop). Only a read that comes
    /// back <c>0</c> while the bound still owes bytes means the source is actually exhausted, and that
    /// is the one case this treats as truncation, throwing <see cref="EndOfStreamException"/> rather than
    /// returning 0 quietly. <c>BoundedBodyStreamTests.An_inner_stream_shorter_than_the_bound_throws_rather_than_ending_quietly</c>
    /// is the proof: its first call asks for 5 bytes and gets back 2 — short, nonzero, and it does NOT
    /// throw — and only the second call, once the source is drained and answers <c>0</c>, does.
    /// </para>
    /// <para>
    /// Returning 0 quietly on that zero-while-owed read would tell <c>ServeRange</c>'s caller — and,
    /// through <c>Content-Length</c>, the page's media element — that the response ended cleanly and
    /// correctly sized, when it is actually short: the same class of silent corruption a cancelled remux
    /// was fixed for elsewhere in this repo (see the commit correcting a cancelled remux reading as a
    /// corrupt file). A resource whose length was mis-stated needs to fail loudly, not play back wrong.
    /// </para>
    /// <para>
    /// 🔴 <b>And "fail loudly" now means something different than it used to, worth being honest about
    /// rather than hedging.</b> This throw fires from inside a PLATFORM read — after whatever produced
    /// this body (<c>WebViewFiles.Serve</c>, for the file case) already returned a committed 200/206 with
    /// its own <c>Content-Length</c> promise. See <c>WebViewFiles.Read</c>'s own doc for the concrete case
    /// this changed.
    /// ✅ <b>WHAT "LOUDLY" LOOKS LIKE IS THE SHELL'S ANSWER, NOT THIS TYPE'S, AND ANDROID'S IS FIXED
    /// (2026-08-13): a page-visible FAILED LOAD.</b> Its handover (<c>MobileWebViewInterceptor</c>) translates
    /// a mid-read throw into a <c>Java.IO.IOException</c>, which arrives in Java as its peer, so Chromium's
    /// <c>InputStreamUtil.read</c> catches it and returns <c>-2</c> — the status the native reader turns into a
    /// net error. Until then that same throw KILLED THE PROCESS: a managed exception reaches Java as
    /// <c>android.runtime.JavaProxyThrowable</c>, which extends <c>java.lang.Error</c> and is outside that catch
    /// by construction, so it left <c>InputStreamAdapter.read</c> uncaught. ⚠ <b>The other two shells still
    /// have no good answer, so do not read Android's as general:</b> iOS neither crashes nor reports (the page
    /// gets its committed <c>200</c> and a body short of the promise — zero bytes in the measured case), and the
    /// DESKTOP host is unmeasured for a mid-read throw (its happy path is not — see <see cref="Seek"/>). Both
    /// mobile measurements were reproduced twice, once through this type over a source truncated mid-request
    /// and once through a stream that simply threw at a fixed offset with no kit code under it, so the
    /// behaviour belongs to the platform rather than to this seam
    /// (<c>.claude/knowledge/mobile-shells.md</c> has every arm and its raw log).
    /// ⚠ <b>The same is true of ANY exception an inner stream raises mid-read, not just this one</b>: an
    /// <see cref="IOException"/> from a pulled volume or a dropped share takes the identical path (see
    /// <c>WebViewFiles.Read</c>, which used to catch every one of them before a byte had left). Do NOT "fix"
    /// it by returning 0 here: that is the silent-corruption path this whole paragraph exists to refuse, and
    /// it would trade a visible crash for a video that plays back wrong.
    /// </para>
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var remaining = _length - _position;
        if (remaining <= 0) return 0;   // the bound was already reached — a clean EOF, not a fresh close

        var toRead = (int)Math.Min(count, remaining);
        if (toRead <= 0) return 0;

        int read;
        try
        {
            read = _inner.Read(buffer, offset, toRead);
        }
        catch (Exception ex)
        {
            // 🔴 THE ONLY MOMENT ANYONE KNOWS THIS BODY WENT SHORT, so it is the only honest place to say so.
            // Measured 2026-08-13: on iOS and on WebView2 a throwing read produces a SILENT SHORT BODY — the
            // page keeps its committed 200 and simply gets fewer bytes than `Content-Length` promised, with
            // no error on the fetch. So the PAGE cannot detect it, by construction: the status line and the
            // length are already out. The host can, and until now the kit knew and said nothing.
            //
            // ⚠ Logged HERE rather than in `Dispose`, and that is not a style choice: iOS NEVER disposes a
            // response body (measured — 712/712 drained instead), so dispose-time detection would be silent
            // on the very shell that most needs it. A read is the one event every shell performs.
            //
            // ⚠ RETHROWN UNCHANGED. Android's shell translates this into a `Java.IO.IOException` so Chromium's
            // own catch sees it and the page gets a visible failed load; swallowing it here would undo that
            // and turn Android's good outcome into the silent one.
            Log(() => $"[Shenora.Core.WebView] BoundedBodyStream FAILED MID-BODY at {_position} of {_length} "
                    + $"byte(s) ({ex.GetType().Name}) — the page has already been sent its status line and "
                    + "Content-Length, so on iOS and WebView2 it will see a SHORT BODY WITH NO ERROR. Verify "
                    + "integrity in the page where completeness matters.");
            CloseInner();
            throw;
        }

        if (read == 0)
        {
            // The source ran dry before the bound did. Close now — this body has failed and nothing will
            // read it again successfully, so there is no later "last byte" to hang the close off.
            Log(() => $"[Shenora.Core.WebView] BoundedBodyStream promised {_length} byte(s) but the inner "
                    + $"stream reached EOF after only {_position} — the caller's length was wrong.");
            CloseInner();
            throw new EndOfStreamException(
                $"BoundedBodyStream expected {remaining} more byte(s) but the inner stream reached EOF.");
        }

        _position += read;
        if (_position >= _length) CloseInner();   // the last byte just left — close ourselves NOW.
        return read;
    }

    public override void Flush() { }

    /// <summary>
    /// Always throws — and ✅ <b>that no platform ever calls it is MEASURED, not assumed (2026-08-13)</b>, which
    /// matters because every <c>UseFiles</c> response now hands a webview a body reporting
    /// <see cref="CanSeek"/> <c>== false</c> where it used to get a seekable <see cref="MemoryStream"/>.
    /// WebView2 was the open question, since <c>CreateWebResourceResponse</c> consumes the stream through a COM
    /// <c>IStream</c> and such a consumer may issue a harmless <c>Seek(0, Current)</c> just to read the position
    /// — which a <see cref="MemoryStream"/> answered and this type refuses. The desktop sample's
    /// <c>InterceptorProbe</c> reports <c>INTERCEPTOR SEAM: PASS</c> across a <c>206</c> with its
    /// <c>Content-Range</c>, an offset pinned by CONTENT rather than by length, the whole file, an unsatisfiable
    /// range and a traversal refusal — so no seek and no position query reaches here, and a
    /// <c>Seek(0, Current)</c> tolerance would be dead code. ⚠ Do not add one speculatively; if a future
    /// consumer needs it, the tolerance is a no-op returning <see cref="Position"/> and nothing wider.
    /// </summary>
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("BoundedBodyStream is forward-only; it cannot seek.");

    public override void SetLength(long value) =>
        throw new NotSupportedException("BoundedBodyStream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("BoundedBodyStream is read-only.");

    /// <summary>
    /// Idempotent by design, via <see cref="Interlocked.Exchange(ref int, int)"/> on <c>_closed</c> rather
    /// than a plain bool check — Android's own dispose (measured above) always arrives AFTER this type's
    /// self-close from <see cref="Read"/> has already run, so the ordinary case is not a race between two
    /// threads but a second call on the SAME logical body. The guard has to be correct either way.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) CloseInner();
        base.Dispose(disposing);
    }

    private void CloseInner()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;   // already closed — the second dispose
        _inner.Dispose();
    }

    private void Log(Func<string> message) => AppCallback.Log(_log, message);
}
