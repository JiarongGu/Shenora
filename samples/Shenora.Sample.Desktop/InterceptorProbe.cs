using System.Text;
using Shenora;
using Shenora.Windows;
using Shenora.Core.WebView;

namespace Shenora.Sample.Desktop;

/// <summary>
/// The SEAM TEST for the D45 resource interceptor on the desktop: a file route registered through
/// <see cref="WebViewInterceptorExtensions.UseFiles"/>, fetched from inside the page by the REAL browser.
/// <para>
/// Unit tests prove the pipeline composition, the containment rule and the range arithmetic in isolation. What
/// they cannot prove is that <see cref="WebViewHost"/> WIRES any of it up: that a filter is registered for the
/// page's own origin, that a bundle miss falls through to the pipeline instead of 404ing, that the request
/// headers survive the hop to the pool thread, and that a 206 and its <c>Content-Range</c> reach the page. This
/// repo has already shipped a resource feature broken on the first of those while every other artefact looked
/// fine (P7.1), which is why the desktop gets the same treatment the mobile shells got on devices.
/// </para>
/// <para>
/// ⚠ It is also the measurement behind <c>WebView2Interceptor.RangeDelivery</c>. The range asked for below
/// starts at an offset that is NOT a multiple of the payload's period, so the body identifies the offset it was
/// really read at — a check that a periodic payload cannot make, and the reason this probe does not simply copy
/// <see cref="RangeSchemeProbe"/>'s.
/// </para>
/// </summary>
internal static class InterceptorProbe
{
    /// <summary>The page-relative route. On its OWN origin, which is the one URL form intercepted everywhere (D44).</summary>
    private const string Route = "_probe/files";

    /// <summary>Set when the route is actually reached — separates "the browser never asked us" from "we answered wrong".</summary>
    public static int RouteHits;

    /// <summary>
    /// 1000 bytes of <c>A..Z</c> repeating. Period 26, so a window starting at 3 reads <c>DEFGH</c> and no other
    /// offset within 25 bytes reads the same thing — asserting the CONTENT therefore pins the OFFSET, which
    /// neither a length check nor a 10-periodic payload can do.
    /// </summary>
    private static readonly byte[] Payload =
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 1000).Select(i => (char)('A' + (i % 26)))));

    /// <summary>
    /// Write the probe's file into <paramref name="directory"/> and register the route. Returns the
    /// registration; disposing it removes the route, as any app's teardown would.
    /// </summary>
    public static IDisposable Register(IWebViewInterceptor interceptor, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.txt"), Payload);
        // A file the route must NEVER reach, one level up from the allowed root. The containment assertion
        // below is only meaningful because this exists and is readable.
        File.WriteAllText(Path.Combine(directory, "..", "off-limits.txt"), "must never be served");

        // The shape ADOPTION.md prescribes: the app owns its route and its roots, the kit owns containment,
        // ranges, content types and the platform's range-delivery rule.
        return interceptor.UseFiles(new WebViewFileOptions
        {
            AllowedRoots = [directory],
            Resolve = uri =>
            {
                if (!uri.AbsolutePath.EndsWith(Route, StringComparison.Ordinal)) return null;   // not ours
                Interlocked.Increment(ref RouteHits);
                var name = Uri.UnescapeDataString(uri.Query.TrimStart('?'));
                return name.Length == 0 ? null : Path.Combine(directory, name);
            },
        });
    }

    /// <summary>
    /// Fetch through the route inside the page and report a one-line verdict — PASS, or FAIL naming what it
    /// actually read. Never a bare boolean.
    /// </summary>
    public static async Task<string> RunAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        // START-THEN-POLL, not a single await: ExecuteScriptAsync resolves when the expression has been
        // EVALUATED and does not await a promise it returns, so an async IIFE serializes as `{}`. Same shape as
        // RangeSchemeProbe — see its comment.
        const string script = $$"""
            window.__interceptorProbe = undefined;
            (async () => {
              const url = '/{{Route}}?payload.txt';
              const out = [`origin=${location.origin}`];
              try {
                const partial = await fetch(url, { headers: { Range: 'bytes=3-7' } });
                out.push(`status=${partial.status}`);
                out.push(`contentRange=${partial.headers.get('Content-Range')}`);
                const body = await partial.text();
                out.push(`len=${body.length}`);
                out.push(`body=${body}`);

                const whole = await fetch(url);
                out.push(`wholeStatus=${whole.status}`);
                out.push(`acceptRanges=${whole.headers.get('Accept-Ranges')}`);
                out.push(`wholeLen=${(await whole.text()).length}`);

                const bad = await fetch(url, { headers: { Range: 'bytes=99999-' } });
                out.push(`unsatisfiable=${bad.status}`);

                // Containment, through the real browser rather than a unit test: one level up from the
                // allowed root, at a file that really exists and really is readable.
                const escape = await fetch('/{{Route}}?' + encodeURIComponent('../off-limits.txt'));
                out.push(`traversal=${escape.status}`);

                // The bundle must still win on the origin it SHARES with the route now. A regression here
                // would be the app's own frontend disappearing, so it is worth one line.
                const doc = await fetch('/index.html');
                out.push(`bundle=${doc.status}`);
              } catch (e) {
                out.push(`fetchThrew=${e && e.message ? e.message : e}`);
              }
              window.__interceptorProbe = out.join('|');
            })();
            """;

        await core.ExecuteScriptAsync(script).ConfigureAwait(true);

        var report = "";
        for (var attempt = 0; attempt < 160; attempt++)   // ~8 s, generous for five fetches
        {
            var raw = await core.ExecuteScriptAsync("window.__interceptorProbe ?? null").ConfigureAwait(true);
            var value = System.Text.Json.JsonSerializer.Deserialize<string?>(raw);
            if (value is not null) { report = value; break; }
            await Task.Delay(50).ConfigureAwait(true);
        }
        if (report.Length == 0) return "INTERCEPTOR SEAM: FAIL — the page never reported (timed out)";

        report += $"|routeHits={Volatile.Read(ref RouteHits)}";

        var fields = report.Split('|')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);

        string? Check(string key, string expected) =>
            fields.TryGetValue(key, out var actual) && actual == expected
                ? null
                : $"{key}: expected '{expected}', got '{(fields.TryGetValue(key, out var a) ? a : "<missing>")}'";

        var failures = new[]
        {
            Check("status", "206"),
            Check("contentRange", "bytes 3-7/1000"),
            Check("len", "5"),
            // DEFGH is bytes 3-7 and nothing else within 25 bytes of it — this is the assertion that says
            // WebView2 delivers SLICED bodies. Under Unsliced delivery the platform would skip three bytes
            // into the five it was handed and the page would read 'GH'.
            Check("body", "DEFGH"),
            Check("wholeStatus", "200"),
            // Without Accept-Ranges a player will not even ATTEMPT a seek, which is indistinguishable from
            // seeking being broken.
            Check("acceptRanges", "bytes"),
            Check("wholeLen", "1000"),
            Check("unsatisfiable", "416"),
            Check("traversal", "404"),
            Check("bundle", "200"),
        }.Where(f => f is not null).ToArray();

        return failures.Length == 0
            ? $"INTERCEPTOR SEAM: PASS ({report})"
            : $"INTERCEPTOR SEAM: FAIL — {string.Join("; ", failures)}  [raw: {report}]";
    }
}
