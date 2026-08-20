using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// The kit's half of a remote media source: a seekable stream over an app-supplied range fetch.
///
/// <para>
/// 🔴 <b>The COUNTED fetches are the point, not the bytes.</b> Every assertion about content here would
/// also pass for an adapter that fetches once per byte — which is the version an adopter writes, and it is
/// unusable rather than merely slow (EBML parses one <c>ReadByte</c> at a time). So the tests that matter
/// are the ones that count round trips.
/// </para>
/// <para>
/// ⚠ No network, by construction: the fetch is a delegate over a <c>MemoryStream</c>, which is the property
/// that made shipping the adapter — and not the transport — the right split.
/// </para>
/// </summary>
public class RangeFetchStreamTests
{
    private const int Window = 64;

    private static byte[] Content(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)(i % 251);   // prime, so a wrong offset shows
        return bytes;
    }

    /// <summary>A source that records every range it was asked for.</summary>
    private sealed class Source(byte[] content, int? clampTo = null)
    {
        public List<(long Offset, int Count)> Asked { get; } = [];

        public MediaByteSource Bytes(int window = Window)
            => MediaByteSource.ForRanges("fixture", content.Length, Fetch, window);

        private Task<Stream> Fetch(long offset, int count, CancellationToken token)
        {
            Asked.Add((offset, count));
            // A server is free to answer a SHORT range; the stream must ask again rather than truncate.
            var give = Math.Min(clampTo ?? count, (int)Math.Min(count, content.Length - offset));
            return Task.FromResult<Stream>(new MemoryStream(content, (int)offset, give, writable: false));
        }
    }

    [Fact]
    public void ReadsTheRightBytesAtEveryOffset()
    {
        var content = Content(1000);
        using var stream = new Source(content).Bytes().Open(CancellationToken.None);

        var got = new byte[1000];
        var read = 0;
        while (read < got.Length)
        {
            var n = stream.Read(got, read, got.Length - read);
            Assert.True(n > 0, $"ran dry at {read}");
            read += n;
        }

        Assert.Equal(content, got);
    }

    [Fact]
    public void ByteAtATimeReadingCostsONEFetchPerWindow()
    {
        // 🔴 THE DEFECT THIS TYPE EXISTS FOR. `MatroskaSampleReader` reads varints with `ReadByte`, so this
        // is the real access pattern — 256 single-byte reads. Unbuffered that is 256 round trips.
        var source = new Source(Content(256));
        using var stream = source.Bytes().Open(CancellationToken.None);

        for (var i = 0; i < 256; i++) Assert.Equal(i % 251, stream.ReadByte());

        Assert.Equal(256 / Window, source.Asked.Count);
    }

    [Fact]
    public void SeekingDoesNotFetch()
    {
        // A Cues-driven read seeks far more often than it reads: the index lands the parser on a cluster it
        // inspects and may skip. Fetching on Seek would spend a round trip per probe.
        var source = new Source(Content(1000));
        using var stream = source.Bytes().Open(CancellationToken.None);

        stream.Seek(500, SeekOrigin.Begin);
        stream.Seek(-100, SeekOrigin.Current);
        stream.Seek(-10, SeekOrigin.End);
        Assert.Equal(990, stream.Position);

        Assert.Empty(source.Asked);
    }

    [Fact]
    public void ReadsBackwardsWithoutLosingItsPlace()
    {
        // SeekHead and Cues live at the END and point BACKWARDS, so this is the ordinary index pattern and
        // not an edge case. A window that only ever moves forward would answer these from the wrong offset.
        var content = Content(1000);
        using var stream = new Source(content).Bytes().Open(CancellationToken.None);

        foreach (var at in new[] { 900, 100, 512, 0, 999 })
        {
            stream.Position = at;
            Assert.Equal(content[at], (byte)stream.ReadByte());
        }
    }

    [Fact]
    public void AReadBiggerThanTheWindowGoesStraightToTheSource()
    {
        // Reading a whole video frame is this case. Serving it through the window would cost one fetch per
        // window-full and evict a window nobody asked to lose.
        var source = new Source(Content(1000));
        using var stream = source.Bytes().Open(CancellationToken.None);

        var got = new byte[Window * 4];
        Assert.Equal(got.Length, stream.Read(got, 0, got.Length));

        Assert.Single(source.Asked);
        Assert.Equal((0L, Window * 4), source.Asked[0]);
    }

    [Fact]
    public void AShortAnswerIsAskedAgainWithinTheSameRead()
    {
        // A server may clamp a range to far less than was asked for. ⚠ This is NOT what prevents silent
        // truncation — the demuxer reads through `ReadAtLeast`, which loops over short reads by itself, and
        // the zero-byte refusal below is the check that catches a dry source. What the re-ask buys is the
        // WINDOW arriving full, which is the difference between one fetch per 64 bytes and one per 7.
        //
        // 🔴 Asserted on a SINGLE Read for exactly that reason: an outer `while (read < length)` loop in the
        // test would retry on its own and pass whether or not the adapter re-asks. Measured — with the
        // re-ask removed this returns 7.
        var content = Content(200);
        var source = new Source(content, clampTo: 7);
        using var stream = source.Bytes().Open(CancellationToken.None);

        var got = new byte[32];
        Assert.Equal(32, stream.Read(got, 0, 32));

        Assert.Equal(content[..32], got);
        Assert.True(source.Asked.Count > 1, "a clamped source must be asked more than once");
    }

    [Fact]
    public void EndOfStreamAnswersZeroRatherThanFetching()
    {
        var source = new Source(Content(100));
        using var stream = source.Bytes().Open(CancellationToken.None);

        stream.Position = 100;
        Assert.Equal(0, stream.Read(new byte[10], 0, 10));
        stream.Position = 500;                                   // past the end is legal to SEEK to
        Assert.Equal(0, stream.Read(new byte[10], 0, 10));

        Assert.Empty(source.Asked);
    }

    [Fact]
    public void ASourceThatStopsAnsweringInsideTheFileFailsRatherThanTruncating()
    {
        // 🔴 Zero bytes for a range INSIDE the declared length is a broken source, not an end. The engine
        // catches this and reports the source as unplannable; silently short bytes would read as corrupt
        // media instead, and blame the file.
        var bytes = MediaByteSource.ForRanges("gone", 1000,
            (_, _, _) => Task.FromResult<Stream>(new MemoryStream([])), Window);
        using var stream = bytes.Open(CancellationToken.None);

        Assert.Throws<IOException>(() => stream.Read(new byte[10], 0, 10));
    }

    // ── What a REAL server does that a MemoryStream never will ────────────────────────────────────────
    // Proven against fakes that MISBEHAVE the way HTTP misbehaves, because the adapter cannot be run
    // against a server until an adopter has one. Each of these is a documented real behaviour, not an
    // invented one.

    [Fact]
    public void ASourceThatIGNORESTheRangeIsCaughtRatherThanBelieved()
    {
        // 🔴 THE ONE FAILURE THAT IS OTHERWISE SILENT. A misconfigured server answers 200 with the whole
        // body; every check here passes — bytes arrive, the count is right, nothing throws — and the parser
        // is handed the START of the file believing it is at `offset`. It reads as corrupt media.
        var content = Content(4096);
        content[0] = 0x1A; content[1] = 0x45; content[2] = 0xDF; content[3] = 0xA3;   // EBML magic

        var bytes = MediaByteSource.ForRanges("ignores-range", content.Length,
            // The defect itself: the offset is DROPPED and the file is served from zero.
            (_, count, _) => Task.FromResult<Stream>(
                new MemoryStream(content, 0, Math.Min(count, content.Length), writable: false)),
            Window);

        using var stream = bytes.Open(CancellationToken.None);
        stream.Position = 2048;

        var why = Assert.Throws<IOException>(() => stream.Read(new byte[16], 0, 16));
        Assert.Contains("ignoring the requested range", why.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDetectorDoesNotFireOnAHONESTServer()
    {
        // ⚠ The other direction, which is the half that makes the check safe to ship: a correct source whose
        // file opens with the same magic must read straight through. A detector that cannot stay quiet is
        // worse than none — it would refuse every real Matroska file.
        var content = Content(4096);
        content[0] = 0x1A; content[1] = 0x45; content[2] = 0xDF; content[3] = 0xA3;

        var source = new Source(content);
        using var stream = source.Bytes().Open(CancellationToken.None);

        stream.Position = 2048;
        var got = new byte[16];
        Assert.Equal(16, stream.Read(got, 0, 16));
        Assert.Equal(content[2048..2064], got);

        stream.Position = 0;                                   // and offset 0 legitimately IS the magic
        Assert.Equal(0x1A, stream.ReadByte());
    }

    [Fact]
    public void AConnectionThatDIESMidRangeIsResumedFromWhereItStopped()
    {
        // A dropped connection surfaces as a body that ends early — survivable, because the rest of the range
        // is simply re-requested. 🔴 The bytes must RESUME, not restart: a re-fetch from the original offset
        // would overwrite what already arrived and leave a duplicated run in the middle of the buffer, which
        // no length check can see. Single Read for the reason the test above gives.
        var content = Content(512);
        var bytes = MediaByteSource.ForRanges("flaky", content.Length, (offset, count, _) =>
        {
            var give = (int)Math.Min(Math.Min(3, count), content.Length - offset);
            return Task.FromResult<Stream>(new MemoryStream(content, (int)offset, give, writable: false));
        }, Window);

        using var stream = bytes.Open(CancellationToken.None);

        var got = new byte[48];
        Assert.Equal(48, stream.Read(got, 0, 48));
        Assert.Equal(content[..48], got);
    }

    [Fact]
    public void ASourceThatOVERANSWERSIsHeldToWhatWasAsked()
    {
        // A server may honour `Range: bytes=100-` by sending everything from 100 to EOF, ignoring the end of
        // the range. Correct, common, and the adapter must take only what it asked for and drop the rest.
        var content = Content(1000);
        var bytes = MediaByteSource.ForRanges("suffix", content.Length,
            (offset, _, _) => Task.FromResult<Stream>(
                new MemoryStream(content, (int)offset, content.Length - (int)offset, writable: false)),
            Window);

        using var stream = bytes.Open(CancellationToken.None);
        stream.Position = 900;

        var got = new byte[50];
        Assert.Equal(50, stream.Read(got, 0, 50));
        Assert.Equal(content[900..950], got);
        Assert.Equal(950, stream.Position);
    }

    [Fact]
    public void AFetchThatTHROWSSurfacesRatherThanBeingReadAsAnEnd()
    {
        // A source that has gone away — an expired presigned url is the everyday case. The engine catches
        // this and reports the source as unplannable; what it must not do is look like a clean EOF.
        var bytes = MediaByteSource.ForRanges("expired", 1000,
            (_, _, _) => throw new HttpRequestException("403"), Window);
        using var stream = bytes.Open(CancellationToken.None);

        Assert.Throws<HttpRequestException>(() => stream.Read(new byte[16], 0, 16));
    }

    [Fact]
    public void ARangeNearTheENDNeverAsksBeyondTheFile()
    {
        // A server answers 416 for a range past the end, so asking for one is a real fault and not merely
        // wasteful. SeekHead and Cues both live here, so this is the ordinary index path.
        var content = Content(300);
        var source = new Source(content);
        using var stream = source.Bytes().Open(CancellationToken.None);

        stream.Position = 290;
        var got = new byte[64];                                // asks for more than remains
        Assert.Equal(10, stream.Read(got, 0, got.Length));

        Assert.All(source.Asked, a => Assert.True(a.Offset + a.Count <= content.Length,
            $"asked for [{a.Offset}, {a.Offset + a.Count}) beyond a {content.Length}-byte file"));
    }

    [Fact]
    public void CancellingStopsTheReadRatherThanTruncatingIt()
    {
        // The token reaches the fetch because a manifest request carries one: the walk it may fall back to
        // runs inside a web request. Cancellation must be distinguishable from a source that ran dry.
        using var cancel = new CancellationTokenSource();
        var bytes = MediaByteSource.ForRanges("slow", 1000, (_, _, token) =>
        {
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new MemoryStream(new byte[64]));
        }, Window);

        using var stream = bytes.Open(cancel.Token);
        Assert.Throws<OperationCanceledException>(() => stream.Read(new byte[16], 0, 16));
    }

    [Fact]
    public void TheLabelIsCarriedAndTheLengthIsStated()
    {
        // `Length` must be known up front: Matroska is read by offset from the END, so a source that cannot
        // state its size cannot be indexed at all.
        var bytes = new Source(Content(4242)).Bytes();
        Assert.Equal("fixture", bytes.Label);

        using var stream = bytes.Open(CancellationToken.None);
        Assert.Equal(4242, stream.Length);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public void RefusesASourceItCouldNotRead()
    {
        Assert.Throws<ArgumentNullException>(() => MediaByteSource.ForRanges("x", 10, null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaByteSource.ForRanges("x", 0, (_, _, _) => Task.FromResult<Stream>(new MemoryStream())));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaByteSource.ForRanges("x", 10, (_, _, _) => Task.FromResult<Stream>(new MemoryStream()), 0));
    }
}
