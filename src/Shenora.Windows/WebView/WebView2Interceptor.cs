using Shenora;
using Shenora.Core.WebView;

namespace Shenora.Windows;

/// <summary>
/// The desktop shell's <see cref="IWebViewInterceptor"/> — the same middleware pipeline the mobile shells
/// expose (D45), over WebView2.
/// <para>
/// ⚠ Obtained from <see cref="WebViewHost.Interceptor"/> rather than constructed: it must be wired into
/// the host's ONE <c>WebResourceRequested</c> subscription, because a second subscription assigning
/// <c>args.Response</c> is last-writer-wins by subscription order.
/// </para>
/// </summary>
internal sealed class WebView2Interceptor : IWebViewInterceptor
{
    private readonly WebViewResourcePipeline _pipeline = new();

    internal WebView2Interceptor() { }

    /// <inheritdoc />
    /// <remarks>
    /// 🔴 WebView2 sends exactly the body it is handed and does NOT apply the <c>Range</c> offset itself,
    /// so a handler must SLICE. Android's webview does the opposite, which is why this is a property
    /// rather than a constant (D44); getting it wrong applies the offset twice and a player asking for a
    /// file's tail retries the identical range for ever. Measured with the sample's
    /// <c>InterceptorProbe</c>.
    /// </remarks>
    public WebViewRangeDelivery RangeDelivery => WebViewRangeDelivery.Sliced;

    /// <inheritdoc />
    public IDisposable Use(WebViewResourceMiddleware middleware) => _pipeline.Use(middleware);

    /// <summary>
    /// Whether anything is registered — an array-length read, cheap enough for the host's shared handler
    /// to ask on every request before it does anything else.
    /// </summary>
    internal bool HasRoutes => !_pipeline.IsEmpty;

    /// <summary>Null when no route is registered — the host's fast path out of the shared handler.</summary>
    internal WebViewResourceHandler? Build() => _pipeline.Build();

    internal void Clear() => _pipeline.Clear();

    /// <summary>
    /// The <c>WebResourceRequested</c> filter patterns the interceptor needs BEYOND the ones the bundle and
    /// the deferred schemes already register.
    /// <para>
    /// 🔴 The rule is that the interceptor sees the PAGE'S OWN ORIGIN (D44). In production that is the
    /// bundle's virtual host, already registered; in development it is the Vite server, which is not —
    /// and without this a route works in a packaged build and 404s all through development.
    /// </para>
    /// <para>
    /// ⚠ A <see cref="WebViewHostOptions.ProductionUrl"/> origin is deliberately NOT filtered: a real
    /// in-process HTTP server is behind it, and shadowing its routes means two servers for one origin.
    /// Nor is a blanket <c>"*"</c> used — it raises the event for every request the page makes, the open
    /// internet included.
    /// </para>
    /// </summary>
    internal static string[] ExtraFilters(bool isDevelopment, string? devUrl)
    {
        if (!isDevelopment || string.IsNullOrWhiteSpace(devUrl)) return [];
        // The ORIGIN, not the configured string: a DevUrl carrying a path would otherwise produce a
        // filter matching only that one document.
        return Uri.TryCreate(devUrl, UriKind.Absolute, out var parsed)
            ? [parsed.GetLeftPart(UriPartial.Authority) + "/*"]
            : [];
    }
}
