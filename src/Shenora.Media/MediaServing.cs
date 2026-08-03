using Shenora.Core;

namespace Shenora.Media;

/// <summary>
/// How much of the resource a handler must hand to the webview for a ranged request.
/// <para>
/// ⚠ <b>This exists because the two mobile shells need OPPOSITE bodies for the same request</b> — measured
/// on devices, not reasoned about (D44). It is the one media asymmetry a portable contract has to hide.
/// </para>
/// </summary>
public enum MediaBodyMode
{
    /// <summary>
    /// Hand over EXACTLY the requested bytes — ordinary, correct HTTP. Right for iOS (`WKURLSchemeHandler`
    /// passes the body through verbatim) and for the desktop's WebView2 serving.
    /// </summary>
    Sliced,

    /// <summary>
    /// Hand over the WHOLE resource from offset 0 and let the platform skip to the range start itself.
    /// <para>
    /// Right for Android's webview, which applies the <c>Range</c> start to whatever body it is given and
    /// then ignores the range end. ⚠ Slicing there applies the offset TWICE: asking <c>bytes=4-11</c>
    /// returns four bytes of file bytes 8-11, and a player asking for a file's tail gets an empty body and
    /// retries the identical range forever. The trap is that the wrong choice plays every faststart file
    /// perfectly and fails every file whose index sits at the end.
    /// </para>
    /// </summary>
    Unsliced,
}

/// <summary>
/// What an app permits to be served, and how. <b>Fail-closed in both directions</b>: the defaults serve
/// nothing at all, so a handler wired up without configuring this refuses rather than exposing a disk.
/// </summary>
public sealed record MediaServingOptions
{
    /// <summary>
    /// The directories whose files may be served. <b>Empty means nothing is servable</b> — deliberately,
    /// because the alternative default is "the whole filesystem".
    /// <para>
    /// This is the local half of the authorization problem, and it is not theoretical: the media URL
    /// carries a path supplied BY THE PAGE. That is exactly the vector the desktop's static serving was
    /// found exposed to (<c>%2e%2e%2f</c> traversal, and <c>Path.Combine</c> discarding its first argument
    /// when the second is rooted), and the fix is generalised here rather than hand-rolled a second time.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];

    /// <summary>
    /// Whether a REMOTE source may be fetched on the page's behalf. <b>Null denies everything</b>, which is
    /// the fail-closed stance the navigation guard already takes.
    /// <para>
    /// A page that can hand the host an arbitrary URL to fetch is an SSRF surface: the host reaches
    /// addresses the page cannot, including link-local metadata endpoints and anything on the LAN. So this
    /// is opt-in per URL, and an app that only ever serves local files never sets it.
    /// </para>
    /// </summary>
    public Func<Uri, bool>? AllowRemote { get; init; }

    /// <summary>How much of the resource to hand over — see <see cref="MediaBodyMode"/>. Defaults to the
    /// correct-HTTP behaviour; Android's shell must override it.</summary>
    public MediaBodyMode BodyMode { get; init; } = MediaBodyMode.Sliced;
}

/// <summary>
/// The authorization half of media serving: deciding whether a page-supplied source may be served at all.
/// <para>
/// Separate from <see cref="MediaRangeServer"/> on purpose. Authorization is a pure decision over strings
/// and options, so it is exhaustively testable without a filesystem — and a security check that can only
/// be exercised through a live webview is a security check nobody exercises.
/// </para>
/// </summary>
public static class MediaAccess
{
    /// <summary>
    /// Resolve a page-supplied LOCAL path to a real, allowed absolute path, or return null to refuse.
    /// <para>
    /// Generalised from the desktop provider's <c>ResolveContained</c>, keeping every check it earned and
    /// adding the one this case needs: the page here supplies an ABSOLUTE path, so the question is not
    /// "is this relative path inside my root" but "is this absolute path inside one of the roots the app
    /// declared". The order matters:
    /// </para>
    /// <list type="number">
    /// <item>Refuse traversal segments BEFORE touching the filesystem.</item>
    /// <item>Resolve to a full path, refusing anything malformed rather than guessing.</item>
    /// <item>Require the result to sit under an allowed root, <b>comparing with the separator appended</b> —
    /// without it, <c>/media-evil</c> passes as a child of <c>/media</c>.</item>
    /// </list>
    /// <para>
    /// ⚠ Returns null for every refusal and never says why. The caller answers 404 with a fixed body: a
    /// distinguishable "forbidden" reply tells a page whether a path exists, which is the existence leak
    /// the desktop's <c>Exists</c> check had to be fixed for.
    /// </para>
    /// </summary>
    public static string? ResolveLocal(string? requestedPath, MediaServingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(requestedPath)) return null;
        if (options.AllowedRoots.Count == 0) return null;   // fail closed

        // Traversal is refused before the filesystem is consulted. A `..` that resolves back inside a root
        // would pass the containment test below, and allowing it means the URL shape is no longer the thing
        // being authorised.
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

        foreach (var root in options.AllowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(root);
            }
            catch (Exception)
            {
                continue;   // a malformed ROOT disqualifies itself, it does not disqualify the request
            }

            var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;

            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return full;
        }

        return null;
    }

    /// <summary>
    /// Whether a REMOTE source may be fetched on the page's behalf. Fail-closed: no policy means no.
    /// <para>
    /// The predicate is the app's, because only the app knows which hosts are legitimate for it. What the
    /// kit guarantees is the DEFAULT — that forgetting to configure this denies rather than permits, which
    /// is the difference between a missing feature and an SSRF hole.
    /// </para>
    /// </summary>
    public static bool IsRemoteAllowed(Uri? source, MediaServingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (source is null) return false;
        if (options.AllowRemote is not { } allow) return false;   // fail closed

        // The app's predicate is app code reached from a place with no caller on the stack, so a throw here
        // must not become an ALLOW. Refusing on error is the only safe reading of "the policy did not say
        // yes" — the same stance AppCallback takes for guarded callbacks.
        try
        {
            return allow(source);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Turns a resource request for a media file into the response a player needs: <c>200</c>, <c>206</c> or
/// <c>416</c>, with <c>Content-Range</c>, <c>Accept-Ranges</c> and <c>Content-Length</c> that describe what
/// is actually being sent.
/// <para>
/// This is the piece that makes the whole media design work — "a URL a <c>&lt;video&gt;</c> element can
/// play" — and it is deliberately the only part that touches a file, so everything else here stays pure.
/// Proven end to end on an Android emulator and an iOS simulator before it was written as library code
/// (D44).
/// </para>
/// </summary>
public static class MediaRangeServer
{
    /// <summary>
    /// Serve <paramref name="path"/> in answer to <paramref name="request"/>.
    /// <para>
    /// The caller has already authorised the path (<see cref="MediaAccess.ResolveLocal"/>) — this method
    /// does not re-check, because a security check in two places drifts in one of them.
    /// </para>
    /// <para>
    /// ⚠ <b>The response's <c>Content-Range</c> describes what is really sent, which under
    /// <see cref="MediaBodyMode.Unsliced"/> is <c>{from}</c>→EOF rather than the range that was asked
    /// for.</b> That is not a lie to the client: the platform truncates the front of the body itself and
    /// streams the rest, so a header describing the requested end would be the inaccurate one.
    /// </para>
    /// </summary>
    /// <param name="request">The intercepted request; its <c>Range</c> header drives everything.</param>
    /// <param name="path">An ALREADY-AUTHORISED absolute path.</param>
    /// <param name="contentType">The MIME type to report. The app's call — it knows its own catalogue.</param>
    /// <param name="options">Serving options; only <see cref="MediaServingOptions.BodyMode"/> is read here.</param>
    /// <returns>The response to hand to the webview, or a fixed 404 when the file cannot be read.</returns>
    public static WebViewResourceResponse Serve(WebViewResourceRequest request, string path,
                                                string contentType, MediaServingOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(options);

        long length;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return WebViewResourceResponse.NotFound();
            length = info.Length;
        }
        catch (Exception)
        {
            // No exception text on the wire, ever: page script can read a response body, and a media
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

        // A start past the end is unsatisfiable, and 416 must carry `bytes */length` so the client learns
        // the real size. Omitting it is what leaves a player retrying the same bad range forever.
        if (!range.IsSatisfiable(length)) return WebViewResourceResponse.RangeNotSatisfiable(length);

        // Unsliced: the body is everything, and the headers describe from→EOF because that is what the
        // platform will deliver after skipping. Sliced: exactly the requested window.
        var unsliced = options.BodyMode is MediaBodyMode.Unsliced;
        var sent = unsliced ? new WebViewByteRange(range.From, length - 1) : range;
        var body = unsliced ? Read(path, 0, length) : Read(path, range.From, range.Length);

        return body is null
            ? WebViewResourceResponse.NotFound()
            : WebViewResourceResponse.PartialContent(body, contentType, sent, length, ContentLength(sent.Length));
    }

    /// <summary>
    /// <c>Content-Length</c> as an explicit header rather than something inferred from the stream.
    /// <para>
    /// It has to be explicit for the unsliced case: the body handed over is the whole file while the
    /// client will receive <c>length - from</c> bytes, so a value derived from the stream would advertise
    /// more than arrives.
    /// </para>
    /// </summary>
    private static Dictionary<string, string> ContentLength(long bytes) =>
        new(StringComparer.OrdinalIgnoreCase) { ["Content-Length"] = bytes.ToString() };

    /// <summary>
    /// Read a window of the file into memory, or null if it cannot be read.
    /// <para>
    /// ⚠ A <c>MemoryStream</c>, and that is a real limit worth stating rather than hiding: a 4 GB file
    /// answered with <c>bytes=0-</c> is 4 GB of RAM. It is acceptable here only because a media player
    /// asks for small windows once it knows the length — the observed pattern on iOS is dozens of requests
    /// of tens to hundreds of bytes. A streaming implementation is the obvious improvement, and it needs a
    /// bounded stream whose lifetime the webview owns; the seam takes a <c>Stream</c> precisely so that can
    /// be added without changing this signature.
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
