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
    /// The app's map from a request URI to a candidate path — how it reads its OWN route and payload shape.
    /// Return null for "not mine" and the request falls through the rest of the pipeline to the platform.
    /// <para>
    /// Required. Whatever this returns is still checked against <see cref="AllowedRoots"/>, so a generous
    /// resolver cannot widen what is reachable — the authorization is not the resolver's job to remember.
    /// </para>
    /// </summary>
    public Func<Uri, string?> Resolve { get; init; } = static _ => null;

    /// <summary>
    /// The directories whose files may be served. <b>Empty means nothing is servable</b>, deliberately,
    /// because the alternative default is the whole filesystem.
    /// <para>
    /// This is the local half of the authorization problem and it is not theoretical: a media or document URL
    /// carries something supplied BY THE PAGE. That is precisely the vector this kit's static serving was
    /// found exposed to — <c>%2e%2e%2f</c> traversal, and <c>Path.Combine</c> discarding its first argument
    /// when the second is rooted — and the fix is generalised here rather than hand-rolled per shell (D45).
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];

    /// <summary>
    /// The MIME type to report. Null means derive it from the extension via
    /// <see cref="WebViewContentTypes.FromPath"/>, which is right for almost everything; set it when the app
    /// knows better than the file name does.
    /// </summary>
    public Func<string, string>? ContentType { get; init; }
}

/// <summary>
/// Serving local files to a page: path containment, and range-correct responses.
/// <para>
/// <b>This is what makes <c>&lt;video&gt;</c>, <c>&lt;audio&gt;</c> and <c>&lt;img&gt;</c> work with no media
/// package at all</b> (D45). It knows nothing about containers or codecs — it answers bytes, honours
/// <c>Range</c>, and reports a content type. A file the platform cannot decode simply errors in the element,
/// which is the honest outcome; deciding what to do about that is <c>Shenora.Media</c>'s job, added as a
/// further middleware.
/// </para>
/// </summary>
public static class WebViewFiles
{
    /// <summary>
    /// Resolve a page-supplied path to a real, allowed absolute path, or null to refuse.
    /// <para>
    /// The order matters and each step is a bug someone hit:
    /// </para>
    /// <list type="number">
    /// <item>Refuse traversal segments BEFORE touching the filesystem — a <c>..</c> that resolves back inside
    /// a root would pass the containment test, and allowing it means the URL shape is no longer what is being
    /// authorised.</item>
    /// <item>Resolve to a full path, refusing anything malformed rather than guessing.</item>
    /// <item>Require the result under an allowed root, <b>comparing with the separator appended</b> — without
    /// it, <c>/media-evil</c> passes as a child of <c>/media</c>.</item>
    /// </list>
    /// <para>
    /// ⚠ Returns null for every refusal and never says why. The caller answers a fixed 404: a distinguishable
    /// "forbidden" reply tells a page whether a path exists, which is the existence leak this kit's own
    /// <c>Exists</c> check had to be fixed for.
    /// </para>
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

            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return full;
        }

        return null;
    }

    /// <summary>
    /// Answer <paramref name="request"/> with <paramref name="path"/>: <c>200</c>, <c>206</c> or <c>416</c>,
    /// with <c>Content-Range</c>, <c>Accept-Ranges</c> and a <c>Content-Length</c> describing what is really
    /// sent.
    /// <para>
    /// The caller has already authorised the path — this does not re-check, because a security check in two
    /// places drifts in one of them.
    /// </para>
    /// <para>
    /// ⚠ Under <see cref="WebViewRangeDelivery.Unsliced"/> the <c>Content-Range</c> describes
    /// <c>{from}</c>→EOF rather than the range asked for. That is not a lie: the platform truncates the front
    /// of the body itself and streams the rest, so a header naming the requested END would be the inaccurate
    /// one.
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
            // No exception text on the wire, ever: page script can read a response body, and a file
            // handler's failure detail is the likeliest of all of them to carry a real path.
            return WebViewResourceResponse.NotFound();
        }

        var rangeHeader = request.GetHeader("Range");

        // No range, or one deliberately declined (multi-range, a non-`bytes` unit): answer the whole
        // resource. `Ok` is what stamps `Accept-Ranges: bytes`, and without that header a player will not
        // even ATTEMPT a seek — indistinguishable from seeking being broken.
        if (!WebViewByteRange.TryParse(rangeHeader, length, out var range))
        {
            return Read(path, 0, length) is { } whole
                ? WebViewResourceResponse.Ok(whole, contentType, ContentLength(length))
                : WebViewResourceResponse.NotFound();
        }

        // A start past the end is unsatisfiable, and the 416 must carry `bytes */length` or a player retries
        // the same bad range forever.
        if (!range.IsSatisfiable(length)) return WebViewResourceResponse.RangeNotSatisfiable(length);

        var unsliced = delivery is WebViewRangeDelivery.Unsliced;
        var sent = unsliced ? new WebViewByteRange(range.From, length - 1) : range;
        var body = unsliced ? Read(path, 0, length) : Read(path, range.From, range.Length);

        return body is null
            ? WebViewResourceResponse.NotFound()
            : WebViewResourceResponse.PartialContent(body, contentType, sent, length, ContentLength(sent.Length));
    }

    /// <summary>
    /// <c>Content-Length</c> explicitly rather than inferred from the stream — it has to be, for the unsliced
    /// case: the body handed over is the whole file while the client receives <c>length - from</c> bytes, so a
    /// value derived from the stream would advertise more than arrives.
    /// </summary>
    private static Dictionary<string, string> ContentLength(long bytes) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Length"] = bytes.ToString(CultureInfo.InvariantCulture),
        };

    /// <summary>
    /// Read a window of the file, or null if it cannot be read.
    /// <para>
    /// ⚠ A <c>MemoryStream</c>, and that is a real limit worth stating rather than hiding: a 4 GB file
    /// answered with <c>bytes=0-</c> is 4 GB of RAM. It is tolerable because a media player asks for small
    /// windows once it knows the length — the observed iOS pattern is dozens of requests of tens to hundreds
    /// of bytes — but a large file requested whole is the case to fix first. The seam takes a
    /// <c>Stream</c> precisely so a bounded, lazily-read implementation can replace this without changing any
    /// signature.
    /// </para>
    /// </summary>
    private static MemoryStream? Read(string path, long from, long count)
    {
        try
        {
            var buffer = new byte[count];
            using var file = File.OpenRead(path);
            file.Seek(from, SeekOrigin.Begin);
            file.ReadExactly(buffer);
            return new MemoryStream(buffer, writable: false);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Wiring a file route onto an interceptor.</summary>
public static class WebViewInterceptorExtensions
{
    /// <summary>
    /// Serve local files through EVERY webview the app hosts — the <c>app.Use*()</c> phase (D64).
    /// <code>
    /// using var app = builder.Build();
    /// app.UseFiles(new WebViewFileOptions { … });
    /// app.Run();
    /// </code>
    /// <para>
    /// Prefer this over the per-interceptor overload below. That one serves ONE webview, so a secondary
    /// window or an auxiliary session browser silently gets nothing — and a window serving no routes looks
    /// exactly like a window whose routes were never needed. Reach for the per-interceptor call only when
    /// one webview is genuinely meant to differ.
    /// </para>
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
    /// and this supplies containment, ranges and content types.
    /// <para>
    /// An extension over the interceptor rather than a middleware the app constructs itself, so
    /// <see cref="IWebViewInterceptor.RangeDelivery"/> is read from the platform and CANNOT be passed in
    /// wrong. That value is a measured platform fact, and the failure mode of getting it wrong is silent —
    /// every faststart file plays and every other one does not (D44).
    /// </para>
    /// <para>
    /// Prefer the <see cref="UseFiles(ShenoraApplication, WebViewFileOptions)"/> overload unless this
    /// webview is genuinely meant to serve something different from the rest of the app.
    /// </para>
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
            // "Not mine" must fall through the REST of the pipeline, not terminate it — that is the whole
            // difference between a middleware and a handler.
            if (requested is null) return next(request, cancellationToken);

            var path = WebViewFiles.ResolveContained(requested, options.AllowedRoots);
            if (path is null)
            {
                // Refused, and indistinguishable from missing — see ResolveContained on the existence leak.
                return Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.NotFound());
            }

            var contentType = options.ContentType?.Invoke(path) ?? WebViewContentTypes.FromPath(path);
            return Task.FromResult<WebViewResourceResponse?>(
                WebViewFiles.Serve(request, path, contentType, delivery));
        });
    }
}
