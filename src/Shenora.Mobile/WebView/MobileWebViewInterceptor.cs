using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using Shenora;
using Shenora.Core.WebView;

namespace Shenora.Mobile;

/// <summary>
/// The mobile shell's <see cref="IWebViewInterceptor"/> — a middleware pipeline over MAUI's
/// <c>HybridWebView.WebResourceRequested</c>.
/// <para>
/// Shared source: this one file is the Android AND the iOS implementation, and the only thing that differs
/// between them is <see cref="RangeDelivery"/>.
/// </para>
/// </summary>
public sealed class MobileWebViewInterceptor : IWebViewInterceptor, IDisposable
{
    private readonly HybridWebView _webView;
    private readonly ILogger? _log;
    private readonly Task<BundleDocument?> _document;
    private readonly WebViewResourcePipeline _pipeline = new();
    private bool _disposed;

    /// <param name="webView">The webview to intercept. Its <c>WebResourceRequested</c> is subscribed here.</param>
    /// <param name="pipeline">
    /// The app-level pipeline (<c>app.UseFiles(…)</c>, <c>app.UseMediaPlayer()</c>), applied to this
    /// interceptor now — routes are read per request, so this is early enough for the first document.
    /// Pass <c>app.Pipeline</c>, or a fresh <see cref="WebViewPipeline"/> for a webview that must serve
    /// nothing. Required (unlike the desktop's equivalent, which is a nullable option property).
    /// </param>
    /// <param name="log">Optional diagnostics. Guarded — a throwing sink must not break serving.</param>
    public MobileWebViewInterceptor(HybridWebView webView, WebViewPipeline pipeline, ILogger? log = null)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        ArgumentNullException.ThrowIfNull(pipeline);
        _log = log;
        _webView.WebResourceRequested += OnWebResourceRequested;

        // Warmed here so the fragment repair can answer from memory, never doing I/O on the platform's
        // event thread. A first load is always fragment-free, so it has that whole navigation to finish.
        _document = ReadBundleDocument();

        // Not guarded: a throwing step is a composition mistake, and must fail loudly rather than leave a
        // webview that silently serves nothing.
        pipeline.ApplyTo(this);

        // ⚠ THE AUTOPLAY POLICY IS NOT LEVELLED HERE. Android requires a user gesture for `play()` and iOS
        // does not, so a page that autoplays on one shell answers `NotAllowedError` on the other — but the
        // documented Android answer, `Settings.MediaPlaybackRequiresUserGesture = false` on MAUI's
        // `MauiHybridWebView` at construction, makes every clip fail to load at all
        // (`MEDIA_ELEMENT_ERROR: Format error`, `readyState=0`). See TASKS.md.
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ A compile-time constant per platform — not a setting, and not something an app may override.
    /// </remarks>
    public WebViewRangeDelivery RangeDelivery =>
#if ANDROID
        // Android's webview applies the Range START to whatever body it is handed, so slicing as well applies
        // the offset twice — a player asking for a file's tail gets an empty body and retries it forever.
        WebViewRangeDelivery.Unsliced;
#elif IOS || MACCATALYST
        // WKURLSchemeHandler passes the body through verbatim, so the handler slices — ordinary correct HTTP.
        WebViewRangeDelivery.Sliced;
#else
        // The wrong answer plays every faststart file perfectly and fails every other one.
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
        // Null = no routes registered. ⚠ The platform repair must still run: the defect it repairs is the
        // PLATFORM's and fires whether or not the app serves anything of its own.
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
            // App middleware get first refusal; the repair is a LAST resort.
            response = Run(handler, request) ?? RepairDocumentRequest(e.Uri);
        }
        catch (Exception ex)
        {
            // A throwing middleware must not become an unhandled exception on the platform's event thread,
            // and its text must not reach the page — page script can read a response body.
            Log(() => "[Shenora.Mobile] Resource middleware failed", ex);
            // ⚠ Sent through `Answer` like every other reply: this type has exactly one `SetResponse` call,
            // and a second one would be a body that skipped `PlatformBody` — a dead process on Android.
            Answer(e, WebViewResourceResponse.NotFound());
            return;
        }

        Answer(e, response);
    }

    /// <summary>
    /// Hand a response to the platform, or leave the event untouched so it serves the request itself.
    /// <para>
    /// 🔴 <b>THE ONE PLACE a managed <see cref="Stream"/> becomes a platform response body</b>, so the
    /// per-platform adjustments (<see cref="PlatformHeaders"/>, <see cref="PlatformBody"/>) belong here and
    /// nowhere else.
    /// </para>
    /// </summary>
    private void Answer(WebViewWebResourceRequestedEventArgs e, WebViewResourceResponse? response)
    {
        // Nothing claimed it — leave `Handled` alone so the platform serves it normally (the bundle).
        if (response is null) return;

        // The PORTABLE overload: every header in the dictionary reaches the native response on both
        // platforms, so `e.PlatformArgs` is not needed.
        e.SetResponse(response.StatusCode, response.ReasonPhrase, PlatformHeaders(response.Headers),
            PlatformBody(response.Content));
        e.Handled = true;
    }

    /// <summary>
    /// The body as the PLATFORM must receive it. Identity on iOS; on Android a thin wrapper that translates
    /// a mid-read failure into the one throwable the webview's own error path can already see.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Without the wrapper, a mid-read throw KILLS THE PROCESS on Android, with nothing in the app's
    /// log.</b> A managed exception marshals into Java as <c>android.runtime.JavaProxyThrowable</c>, which
    /// extends <c>java.lang.Error</c> and so escapes Chromium's <c>catch (IOException)</c> in
    /// <c>InputStreamAdapter.read</c>; a <c>Java.IO.IOException</c> arrives as its PEER and hits that catch,
    /// giving a page-visible failed load.
    /// <para>
    /// ⚠ Only the READ path is translated; a throwing <c>Close</c> is not. iOS is unchanged, where the same
    /// failure is silent — a committed <c>200</c> with a short body (<c>docs/design/mobile-shells.md</c>).
    /// </para>
    /// </remarks>
    private Stream PlatformBody(Stream content)
#if ANDROID
        => new AndroidResponseBody(content, _log);
#else
        => content;
#endif

#if ANDROID
    /// <summary>
    /// A response body whose mid-read failure reaches Java as <c>java.io.IOException</c> — the one throwable
    /// Chromium's <c>InputStreamUtil.read</c> catches; anything else kills the process.
    /// <see cref="PlatformBody"/> has the mechanism.
    /// </summary>
    /// <remarks>
    /// ⚠ It must NOT return 0 to hide the failure: that is the silent short body a bounded stream throws
    /// precisely to avoid (<c>BoundedBodyStream.Read</c>). Translation, not suppression.
    /// </remarks>
    private sealed class AndroidResponseBody(Stream inner, ILogger? log) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        /// <summary>
        /// The array overload only: <c>InputStreamAdapter</c> calls it, and
        /// <see cref="Stream.Read(Span{byte})"/>'s base implementation forwards here.
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return inner.Read(buffer, offset, count);
            }
            catch (Java.IO.IOException)
            {
                // What `InputStreamUtil.read`'s `catch (IOException)` expects, so it passes through with its
                // real type and message. ⚠ The test is "already catchable", NOT "has a Java peer": any other
                // peered throwable — a revoked SAF permission mid-read raises `Java.Lang.SecurityException`
                // — misses that catch and kills the process, so it is translated below instead.
                throw;
            }
            catch (Java.Lang.Error)
            {
                // A genuine `java.lang.Error` (OOM, StackOverflow) SHOULD still take the process. ⚠ Never
                // reached by a wrapped MANAGED exception: the `JavaProxyThrowable` that extends
                // `java.lang.Error` is minted at the marshalling boundary, and does not exist on this side.
                throw;
            }
            catch (Exception ex)
            {
                // The throwable carries only the TYPE: Chromium logs its message itself (`Log.e` in
                // `InputStreamUtil.read`), and a file body's detail is the likeliest to carry a real path.
                AppCallback.Log(log, () => "[Shenora.Mobile] The response body failed mid-read, so the "
                    + $"webview is being handed a java.io.IOException it can report as a failed load: {ex}");
                throw new Java.IO.IOException(
                    $"Shenora: the response body failed mid-read ({ex.GetType().Name}).");
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>
        /// Forwarded, and load-bearing: Android DISPOSES a response body — including one it abandons
        /// mid-download — and that dispose releases the source handle behind a bounded body. A wrapper that
        /// swallowed it would leak a file handle per abandoned request.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
#endif

    /// <summary>
    /// The kit's repair for a platform that cannot serve its own bundle for a given request shape. Null —
    /// "nothing to repair" — for every request except the one case below, and null again when the bundle
    /// cannot be read: an app that serves its document some other way must be left exactly as it was.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Android maps a top-level <c>#fragment</c> URL onto an asset name and answers 404</b>, so every
    /// hash-routed page dies on RELOAD with <c>net::ERR_INVALID_RESPONSE</c>; on iOS the navigation never
    /// completes. The request fails before any script runs, so this interceptor is the only seam that can
    /// answer it (table on <see cref="WebViewResourceRequest.IsRootWithFragment"/>).
    /// <para>
    /// It answers the SAME bytes the platform would have served for the fragment-free URL —
    /// <c>HybridRoot/DefaultFile</c> from the app package — so the page boots exactly as on a first load and
    /// its router reads the fragment off <c>location</c> as usual.
    /// </para>
    /// </remarks>
    private WebViewResourceResponse? RepairDocumentRequest(Uri uri)
    {
        if (!WebViewResourceRequest.IsRootWithFragment(uri)) return null;

        // ⚠ NEVER BLOCK HERE. `_document` is warmed once at construction; if it is not ready we DECLINE
        // rather than wait, because `.Result`/`.GetAwaiter().GetResult()` deadlocks iOS's main thread — the
        // app stays alive with this as the last line it ever logs.
        if (_document is not { IsCompletedSuccessfully: true, Result: { } document })
        {
            Log(() => "[Shenora.Mobile] Fragment document repair declined — the bundle is not readable, or "
                    + $"not yet read, for '{uri.Fragment}'.");
            return null;
        }

        Log(() => "[Shenora.Mobile] Served the app's document for a fragment document request "
                + $"(fragment '{uri.Fragment}').");
        return WebViewResourceResponse.Ok(new MemoryStream(document.Bytes, writable: false), document.ContentType);
    }

    /// <summary>The bundle's own document, read ONCE so the repair never has to do I/O on the platform's thread.</summary>
    private readonly record struct BundleDocument(byte[] Bytes, string ContentType);

    /// <summary>
    /// Start reading the app's document in the background, at construction, so a later fragment repair can
    /// answer from memory.
    /// <para>
    /// 🔴 <b>The path is built entirely from APP CONFIGURATION</b> — a request URI decides only WHETHER to
    /// repair, never WHAT to serve, so there is no page input on this path to contain. It is read from the
    /// WEBVIEW rather than from constants: both are settable, and assuming the defaults would silently stop
    /// repairing the apps that changed them.
    /// </para>
    /// </summary>
    private Task<BundleDocument?> ReadBundleDocument()
    {
        var root = (_webView.HybridRoot ?? string.Empty).Trim('/');
        var file = string.IsNullOrWhiteSpace(_webView.DefaultFile) ? "index.html" : _webView.DefaultFile.Trim('/');
        var asset = root.Length == 0 ? file : $"{root}/{file}";

        return Task.Run(async () =>
        {
            try
            {
                await using var source = await Microsoft.Maui.Storage.FileSystem.OpenAppPackageFileAsync(asset)
                    .ConfigureAwait(false);
                var body = new MemoryStream();
                await source.CopyToAsync(body).ConfigureAwait(false);
                return (BundleDocument?)new BundleDocument(body.ToArray(), WebViewContentTypes.FromPath(file));
            }
            catch (Exception ex)
            {
                Log(() => $"[Shenora.Mobile] The app document '{asset}' could not be read, so the fragment "
                        + $"repair will decline: {ex.Message}");
                return null;
            }
        });
    }

    /// <summary>
    /// The headers as the PLATFORM should receive them — not always the ones the response carries, because
    /// one platform emits some itself and then passes ours through as well.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Android duplicates</b> — measured with a route that varied only which headers the kit supplied:
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
    /// So the platform ALWAYS emits a <c>Content-Type</c> and a <c>Content-Length: 0</c> of its own AND
    /// copies our dictionary verbatim — a custom <c>X-</c> header arrived exactly once in every variant, so
    /// it is these two fields being re-derived, not blanket duplication. No <c>SetResponse</c> overload
    /// takes a content type AND a dictionary, so neither is avoidable by choosing another.
    /// </para>
    /// <para>
    /// <c>Content-Length</c> is therefore dropped: two DIFFERENT values for it is an invalid HTTP message
    /// (RFC 9110 §8.6) and a consumer taking the first reads the payload as EMPTY. <c>Content-Type</c>
    /// STAYS — dropping it yields the table's <c>application/octet-stream</c>, which no <c>&lt;video&gt;</c>
    /// will touch. ⚠ Android only: iOS builds an <c>NSHTTPURLResponse</c> through entirely different
    /// platform code and has NOT been measured, so do not generalise without a device run.
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
    /// ⚠ Resolved SYNCHRONOUSLY: both mobile platforms need the status line and headers at the moment the
    /// event returns, so the metadata cannot be awaited (the desktop shell has a deferral and does not).
    /// Laziness belongs in the BODY — the response carries a <c>Stream</c> the platform reads afterwards —
    /// so middleware must not do slow work before returning (<see cref="IWebViewInterceptor.Use"/>).
    /// </para>
    /// </summary>
    private static WebViewResourceResponse? Run(WebViewResourceHandler handler, WebViewResourceRequest request)
        => handler(request, CancellationToken.None).GetAwaiter().GetResult();

    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_log, message, exception: failure);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _webView.WebResourceRequested -= OnWebResourceRequested; }
        catch (Exception ex) { Log(() => "[Shenora.Mobile] Interceptor dispose", ex); }
        _pipeline.Clear();
    }
}
