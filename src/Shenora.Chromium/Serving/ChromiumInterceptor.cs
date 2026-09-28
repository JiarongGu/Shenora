using Shenora.Core.WebView;

namespace Shenora.Chromium.Serving;

/// <summary>
/// The Chromium shell's side of the resource pipeline (D45), so an app's <c>UseFiles</c> and its own
/// routes reach a Chromium window unchanged. CEF sends exactly the bytes it is given, so a handler
/// answering a <c>Range</c> slices, as it does on WebView2.
/// </summary>
internal sealed class ChromiumInterceptor : IWebViewInterceptor
{
    private readonly WebViewResourcePipeline _pipeline = new();

    public WebViewRangeDelivery RangeDelivery => WebViewRangeDelivery.Sliced;

    public IDisposable Use(WebViewResourceMiddleware middleware) => _pipeline.Use(middleware);

    /// <summary>The pipeline's answer; a null result is a decline (the pipeline's own terminal), as is an empty pipeline.</summary>
    public Task<WebViewResourceResponse?> Handle(WebViewResourceRequest request, CancellationToken cancellationToken) =>
        _pipeline.Build() is { } handler ? handler(request, cancellationToken) : Task.FromResult<WebViewResourceResponse?>(null);
}
