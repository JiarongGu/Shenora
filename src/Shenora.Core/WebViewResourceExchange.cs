using System.Globalization;

namespace Shenora.Core;

// MOVED HERE FROM Shenora.Windows on 2026-08-03 (D2a). These three types describe a resource exchange
// between a host and a page — "URI plus headers in, status plus content-type plus a stream out" — and
// nothing about that is Windows-specific. They were only in the Windows package because that was the
// one shell when they were written.
//
// What forced the move: MAUI's HybridWebView turns out to HAVE a request-interception seam in .NET 10
// (`WebResourceRequested`, with readable request headers and a SetResponse that accepts 206), so the
// mobile shells can serve dynamic, seekable content too — and `src/Shenora.Mobile/` cannot reference
// Shenora.Windows. Portable contracts live in Core (D19/D20); this is that rule catching up with a
// capability the platform gained after the split.
//
// It is a BREAKING namespace change on published types, documented under `### Breaking`. There is no
// type-forward shim: forwarding preserves the full name INCLUDING the namespace, so it would leave a
// `Shenora.Windows.*` type name living in the Core assembly — which contradicts the one-namespace-per-
// package convention the whole kit reads by, to spare consumers a single `using`.

/// <summary>
/// The request a deferred-scheme handler is answering.
/// <para>
/// Handlers used to receive only a <see cref="Uri"/>, which made a whole class of response
/// impossible to write: anything that depends on a request HEADER. The one that matters in practice
/// is <c>Range</c> — without it a page cannot SEEK a large file, because the handler has no way to
/// know what byte offset was asked for and no way to answer <c>206</c>. One of the surveyed apps
/// hit exactly that and had to bypass the kit's seam entirely and hook WebView2 itself (P6.6).
/// </para>
/// </summary>
public sealed class WebViewResourceRequest
{
    /// <summary>The requested URI.</summary>
    public required Uri Uri { get; init; }

    /// <summary>HTTP method, uppercase (<c>GET</c>, <c>HEAD</c>, …).</summary>
    public required string Method { get; init; }

    /// <summary>Request headers, case-insensitive. Prefer <see cref="GetHeader"/>.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>One header, or null when absent. Case-insensitive, as HTTP header names are.</summary>
    public string? GetHeader(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Headers.TryGetValue(name, out var value) ? value : null;
    }
}

/// <summary>
/// The response a deferred-scheme handler returns: a status, headers, and a CONTENT STREAM.
/// <para>
/// A stream rather than a <c>byte[]</c> on purpose. The old signature returned the complete bytes,
/// so serving a 4 GB file meant materialising 4 GB in memory — the seam could not express streaming
/// at all, only "here is the whole thing". Use the factories; they set the status line and the
/// headers that go with it, which is where a hand-rolled version gets it subtly wrong.
/// </para>
/// </summary>
public sealed class WebViewResourceResponse
{
    /// <summary>
    /// The body. Ownership passes to WebView2, which READS it after the handler has returned — so do
    /// NOT dispose it yourself or wrap it in a <c>using</c>; that truncates the response. The host
    /// disposes it only if handing it over failed (a webview torn down mid-flight), which is the one
    /// case where nothing else ever will.
    /// </summary>
    public required Stream Content { get; init; }

    /// <summary>HTTP status code. 200 unless a factory says otherwise.</summary>
    public int StatusCode { get; init; } = 200;

    /// <summary>HTTP reason phrase, paired with <see cref="StatusCode"/>.</summary>
    public string ReasonPhrase { get; init; } = "OK";

    /// <summary>
    /// Response headers, case-insensitive. <c>Content-Type</c> belongs here like any other; the host
    /// adds the scheme's <c>Cache-Control</c> unless this already carries one.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>A complete 200 response.</summary>
    public static WebViewResourceResponse Ok(Stream content, string contentType,
                                             IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        var all = Merge(headers);
        all["Content-Type"] = contentType;
        // Advertised so the page knows it MAY seek. Without it a media element will not even try,
        // which looks exactly like "seeking is broken" while the handler is perfectly capable.
        all.TryAdd("Accept-Ranges", "bytes");
        return new WebViewResourceResponse { Content = content, Headers = all };
    }

    /// <summary>A complete 200 response over an in-memory body.</summary>
    public static WebViewResourceResponse Bytes(byte[] content, string contentType,
                                                IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Ok(new MemoryStream(content, writable: false), contentType, headers);
    }

    /// <summary>
    /// A <c>206 Partial Content</c> response for <paramref name="range"/> of a
    /// <paramref name="totalLength"/>-byte resource. <paramref name="content"/> must already be
    /// positioned at, and bounded to, the requested range.
    /// </summary>
    public static WebViewResourceResponse PartialContent(Stream content, string contentType,
                                                         WebViewByteRange range, long totalLength,
                                                         IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        var all = Merge(headers);
        all["Content-Type"] = contentType;
        all["Accept-Ranges"] = "bytes";
        all["Content-Range"] = string.Create(CultureInfo.InvariantCulture,
            $"bytes {range.From}-{range.To}/{totalLength}");
        return new WebViewResourceResponse
        {
            Content = content,
            StatusCode = 206,
            ReasonPhrase = "Partial Content",
            Headers = all,
        };
    }

    /// <summary>
    /// A 404 carrying the kit's single fixed body. The text is a CONSTANT on purpose (P5.5 H3: one
    /// body for every 404) — page script can read a response body, and an app scheme handler's own
    /// failure detail is the most likely of all of them to carry a real path or a remote URL, so the
    /// diagnosis goes to the host log and never here (design §5).
    /// </summary>
    public static WebViewResourceResponse NotFound() => new()
    {
        Content = new MemoryStream(NotFoundBody, writable: false),
        StatusCode = 404,
        ReasonPhrase = "Not Found",
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "text/plain",
        },
    };

    private static readonly byte[] NotFoundBody = System.Text.Encoding.UTF8.GetBytes("Not Found");

    /// <summary>
    /// A <c>416</c> for a range outside the resource. The <c>Content-Range</c> is required by the
    /// spec so the client learns the real size and can retry — omitting it is the classic bug that
    /// leaves a player retrying the same bad range forever.
    /// </summary>
    public static WebViewResourceResponse RangeNotSatisfiable(long totalLength) => new()
    {
        Content = new MemoryStream([], writable: false),
        StatusCode = 416,
        ReasonPhrase = "Range Not Satisfiable",
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Range"] = string.Create(CultureInfo.InvariantCulture, $"bytes */{totalLength}"),
        },
    };

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string>? headers)
    {
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null) return all;
        foreach (var (key, value) in headers) all[key] = value;
        return all;
    }
}

/// <summary>
/// One resolved byte range, inclusive at both ends — the form <c>Content-Range</c> uses.
/// </summary>
/// <param name="From">First byte offset, inclusive.</param>
/// <param name="To">Last byte offset, inclusive.</param>
public readonly record struct WebViewByteRange(long From, long To)
{
    /// <summary>Number of bytes in the range.</summary>
    public long Length => To - From + 1;

    /// <summary>
    /// Parse a single-range <c>Range</c> header against a known resource length.
    /// <para>
    /// This is protocol plumbing, not policy, and it ships because every one of the three forms is a
    /// separate chance to be wrong: <c>bytes=0-499</c> (closed), <c>bytes=500-</c> (open-ended, what a
    /// media element actually sends when it seeks), and <c>bytes=-500</c> (a SUFFIX — the last 500
    /// bytes, not "from 500"), which is the one hand-rolled parsers reliably get backwards.
    /// A range that starts past the end is unsatisfiable and must be answered <c>416</c>, not clamped.
    /// </para>
    /// </summary>
    /// <param name="headerValue">The raw header, or null.</param>
    /// <param name="totalLength">The resource's full length in bytes.</param>
    /// <param name="range">The resolved, clamped range.</param>
    /// <returns>
    /// False when there is no range to honour (absent, malformed, multi-range, or a non-<c>bytes</c>
    /// unit) — answer 200 with the whole resource. True with a resolved range otherwise; check
    /// <see cref="IsSatisfiable"/> against the length to decide between 206 and 416.
    /// </returns>
    public static bool TryParse(string? headerValue, long totalLength, out WebViewByteRange range)
    {
        range = default;
        if (totalLength <= 0 || string.IsNullOrWhiteSpace(headerValue)) return false;

        const string unit = "bytes=";
        var value = headerValue.Trim();
        if (!value.StartsWith(unit, StringComparison.OrdinalIgnoreCase)) return false;

        var spec = value[unit.Length..].Trim();
        // Multi-range is legal HTTP and needs a multipart/byteranges body. Nothing in the family has
        // ever needed it, so it is honestly declined (answer 200) rather than half-implemented.
        if (spec.Contains(',', StringComparison.Ordinal)) return false;

        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0) return false;

        var fromText = spec[..dash].Trim();
        var toText = spec[(dash + 1)..].Trim();

        long from;
        long to;
        if (fromText.Length == 0)
        {
            // Suffix form: the LAST n bytes.
            if (!long.TryParse(toText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix)
                || suffix <= 0) return false;
            from = Math.Max(0, totalLength - suffix);
            to = totalLength - 1;
        }
        else
        {
            if (!long.TryParse(fromText, NumberStyles.None, CultureInfo.InvariantCulture, out from)) return false;
            if (toText.Length == 0) to = totalLength - 1;
            else if (!long.TryParse(toText, NumberStyles.None, CultureInfo.InvariantCulture, out to)) return false;
        }

        if (from < 0) return false;
        // An EXPLICITLY inverted range (bytes=20-10) is malformed — decline it. Note the guard is on
        // `toText`, not on the computed value: for an open-ended range past the end, `to` was already
        // resolved to the last byte and would look inverted while being perfectly well-formed. Those
        // two cases got conflated on the first attempt and the test below caught it.
        if (toText.Length > 0 && to < from) return false;

        if (from >= totalLength)
        {
            // Well-formed, but outside the resource: the caller must answer 416. Deliberately NOT
            // clamped — clamping the START would silently serve bytes nobody asked for, with no error.
            range = new WebViewByteRange(from, to);
            return true;
        }

        // Clamp only the END, and only once we know the range lands inside the resource.
        if (to > totalLength - 1) to = totalLength - 1;

        range = new WebViewByteRange(from, to);
        return true;
    }

    /// <summary>True when this range lies inside a <paramref name="totalLength"/>-byte resource.</summary>
    public bool IsSatisfiable(long totalLength) => From >= 0 && From < totalLength && To >= From;
}
