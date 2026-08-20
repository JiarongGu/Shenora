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
    public void AShortAnswerIsAskedAgainRatherThanTruncated()
    {
        // A server may clamp a range to less than was asked for. Accepting the short answer as an end would
        // hand the parser a silently truncated file — bytes individually valid, structure wrong.
        var content = Content(200);
        var source = new Source(content, clampTo: 7);
        using var stream = source.Bytes().Open(CancellationToken.None);

        var got = new byte[128];
        var read = 0;
        while (read < got.Length) read += stream.Read(got, read, got.Length - read);

        Assert.Equal(content[..128], got);
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
