using System.Text;
using Microsoft.Extensions.Logging;
using Shenora.Core.WebView;

namespace Shenora.Chromium.Serving;

/// <summary>
/// What a Chromium window answers for its own origins, once <see cref="ChromiumRouting"/> has decided a
/// request is the shell's.
/// <list type="bullet">
/// <item>The bundle first, then the app's interceptor pipeline on a miss, then a fixed 404 (D45's order).
/// An HTML document from the bundle is MARKED (D83), so the page finds the transport.</item>
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

    public ChromiumServing(string? contentRoot, ChromiumOrigins origins, ChromiumInterceptor interceptor, HttpClient? dev = null, ILogger? log = null)
    {
        _contentRoot = contentRoot;
        _origins = origins;
        _interceptor = interceptor;
        _dev = dev;
        _log = log;
    }

    public static WebViewResourceResponse Accepted() => Constant(204, "No Content", "");

    public static WebViewResourceResponse Forbidden() => Constant(403, "Forbidden", "forbidden");

    public async Task<WebViewResourceResponse> ServeAsync(ChromiumRoute route, WebViewResourceRequest request, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case ChromiumRoute.Bundle:
                return TryBundle(request)
                    ?? await _interceptor.Handle(request, cancellationToken).ConfigureAwait(false)
                    ?? WebViewResourceResponse.NotFound();
            case ChromiumRoute.DevDocument:
                return await DevDocumentAsync(request, cancellationToken).ConfigureAwait(false);
            case ChromiumRoute.Ipc:
                return Accepted();
            default:
                return Forbidden();
        }
    }

    /// <summary>The bundle's file for this request, or null to fall through to the pipeline.</summary>
    private WebViewResourceResponse? TryBundle(WebViewResourceRequest request)
    {
        if (_contentRoot is null || !string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)) return null;

        // Unescaped FIRST, so `%2e%2e%2f` arrives as `../` and meets the containment check as what it is.
        var relative = Uri.UnescapeDataString(request.Uri.AbsolutePath).TrimStart('/');
        if (relative.Length == 0) relative = "index.html";
        var full = WebViewFiles.ResolveContained(Path.Combine(_contentRoot, relative), [_contentRoot]);
        if (full is null || !File.Exists(full)) return null;

        var contentType = WebViewContentTypes.FromPath(full);
        if (!contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            return WebViewFiles.Serve(request, full, contentType, _interceptor.RangeDelivery);

        var marked = ChromiumTransport.MarkHtml(File.ReadAllText(full, Encoding.UTF8), _origins.IpcPath);
        return WebViewResourceResponse.Bytes(Encoding.UTF8.GetBytes(marked), "text/html; charset=utf-8",
            new Dictionary<string, string> { ["Cache-Control"] = WebViewContentTypes.CacheControlFromPath(full) });
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
