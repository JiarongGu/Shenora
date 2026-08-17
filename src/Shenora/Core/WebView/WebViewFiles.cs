using System.Globalization;

namespace Shenora.Core.WebView;

/// <summary>
/// What an app permits a page to load from disk, and how its route maps to a file.
/// <b>Fail-closed</b>: the defaults serve nothing, so a middleware wired up before it is configured refuses
/// rather than exposing a filesystem.
/// </summary>
public sealed record WebViewFileOptions
{
    /// <summary>
    /// The app's map from a request URI to a candidate path; null means "not mine" and the request falls
    /// through the rest of the pipeline to the platform. Required. Whatever it returns is still checked
    /// against <see cref="AllowedRoots"/>, so a generous resolver cannot widen what is reachable.
    /// </summary>
    public Func<Uri, string?> Resolve { get; init; } = static _ => null;

    /// <summary>
    /// The directories whose files may be served. <b>Empty means nothing is servable</b> — the alternative
    /// default is the whole filesystem. This is the local half of the authorization problem: a media or
    /// document URL carries a path supplied BY THE PAGE, so it is checked here rather than per shell (D45).
    /// </summary>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];

    /// <summary>
    /// The MIME type to report. Null derives it from the extension via
    /// <see cref="WebViewContentTypes.FromPath"/>; set it when the app knows better than the file name does.
    /// </summary>
    public Func<string, string>? ContentType { get; init; }
}

/// <summary>
/// Serving local files to a page: path containment, and range-correct responses.
/// <b>This is what makes <c>&lt;video&gt;</c>, <c>&lt;audio&gt;</c> and <c>&lt;img&gt;</c> work with no media
/// package at all</b> (D45): it answers bytes, honours <c>Range</c> and reports a content type, knowing
/// nothing about containers or codecs. Deciding what to do about a file the platform cannot decode is
/// <c>Shenora.Modules.Media</c>'s job, added as a further middleware.
/// </summary>
public static class WebViewFiles
{
    /// <summary>
    /// Resolve a page-supplied path to a real, allowed absolute path, or null to refuse. The order is
    /// load-bearing:
    /// <list type="number">
    /// <item>Refuse traversal segments BEFORE touching the filesystem — a <c>..</c> that resolves back inside
    /// a root would pass the containment test.</item>
    /// <item>Resolve to a full path, refusing anything malformed rather than guessing.</item>
    /// <item>Require the result under an allowed root, <b>comparing with the separator appended</b> — without
    /// it, <c>/media-evil</c> passes as a child of <c>/media</c>.</item>
    /// </list>
    /// ⚠ Returns null for every refusal and never says why: the caller answers a fixed 404, because a
    /// distinguishable "forbidden" reply tells a page whether a path exists.
    /// </summary>
    public static string? ResolveContained(string? requestedPath, IReadOnlyList<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        if (string.IsNullOrWhiteSpace(requestedPath)) return null;
        if (allowedRoots.Count == 0) return null;   // fail closed

        foreach (var segment in requestedPath.Split('/', '\\'))
        {
            if (segment == "..") return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(requestedPath);
        }
        catch (Exception)
        {
            return null;   // malformed (invalid characters, too long, …) — never serve it
        }

        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(root);
            }
            catch (Exception)
            {
                continue;   // a malformed ROOT disqualifies itself, not the request
            }

            var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;

            // The case rule is SHARED with `Engine.Files.PathClaims.IsContained` (PathComparison). ⚠ The
            // two are otherwise deliberately different and must NOT be collapsed — see
            // `docs/design/missions-and-files.md`.
            if (full.StartsWith(prefix, PathComparison.ForPaths)) return full;
        }

        return null;
    }

    /// <summary>
    /// Answer <paramref name="request"/> with <paramref name="path"/>: <c>200</c>, <c>206</c> or <c>416</c>,
    /// with <c>Content-Range</c>, <c>Accept-Ranges</c> and a <c>Content-Length</c> describing what is really
    /// sent. The caller has already authorised the path — this does not re-check.
    /// <para>
    /// ⚠ Under <see cref="WebViewRangeDelivery.Unsliced"/> the <c>Content-Range</c> describes
    /// <c>{from}</c>→EOF rather than the range asked for: the platform truncates the front of the body itself
    /// and streams the rest, so a header naming the requested END would be the inaccurate one.
    /// </para>
    /// </summary>
    public static WebViewResourceResponse Serve(WebViewResourceRequest request, string path,
                                                string contentType, WebViewRangeDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        long length;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return WebViewResourceResponse.NotFound();
            length = info.Length;
        }
        catch (Exception)
        {
            // No exception text on the wire, ever — a file handler's failure detail is the likeliest of
            // all of them to carry a real path.
            return WebViewResourceResponse.NotFound();
        }

        return ServeRange(request, length, contentType, delivery, (from, count) => Read(path, from, count));
    }

    /// <summary>
    /// The same answer for a body that is not a file: the status line, the range arithmetic and the
    /// <b>per-platform delivery rule</b>, over any producer of bytes — so
    /// 🔴 <see cref="WebViewRangeDelivery"/> has exactly ONE implementation. D44 is a measured platform fact
    /// whose failure mode is silent: the wrong choice serves correct-looking bytes at the wrong offset,
    /// playing every faststart file and breaking every file whose index sits at the end.
    /// ⚠ Internal — a seam inside this assembly, not a surface for an app to compose against.
    /// </summary>
    /// <param name="request">The request, read for its <c>Range</c> header.</param>
    /// <param name="totalLength">
    /// The WHOLE resource's length, even when a single window is sent — what <c>Content-Range</c> states,
    /// and on some platforms the only place a media element learns its duration and seekable window (D71).
    /// </param>
    /// <param name="contentType">The type of what is being SENT — for a computed body not the source
    /// file's, which a media element refuses before trying a byte.</param>
    /// <param name="delivery">The platform's rule, read from <see cref="IWebViewInterceptor.RangeDelivery"/>.</param>
    /// <param name="read">Produce bytes <c>[from, from + count)</c>, or null when they cannot be produced —
    /// the caller then answers the kit's single fixed 404. Called at most once per response.</param>
    internal static WebViewResourceResponse ServeRange(WebViewResourceRequest request, long totalLength,
                                                      string contentType, WebViewRangeDelivery delivery,
                                                      Func<long, long, Stream?> read)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(read);

        var rangeHeader = request.GetHeader("Range");

        // No range, or one declined (multi-range, a non-`bytes` unit): answer the whole resource through
        // `Ok`, which is what stamps `Accept-Ranges: bytes`.
        if (!WebViewByteRange.TryParse(rangeHeader, totalLength, out var range))
        {
            return read(0, totalLength) is { } whole
                ? WebViewResourceResponse.Ok(whole, contentType, ContentLength(totalLength))
                : WebViewResourceResponse.NotFound();
        }

        // A start past the end is unsatisfiable: 416, never a clamp.
        if (!range.IsSatisfiable(totalLength)) return WebViewResourceResponse.RangeNotSatisfiable(totalLength);

        var unsliced = delivery is WebViewRangeDelivery.Unsliced;
        var sent = unsliced ? new WebViewByteRange(range.From, totalLength - 1) : range;
        var body = unsliced ? read(0, totalLength) : read(range.From, range.Length);

        return body is null
            ? WebViewResourceResponse.NotFound()
            : WebViewResourceResponse.PartialContent(body, contentType, sent, totalLength, ContentLength(sent.Length));
    }

    /// <summary>
    /// <c>Content-Length</c> explicitly rather than inferred from the stream: in the unsliced case the body
    /// handed over is the whole file while the client receives <c>length - from</c> bytes, so a derived value
    /// would advertise more than arrives.
    /// </summary>
    private static Dictionary<string, string> ContentLength(long bytes) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Length"] = bytes.ToString(CultureInfo.InvariantCulture),
        };

    /// <summary>
    /// Read a window of the file, or null if it cannot be read. Opens and seeks only — it touches no bytes.
    /// <para>
    /// 🔴 <b>The body is a <see cref="BoundedBodyStream"/> over an open, UNDISPOSED
    /// <see cref="FileStream"/> — the missing <c>using</c> is deliberate.</b> The platform reads
    /// <see cref="WebViewResourceResponse.Content"/> AFTER this returns, so disposing here would close the
    /// file before a byte was sent; a <see cref="MemoryStream"/> would materialise the whole window, which
    /// under <see cref="WebViewRangeDelivery.Unsliced"/> IS the whole file (D44).
    /// </para>
    /// <para>
    /// ⚠ <b>A lazy body moves EVERY mid-read failure past the committed headers, and that cannot be
    /// eliminated.</b> A shrunk file, a pulled volume, a dropped share, a revoked permission — all of them
    /// now surface when the PLATFORM pulls from the bound, after <see cref="ServeRange"/> has committed a
    /// 200/206 and a <c>Content-Length</c> built from the stale length. What each shell does with such a
    /// throw is the shell's own answer and only Android's is good — see
    /// <see cref="BoundedBodyStream.Read"/>, with the per-shell arms in
    /// <c>docs/design/mobile-shells.md</c>.
    /// </para>
    /// <para>
    /// ⚠ <b>The handle stays open for as long as the platform holds the response</b>, including a request
    /// the page ABANDONS before EOF (a seek away, a navigation). Hence <see cref="FileShare.Delete"/>
    /// alongside <see cref="FileShare.Read"/> and NOT <see cref="FileShare.ReadWrite"/>: a concurrent
    /// DELETE is safe (NTFS defers it until every handle closes), while a concurrent WRITE would tear the
    /// bytes this handle is mid-read of.
    /// </para>
    /// </summary>
    private static Stream? Read(string path, long from, long count)
    {
        FileStream? file = null;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            file.Seek(from, SeekOrigin.Begin);
            return new BoundedBodyStream(file, count);
        }
        catch (Exception)
        {
            // Ownership has NOT passed to the platform on this path (construction or the seek failed), so
            // this is the one place left that must close the handle itself.
            file?.Dispose();
            return null;
        }
    }
}

/// <summary>Wiring a file route onto an interceptor.</summary>
public static class WebViewInterceptorExtensions
{
    /// <summary>
    /// Serve local files through EVERY webview the app hosts — the <c>app.Use*()</c> phase (D64). Prefer
    /// this over the per-interceptor overload below, which serves ONE webview: a secondary window or an
    /// auxiliary session browser then silently gets nothing, and a window serving no routes looks exactly
    /// like a window whose routes were never needed.
    /// </summary>
    /// <returns>The app, so calls chain.</returns>
    public static ShenoraApplication UseFiles(this ShenoraApplication app, WebViewFileOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        return app.Use(interceptor => interceptor.UseFiles(options));
    }

    /// <summary>
    /// Serve local files through ONE <paramref name="interceptor"/>: the app supplies its route and roots,
    /// and this supplies containment, ranges and content types. An extension over the interceptor rather
    /// than a middleware the app constructs itself, so <see cref="IWebViewInterceptor.RangeDelivery"/> is
    /// read from the platform and CANNOT be passed in wrong — a measured platform fact whose failure mode
    /// is silent: every faststart file plays and every other one does not (D44).
    /// </summary>
    /// <returns>Dispose to remove the route.</returns>
    public static IDisposable UseFiles(this IWebViewInterceptor interceptor, WebViewFileOptions options)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        ArgumentNullException.ThrowIfNull(options);

        var delivery = interceptor.RangeDelivery;

        return interceptor.Use((request, next, cancellationToken) =>
        {
            var requested = options.Resolve(request.Uri);
            // "Not mine" must fall through the REST of the pipeline, not terminate it.
            if (requested is null) return next(request, cancellationToken);

            var path = WebViewFiles.ResolveContained(requested, options.AllowedRoots);
            if (path is null)
            {
                // Refused, and indistinguishable from missing — see ResolveContained.
                return Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.NotFound());
            }

            var contentType = options.ContentType?.Invoke(path) ?? WebViewContentTypes.FromPath(path);
            return Task.FromResult<WebViewResourceResponse?>(
                WebViewFiles.Serve(request, path, contentType, delivery));
        });
    }
}
