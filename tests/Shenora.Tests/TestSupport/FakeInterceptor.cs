using Shenora.Core.WebView;

namespace Shenora.Tests.TestSupport;

/// <summary>
/// A fake interceptor: no webview, just the pipeline the shells share — so a route's composition, its
/// fall-through and its range answers are all provable with nothing platform-specific in the test.
/// <para>
/// ⚠ <b>The ONE owner of this harness, after it had been written twice</b> (<c>MediaConversionTests</c> and
/// <c>SegmentStreamTests</c> each carried a private copy). Two copies is how the two drift, and the drift
/// that mattered here is <see cref="RangeDelivery"/>: both copies hard-coded
/// <see cref="WebViewRangeDelivery.Sliced"/>, so no route test could ever exercise the ANDROID rule (D44) —
/// the one whose failure mode is silent, correct-looking bytes at the wrong offset. Same reasoning as
/// <see cref="TempDir"/>: the shared version is the one that can be made better once.
/// </para>
/// </summary>
/// <param name="delivery">
/// The platform fact a route must honour. Defaults to <see cref="WebViewRangeDelivery.Sliced"/> (WebView2 and
/// iOS); pass <see cref="WebViewRangeDelivery.Unsliced"/> to drive a route the way Android's webview does.
/// </param>
internal sealed class FakeInterceptor(WebViewRangeDelivery delivery = WebViewRangeDelivery.Sliced)
    : IWebViewInterceptor
{
    private readonly WebViewResourcePipeline _pipeline = new();

    /// <inheritdoc />
    public WebViewRangeDelivery RangeDelivery { get; } = delivery;

    /// <inheritdoc />
    public IDisposable Use(WebViewResourceMiddleware middleware) => _pipeline.Use(middleware);

    /// <summary>
    /// Drive the pipeline with one request. Null comes back when the whole pipeline DECLINED it — which is
    /// what "the platform would have served this" looks like from a test, and the assertion a fall-through
    /// case makes.
    /// </summary>
    /// <param name="url">The absolute request URL.</param>
    /// <param name="range">
    /// A raw <c>Range</c> header value (<c>bytes=0-99</c>), or null for a request that carries none. The
    /// header is what a media element's seek IS, so a route test that cannot send one cannot test seeking.
    /// </param>
    public Task<WebViewResourceResponse?> AskAsync(string url, string? range = null) =>
        _pipeline.Build() is { } handler
            ? handler(new WebViewResourceRequest
            {
                Uri = new Uri(url),
                Method = "GET",
                Headers = range is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Range"] = range },
            }, CancellationToken.None)
            : Task.FromResult<WebViewResourceResponse?>(null);
}
