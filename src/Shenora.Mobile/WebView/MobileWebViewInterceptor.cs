using Microsoft.Maui.Controls;
using Shenora;
using Shenora.Core.WebView;

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
        // Null = no routes registered, so this shell costs nothing beyond the event itself — except the
        // platform repair, which must still run. ⚠ The defect it repairs belongs to the PLATFORM, and the
        // adopter's A/B proved it fires with no interceptor constructed at all: gating it on the pipeline
        // being non-empty would leave it working only for apps that happen to serve something.
        if (_pipeline.Build() is not { } handler)
        {
            Answer(e, RepairDocumentRequest(e.Uri));
            return;
        }

        WebViewResourceResponse? response;
        try
        {
            var request = new WebViewResourceRequest
            {
                Uri = e.Uri,
                Method = e.Method,
                Headers = e.Headers,
            };
            // The app's middleware get first refusal — the repair is a LAST resort, so an app that serves
            // its own document keeps doing so and never meets this at all.
            response = Run(handler, request) ?? RepairDocumentRequest(e.Uri);
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

        Answer(e, response);
    }

    /// <summary>
    /// Hand a response to the platform, or leave the event untouched so it serves the request itself.
    /// </summary>
    private static void Answer(WebViewWebResourceRequestedEventArgs e, WebViewResourceResponse? response)
    {
        // Nothing claimed it — leave `Handled` alone so the platform serves it normally (the bundle).
        if (response is null) return;

        // ⚠ The PORTABLE overload, with a header dictionary. `e.PlatformArgs` is not needed on either
        // platform: every header reaches the native response, verified on devices after this repo spent a
        // session believing otherwise (D44).
        e.SetResponse(response.StatusCode, response.ReasonPhrase, PlatformHeaders(response.Headers), response.Content);
        e.Handled = true;
    }

    /// <summary>
    /// The kit's repair for a platform that cannot serve its own bundle for a given request shape. Null —
    /// "nothing to repair" — for every request on every platform except the one measured case below.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Android maps a top-level <c>#fragment</c> URL onto an asset name and answers 404</b>, so every
    /// hash-routed page dies on RELOAD with <c>net::ERR_INVALID_RESPONSE</c>. The measurement, the table and
    /// the reason iOS is excluded are on <see cref="WebViewResourceRequest.IsRootWithFragment"/>; what
    /// matters here is that this is the ONE place that can fix it. The request fails before any script runs,
    /// so a page cannot; abandoning hash routing is not a fix; and the interceptor is the only seam that
    /// sees the document request while it can still be answered.
    /// </para>
    /// <para>
    /// It answers the SAME bytes the platform would have served for the fragment-free URL —
    /// <c>HybridRoot/DefaultFile</c>, read from the app package — so the repair is invisible: the page boots
    /// exactly as it does on a first load, and its router reads the fragment off <c>location</c> as usual.
    /// </para>
    /// <para>
    /// <b>It declines rather than 404s when the bundle cannot be read.</b> An app that serves its document
    /// some other way must be left exactly as it was; turning a working page into a fixed 404 to repair a
    /// defect it does not have would be the worse failure by far.
    /// </para>
    /// </remarks>
    private WebViewResourceResponse? RepairDocumentRequest(Uri uri)
    {
#if ANDROID
        if (!WebViewResourceRequest.IsRootWithFragment(uri)) return null;

        // 🔴 THE PATH IS BUILT ENTIRELY FROM APP CONFIGURATION — the URI decides only WHETHER to act, never
        // WHAT to serve, and it must stay that way. `uri` here is page-controlled (a page can navigate
        // itself to any fragment it likes), so deriving any part of the asset name from it — the fragment
        // as a route, say — would put page input straight into an app-package path with no containment
        // check anywhere on this path. There is deliberately none, because there is deliberately no input.
        //
        // Read from the WEBVIEW, not from constants: both are settable, and a kit that assumed the defaults
        // would silently stop repairing the apps that changed them.
        var root = (_webView.HybridRoot ?? string.Empty).Trim('/');
        var file = string.IsNullOrWhiteSpace(_webView.DefaultFile) ? "index.html" : _webView.DefaultFile.Trim('/');
        var asset = root.Length == 0 ? file : $"{root}/{file}";

        try
        {
            // Blocking, and it has to be: both platforms need the status line at the moment this event
            // returns (see Run). The app package is local and this is one small document, which is the only
            // reason that is acceptable here and would not be for a route.
            using var source = Microsoft.Maui.Storage.FileSystem.OpenAppPackageFileAsync(asset)
                .GetAwaiter().GetResult();
            var body = new MemoryStream();
            source.CopyTo(body);
            body.Position = 0;

            Log(() => $"[Shenora.Mobile] Served '{asset}' for a fragment document request — the platform "
                    + $"maps the fragment into the asset name and answers 404 (fragment '{uri.Fragment}').");
            return WebViewResourceResponse.Ok(body, WebViewContentTypes.FromPath(file));
        }
        catch (Exception ex)
        {
            // No bundle to serve. Decline — see the remarks.
            Log(() => $"[Shenora.Mobile] Fragment document repair declined, '{asset}' is not in the app "
                    + $"package: {ex.Message}");
            return null;
        }
#else
        // iOS reaches the shell with the fragment too, and the same repair MAKES IT WORSE there (measured by
        // the adopter: no document request at all afterwards, and native evaluation stopped answering).
        // A change to main-frame fall-through with no reproduction is verifiable in neither direction, so
        // nothing is applied — the sample's reload gate measures the platform instead.
        _ = uri;
        return null;
#endif
    }

    /// <summary>
    /// The headers as the PLATFORM should receive them — which is not always the headers the response
    /// carries, because one platform emits some of them itself and then passes ours through as well.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Android duplicates.</b> Measured on a device (2026-08-05, from the first adopter's report) with a
    /// route that varied only which headers the kit supplied, so each value could be attributed:
    /// </para>
    /// <code>
    /// kit supplies      page receives content-type                 page receives content-length
    /// ────────────      ─────────────────────────                  ───────────────────────────
    /// type + length     application/x-probe, application/x-probe   0, 32
    /// type only         application/x-probe, application/x-probe   0
    /// length only       application/octet-stream                   0, 32
    /// neither           application/octet-stream                   0
    /// </code>
    /// <para>
    /// So the platform ALWAYS emits a <c>Content-Type</c> and a <c>Content-Length: 0</c> of its own, AND
    /// copies our dictionary verbatim — a custom <c>X-</c> header arrived exactly once in every variant, so
    /// this is not blanket duplication, it is these two fields being re-derived. MAUI's Android intercept
    /// path reads <c>Content-Type</c> out of the dictionary to use as the native response's mime type and
    /// then hands the same dictionary over; its own comment concedes it cannot know a length
    /// (<c>MauiHybridWebViewClient</c>), which is where the <c>0</c> comes from. There is no
    /// <c>SetResponse</c> overload taking a content type AND a dictionary, so neither can be avoided by
    /// choosing a different one.
    /// </para>
    /// <para>
    /// <b>What is dropped and why only that.</b> <c>Content-Length</c> goes: two DIFFERENT values for it is
    /// an invalid HTTP message (RFC 9110 §8.6 — a recipient must reject or repair it), a consumer taking the
    /// first reads the payload as EMPTY, and ours buys nothing because the platform ignores both and
    /// delivered the complete body in every variant above. <c>Content-Type</c> STAYS: dropping it is what
    /// produces <c>application/octet-stream</c> in the table, and no <c>&lt;video&gt;</c> will touch that —
    /// a far worse regression than a repeated field whose two values are identical and therefore cannot
    /// mislead anyone about the type.
    /// </para>
    /// <para>
    /// ⚠ <b>Android only, deliberately.</b> iOS builds an <c>NSHTTPURLResponse</c> through completely
    /// different platform code and has NOT been measured for this; AVFoundation is the pickiest consumer the
    /// kit has (D44), so its headers are left exactly as D44 proved them. Do not generalise this without a
    /// device run.
    /// </para>
    /// </remarks>
    private static IReadOnlyDictionary<string, string> PlatformHeaders(IReadOnlyDictionary<string, string> headers)
    {
#if ANDROID
        if (!headers.ContainsKey("Content-Length")) return headers;
        var trimmed = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        trimmed.Remove("Content-Length");
        return trimmed;
#else
        return headers;
#endif
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
