using System.Text;
using Microsoft.Extensions.Logging;
using Shenora.Core.WebView;

namespace Shenora.Chromium.Serving;

/// <summary>
/// What a Chromium window answers for its own origins, once <see cref="ChromiumRouting"/> has decided a
/// request is the shell's.
/// <list type="bullet">
/// <item>The bundle (a folder, or a provider) first, then the app's interceptor pipeline on a miss, then a fixed 404
/// (D45's order). A provider that throws is a fixed 404, never handed to the app's pipeline.
/// An HTML document from either is MARKED (D83), so the page finds the transport, as it does on WebView2
/// whatever served it.</item>
/// <item>The dev server's top-level document is fetched and marked the same way.</item>
/// <item>Every refusal and failure is a constant body: every response here is readable by page script.</item>
/// </list>
/// </summary>
internal sealed class ChromiumServing
{
    private readonly string? _contentRoot;
    private readonly ChromiumOrigins _origins;
    private readonly ChromiumInterceptor _interceptor;
    private readonly HttpClient? _dev;
    private readonly ILogger? _log;
    private readonly IWebViewResourceProvider? _provider;
    private readonly string? _notFoundPage;

    public ChromiumServing(string? contentRoot, ChromiumOrigins origins, ChromiumInterceptor interceptor, HttpClient? dev = null,
        ILogger? log = null, IWebViewResourceProvider? provider = null, string? notFoundPage = null)
    {
        _contentRoot = contentRoot;
        _origins = origins;
        _interceptor = interceptor;
        _dev = dev;
        _log = log;
        _provider = provider;
        _notFoundPage = notFoundPage;
        if (provider is not null)
            AppCallback.Run(provider.BeginWarmup,
                ex => AppCallback.Log(log, () => "[Shenora.Chromium] Warming the resource provider failed", LogLevel.Warning, ex));
    }

    public static WebViewResourceResponse Accepted() => Constant(204, "No Content", "");

    public static WebViewResourceResponse Forbidden() => Constant(403, "Forbidden", "forbidden");

    public async Task<WebViewResourceResponse> ServeAsync(ChromiumRoute route, WebViewResourceRequest request, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case ChromiumRoute.Bundle:
            case ChromiumRoute.BundlePage:
                if (TryBundle(request, out var faulted) is { } file) return file;
                if (!faulted && await _interceptor.Handle(request, cancellationToken).ConfigureAwait(false) is { } routed)
                    return await MarkedAsync(routed, cancellationToken).ConfigureAwait(false);
                return route == ChromiumRoute.BundlePage ? NotFoundPage(request) : WebViewResourceResponse.NotFound();
            case ChromiumRoute.DevDocument:
                return await DevDocumentAsync(request, cancellationToken).ConfigureAwait(false);
            default:
                return Forbidden();
        }
    }

    /// <summary>
    /// Serve the bundle's root document once and discard it: the file read, the marking and the code that does them
    /// run before the first real request needs them. Nothing when there is no bundle.
    /// </summary>
    public void Warm(Uri root) =>
        TryBundle(new WebViewResourceRequest { Uri = root, Method = "GET", Headers = new Dictionary<string, string>() }, out _)?.Content?.Dispose();

    /// <summary>
    /// The bundle's file for this request, or null to fall through to the pipeline. <paramref name="faulted"/>: the
    /// provider threw, which the shell answers itself — never handed on to the app's routes.
    /// </summary>
    private WebViewResourceResponse? TryBundle(WebViewResourceRequest request, out bool faulted)
    {
        faulted = false;
        if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)) return null;

        // Unescaped FIRST, so `%2e%2e%2f` arrives as `../` and meets the containment check as what it is.
        var relative = Uri.UnescapeDataString(request.Uri.AbsolutePath).TrimStart('/');
        if (relative.Length == 0) relative = "index.html";
        try
        {
            return Read(relative, request, 200);
        }
        catch (Exception ex) when (_provider is not null)
        {
            AppCallback.Log(_log, () => $"[Shenora.Chromium] Serving '{relative}' from {Source} failed", LogLevel.Warning, ex);
            faulted = true;
            return null;
        }
    }

    /// <summary>
    /// The bundle's file at <paramref name="relative"/>, from the folder or the provider, with
    /// <paramref name="status"/>; null when the bundle has none. An HTML document is MARKED (D83).
    /// </summary>
    private WebViewResourceResponse? Read(string relative, WebViewResourceRequest request, int status)
    {
        if (_contentRoot is not null)
        {
            var full = WebViewFiles.ResolveContained(Path.Combine(_contentRoot, relative), [_contentRoot]);
            if (full is null || !File.Exists(full)) return null;
            var type = WebViewContentTypes.FromPath(full);
            if (type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                return Html(File.ReadAllText(full, Encoding.UTF8), full, status);
            return status == 200
                ? WebViewFiles.Serve(request, full, type, _interceptor.RangeDelivery)
                : Whole(File.ReadAllBytes(full), full, status);
        }
        if (_provider is null || relative.Split('/', '\\').Contains("..")) return null;
        using var stream = _provider.GetResourceStream(relative);
        if (stream is null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return WebViewContentTypes.FromPath(relative).StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
            ? Html(Encoding.UTF8.GetString(copy.ToArray()), relative, status)
            : Whole(copy.ToArray(), relative, status);
    }

    /// <summary>A page load that found nothing: the bundle's own not-found page, else the kit's; a Warning either way.</summary>
    private WebViewResourceResponse NotFoundPage(WebViewResourceRequest request)
    {
        WebViewResourceResponse? own = null;
        var page = _notFoundPage?.Replace('\\', '/').TrimStart('/');
        if (page is { Length: > 0 } && (_contentRoot is not null || _provider is not null))
        {
            try { own = Read(page, request, 404); }
            catch (Exception ex)
            {
                AppCallback.Log(_log, () => $"[Shenora.Chromium] Reading the bundle's '{page}' from {Source} failed", LogLevel.Warning, ex);
            }
        }
        AppCallback.Log(_log, () => $"[Shenora.Chromium] No page at '{request.Uri.AbsolutePath}' ({Source}): showing "
            + (own is null ? "the kit's not-found page" : $"the bundle's '{page}'"), LogLevel.Warning);
        return own ?? WebViewResourceResponse.NotFoundDocument();
    }

    /// <summary>Where the bundle comes from, for the host log.</summary>
    private string Source => _contentRoot is not null ? $"the folder '{_contentRoot}'"
        : _provider is not null ? $"the resource provider {_provider.GetType().Name}"
        : "no ContentRoot or ResourceProvider is set";

    private WebViewResourceResponse Html(string html, string path, int status) =>
        Whole(Encoding.UTF8.GetBytes(ChromiumTransport.MarkHtml(html, _origins.IpcPath)), path, status, "text/html; charset=utf-8");

    private static WebViewResourceResponse Whole(byte[] bytes, string path, int status, string? type = null) => new()
    {
        StatusCode = status,
        ReasonPhrase = status == 200 ? "OK" : "Not Found",
        Content = new MemoryStream(bytes, writable: false),
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = type ?? WebViewContentTypes.FromPath(path),
            // A not-found page is never cached as if it were the page asked for.
            ["Cache-Control"] = status == 200 ? WebViewContentTypes.CacheControlFromPath(path) : "no-store",
        },
    };

    /// <summary>
    /// An HTML document the app's pipeline served (its own route, an embedded bundle), marked like a bundle file.
    /// Anything else, and HTML in a charset other than UTF-8, passes through untouched.
    /// </summary>
    private async Task<WebViewResourceResponse> MarkedAsync(WebViewResourceResponse response, CancellationToken cancellationToken)
    {
        var type = response.Headers?.FirstOrDefault(h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
        if (type is null || !type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)) return response;
        var charset = type.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        if (charset >= 0 && !type[(charset + 8)..].TrimStart('"').StartsWith("utf-8", StringComparison.OrdinalIgnoreCase)) return response;

        string html;
        await using (var body = response.Content)
        using (var reader = new StreamReader(body, Encoding.UTF8))
            html = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in response.Headers!)
            if (!string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)) headers[name] = value;
        headers["Content-Type"] = "text/html; charset=utf-8";
        return new WebViewResourceResponse
        {
            StatusCode = response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Content = new MemoryStream(Encoding.UTF8.GetBytes(ChromiumTransport.MarkHtml(html, _origins.IpcPath)), writable: false),
            Headers = headers,
        };
    }

    /// <summary>The dev server's document, marked. The dev server serves everything else itself.</summary>
    private async Task<WebViewResourceResponse> DevDocumentAsync(WebViewResourceRequest request, CancellationToken cancellationToken)
    {
        if (_dev is null) return Constant(502, "Bad Gateway", "the dev server is not configured");
        try
        {
            using var response = await _dev.GetAsync(request.Uri, cancellationToken).ConfigureAwait(false);
            var type = response.Content.Headers.ContentType?.ToString() ?? "text/html";
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                bytes = Encoding.UTF8.GetBytes(ChromiumTransport.MarkHtml(Encoding.UTF8.GetString(bytes), _origins.IpcPath));
                type = "text/html; charset=utf-8";
            }
            return new WebViewResourceResponse
            {
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase ?? "",
                Content = new MemoryStream(bytes, writable: false),
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = type },
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AppCallback.Log(_log, () => "[Shenora.Chromium] The dev server did not answer", LogLevel.Warning, ex);
            return Constant(502, "Bad Gateway", "the dev server did not answer");
        }
    }

    private static WebViewResourceResponse Constant(int status, string phrase, string body) => new()
    {
        StatusCode = status,
        ReasonPhrase = phrase,
        Content = new MemoryStream(Encoding.UTF8.GetBytes(body), writable: false),
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "text/plain" },
    };
}
