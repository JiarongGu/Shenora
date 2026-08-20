using System.Globalization;

namespace Shenora.Core.WebView;

/// <summary>
/// The request a deferred-scheme handler is answering. It carries the request HEADERS: without
/// <c>Range</c> a handler cannot know what byte offset was asked for nor answer <c>206</c>, so a page
/// cannot SEEK a large file.
/// </summary>
public sealed class WebViewResourceRequest
{
    /// <summary>
    /// The requested URI. ⚠ <b>It can carry a <c>#fragment</c></b>: a top-level navigation to
    /// <c>https://host/#/library</c> arrives with <c>Fragment = "#/library"</c> and
    /// <c>AbsolutePath = "/"</c>, so a resolver reading <see cref="System.Uri.AbsolutePath"/> is fine and
    /// one reading <c>ToString()</c> or <c>PathAndQuery</c> mis-resolves. That safe reading also HIDES the
    /// fragment, so log the whole <see cref="System.Uri"/> when a document request surprises you.
    /// </summary>
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

    /// <summary>
    /// True when <paramref name="uri"/> asks for the site ROOT <b>and</b> carries a <c>#fragment</c> — the
    /// shape a hash-routed page reloads at (<c>https://host/#/library</c>), which both mobile shells
    /// repair with no app code. The break is the platform's: MAUI's request→asset mapping removes a query
    /// string and not a fragment, so <c>/#zzz</c> looks for an asset literally named <c>#zzz</c> and
    /// Chromium turns that bodyless 404 into <c>ERR_INVALID_RESPONSE</c> (measured on MAUI 10.0.20 /
    /// WebView 110). Scoped to the root path — <c>/index.html#x</c> is not claimed.
    /// <para>
    /// ⚠ A repair must not read the bundle INSIDE the handler: that deadlocks iOS's main thread. Read at
    /// construction.
    /// </para>
    /// </summary>
    /// <param name="uri">The request URI. A relative URI is never one of these, and answers false.</param>
    public static bool IsRootWithFragment(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        // Fragment/AbsolutePath THROW on a relative Uri.
        if (!uri.IsAbsoluteUri) return false;
        // Any '#' at all, including a bare one: over-repairing costs a correct document, under-repairing
        // costs the whole page.
        if (uri.Fragment.Length == 0) return false;
        return uri.AbsolutePath is "/" or "";
    }
}

/// <summary>
/// The response a deferred-scheme handler returns: a status, headers, and a CONTENT STREAM — a stream
/// rather than a <c>byte[]</c>, so serving a 4 GB file does not materialise 4 GB. Use the factories,
/// which set the status line and the headers that belong with it.
/// </summary>
public sealed class WebViewResourceResponse
{
    /// <summary>
    /// The body. Ownership passes to THE PLATFORM, which READS it after the handler has returned — do
    /// NOT dispose it yourself or wrap it in a <c>using</c>; that truncates the response. The host
    /// disposes it only if handing it over failed (a webview torn down mid-flight).
    /// <para>
    /// ⚠ The per-shell arms differ in when they close it, and iOS never disposes a body at all — see
    /// <c>docs/design/mobile-shells.md</c>.
    /// </para>
    /// </summary>
    public required Stream Content { get; init; }

    /// <summary>HTTP status code. 200 unless a factory says otherwise.</summary>
    public int StatusCode { get; init; } = 200;

    /// <summary>HTTP reason phrase, paired with <see cref="StatusCode"/>.</summary>
    public string ReasonPhrase { get; init; } = "OK";

    /// <summary>Response headers, case-insensitive (<c>Content-Type</c> included). The host adds the
    /// scheme's <c>Cache-Control</c> unless this already carries one.</summary>
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
        // Without this a media element will not even ATTEMPT a seek — indistinguishable from seeking
        // being broken.
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
    /// A 404 carrying the kit's single fixed body. Page script can read a response body and a scheme
    /// handler's failure detail is the likeliest to carry a real path, so diagnosis goes to the host log.
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
    /// A <c>416</c> for a range outside the resource. Its <c>Content-Range</c> tells the client the real
    /// size; without it a player retries the same bad range forever.
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

/// <summary>One resolved byte range, inclusive at both ends — the form <c>Content-Range</c> uses.</summary>
/// <param name="From">First byte offset, inclusive.</param>
/// <param name="To">Last byte offset, inclusive.</param>
public readonly record struct WebViewByteRange(long From, long To)
{
    /// <summary>Number of bytes in the range.</summary>
    public long Length => To - From + 1;

    /// <summary>
    /// Parse a single-range <c>Range</c> header against a known resource length. All three forms:
    /// <c>bytes=0-499</c> (closed), <c>bytes=500-</c> (open-ended, what a media element sends when it
    /// seeks) and <c>bytes=-500</c> (a SUFFIX — the LAST 500 bytes, not "from 500").
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
        // Multi-range needs a multipart/byteranges body: declined (answer 200), not half-implemented.
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
        // An EXPLICITLY inverted range (bytes=20-10) is malformed. The guard is on `toText`, not on the
        // computed value: for an open-ended range past the end, `to` is already the last byte.
        if (toText.Length > 0 && to < from) return false;

        if (from >= totalLength)
        {
            // Well-formed but outside the resource: the caller must answer 416. NOT clamped — clamping
            // the START would silently serve bytes nobody asked for.
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
