namespace Shenora.Core;

/// <summary>
/// How a platform's webview delivers the body of a ranged response.
/// <para>
/// ⚠ <b>This is a property of the INTERCEPTION, not of the content.</b> It lives here rather than beside any
/// feature because it is the same fact whether the bytes are video, audio, an image, a PDF or a generated
/// export: the platform either sends the body you hand it, or it skips into that body itself.
/// </para>
/// <para>
/// It exists because the shells disagree, measured on devices (D44). Getting it wrong is not a graceful
/// degradation — the wrong choice serves correct-looking bytes at the wrong offset, which plays every
/// faststart file perfectly and fails every file whose index sits at the end.
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
/// <para>
/// Middleware rather than a flat list of handlers <b>because the cross-cutting concerns are the point.</b>
/// Path containment, an SSRF guard, a cache, a log of what a payload decoded to, a metric — each is a layer
/// that wraps the next rather than a terminal answer, and expressing them as separate middleware is what
/// stops every route re-implementing them. The kit already made this choice once, for the same reason:
/// <c>IMessageDispatcher</c> is a composable middleware pipeline over one transport, and this is that shape
/// applied to resources instead of messages.
/// </para>
/// <para>
/// Today the only consumer is media. That is deliberately not what the contract is shaped around — local
/// file access, generated images, exports and thumbnails are the same problem, and a media-shaped seam
/// would have to be broken to admit the second one.
/// </para>
/// </summary>
public delegate Task<WebViewResourceResponse?> WebViewResourceMiddleware(
    WebViewResourceRequest request, WebViewResourceHandler next, CancellationToken cancellationToken);

/// <summary>
/// A host that can answer its webview's resource requests — the seam a feature uses to serve bytes without
/// knowing which webview it is talking to.
/// <para>
/// Implemented once per shell (<c>Shenora.Windows</c>, <c>Shenora.Android</c>, <c>Shenora.iOS</c>), because
/// intercepting a request configures a WEBVIEW: that is a shell capability, not a feature's. A feature —
/// media, an export, a generated image — depends on this contract and stays portable, which is why it lives
/// in <c>Shenora.Core</c> beside the request and response types it speaks (D19/D20).
/// </para>
/// <para>
/// <b>Every shell needs it, not just the mobile ones.</b> A page cannot reach a local file directly on any of
/// them: <c>file://</c> is blocked from a virtual-host origin, and it would be the wrong answer even if it
/// were not, because it hands the page the whole filesystem. So an interceptor is how local content reaches a
/// page AT ALL — and routing every shell through one contract means the path-containment check is written
/// once rather than three times. A hand-rolled containment check is precisely the defect this kit already had
/// to fix once (<c>%2e%2e%2f</c> traversal, and <c>Path.Combine</c> discarding its first argument on a rooted
/// path).
/// </para>
/// <para>
/// Data in, data out (<see cref="WebViewResourceRequest"/> → <see cref="WebViewResourceResponse"/>) rather
/// than a platform event, so one pipeline compiles on every shell and can be unit-tested with no webview.
/// </para>
/// </summary>
public interface IWebViewInterceptor
{
    /// <summary>
    /// How this platform delivers a ranged body. <b>A handler must honour it</b> — see
    /// <see cref="WebViewRangeDelivery"/> for what goes wrong otherwise.
    /// <para>
    /// A property rather than something the caller configures: it is a fact about the platform, not a
    /// preference, and letting an app supply its own value is how a setting copied from another shell breaks
    /// one of them silently.
    /// </para>
    /// </summary>
    WebViewRangeDelivery RangeDelivery { get; }

    /// <summary>
    /// Add a middleware to the pipeline. Middleware run in registration order, each able to answer, decline,
    /// or delegate to the next; the platform handles anything the whole pipeline declines.
    /// <para>
    /// Dispose the return value to remove it — a route that outlives the page it served would answer for the
    /// next one, which is the same class of bug as a subscribe API on a pooled object.
    /// </para>
    /// </summary>
    /// <param name="middleware">
    /// ⚠ Runs on whatever thread the platform raises its event on, and must not block: the webview cannot
    /// paint while it waits. Return quickly, or return a response whose stream produces bytes lazily — the
    /// platforms support a deferred body for exactly this.
    /// </param>
    IDisposable Use(WebViewResourceMiddleware middleware);
}
