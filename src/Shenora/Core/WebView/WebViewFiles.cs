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
/// which is the honest outcome; deciding what to do about that is <c>Shenora.Modules.Media</c>'s job, added as a
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

            // 🔴 PLATFORM-CORRECT, not `OrdinalIgnoreCase` — which is what this was until 2026-08-14, and
            // it was WIDER THAN THE FILESYSTEM on the one shell that matters. Android's ext4/f2fs is
            // case-SENSITIVE, so with an allowed root of `…/files/public` a page asking for
            // `…/files/Public/secret` passed containment and was served out of a directory the app never
            // allowed. (NTFS and iOS's APFS are case-insensitive by default, so those two were correct by
            // accident.) `Shenora.Engine.Files.PathClaims.IsContained` is the canonical statement of this
            // rule and has always had it right; the comparison is repeated rather than called because
            // `Core` does not depend on `Engine` (D65's layer direction) — if that ever changes, delete
            // this and call it.
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (full.StartsWith(prefix, comparison)) return full;
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

        return ServeRange(request, length, contentType, delivery, (from, count) => Read(path, from, count));
    }

    /// <summary>
    /// The same answer for a body that is not a file: the status line, the range arithmetic and the
    /// <b>per-platform delivery rule</b>, over any producer of bytes.
    ///
    /// <para>
    /// 🔴 <b>It exists so <see cref="WebViewRangeDelivery"/> has exactly ONE implementation.</b> D44 is a
    /// measured platform fact whose failure mode is silent — the wrong choice serves correct-looking bytes at
    /// the wrong offset, which plays every faststart file and breaks every file whose index sits at the end —
    /// so a second delivery path re-deriving it is how the two come to disagree. The media tier's computed
    /// remux (D71) answers ranges for a file that has never been written, and that is this same protocol
    /// problem with a different source of bytes, not a new one.
    /// </para>
    ///
    /// <para>
    /// ⚠ Internal rather than public: it is a seam INSIDE this assembly, not a surface for an app to compose
    /// against. An app that wants a computed body registers the middleware that owns it.
    /// </para>
    /// </summary>
    /// <param name="request">The request, read for its <c>Range</c> header.</param>
    /// <param name="totalLength">
    /// The WHOLE resource's length — what <c>Content-Range</c> states, and the only place some platforms can
    /// learn a media element's duration and seekable window from (D71's Android measurement). It stays the
    /// full length even when a single window is being sent.
    /// </param>
    /// <param name="contentType">
    /// The type of what is being SENT — which for a computed body is not the source file's, and answering
    /// with the source's is a body a media element refuses before trying a byte.
    /// </param>
    /// <param name="delivery">The platform's rule, read from <see cref="IWebViewInterceptor.RangeDelivery"/>.</param>
    /// <param name="read">
    /// Produce bytes <c>[from, from + count)</c> of the resource, or null when they cannot be produced — the
    /// caller then answers the kit's single fixed 404. Called at most once per response.
    /// </param>
    internal static WebViewResourceResponse ServeRange(WebViewResourceRequest request, long totalLength,
                                                      string contentType, WebViewRangeDelivery delivery,
                                                      Func<long, long, Stream?> read)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(read);

        var rangeHeader = request.GetHeader("Range");

        // No range, or one deliberately declined (multi-range, a non-`bytes` unit): answer the whole
        // resource. `Ok` is what stamps `Accept-Ranges: bytes`, and without that header a player will not
        // even ATTEMPT a seek — indistinguishable from seeking being broken.
        if (!WebViewByteRange.TryParse(rangeHeader, totalLength, out var range))
        {
            return read(0, totalLength) is { } whole
                ? WebViewResourceResponse.Ok(whole, contentType, ContentLength(totalLength))
                : WebViewResourceResponse.NotFound();
        }

        // A start past the end is unsatisfiable, and the 416 must carry `bytes */length` or a player retries
        // the same bad range forever.
        if (!range.IsSatisfiable(totalLength)) return WebViewResourceResponse.RangeNotSatisfiable(totalLength);

        var unsliced = delivery is WebViewRangeDelivery.Unsliced;
        var sent = unsliced ? new WebViewByteRange(range.From, totalLength - 1) : range;
        var body = unsliced ? read(0, totalLength) : read(range.From, range.Length);

        return body is null
            ? WebViewResourceResponse.NotFound()
            : WebViewResourceResponse.PartialContent(body, contentType, sent, totalLength, ContentLength(sent.Length));
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
    /// 🔴 <b>No longer a <c>MemoryStream</c></b>, and that used to be a real cost worth stating rather than
    /// hiding: a 4 GB file answered with <c>bytes=0-</c> was 4 GB of RAM, and under
    /// <see cref="WebViewRangeDelivery.Unsliced"/> the requested window IS the whole file (D44) — so
    /// <b>every <c>UseFiles</c> response on Android allocated the entire file</b>, independent of what a
    /// media player actually asked for. The body is now a <see cref="BoundedBodyStream"/> over an open,
    /// UNDISPOSED <see cref="FileStream"/>: it seeks once, hands the handle to the bound, and never
    /// materialises the window into a byte array at all.
    /// </para>
    /// <para>
    /// ⚠ <b>The <c>using</c> that used to wrap <c>file</c> is gone on purpose.</b> Ownership of the handle
    /// passes to the <see cref="BoundedBodyStream"/>, and from there to whatever the caller hands the
    /// response to — <see cref="WebViewResourceResponse.Content"/>'s own doc says the platform reads it
    /// AFTER this method has returned, so disposing here would close the file before a single byte of it
    /// had actually been sent. <see cref="BoundedBodyStream"/> is what closes it instead, and it does so
    /// under the constraint measured on both mobile shells (2026-08-12): Android disposes a response's
    /// <c>Content</c> after reading it to EOF, iOS never does, so the body has to close itself at EOF and
    /// tolerate a second close from whichever platform still bothers to.
    /// </para>
    /// <para>
    /// 🔴 <b>EVERY MID-READ FAILURE MOVED, NOT ONLY A SHRINKING FILE — and this is worth stating rather than
    /// trying to eliminate, because it cannot be.</b> The old, buffered implementation did <c>OpenRead</c> →
    /// <c>Seek</c> → <c>ReadExactly</c> synchronously INSIDE this method under ONE
    /// <c>catch (Exception) { return null; }</c>, and null is a clean 404 from <see cref="Serve"/> — so
    /// <b>any</b> failure of the actual read was a 404 with nothing yet sent: a file shrunk since
    /// <see cref="Serve"/>'s <see cref="FileInfo.Length"/> stat, a removable volume pulled out, a network
    /// share dropping, an <see cref="IOException"/> straight from the OS, a permission revoked mid-read.
    /// This method now only opens and seeks — it touches no bytes — so <b>all of those now surface once the
    /// PLATFORM pulls from the <see cref="BoundedBodyStream"/></b>, after <see cref="ServeRange"/> has
    /// committed a 200/206 status line and a <c>Content-Length</c> promise built from the stale length. (A
    /// truncation arrives as <see cref="EndOfStreamException"/>; the rest arrive as whatever the OS threw.)
    /// There is no way around this for a genuinely lazy body: knowing a read will succeed before committing
    /// headers means reading it first, which is the buffering this change exists to remove. Real rather than
    /// hypothetical — this repo's own LRU eviction of cached derived artifacts shrinks files, and
    /// <c>UseFiles</c> serves whatever an app points it at, including removable and remote storage.
    /// 🔴 <b>AND WHAT EACH SHELL DOES WITH SUCH A THROW IS THE SHELL'S OWN ANSWER — ONE OF THE THREE IS GOOD
    /// SO FAR.</b> ✅ <b>ANDROID: a page-visible FAILED LOAD (fixed 2026-08-13).</b> Its handover
    /// (<c>MobileWebViewInterceptor</c>) translates the throw into a <c>Java.IO.IOException</c>, whose Java peer
    /// is what <c>InputStreamUtil.read</c>'s <c>catch (IOException)</c> already expects — it returns <c>-2</c>, a
    /// status distinct from <c>-1</c> (EOF) that the native reader turns into a net error, and the app's own log
    /// carries a line naming the failure. Until then the same throw KILLED THE PROCESS, because a managed
    /// exception reaches Java as <c>android.runtime.JavaProxyThrowable</c> (a <c>java.lang.Error</c>, outside
    /// that catch by construction). ⚠ <b>iOS is the opposite and still unfixed</b>: the page receives its
    /// committed <c>200</c> with a body SHORTER than the promise (zero bytes in the measured case) and no error
    /// at all — a separate task, because the answer there is a different mechanism, not the same wrapper.
    /// ⚠ <b>The DESKTOP host is UNMEASURED for the THROW</b> — this method serves there too, through
    /// <c>WebViewHost</c>/WebView2, and nobody has made a body throw at it; its HAPPY path is measured, however
    /// (see <see cref="BoundedBodyStream.Seek"/>). So this paragraph's "there is no way around this for a
    /// genuinely lazy body" is still true; what changed is that on one shell the consequence is now a failed
    /// load rather than a dead app. Whoever takes the remaining two: the fix cannot live here — a body that
    /// discovers the failure has already been handed over — it belongs at the per-platform seam that READS the
    /// body, which is where Android's went (<c>.claude/knowledge/mobile-shells.md</c> has every arm, the raw
    /// logcat and the A/B; <c>TASKS.md</c> has what is left).
    /// </para>
    /// <para>
    /// ⚠ <b>The handle can now stay open for as long as the platform holds the response — including a
    /// request the page ABANDONS before EOF</b> (a seek away, a navigation): a buffered body never had this
    /// problem, since it closed the file before this method's caller even returned. That is why the share
    /// mode below is <see cref="FileShare.Delete"/> alongside the plain <see cref="FileShare.Read"/> a
    /// <see cref="File.OpenRead"/> would give, and deliberately NOT widened to
    /// <see cref="FileShare.ReadWrite"/> — see <c>ComputedRemuxRoute.Answer</c>'s own remarks for the full
    /// reasoning, which applies identically here: allowing a concurrent DELETE is safe (NTFS defers it until
    /// every handle closes, so an in-flight read keeps seeing the same bytes), while allowing a concurrent
    /// WRITE would let a rewrite tear the very bytes this handle is mid-read of.
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
            // Ownership has NOT passed to the platform yet on this path — construction failed, or the
            // seek did — so this is the one place left that must close the handle itself.
            file?.Dispose();
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
