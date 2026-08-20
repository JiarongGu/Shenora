using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// The range adapter against a REAL HTTP server — a loopback socket speaking real HTTP/1.1, a real
/// <see cref="HttpClient"/>, real <c>Range</c> headers and real <c>206 Partial Content</c> responses.
///
/// <para>
/// 🔴 <b>Everything else in this area fakes the transport with a <c>MemoryStream</c>, which cannot get HTTP
/// wrong.</b> It always honours the offset, always returns exactly what was asked, never redirects and never
/// answers a <c>Range</c> with <c>200</c>. This class exists so the adapter is not first exercised over HTTP
/// inside an adopter's app, where a failure looks like corrupt media rather than a protocol mistake.
/// </para>
/// <para>
/// ⚠ Loopback and OS-assigned port, so it needs no URL ACL, no admin and no firewall exception; the server
/// is ~40 lines rather than a dependency. What it still does NOT cover is a real network — TLS, proxies,
/// redirects, latency and a connection that dies mid-body are an adopter's server to prove (`TASKS.md`).
/// </para>
/// </summary>
public class RangeFetchOverHttpTests
{
    private static string Fixture
        => Path.Combine(AppContext.BaseDirectory, "TestAssets", "media", "clip-h264-aac.mkv");

    private const double SegmentSeconds = 4.0;

    /// <summary>
    /// A minimal HTTP/1.1 file server. <paramref name="ignoreRange"/> reproduces the one misconfiguration
    /// that is otherwise silent: answering a ranged request with the WHOLE file and a <c>200</c>.
    /// </summary>
    private sealed class LoopbackFileServer(byte[] content, bool ignoreRange = false) : IDisposable
    {
        private readonly TcpListener _listener = Listening();
        private readonly CancellationTokenSource _stop = new();
        private int _served;

        public int Served => Volatile.Read(ref _served);

        public Uri Url => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/clip.mkv");

        private static TcpListener Listening()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return listener;
        }

        public LoopbackFileServer Start()
        {
            _ = Task.Run(Accept);
            return this;
        }

        private async Task Accept()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }                                  // stopped, or the listener went away
                _ = Task.Run(() => Handle(client));
            }
        }

        private async Task Handle(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var head = await ReadHead(stream);
                if (head is null) return;
                Interlocked.Increment(ref _served);

                var (from, to) = RequestedRange(head);
                var ranged = !ignoreRange && from >= 0;

                var start = ranged ? (int)from : 0;
                var end = ranged ? (int)Math.Min(to < 0 ? content.Length - 1 : to, content.Length - 1)
                                 : content.Length - 1;
                var count = end - start + 1;

                var header = new StringBuilder();
                header.Append(ranged ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                if (ranged) header.Append($"Content-Range: bytes {start}-{end}/{content.Length}\r\n");
                header.Append("Accept-Ranges: bytes\r\n")
                      .Append("Content-Type: video/x-matroska\r\n")
                      .Append($"Content-Length: {count}\r\n")
                      .Append("Connection: close\r\n\r\n");

                await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()));
                await stream.WriteAsync(content.AsMemory(start, count));
                await stream.FlushAsync();
            }
        }

        private static async Task<string?> ReadHead(NetworkStream stream)
        {
            var seen = new List<byte>(512);
            var one = new byte[1];
            while (seen.Count < 8192)
            {
                if (await stream.ReadAsync(one) == 0) return null;
                seen.Add(one[0]);
                if (seen.Count >= 4 && seen[^4] == '\r' && seen[^3] == '\n'
                                    && seen[^2] == '\r' && seen[^1] == '\n')
                    return Encoding.ASCII.GetString([.. seen]);
            }

            return null;
        }

        /// <summary>`Range: bytes=a-b`, or (-1, -1) when the request carried none.</summary>
        private static (long From, long To) RequestedRange(string head)
        {
            var at = head.IndexOf("Range: bytes=", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return (-1, -1);

            var value = head[(at + "Range: bytes=".Length)..];
            value = value[..value.IndexOf('\r')];
            var halves = value.Split('-');
            var from = long.TryParse(halves[0], out var f) ? f : -1;
            var to = halves.Length > 1 && long.TryParse(halves[1], out var t) ? t : -1;
            return (from, to);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }

    /// <summary>
    /// The fetch an adopter writes, and the shape <c>docs/ADOPTION.md</c> documents. ⚠ It reads the response
    /// HEADERS only before handing the body over, so a 78 MB file is never buffered to answer a 256 KB range.
    /// </summary>
    private static Func<long, int, CancellationToken, Task<Stream>> Fetching(HttpClient http, Uri url)
        => async (offset, count, token) =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);

            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(token);
        };

    [Fact]
    public void A_real_HTTP_server_yields_the_same_plan_as_the_file_on_disk()
    {
        Assert.True(File.Exists(Fixture), $"real media fixture missing: {Fixture}");
        var content = File.ReadAllBytes(Fixture);

        using var server = new LoopbackFileServer(content).Start();
        using var http = new HttpClient();

        var overHttp = MediaByteSource.ForRanges("clip-h264-aac.mkv", content.Length,
                                                 Fetching(http, server.Url));

        var lines = new List<string>();
        var engine = new DefaultSegmentEngine(new NoConversion(), AppCallback.Logger(lines.Add));

        var planned = engine.PlanSegments(overHttp, SegmentLengths.Of(SegmentSeconds));
        var onDisk = engine.PlanSegments(MediaByteSource.ForFile(Fixture), SegmentLengths.Of(SegmentSeconds));

        Assert.NotNull(planned);
        Assert.NotNull(onDisk);
        Assert.Equal(onDisk.Count, planned.Count);
        for (var i = 0; i < onDisk.Count; i++)
            Assert.Equal(onDisk.StartOf(i), planned.StartOf(i), precision: 6);

        // The index was used over the wire too — the walk it replaces would drag most of the file across.
        Assert.DoesNotContain(lines, l => l.Contains("walking its clusters", StringComparison.Ordinal));

        // ⚠ Anti-vacuity: a server that was never asked anything would satisfy every assertion above if the
        // plan came from the disk read alone.
        Assert.True(server.Served > 0, "the HTTP server was never asked for a single range");

        // Measured at 4 requests for this 456 KB file — the same count the fake transport takes, which is
        // the point: nothing about real HTTP changed the access pattern. ⚠ A ceiling, not an equality: it
        // must survive a harmless change to read sizes and fail only if the buffering stops working.
        Assert.True(server.Served < 64,
            $"{server.Served} HTTP requests to plan a {content.Length / 1024} KB file");
    }

    [Fact]
    public void A_real_server_that_IGNORES_the_Range_header_is_CAUGHT_not_believed()
    {
        // 🔴 The failure this whole detector exists for, reproduced against a real server rather than argued
        // about: `200 OK` with the whole body. The adopter's fetch is correct and `EnsureSuccessStatusCode`
        // is happy — a 200 IS a success — so nothing upstream can notice. Without the check the demuxer is
        // handed the start of the file believing it is 300 KB in, and reports the file as corrupt.
        var content = File.ReadAllBytes(Fixture);

        using var server = new LoopbackFileServer(content, ignoreRange: true).Start();
        using var http = new HttpClient();

        var bytes = MediaByteSource.ForRanges("clip-h264-aac.mkv", content.Length,
                                              Fetching(http, server.Url));

        using var stream = bytes.Open(CancellationToken.None);
        stream.Position = 300_000;

        var why = Assert.Throws<IOException>(() => stream.Read(new byte[64], 0, 64));
        Assert.Contains("ignoring the requested range", why.Message, StringComparison.Ordinal);
    }

    /// <summary>Planning must never reach a converter; this records rather than throws, so a call is visible.</summary>
    private sealed class NoConversion : IMediaStreamConversion
    {
        public bool CanConvert(MediaStreamKind kind, string codec) => false;

        public IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate) => null;
    }
}
