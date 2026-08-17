namespace Shenora.Core.WebView;

/// <summary>
/// How a platform's webview delivers the body of a ranged response — the shells disagree (D44).
/// <para>
/// ⚠ Getting it wrong is not a graceful degradation: the wrong choice serves correct-looking bytes at the
/// wrong offset, which plays every faststart file perfectly and fails every file whose index sits at the
/// end.
/// </para>
/// </summary>
public enum WebViewRangeDelivery
{
    /// <summary>
    /// The platform sends exactly the bytes it is given — ordinary, correct HTTP, so a handler answering a
    /// <c>Range</c> must slice. True of WebView2 and of iOS's <c>WKURLSchemeHandler</c>.
    /// </summary>
    Sliced,

    /// <summary>
    /// The platform applies the <c>Range</c> START to whatever body it is given and ignores the range end,
    /// so a handler must hand over the WHOLE resource from offset 0 and let the platform skip. True of
    /// Android's webview.
    /// <para>
    /// ⚠ Slicing anyway applies the offset TWICE: asking <c>bytes=4-11</c> returns four bytes of file bytes
    /// 8-11, and a player asking for a file's tail receives an empty body and retries forever.
    /// </para>
    /// <para>
    /// 🔴 <b>THE SKIP IS A READ, AND IT CANNOT BE MADE A SEEK FROM THIS SIDE.</b> Measured: a
    /// <c>Range: bytes=0-65535</c> on a 79 MiB film reads 82,843,185 bytes in 117,285 reads, 26–31 s. The
    /// obvious fix — make the body seekable so the platform skips cheaply — was checked against the
    /// platform and REFUTED: <c>Android.Runtime.InputStreamAdapter</c> (the Stream→InputStream binding,
    /// decompiled from <c>Mono.Android.dll</c> 36.1.69) overrides only <c>Read</c>, so <c>skip()</c> falls
    /// through to <c>java.io.InputStream</c>'s default, which reads into a throwaway buffer. The adapter
    /// names neither <c>CanSeek</c> nor <c>Seek</c> anywhere in its IL. A seekable
    /// <see cref="Shenora.Core.WebView.BoundedBodyStream"/> would therefore change nothing and add a state
    /// the three shells do not expect.
    /// </para>
    /// <para>
    /// ⚠ <b>Do not "fix" it by serving cheap filler for the discarded prefix either</b>, tempting as it is
    /// (the platform throws those bytes away, so zeros would do). It stakes correctness on the delivery
    /// model being exactly "skip N, then stream" forever, and the failure mode if that ever changes is
    /// this enum's own worst case: correct-looking bytes at the wrong offset, silently.
    /// </para>
    /// </summary>
    Unsliced,
}

/// <summary>
/// The terminal step of a resource pipeline: answer this request, or return null to decline it.
/// </summary>
/// <returns>A response, or null to let the platform handle the request normally.</returns>
public delegate Task<WebViewResourceResponse?> WebViewResourceHandler(
    WebViewResourceRequest request, CancellationToken cancellationToken);

/// <summary>
/// One step in a resource pipeline: inspect the request, optionally answer it, or pass it to
/// <paramref name="next"/> — and optionally post-process what comes back.
/// </summary>
public delegate Task<WebViewResourceResponse?> WebViewResourceMiddleware(
    WebViewResourceRequest request, WebViewResourceHandler next, CancellationToken cancellationToken);

/// <summary>
/// A host that can answer its webview's resource requests — the seam a feature uses to serve bytes without
/// knowing which webview it is talking to. Implemented once per shell (<c>Shenora.Windows</c>,
/// <c>Shenora.Android</c>, <c>Shenora.iOS</c>).
/// <para>
/// A page cannot reach a local file directly on ANY shell — <c>file://</c> is blocked from a virtual-host
/// origin — so an interceptor is how local content reaches a page at all.
/// </para>
/// <para>
/// ⚠ Middleware that turns a request path into a FILE path must resolve it through
/// <see cref="WebViewFiles.ResolveContained"/>. A hand-rolled check misses <c>%2e%2e%2f</c> traversal and
/// <c>Path.Combine</c> silently discarding its first argument when the second is rooted — both serve the
/// wrong file successfully, with nothing logged.
/// </para>
/// </summary>
public interface IWebViewInterceptor
{
    /// <summary>
    /// How this platform delivers a ranged body — a fact about the platform, not a preference.
    /// <b>A handler must honour it</b>; see <see cref="WebViewRangeDelivery"/> for what goes wrong
    /// otherwise.
    /// </summary>
    WebViewRangeDelivery RangeDelivery { get; }

    /// <summary>
    /// Add a middleware to the pipeline. Middleware run in registration order, each able to answer, decline,
    /// or delegate to the next; the platform handles anything the whole pipeline declines. Dispose the
    /// return value to remove it — a route that outlives the page it served would answer for the next one.
    /// </summary>
    /// <param name="middleware">
    /// ⚠ Runs on whatever thread the platform raises its event on, and must not block: the webview cannot
    /// paint while it waits. Return quickly, or return a response whose stream produces bytes lazily — the
    /// platforms support a deferred body for exactly this.
    /// <para>
    /// 🔴 <b>On the MOBILE shells the cost of blocking is not a pause, it is a DEADLOCK.</b> Both mobile
    /// platforms need the status line and headers by the time the platform event returns, so the shell
    /// (<c>MobileWebViewInterceptor</c>) resolves this pipeline SYNCHRONOUSLY
    /// (<c>GetAwaiter().GetResult()</c>) on the main thread. A middleware that <c>await</c>s anything whose
    /// continuation is posted back to that thread — any <c>await</c> without <c>ConfigureAwait(false)</c> —
    /// therefore waits on a thread only it could free. ⚠ The symptom names nothing: on iOS the app stays
    /// alive, the request is the last line it ever logs, and every later native evaluation simply never
    /// answers.
    /// </para>
    /// <para>
    /// So: do the slow work BEFORE the request (warm a cache at startup), or hand back a lazy body and do
    /// it as the platform reads; <c>ConfigureAwait(false)</c> on any await inside the middleware itself.
    /// </para>
    /// </param>
    IDisposable Use(WebViewResourceMiddleware middleware);
}
