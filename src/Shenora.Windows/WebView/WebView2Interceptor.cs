using Shenora;
using Shenora.Core.WebView;

namespace Shenora.Windows;

/// <summary>
/// The desktop shell's <see cref="IWebViewInterceptor"/> — the same middleware pipeline the mobile shells
/// expose, over WebView2.
/// <para>
/// <b>Why the desktop needs one at all.</b> It is tempting to read interception as a mobile workaround, since
/// that is where it was first needed; it is not. A page cannot reach a local file on Windows either —
/// <c>file://</c> is refused from a virtual-host origin — so serving local content to the page is interception
/// here too, and it has been all along: <see cref="WebViewDeferredScheme"/> is that mechanism with an
/// app-shaped API. What this adds is the SHARED contract (D45): one <c>UseFiles</c> route, one containment
/// check, one range-delivery rule, written once and correct on three shells instead of once per app.
/// </para>
/// <para>
/// Obtained from <see cref="WebViewHost.Interceptor"/> rather than constructed: it must be wired into the
/// host's ONE <c>WebResourceRequested</c> subscription. A second subscription assigning <c>args.Response</c>
/// is last-writer-wins by subscription order, which is not a contract anything should rest on — the same
/// reasoning that made <c>SessionBrowser.DecideRequest</c> one decision instead of two handlers.
/// </para>
/// </summary>
public sealed class WebView2Interceptor : IWebViewInterceptor
{
    private readonly WebViewResourcePipeline _pipeline = new();

    internal WebView2Interceptor() { }

    /// <inheritdoc />
    /// <remarks>
    /// MEASURED on this platform, not inferred from the other two. Setting this to
    /// <see cref="WebViewRangeDelivery.Unsliced"/> and running the sample's <c>InterceptorProbe</c> answers
    /// <c>Range: bytes=3-7</c> with the whole file from offset 3 — and the page reads back 1000 bytes starting
    /// at <c>A</c>, not at <c>D</c>. WebView2 therefore does NOT apply the offset itself: it sends exactly the
    /// body it is handed, so a handler must slice. (Restored to <c>Sliced</c>, the same probe reads
    /// <c>DEFGH</c>.) Android's webview does the opposite, which is the whole reason this is a property
    /// rather than a constant (D44).
    /// </remarks>
    public WebViewRangeDelivery RangeDelivery => WebViewRangeDelivery.Sliced;

    /// <inheritdoc />
    public IDisposable Use(WebViewResourceMiddleware middleware) => _pipeline.Use(middleware);

    /// <summary>
    /// Whether anything is registered — an array-length read, cheap enough for the host's shared handler to
    /// ask on every request before it does anything else.
    /// </summary>
    internal bool HasRoutes => !_pipeline.IsEmpty;

    /// <summary>Null when no route is registered — the host's fast path out of the shared handler.</summary>
    internal WebViewResourceHandler? Build() => _pipeline.Build();

    internal void Clear() => _pipeline.Clear();

    /// <summary>
    /// The <c>WebResourceRequested</c> filter patterns the interceptor needs BEYOND the ones the bundle and the
    /// deferred schemes already register. Pure and tested, because it is the whole answer to "which requests can
    /// a middleware even see" and getting it wrong is silent — a route that works in production and 404s in dev.
    /// <para>
    /// <b>The rule: the interceptor sees the PAGE'S OWN ORIGIN.</b> That is what D44 settled — a relative URL on
    /// the page's own origin is the one form intercepted on all three shells — so it is the origin that has to be
    /// filtered. In production that origin is the bundle's virtual host, whose pattern is already registered; in
    /// development it is the Vite server, which is not, and without this a media route would work in a packaged
    /// build and 404 during every day of development.
    /// </para>
    /// <para>
    /// ⚠ A <see cref="WebViewHostOptions.ProductionUrl"/> origin is deliberately NOT filtered. That profile puts a
    /// real in-process HTTP server behind the page, and Kestrel already serves files with correct ranges; letting
    /// middleware shadow its routes would mean two servers for one origin, silently disagreeing. Nor is a blanket
    /// <c>"*"</c> used: it raises the event for every request the page makes, including ones on the open internet.
    /// </para>
    /// </summary>
    internal static string[] ExtraFilters(bool isDevelopment, string? devUrl)
    {
        if (!isDevelopment || string.IsNullOrWhiteSpace(devUrl)) return [];
        // The ORIGIN, not the configured string: a DevUrl carrying a path ("http://localhost:3517/index.html")
        // would otherwise produce a filter that matches only that one document.
        return Uri.TryCreate(devUrl, UriKind.Absolute, out var parsed)
            ? [parsed.GetLeftPart(UriPartial.Authority) + "/*"]
            : [];
    }
}
