using System.Buffers;

namespace Shenora.Modules.Media;

/// <summary>
/// A seekable <see cref="Stream"/> over an app-supplied range fetch — the half of a remote media source
/// that is identical for every transport, so the kit ships it and the app ships only the fetch.
/// <para>
/// 🔴 <b>THE WINDOW IS THE WHOLE POINT, NOT AN OPTIMISATION.</b> Matroska is parsed by EBML varint, which
/// reads ONE BYTE AT A TIME (<c>MatroskaSampleReader</c>), so an unbuffered adapter issues one round trip
/// per byte and a remote source never finishes. A local <c>FileStream</c> hides this behind its own buffer,
/// which is why nothing noticed until the tier grew a transport-agnostic seam.
/// </para>
/// <para>
/// ⚠ <b>Blocking, and safe only where this is used.</b> The parser is synchronous, so an async fetch has to
/// be waited on; the segment route runs on a POOL thread (no <see cref="SynchronizationContext"/>), which is
/// what makes that sound here and unsound on a UI thread.
/// </para>
/// </summary>
internal sealed class RangeFetchStream : Stream
{
    private readonly Func<long, int, CancellationToken, Task<Stream>> _fetch;
    private readonly CancellationToken _token;
    private readonly long _length;
    private readonly byte[] _window;

    /// <summary>Absolute offset of <c>_window[0]</c>, or -1 when the window holds nothing.</summary>
    private long _windowStart = -1;
    private int _windowLength;
    private long _position;

    public RangeFetchStream(long length, Func<long, int, CancellationToken, Task<Stream>> fetch,
                            int windowBytes, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowBytes);

        _length = length;
        _fetch = fetch;
        _token = token;
        _window = new byte[windowBytes];
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var remaining = _length - _position;
        if (remaining <= 0 || buffer.IsEmpty) return 0;
        var want = (int)Math.Min(buffer.Length, remaining);

        if (WindowHolds(_position))
        {
            var from = (int)(_position - _windowStart);
            var take = Math.Min(want, _windowLength - from);
            _window.AsSpan(from, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        // A read at least as big as the window would evict it to serve one caller and gain nothing, so it
        // goes straight to the source — a whole video frame is this case.
        if (want >= _window.Length)
        {
            var rented = ArrayPool<byte>.Shared.Rent(want);
            try
            {
                var got = Fill(rented, 0, want, _position);
                rented.AsSpan(0, got).CopyTo(buffer);
                _position += got;
                return got;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        // ⚠ Invalidated BEFORE the fetch: a throw part-way through must not leave the window claiming to
        // hold bytes from an offset it never filled.
        _windowStart = -1;
        _windowLength = 0;

        var filled = Fill(_window, 0, (int)Math.Min(_window.Length, remaining), _position);
        if (filled == 0) return 0;

        _windowStart = _position;
        _windowLength = filled;

        var n = Math.Min(want, filled);
        _window.AsSpan(0, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));

        // ⚠ Fetches NOTHING. A Cues-driven read seeks far more often than it reads — the index lands the
        // parser on a cluster it may then skip — so seeking eagerly would spend a round trip per probe.
        // Past the end is legal too; the next Read answers 0.
        _position = target;
        return _position;
    }

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private bool WindowHolds(long offset)
        => _windowStart >= 0 && offset >= _windowStart && offset < _windowStart + _windowLength;

    /// <summary>The four bytes every Matroska file opens with. They are legitimate at offset 0 and nowhere else.</summary>
    private static ReadOnlySpan<byte> EbmlMagic => [0x1A, 0x45, 0xDF, 0xA3];

    /// <summary>
    /// 🔴 <b>A server that IGNORES <c>Range</c> and answers the whole file is the one real-world failure this
    /// adapter cannot survive silently.</b> Every other way a fetch can go wrong is loud — a throw, a short
    /// read, no bytes — but a <c>200</c> carrying the file from byte 0 satisfies every check, and the parser
    /// is then handed the START of the file while believing it is somewhere else. The result reads as corrupt
    /// media and blames the file.
    /// <para>
    /// The kit is given a bare <see cref="Stream"/>, so it cannot see the status code. What it CAN see is
    /// that a range starting past zero came back beginning with the container's opening bytes — which is
    /// legitimate only at offset 0. Format-specific on purpose: this adapter exists to be read by a Matroska
    /// demuxer, and a specific detector beats a general one that cannot fire.
    /// </para>
    /// </summary>
    private static void RefuseAWholeFileAnsweringARange(byte[] target, int at, int length, long offset)
    {
        if (offset == 0 || length < EbmlMagic.Length) return;
        if (!target.AsSpan(at, EbmlMagic.Length).SequenceEqual(EbmlMagic)) return;

        throw new IOException(
            $"a range starting at {offset} came back with the bytes that open the file — the source is "
          + "answering the WHOLE file and ignoring the requested range. Check the fetch sends a Range "
          + "header and treats anything but 206 Partial Content as a failure.");
    }

    /// <summary>
    /// Fill <paramref name="count"/> bytes at <paramref name="start"/>, asking again while the source keeps
    /// answering short — a server is free to clamp a range to less than was asked for.
    /// </summary>
    private int Fill(byte[] target, int start, int count, long offset)
    {
        var filled = 0;
        while (filled < count)
        {
            _token.ThrowIfCancellationRequested();
            var want = count - filled;

            using var body = _fetch(offset + filled, want, _token).GetAwaiter().GetResult()
                ?? throw new IOException($"the range source answered nothing at offset {offset + filled}");

            var fromBody = 0;
            while (fromBody < want)
            {
                // ReadAsync, not Read: a platform HTTP handler is not required to implement the synchronous
                // path, and the whole call is already being waited on.
                var n = body.ReadAsync(target.AsMemory(start + filled + fromBody, want - fromBody), _token)
                            .AsTask().GetAwaiter().GetResult();
                if (n <= 0) break;
                fromBody += n;
            }

            // 🔴 Zero bytes for a range INSIDE the declared length is a broken source, not an end. Accepting
            // it would hand the parser a silently truncated file, which reads as corrupt media.
            if (fromBody == 0)
                throw new IOException($"the range source returned no bytes at offset {offset + filled}");

            RefuseAWholeFileAnsweringARange(target, start + filled, fromBody, offset + filled);
            filled += fromBody;
        }

        return filled;
    }
}
