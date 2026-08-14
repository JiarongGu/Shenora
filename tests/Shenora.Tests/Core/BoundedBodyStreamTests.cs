using Shenora.Core.WebView;

namespace Shenora.Tests.Core;

/// <summary>
/// The primitive that makes a response body lazy: at most <c>Length</c> bytes out of an inner stream,
/// self-closing at EOF and idempotent to a second dispose.
/// <para>
/// The two measurements this whole design turns on (real devices, 2026-08-12): Android disposes a
/// response's <c>Content</c> after reading it to EOF; iOS never does. A stream that gets only one of
/// those right leaks a handle on whichever shell it skipped — and iOS is the shell that issues ~508
/// range requests for a single 60-second clip, so it is the one that would run out.
/// </para>
/// </summary>
public class BoundedBodyStreamTests
{
    private sealed class Tracked(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public int Disposals { get; private set; }
        protected override void Dispose(bool disposing) { Disposals++; base.Dispose(disposing); }
    }

    /// 🔴 iOS NEVER disposes a response body (measured 2026-08-12), so the stream must close its own
    /// handle the moment the last byte leaves — otherwise 508 requests for one clip leak 508 handles.
    [Fact]
    public void It_closes_the_inner_stream_the_moment_the_last_byte_is_read()
    {
        var inner = new Tracked([1, 2, 3, 4]);
        using var body = new BoundedBodyStream(inner, 4);
        var buffer = new byte[4];
        Assert.Equal(4, body.Read(buffer, 0, 4));
        Assert.Equal(1, inner.Disposals);          // closed at EOF, WITHOUT the caller disposing
    }

    /// 🔴 …and Android DOES dispose it, so a stream that already closed itself must not throw or
    /// double-close when the platform closes it too.
    [Fact]
    public void Disposing_after_it_already_closed_itself_is_a_no_op()
    {
        var inner = new Tracked([1, 2, 3]);
        var body = new BoundedBodyStream(inner, 3);
        body.ReadExactly(new byte[3]);
        body.Dispose();
        body.Dispose();
        Assert.Equal(1, inner.Disposals);
    }

    /// An abandoned request (neither EOF nor a platform close) must still release the handle on dispose.
    [Fact]
    public void Disposing_before_EOF_still_closes_the_inner_stream()
    {
        var inner = new Tracked([1, 2, 3, 4, 5]);
        var body = new BoundedBodyStream(inner, 5);
        Assert.Equal(2, body.Read(new byte[2], 0, 2));
        body.Dispose();
        Assert.Equal(1, inner.Disposals);
    }

    /// The bound is the contract: never serve past it even if the inner stream has more.
    [Fact]
    public void It_never_serves_more_than_its_bound()
    {
        using var body = new BoundedBodyStream(new Tracked([1, 2, 3, 4, 5]), 3);
        var buffer = new byte[10];
        Assert.Equal(3, body.Read(buffer, 0, 10));
        Assert.Equal(0, body.Read(buffer, 0, 10));   // EOF, not a wrap
    }

    /// A short inner stream is a TRUNCATED body, and the platform must not be told it is complete.
    [Fact]
    public void An_inner_stream_shorter_than_the_bound_throws_rather_than_ending_quietly()
    {
        using var body = new BoundedBodyStream(new Tracked([1, 2]), 5);
        Assert.Throws<EndOfStreamException>(() => body.ReadExactly(new byte[5]));
    }

    /// A 0-byte bound (an empty file, or an empty range) never takes the read that would otherwise
    /// trigger the close — there is no "last byte" for Read() to notice — so the constructor has to close
    /// immediately, or the handle leaks for as long as whichever platform holds the response, which on
    /// iOS is indefinite.
    [Fact]
    public void A_zero_length_bound_closes_immediately_and_still_reads_as_a_clean_EOF()
    {
        var inner = new Tracked([]);
        var body = new BoundedBodyStream(inner, 0);
        Assert.Equal(1, inner.Disposals);               // closed in the CONSTRUCTOR, before any Read
        Assert.Equal(0, body.Read(new byte[4], 0, 4));  // a clean EOF, not an error — Read never touches
                                                         // the already-disposed inner stream
        body.Dispose();
        Assert.Equal(1, inner.Disposals);               // still just the one close — a safe no-op
    }
    /// <summary>A body whose read FAILS partway, the way an ejected card or a dropped share does.</summary>
    private sealed class FailsAfter(int bytes) : Stream
    {
        private int _served;

        /// <summary>Counted, because "was the handle released at the failure" is the actual property.</summary>
        public int Disposals { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _served; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_served >= bytes) throw new IOException("the volume went away");
            var take = Math.Min(count, bytes - _served);
            _served += take;
            return take;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// 🔴 <b>A MID-BODY FAILURE IS REPORTED, because it is the one thing the PAGE cannot see.</b> Measured
    /// 2026-08-13: on iOS and on WebView2 a throwing read produces a committed <c>200</c> and a body SHORT of
    /// its <c>Content-Length</c>, with no error on the <c>fetch</c> — the status line is already out, so
    /// nothing downstream can tell. The host CAN know, and until this the kit knew and said nothing.
    /// <para>
    /// ⚠ The message must carry BOTH numbers: "failed at N of M" is what makes a log line actionable, where
    /// a bare exception name reads as noise — the trap `probe-diagnostics.md` records.
    /// </para>
    /// </summary>
    [Fact]
    public void A_read_that_THROWS_reports_how_short_the_body_is()
    {
        var lines = new List<string>();
        using var body = new BoundedBodyStream(new FailsAfter(4), 10, lines.Add);

        Assert.Equal(4, body.Read(new byte[4], 0, 4));
        Assert.Throws<IOException>(() => body.Read(new byte[4], 0, 4));

        var line = Assert.Single(lines.Where(l => l.Contains("FAILED MID-BODY", StringComparison.Ordinal)));
        Assert.Contains("4 of 10", line, StringComparison.Ordinal);
        Assert.Contains("IOException", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ <b>The exception is RETHROWN UNCHANGED</b> — swallowing it would undo Android's whole fix, where the
    /// shell translates it into a `Java.IO.IOException` so Chromium's own catch turns it into a visible failed
    /// load. Reporting must not change behaviour.
    /// </summary>
    [Fact]
    public void A_failing_read_still_propagates_and_closes_the_inner_stream()
    {
        var inner = new FailsAfter(2);
        var body = new BoundedBodyStream(inner, 8);

        body.Read(new byte[2], 0, 2);
        Assert.Throws<IOException>(() => body.Read(new byte[2], 0, 2));

        // Closed AT THE FAILURE: nothing will read this body again successfully, so the handle must not wait
        // for a Dispose that iOS never performs (measured — 712/712 drained instead of disposed).
        Assert.Equal(1, inner.Disposals);
    }

    /// <summary>
    /// ⚠ And a body that completes NORMALLY says nothing — the half a report usually gets wrong. A line on
    /// every successful response would train its reader to ignore the one that matters.
    /// </summary>
    [Fact]
    public void A_body_that_completes_reports_NOTHING()
    {
        var lines = new List<string>();
        using var body = new BoundedBodyStream(new Tracked([1, 2, 3, 4]), 4, lines.Add);

        Assert.Equal(4, body.Read(new byte[4], 0, 4));

        Assert.DoesNotContain(lines, l => l.Contains("FAILED MID-BODY", StringComparison.Ordinal));
    }

}
