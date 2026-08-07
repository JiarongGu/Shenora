namespace Shenora.Core.WebView;

/// <summary>
/// Response-header policy for anything a host serves to a page: MIME type and caching, by extension.
/// <para>
/// MOVED HERE FROM <c>Shenora.Windows</c> on 2026-08-04, and it was already the wrong home — a MIME map has
/// nothing Windows-specific about it, and it only lived there because the packaged-bundle host was the first
/// thing that needed one. Now every shell's resource interceptor needs it (D45), so it belongs beside the
/// request and response types it describes. <c>internal</c> became public with the move: an app writing its
/// own middleware needs to answer the same question.
/// </para>
/// <para>
/// ⚠ <b>Media types were ADDED with the move, and their absence is the point.</b> This map was built for
/// bundle assets — html, css, js, fonts — so it had no audio or video entries at all, and would have
/// answered <c>application/octet-stream</c> for an mp4. A media element given octet-stream refuses to play,
/// which is a failure that looks like a broken file rather than a missing table row.
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

            // Video. ⚠ `.mkv` and `.avi` are deliberately named rather than left to fall through: a page
            // may well ask for one, and answering octet-stream makes the element refuse before it has even
            // tried — which reads as a broken file. Naming them lets the element decide, and its refusal is
            // then the honest "this platform cannot decode this", which is exactly the case
            // `Shenora.Media`'s planner exists to answer.
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
    /// The family's static caching policy: HTML (the entry document — its name never changes across
    /// releases) is <c>no-cache</c>; everything else in a Vite-style bundle is content-hashed and safely
    /// immutable. The source app served EVERYTHING immutable including <c>index.html</c> — a stale-bundle
    /// trap after updates; fixed here.
    /// <para>
    /// ⚠ This is a BUNDLE policy and is wrong for user content. A file served from disk is not
    /// content-hashed, so <c>immutable</c> would pin a stale copy in the webview's cache after the user
    /// replaces the file. The file middleware therefore does not apply this — it sets no
    /// <c>Cache-Control</c> at all and lets the app decide, which is why this stayed a separate method
    /// rather than being folded into <see cref="FromPath"/>.
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
