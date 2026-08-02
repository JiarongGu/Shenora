using System.Globalization;
using System.Text;
using Shenora.Windows;

namespace Shenora.Sample.Desktop;

/// <summary>
/// The SEAM TEST for the request/response resource seam (P6.6/P7.1): a deferred scheme that honours
/// HTTP <c>Range</c>, plus a startup self-check that fetches from it through the REAL browser.
/// <para>
/// Unit tests prove <see cref="WebViewByteRange.TryParse"/> and the response factories in isolation.
/// What they cannot prove is that <see cref="WebViewHost"/> WIRES them up — that the scheme is
/// registered with the environment, that request headers survive the hop to the handler's pool
/// thread, and that a 206 and its <c>Content-Range</c> reach the page rather than being flattened.
/// The feature shipped broken on the first of those while every other artefact looked fine.
/// </para>
/// </summary>
internal static class RangeSchemeProbe
{
    /// <summary>The scheme the probe serves on. Nothing else in the sample uses it.</summary>
    public const string Scheme = "probe";

    /// <summary>Set when the handler is actually reached — distinguishes "browser refused" from "we answered wrong".</summary>
    public static int HandlerHits;

    /// <summary>A synthetic body big enough that a partial read is obviously partial.</summary>
    private static readonly byte[] Payload =
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 1000).Select(i => (i % 10).ToString(CultureInfo.InvariantCulture))));

    /// <summary>The scheme registration — the shape an adopting app copies for media serving.</summary>
    public static WebViewDeferredScheme CreateScheme() => new()
    {
        Scheme = Scheme,
        // No caching: a cached 200 would mask a broken 206 on the next run.
        CacheControl = "no-store",
        Handler = request =>
        {
            Interlocked.Increment(ref HandlerHits);
            var total = Payload.LongLength;

            // The three-line shape ADOPTION.md prescribes for anything seekable.
            if (!WebViewByteRange.TryParse(request.GetHeader("Range"), total, out var range))
                return Task.FromResult<WebViewResourceResponse?>(
                    WebViewResourceResponse.Bytes(Payload, "application/octet-stream"));

            if (!range.IsSatisfiable(total))
                return Task.FromResult<WebViewResourceResponse?>(
                    WebViewResourceResponse.RangeNotSatisfiable(total));

            // A real handler seeks a FileStream here; either way only the requested window is
            // materialised, never the whole resource.
            var slice = new MemoryStream(Payload, (int)range.From, (int)range.Length, writable: false);
            return Task.FromResult<WebViewResourceResponse?>(
                WebViewResourceResponse.PartialContent(slice, "application/octet-stream", range, total));
        },
    };

    /// <summary>
    /// Fetch from the scheme inside the page and report a one-line verdict — PASS, or FAIL naming what
    /// it actually read. Never a bare boolean.
    /// </summary>
    public static async Task<string> RunAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        // START-THEN-POLL, not a single await. `ExecuteScriptAsync` resolves when the expression has
        // been EVALUATED and does NOT await a promise it returns — an async IIFE serializes as `{}`.
        // So the script parks its result on a global and the host polls for it.
        //
        // It probes two ways in one run because a sample launch is slow: `fetch` exercises CORS, and
        // an XHR exercises the same path with different plumbing. <see cref="HandlerHits"/> is the
        // decisive signal though — it separates "the browser refused" from "we answered wrongly", and
        // reading a non-zero count against three page-side failures is precisely what identified this
        // as a CORS problem rather than a registration one.
        const string script = """
            window.__rangeProbe = undefined;
            (async () => {
              const url = 'probe://sample/data';
              const out = [`origin=${location.origin}`, `href=${location.href}`];
              try {
                const partial = await fetch(url, { headers: { Range: 'bytes=10-19' } });
                const body = await partial.text();
                out.push(`status=${partial.status}`);
                out.push(`contentRange=${partial.headers.get('Content-Range')}`);
                out.push(`len=${body.length}`);
                out.push(`body=${body}`);
                const whole = await fetch(url);
                out.push(`wholeStatus=${whole.status}`);
                out.push(`wholeLen=${(await whole.text()).length}`);
                const bad = await fetch(url, { headers: { Range: 'bytes=99999-' } });
                out.push(`unsatisfiable=${bad.status}`);
              } catch (e) {
                out.push(`fetchThrew=${e && e.message ? e.message : e}`);
              }
              // XHR: same URL, different plumbing.
              out.push(await new Promise((resolve) => {
                try {
                  const x = new XMLHttpRequest();
                  x.open('GET', url, true);
                  x.onload = () => resolve(`xhr=${x.status}:${(x.responseText || '').length}`);
                  x.onerror = () => resolve('xhr=ERROR');
                  x.send();
                } catch (e) { resolve(`xhr=THREW:${e && e.message ? e.message : e}`); }
              }));
              window.__rangeProbe = out.join('|');
            })();
            """;

        await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        var report = "";
        for (var attempt = 0; attempt < 160; attempt++)   // ~8 s, generous for the three probes
        {
            var raw = await core.ExecuteScriptAsync("window.__rangeProbe ?? null").ConfigureAwait(true);
            var value = System.Text.Json.JsonSerializer.Deserialize<string?>(raw);
            if (value is not null) { report = value; break; }
            await Task.Delay(50).ConfigureAwait(true);
        }
        if (report.Length == 0) return "RANGE SEAM: FAIL — the page never reported (timed out)";

        report += $"|handlerHits={Volatile.Read(ref HandlerHits)}";

        var fields = report.Split('|')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);

        string? Check(string key, string expected) =>
            fields.TryGetValue(key, out var actual) && actual == expected
                ? null
                : $"{key}: expected '{expected}', got '{(fields.TryGetValue(key, out var a) ? a : "<missing>")}'";

        // Bytes 10-19 of "0123456789" repeated is another "0123456789" — asserting CONTENT proves the
        // OFFSET, which a length check never could: a wrong offset is still 10 bytes long.
        var failures = new[]
        {
            Check("status", "206"),
            Check("contentRange", "bytes 10-19/1000"),
            Check("len", "10"),
            Check("body", "0123456789"),
            Check("wholeStatus", "200"),
            Check("wholeLen", "1000"),
            Check("unsatisfiable", "416"),
        }.Where(f => f is not null).ToArray();

        return failures.Length == 0
            ? $"RANGE SEAM: PASS ({report})"
            : $"RANGE SEAM: FAIL — {string.Join("; ", failures)}  [raw: {report}]";
    }
}
