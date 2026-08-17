using Microsoft.Extensions.Logging;

namespace Shenora.Core.WebView;

/// <summary>
/// A response body that yields at most <see cref="Length"/> bytes from an inner stream, closes the inner
/// stream the moment the last byte is read, and tolerates being disposed again afterwards. Forward-only:
/// <see cref="CanSeek"/> is false and the body cannot be re-read.
/// <para>
/// 🔴 <b>It must close itself at its bound AND survive a second close, because no platform does both.</b>
/// <see cref="WebViewResourceResponse.Content"/> is read AFTER the handler that produced it has returned,
/// so the seam handing a body over cannot dispose anything itself.
/// Android disposes a response's <c>Content</c> after reading it to EOF; iOS never does; the desktop host
/// disposes one only when the handover FAILED. Getting either half wrong leaks the underlying handle on
/// whichever shell was skipped. Per-shell measurements: <c>docs/design/mobile-shells.md</c>.
/// </para>
/// </summary>
internal sealed class BoundedBodyStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private readonly ILogger? _log;
    private long _position;
    private int _closed;

    /// <param name="inner">
    /// The source of bytes. Ownership passes to this stream: nobody else must read it, seek it or dispose
    /// it once construction succeeds — a caller's own failure path (<c>WebViewFiles.Read</c>) is the one
    /// place that still has to.
    /// </param>
    /// <param name="length">
    /// The bound. Never negative; zero is legal (an empty range) and closes <paramref name="inner"/>
    /// immediately, because a zero-byte body never takes the read that would otherwise trigger the close.
    /// </param>
    /// <param name="log">
    /// Optional diagnostic sink (<see cref="AppCallback.Log"/>). Used only for a source that runs dry
    /// before the bound does — a caller telling this type a length it cannot actually deliver.
    /// </param>
    public BoundedBodyStream(Stream inner, long length, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        _inner = inner;
        _length = length;
        _log = log;

        // Read short-circuits on `remaining <= 0` without touching `_inner`, so a zero-byte body never
        // takes the read the close normally hangs off. Close here instead, or an empty file/range leaks
        // its handle for as long as the platform holds this response — on iOS, indefinitely.
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
    /// Read up to <paramref name="count"/> bytes, clamped to what the bound has left. Closes <c>_inner</c>
    /// the moment <see cref="Position"/> reaches <see cref="Length"/>, INSIDE this call — iOS never
    /// disposes the response body.
    /// <para>
    /// ⚠ <b>Truncation is judged on EXACTLY ZERO, not on "short".</b> One call to <c>_inner.Read</c> per
    /// call of its own, never looping to fill the buffer, so an ordinary nonzero short read passes straight
    /// through — legal <see cref="Stream.Read(byte[], int, int)"/> behaviour, and a caller wanting the whole
    /// window loops itself. Only a <c>0</c> returned while the bound still owes bytes means the source is
    /// exhausted, and that throws <see cref="EndOfStreamException"/>.
    /// </para>
    /// <para>
    /// 🔴 <b>Do NOT "fix" that by returning 0.</b> A quiet 0 tells the caller — and, through
    /// <c>Content-Length</c>, the page's media element — that the response ended cleanly and correctly
    /// sized when it is short. Any exception the inner stream raises mid-read gets out for the same reason.
    /// </para>
    /// <para>
    /// ⚠ <b>The throw then gets the SHELL's answer, and only one of the three is good.</b> It fires from
    /// inside a platform read, after a committed 200/206 and its <c>Content-Length</c> promise. Android
    /// translates it at the handover (<c>MobileWebViewInterceptor</c>) into a <c>Java.IO.IOException</c>
    /// that Chromium's own catch sees — a page-visible failed load. iOS neither crashes nor reports: the
    /// page keeps its committed <c>200</c> and gets a short body. The desktop host is unmeasured for a
    /// mid-read throw.
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
            // 🔴 The only moment anyone knows this body went short: on iOS and WebView2 a throwing read
            // produces a SILENT short body the page cannot detect, its status line and Content-Length
            // already out. Logged HERE rather than in Dispose — iOS never disposes a response body, so
            // dispose-time detection would be silent on the shell that most needs it.
            //
            // ⚠ RETHROWN UNCHANGED — Android's shell translates this into a Java.IO.IOException so
            // Chromium's own catch sees it. Swallowing it here turns that good outcome into the silent one.
            Log(() => $"[Shenora.Core.WebView] BoundedBodyStream FAILED MID-BODY at {_position} of {_length} "
                    + $"byte(s) ({ex.GetType().Name}) — the page has already been sent its status line and "
                    + "Content-Length, so on iOS and WebView2 it will see a SHORT BODY WITH NO ERROR. Verify "
                    + "integrity in the page where completeness matters.");
            CloseInner();
            throw;
        }

        if (read == 0)
        {
            // The source ran dry before the bound did — this body has failed, so there is no later
            // "last byte" to hang the close off.
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
    /// Always throws — every <c>UseFiles</c> response hands the webview a body reporting
    /// <see cref="CanSeek"/> <c>== false</c>, and no platform has been observed to seek one, not even the
    /// <c>Seek(0, Current)</c> position query a COM <c>IStream</c> consumer can make
    /// (<c>InterceptorProbe</c>, desktop sample).
    /// </summary>
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("BoundedBodyStream is forward-only; it cannot seek.");

    public override void SetLength(long value) =>
        throw new NotSupportedException("BoundedBodyStream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("BoundedBodyStream is read-only.");

    /// <summary>
    /// Idempotent, via <see cref="Interlocked.Exchange(ref int, int)"/> rather than a plain bool check:
    /// Android's own dispose arrives AFTER this type's self-close from <see cref="Read"/>, so the ordinary
    /// case is a second call on the same logical body, and a two-thread race must also be safe.
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

    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_log, message, exception: failure);
}
