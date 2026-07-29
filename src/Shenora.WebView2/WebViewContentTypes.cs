namespace Shenora.WebView2;

/// <summary>Response-header policy for the packaged-frontend virtual host.</summary>
internal static class WebViewContentTypes
{
    /// <summary>MIME type by extension (the family's proven set; octet-stream fallback).</summary>
    public static string FromPath(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" or ".mjs" => "application/javascript",
            ".json" or ".map" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".eot" => "application/vnd.ms-fontobject",
            ".txt" => "text/plain",
            ".wasm" => "application/wasm",
            _ => "application/octet-stream",
        };
    }

    /// <summary>
    /// The family's static caching policy: HTML (the entry document — its name never changes
    /// across releases) is <c>no-cache</c>; everything else in a Vite-style bundle is
    /// content-hashed and safely immutable. The source app served EVERYTHING immutable including
    /// <c>index.html</c> — a stale-bundle trap after updates; fixed here.
    /// </summary>
    public static string CacheControlFromPath(string path) =>
        Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase)
            ? "no-cache"
            : "public, max-age=31536000, immutable";
}
