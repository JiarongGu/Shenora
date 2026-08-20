namespace Shenora.Core.WebView;

/// <summary>
/// Response-header policy for anything a host serves to a page: MIME type and caching, by extension.
/// <para>
/// ⚠ <b>A missing entry is not a missing feature.</b> A media element given the
/// <c>application/octet-stream</c> fallback refuses to play, which looks like a broken file rather than a
/// missing table row.
/// </para>
/// </summary>
public static class WebViewContentTypes
{
    /// <summary>MIME type by extension (the family's proven set; octet-stream fallback).</summary>
    public static string FromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            // The packaged frontend.
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" or ".mjs" => "application/javascript",
            ".json" or ".map" => "application/json",
            ".wasm" => "application/wasm",
            ".txt" => "text/plain",

            // Images.
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".avif" => "image/avif",
            ".bmp" => "image/bmp",
            ".ico" => "image/x-icon",

            // Fonts.
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".eot" => "application/vnd.ms-fontobject",

            // Video. ⚠ `.mkv` and `.avi` are named rather than left to fall through, so the element
            // decides: its refusal is then an honest "this platform cannot decode this".
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".ts" => "video/mp2t",

            // Audio.
            ".mp3" => "audio/mpeg",
            ".m4a" or ".aac" => "audio/mp4",
            ".ogg" or ".oga" => "audio/ogg",
            ".opus" => "audio/opus",
            ".flac" => "audio/flac",
            ".wav" => "audio/wav",

            // Documents an app may hand a page directly.
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",

            _ => "application/octet-stream",
        };
    }

    /// <summary>
    /// The caching policy for a BUNDLE: HTML (the entry document, whose name never changes across
    /// releases) is <c>no-cache</c>; everything else in a Vite-style bundle is content-hashed and safely
    /// immutable.
    /// <para>
    /// ⚠ <b>Wrong for user content</b>, which is why it is separate from <see cref="FromPath"/>: a file
    /// served from disk is not content-hashed, so <c>immutable</c> would pin a stale copy in the webview's
    /// cache after the user replaces the file. The file middleware sets no <c>Cache-Control</c> at all.
    /// </para>
    /// </summary>
    public static string CacheControlFromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase)
            ? "no-cache"
            : "public, max-age=31536000, immutable";
    }
}
