using Microsoft.Maui.Controls;
using Shenora.Core;

namespace Shenora.Mobile;

/// <summary>
/// The mobile shell's <see cref="IWebViewInterceptor"/> — a middleware pipeline over MAUI's
/// <c>HybridWebView.WebResourceRequested</c>.
/// <para>
/// Interception lives HERE, in the shell, rather than in any feature package: it configures a webview, which
/// is a shell capability (D45). A feature — media, an export, a generated image — adds middleware and stays
/// portable.
/// </para>
/// <para>
/// Shared source, so this one file is the Android AND the iOS implementation. The only thing that differs is
/// <see cref="RangeDelivery"/>, and it differs because the platforms genuinely do.
/// </para>
/// </summary>
public sealed class MobileWebViewInterceptor : IWebViewInterceptor, IDisposable
{
    private readonly HybridWebView _webView;
    private readonly Action<string>? _log;
    // The registry and composition are PORTABLE and shared with the desktop shell — see
    // WebViewResourcePipeline for why this is not hand-rolled per shell. All that is left here is the glue.
    private readonly WebViewResourcePipeline _pipeline = new();
    private bool _disposed;

    /// <param name="webView">The webview to intercept. Its <c>WebResourceRequested</c> is subscribed here.</param>
    /// <param name="log">Optional diagnostics. Guarded — a throwing sink must not break serving.</param>
    public MobileWebViewInterceptor(HybridWebView webView, Action<string>? log = null)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _log = log;
        _webView.WebResourceRequested += OnWebResourceRequested;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ A compile-time constant per platform, measured on devices (D44) — not a setting, and not something
    /// an app may override.
    /// </remarks>
    public WebViewRangeDelivery RangeDelivery =>
#if ANDROID
        // Android's webview applies the Range START to whatever body it is handed, and ignores the range end.
        // Slicing anyway applies the offset twice: bytes=4-11 came back as four bytes of file bytes 8-11, and
        // a player asking for a file's tail got an empty body and retried the identical range forever.
        WebViewRangeDelivery.Unsliced;
#elif IOS || MACCATALYST
        // WKURLSchemeHandler passes the body through verbatim, so the handler slices — ordinary correct HTTP.
        WebViewRangeDelivery.Sliced;
#else
        // A hard COMPILE error, not a default. A new platform must DECIDE this, measured on a device, because
        // the wrong answer plays every faststart file perfectly and fails every other one — the same
        // fail-closed reasoning as the partial method that stopped a fourth shell shipping an undefined save.
#error Shenora.Mobile: this platform has not declared its WebViewRangeDelivery. Measure it on a device (serve a file whose mp4 index sits at the END and see whether a sliced body plays) — do not guess.
#endif

    /// <inheritdoc />
    public IDisposable Use(WebViewResourceMiddleware middleware)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pipeline.Use(middleware);
    }

    private void OnWebResourceRequested(object? sender, WebViewWebResourceRequestedEventArgs e)
    {
        // Null = no routes registered, so this shell costs nothing beyond the event itself.
        if (_pipeline.Build() is not { } handler) return;

        WebViewResourceResponse? response;
        try
        {
            var request = new WebViewResourceRequest
            {
                Uri = e.Uri,
                Method = e.Method,
                Headers = e.Headers,
            };
            response = Run(handler, request);
        }
        catch (Exception ex)
        {
            // A throwing middleware must not become an unhandled exception on the platform's event thread,
            // and must not leak its text to the page: page script can read a response body, and a file
            // handler's failure detail is the likeliest of all of them to carry a real path.
            Log(() => $"[Shenora.Mobile] Resource middleware failed: {ex}");
            // The kit's ONE fixed 404 rather than a hand-built reply, so a failure here is
            // indistinguishable from a missing file and carries no detail. ⚠ Not `SetResponse(…, null, …)`:
            // a null headers argument is AMBIGUOUS between the dictionary and content-type overloads
            // (CS0121), which is a compile error worth meeting once and never again.
            var refusal = WebViewResourceResponse.NotFound();
            e.SetResponse(refusal.StatusCode, refusal.ReasonPhrase, refusal.Headers, refusal.Content);
            e.Handled = true;
            return;
        }

        // Nothing claimed it — leave `Handled` alone so the platform serves it normally (the bundle).
        if (response is null) return;

        // ⚠ The PORTABLE overload, with a header dictionary. `e.PlatformArgs` is not needed on either
        // platform: every header reaches the native response, verified on devices after this repo spent a
        // session believing otherwise (D44).
        e.SetResponse(response.StatusCode, response.ReasonPhrase, response.Headers, response.Content);
        e.Handled = true;
    }

    /// <summary>
    /// Run the composed pipeline.
    /// <para>
    /// ⚠ Resolved SYNCHRONOUSLY, and that is forced rather than chosen: both mobile platforms need the status
    /// line and headers at the moment the event returns, so the metadata cannot be awaited. (The desktop shell
    /// has a deferral and therefore does NOT have to do this — the one real difference between the two
    /// implementations of this contract.) Laziness belongs in the BODY — the response carries a
    /// <c>Stream</c> the platform reads afterwards — which is why middleware must not do slow work before
    /// returning. Documented on <see cref="IWebViewInterceptor.Use"/>.
    /// </para>
    /// </summary>
    private static WebViewResourceResponse? Run(WebViewResourceHandler handler, WebViewResourceRequest request)
        => handler(request, CancellationToken.None).GetAwaiter().GetResult();

    private void Log(Func<string> message) => AppCallback.Log(_log, message);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _webView.WebResourceRequested -= OnWebResourceRequested; }
        catch (Exception ex) { Log(() => $"[Shenora.Mobile] Interceptor dispose: {ex.Message}"); }
        _pipeline.Clear();
    }
}
