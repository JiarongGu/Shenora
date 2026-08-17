using Shenora.Core.WebView;
using Shenora.Windows;

namespace Shenora.Sample.Desktop;

/// <summary>
/// 🔴 <b>WebView2 NEVER disposes a handler's response body — so a body that does not close ITSELF is a
/// leaked handle, and that is a load-bearing invariant rather than a defensive one.</b>
///
/// <para>
/// Measured here, 2026-08-15: a body read to its very END is still never disposed by the browser.
/// <see cref="WebViewHost"/> says as much in prose ("if <c>Build</c> never runs, nothing else will ever
/// close the body"), and what this probe adds is that the SUCCESS path is no different.
/// <c>BoundedBodyStream</c>'s at-bound self-close is the only thing releasing a real <c>FileStream</c>,
/// on Windows also freeing the file to be moved or deleted.
/// </para>
/// <para>
/// ⚠ <b>So the assertion is about the SELF-CLOSING body, not about the browser.</b> A probe that merely
/// reported the platform's behaviour would print LEAK on every run and teach its reader to skim. Two
/// bodies are served instead: one that closes at its bound, which MUST be released, and one that does not,
/// which is expected not to be — and if that second one ever starts being disposed, WebView2 changed and
/// the invariant can be relaxed.
/// </para>
/// <para>
/// ⚠ <b>And the abandonment question is moot on this platform: WebView2 buffers the WHOLE body before the
/// page reads a byte.</b> Both requests below deliver 32 MiB in a SINGLE chunk — which is what the
/// <c>bytes/chunks</c> pair in the report is for — so a page cannot abandon one mid-body the way it can on
/// Android. That is also why every body here is drained, and drained is exactly what makes the self-close
/// sufficient.
/// </para>
/// </summary>
internal static class BodyDisposalProbe
{
    public const string Scheme = "bodyprobe";

    /// <summary>Big enough that the page cannot drain it accidentally between one chunk and an abort.</summary>
    private const int BodyBytes = 32 * 1024 * 1024;

    private static int _openedBounded;
    private static int _disposedBounded;
    private static int _openedRaw;
    private static int _disposedRaw;

    /// <summary>
    /// A body that reports its own disposal, and optionally closes itself at its bound the way
    /// <c>BoundedBodyStream</c> does. Deliberately NOT a `MemoryStream` over a byte array: the question is
    /// what happens to a stream the browser is finished with, so the stream has to be able to say.
    /// </summary>
    private sealed class CountingStream(long length, bool selfClosing) : Stream
    {
        private long _position;
        private int _closedAlready;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - _position;
            if (remaining <= 0)
            {
                // The at-bound self-close: the ONLY thing that releases a body on the success path.
                if (selfClosing) Dispose();
                return 0;
            }

            var take = (int)Math.Min(count, Math.Min(remaining, 64 * 1024));
            // Content does not matter; only that reading it takes real reads.
            Array.Fill(buffer, (byte)'x', offset, take);
            _position += take;
            return take;
        }

        protected override void Dispose(bool disposing)
        {
            // Once per stream — a double dispose must not read as two released bodies.
            if (Interlocked.Exchange(ref _closedAlready, 1) == 0)
            {
                if (selfClosing) Interlocked.Increment(ref _disposedBounded);
                else Interlocked.Increment(ref _disposedRaw);
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public static WebViewDeferredScheme CreateScheme() => new()
    {
        Scheme = Scheme,
        CacheControl = "no-store",
        Handler = request =>
        {
            // The query says which body to serve, so one scheme covers both arms of the comparison.
            var selfClosing = !request.Uri.Query.Contains("raw", StringComparison.Ordinal);
            if (selfClosing) Interlocked.Increment(ref _openedBounded);
            else Interlocked.Increment(ref _openedRaw);

            return Task.FromResult<WebViewResourceResponse?>(
                WebViewResourceResponse.Ok(new CountingStream(BodyBytes, selfClosing), "application/octet-stream"));
        },
    };

    /// <summary>
    /// Serve a self-closing body and a raw one, then report which were released.
    /// </summary>
    public static async Task<string> RunAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        Volatile.Write(ref _openedBounded, 0);
        Volatile.Write(ref _disposedBounded, 0);
        Volatile.Write(ref _openedRaw, 0);
        Volatile.Write(ref _disposedRaw, 0);

        // Each request carries a different query so nothing can be served from a cache and counted once.
        const string script = """
            (async () => {
              const out = [];
              const read = async (tag) => {
                try {
                  const r = await fetch(`bodyprobe://probe/body?case=${tag}`);
                  const reader = r.body.getReader();
                  let got = 0, chunks = 0;
                  for (;;) {
                    const { value, done } = await reader.read();
                    if (done) break;
                    got += value.byteLength; chunks++;
                  }
                  // `chunks` is the evidence for the buffering claim: WebView2 delivers ONE.
                  out.push(`${tag}=${got}/${chunks}`);
                } catch (e) { out.push(`${tag}=THREW:${e && e.message ? e.message : e}`); }
              };

              // A body that closes itself at its bound — the kit's own shape, which MUST be released.
              await read('bounded');
              // The same body without that self-close, to show what the browser does NOT do for you.
              await read('raw');

              window.__bodyProbe = out.join('|');
            })();
            """;

        await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        var report = "";
        for (var attempt = 0; attempt < 200; attempt++)   // ~10 s: a 32 MiB drain is real work
        {
            var raw = await core.ExecuteScriptAsync("window.__bodyProbe ?? null").ConfigureAwait(true);
            if (System.Text.Json.JsonSerializer.Deserialize<string?>(raw) is { } value) { report = value; break; }
            await Task.Delay(50).ConfigureAwait(true);
        }
        if (report.Length == 0) return "BODY DISPOSAL: FAIL — the page never reported (timed out)";

        // Release is not synchronous with the page finishing: the browser tears the request down on its
        // own thread. Poll for the count to SETTLE rather than reading it once and calling it an answer.
        var bounded = 0;
        var stable = 0;
        for (var attempt = 0; attempt < 100 && stable < 6; attempt++)   // settle for ~300 ms, up to 5 s
        {
            var now = Volatile.Read(ref _disposedBounded);
            stable = now == bounded ? stable + 1 : 0;
            bounded = now;
            await Task.Delay(50).ConfigureAwait(true);
        }

        var openedBounded = Volatile.Read(ref _openedBounded);
        var openedRaw = Volatile.Read(ref _openedRaw);
        var rawReleased = Volatile.Read(ref _disposedRaw);

        // 🔴 The assertion is the SELF-CLOSING body being released. The raw one is reported, never failed:
        // the browser not disposing it is the platform fact this exists to keep visible, and a probe that
        // failed on it would print the same complaint for ever.
        var verdict = openedBounded == 0 ? "FAIL — the handler was never reached"
                    : bounded < openedBounded
                        ? $"FAIL — {openedBounded - bounded} of {openedBounded} SELF-CLOSING bodies were not released"
                        : rawReleased > 0
                            ? "PASS (and WebView2 now disposes raw bodies too — the self-close may be relaxable)"
                            : "PASS — self-closing bodies released; raw ones are NOT, as expected";

        return $"BODY DISPOSAL: {verdict} "
             + $"(bounded={bounded}/{openedBounded} raw={rawReleased}/{openedRaw} | {report})";
    }
}
