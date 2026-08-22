using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using Shenora;
using Shenora.Core.WebView;

namespace Shenora.Mobile;

/// <summary>
/// The mobile shell's <see cref="IWebViewInterceptor"/> — a middleware pipeline over MAUI's
/// <c>HybridWebView.WebResourceRequested</c>. One shared source is both the Android and the iOS
/// implementation; only <see cref="RangeDelivery"/> differs.
/// </summary>
public sealed class MobileWebViewInterceptor : IWebViewInterceptor, IDisposable
{
    /// <summary>The MAUI bridge script a page must load for <c>window.HybridWebView</c> to exist.</summary>
    private const string BridgeScript = "hybridwebview.js";

    /// <summary>Largest document the bridge-tag check will read. A document is small; anything bigger is not one.</summary>
    private const int BridgeTagScanLimit = 2 * 1024 * 1024;

    /// <summary>
    /// How many PASSING documents the bridge-tag check will read before it stops looking. A warning latches
    /// it permanently; this bounds only the quiet case.
    /// <para>
    /// 🔴 <b>More than one, because the first document served is not always the one the page ends up
    /// running.</b> An app that serves its PACKAGED <c>index.html</c> — tagged by the build step, so it
    /// passes — and later serves a runtime-fetched bundle would otherwise spend the check on the document
    /// that was never in doubt. ⚠ Small, because each pass costs a read of up to
    /// <see cref="BridgeTagScanLimit"/>.
    /// </para>
    /// </summary>
    private const int BridgeTagScanBudget = 4;

    private readonly HybridWebView _webView;
    private readonly ILogger? _log;
    private readonly Task<BundleDocument?> _document;
    private readonly WebViewResourcePipeline _pipeline = new();
    private readonly string _defaultFile;
    private readonly bool _attachedAfterRealize;
    private bool _firstRequestSeen;
    private bool _bridgeTagChecked;
    private int _bridgeTagScans;
    private bool _disposed;

    /// <param name="webView">
    /// The webview to intercept. Its <c>WebResourceRequested</c> is subscribed here — <b>so the moment this
    /// constructor runs is what decides whether the app's DOCUMENT reaches the pipeline</b>. Construct it in
    /// the page CONSTRUCTOR, before <c>Content = webView</c>; <c>Loaded</c>/<c>OnAppearing</c> is already too
    /// late, because the handler is realized and the webview has navigated by then
    /// (<see cref="WarnIfAttachedTooLate"/> says so at runtime).
    /// </param>
    /// <param name="pipeline">
    /// The app-level pipeline (<c>app.UseFiles(…)</c>, <c>app.UseMediaPlayer()</c>), applied now — routes
    /// are read per request, so this is early enough for the first document <b>provided this constructor
    /// ran before the webview navigated</b> (see <paramref name="webView"/>: the routes being late is not
    /// the failure mode, the SUBSCRIPTION being late is). Pass <c>app.Pipeline</c>, or a fresh
    /// <see cref="WebViewPipeline"/> for a webview that must serve nothing.
    /// </param>
    /// <param name="log">Optional diagnostics. Guarded — a throwing sink must not break serving.</param>
    public MobileWebViewInterceptor(HybridWebView webView, WebViewPipeline pipeline, ILogger? log = null)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        ArgumentNullException.ThrowIfNull(pipeline);
        _log = log;

        // 🔴 SAMPLED BEFORE ANYTHING ELSE, because it is the only moment it means anything. A realized
        // handler says the platform view already exists, so this interceptor is being attached to a webview
        // that has probably navigated — the shape that silently sends the document to the platform. Half of
        // a diagnosis, never a warning on its own: `WarnIfAttachedTooLate` waits for the first request.
        _attachedAfterRealize = webView.Handler is not null;

        _webView.WebResourceRequested += OnWebResourceRequested;

        _defaultFile = string.IsNullOrWhiteSpace(_webView.DefaultFile) ? "index.html" : _webView.DefaultFile.Trim('/');

        // Warmed here so the fragment repair answers from memory, never doing I/O on the platform's event
        // thread. A first load is always fragment-free, so it has that whole navigation to finish.
        _document = ReadBundleDocument();

        // Not guarded: a throwing step is a composition mistake. Failing loudly beats a webview that
        // silently serves nothing.
        pipeline.ApplyTo(this);

        // ⚠ THE AUTOPLAY POLICY IS NOT LEVELLED HERE. Android requires a user gesture for `play()` and iOS
        // does not; the documented Android answer — `MediaPlaybackRequiresUserGesture = false` on MAUI's
        // `MauiHybridWebView` — makes every clip fail to LOAD instead (`readyState=0`). See TASKS.md.
    }

    /// <inheritdoc />
    /// <remarks>⚠ A compile-time constant per platform, not a setting an app may override.</remarks>
    public WebViewRangeDelivery RangeDelivery =>
#if ANDROID
        // Android's webview applies the Range START to whatever body it is handed, so slicing as well
        // applies the offset twice — a player asking for a file's tail gets an empty body and retries for
        // ever. 🔴 NOT a stale workaround for an old Chromium: still true on Android 16 (SDK 36, WebView
        // 133.0.6943.137), where `Sliced` on a non-faststart file produced 35 requests, 28 of them the
        // identical tail range, each answered `206` with a correct `Content-Range`; `Unsliced` serves the
        // same clip in FOUR. A correct 206 is not enough.
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
        WarnIfAttachedTooLate(e.Uri);

        // Null = no routes registered. ⚠ The platform repair must still run: the defect it repairs is the
        // PLATFORM's and fires whether or not the app serves anything of its own.
        if (_pipeline.Build() is not { } handler)
        {
            var repaired = RepairDocumentRequest(e.Uri);
            WarnIfDocumentHasNoBridge(e.Uri, repaired);
            Answer(e, repaired);
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
            // ⚠ Through `Answer` like every other reply: this type has exactly one `SetResponse` call, and a
            // second would be a body that skipped `PlatformBody` — a dead process on Android.
            Answer(e, WebViewResourceResponse.NotFound());
            return;
        }

        WarnIfDocumentHasNoBridge(e.Uri, response);
        Answer(e, response);
    }

    /// <summary>
    /// Say ONCE, on the first request, that this interceptor was attached after its webview was realized
    /// <b>and</b> the first thing it ever saw was not the app's document.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Attachment TIME decides whether any route works, and getting it wrong is otherwise perfectly
    /// silent</b> — nothing throws, nothing warns, and <see cref="Use"/> returns a live registration either
    /// way. Measured in an adopter's app: constructed in <c>MainPage.OnLoaded</c>, which is the natural
    /// place because that is where the DI services are reachable, so the webview had already navigated and
    /// the document and every asset came from <c>Resources/Raw/wwwroot</c> — only the favicons ever reached
    /// the pipeline. It survived months because offline files and the media routes all answer requests the
    /// PAGE makes later; it surfaces the first time an app serves the app SHELL from the interceptor, and
    /// then reports a WRONG answer rather than none (a client-update watchdog confirmed a deliberately
    /// broken bundle, because the previously packaged client was what was really running).
    /// <para>
    /// ⚠ <b>BOTH halves are required, so this cannot cry wolf.</b> A realized handler alone is not proof of
    /// anything, and a non-document first request alone is normal for a webview that serves nothing. A
    /// correctly attached interceptor sees the document FIRST and never reaches this log.
    /// </para>
    /// </remarks>
    private void WarnIfAttachedTooLate(Uri uri)
    {
        if (_firstRequestSeen) return;
        _firstRequestSeen = true;
        if (!_attachedAfterRealize || IsDocumentRequest(uri)) return;

        Log(() => "[Shenora.Mobile] This interceptor was constructed after its webview's handler already "
                + $"existed, and the first request it saw was '{uri}' rather than the app's document — so "
                + "the document was served by the PLATFORM and none of the pipeline's routes answered it. "
                + "Construct the interceptor in the page CONSTRUCTOR, before `Content = webView`; "
                + "`Loaded`/`OnAppearing` is already too late.");
    }

    /// <summary>
    /// Say ONCE that a document this pipeline served carries no MAUI bridge script — which silently
    /// disables every <c>invoke</c>/<c>post</c> the page makes.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Only the kit is in a position to notice, which is why it does.</b> The tag exists solely
    /// because the kit's transport needs it, and a document served from the PIPELINE never passes through
    /// the build step that injects it into the packaged <c>index.html</c> — so a client-update bundle or a
    /// dev proxy arrives untagged and <c>window.HybridWebView</c> never exists. With no bridge there is no
    /// handshake, so anything gated on the page confirming itself (exactly what a safe client update is)
    /// can never confirm and rolls back for ever.
    /// <para>
    /// ⚠ <b>It must never cost a response, so it reads only what it can put back.</b> An in-memory body is
    /// read with <see cref="MemoryStream.ToArray"/>, which does not move <c>Position</c>; any other SEEKABLE
    /// body is read and its position restored (<see cref="ReadDocument"/>), which is safe only because this
    /// runs before the body reaches the platform. A body that cannot seek is left alone and leaves the check
    /// unspent. Synchronous by construction — an <c>await</c> on this thread deadlocks iOS's main thread
    /// (<see cref="RepairDocumentRequest"/>).
    /// </para>
    /// </remarks>
    private void WarnIfDocumentHasNoBridge(Uri uri, WebViewResourceResponse? response)
    {
        if (_bridgeTagChecked || response is null) return;
        if (response.StatusCode != 200 || !IsDocumentRequest(uri)) return;
        if (!response.Headers.TryGetValue("Content-Type", out var type)
            || !type.Contains("html", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            // 🔴 The budget is spent only once a body has actually been SCANNED, and that ordering is the
            // whole feature. Counting the first document REQUEST would spend it on a document that was
            // never read, and then skip the bundle served later — precisely what this check exists for.
            if (ReadDocument(response.Content) is not { } text) return;
            if (++_bridgeTagScans >= BridgeTagScanBudget) _bridgeTagChecked = true;

            if (text.Contains(BridgeScript, StringComparison.OrdinalIgnoreCase)) return;

            // 🔴 Latched here regardless of the budget: this warning is said once per interceptor, and a
            // document that has already failed cannot fail more informatively on the next navigation.
            _bridgeTagChecked = true;

            Log(() => $"[Shenora.Mobile] The document served for '{uri}' does not reference "
                    + $"'{BridgeScript}', so `window.HybridWebView` will not exist and every bridge call "
                    + "the page makes will fail silently. A document served from the pipeline does not pass "
                    + "through the build step that tags the packaged index.html — inject the tag at serve "
                    + "time.");
        }
        catch (Exception ex)
        {
            // A diagnostic that breaks serving is worse than no diagnostic.
            Log(() => "[Shenora.Mobile] Bridge-tag check skipped", ex);
        }
    }

    /// <summary>
    /// The document's text, or null when it cannot be read WITHOUT disturbing the response — which is the
    /// only condition this check is allowed to impose on a body it does not own.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>A document served from DISK is the case this check exists for, so it cannot be the case it
    /// skips.</b> A bundle fetched at runtime is written to app data and served from a
    /// <see cref="FileStream"/> — and it is the one that never passed through the build step that injects
    /// the tag, so reading only a <see cref="MemoryStream"/> looked cautious while covering the wrong half.
    /// <para>
    /// ⚠ <b>Safe because of WHEN it runs.</b> The caller has not yet handed the body to the platform, so
    /// nothing else can be reading it, and the platform then reads sequentially from <c>Position</c> — which
    /// is restored in a <c>finally</c>, including when the read throws. A stream that cannot seek is still
    /// left alone: rewinding it is not possible, and a consumed body is a blank page.
    /// </para>
    /// </remarks>
    private static string? ReadDocument(Stream content)
    {
        // `ToArray` does not move Position, so an in-memory body needs no restoring at all.
        if (content is MemoryStream memory)
        {
            return memory.Length is 0 or > BridgeTagScanLimit
                ? null
                : System.Text.Encoding.UTF8.GetString(memory.ToArray());
        }

        if (!content.CanSeek || content.Length is 0 or > BridgeTagScanLimit) return null;

        var resume = content.Position;
        try
        {
            var bytes = new byte[content.Length - resume];
            var read = content.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            return read <= 0 ? null : System.Text.Encoding.UTF8.GetString(bytes, 0, read);
        }
        finally
        {
            content.Position = resume;
        }
    }

    /// <summary>
    /// True when <paramref name="uri"/> asks for the app's own document — the site root, the root with a
    /// <c>#fragment</c>, or the webview's configured <c>DefaultFile</c> by name.
    /// </summary>
    private bool IsDocumentRequest(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return false;
        if (WebViewResourceRequest.IsRootWithFragment(uri)) return true;
        var path = uri.AbsolutePath.Trim('/');
        return path.Length == 0 || path.Equals(_defaultFile, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Hand a response to the platform, or leave the event untouched so it serves the request itself.
    /// <para>
    /// 🔴 <b>THE ONE PLACE a managed <see cref="Stream"/> becomes a platform response body</b>, so the
    /// per-platform adjustments (<see cref="PlatformHeaders"/>, <see cref="PlatformBody"/>) belong here.
    /// </para>
    /// </summary>
    private void Answer(WebViewWebResourceRequestedEventArgs e, WebViewResourceResponse? response)
    {
        // Nothing claimed it — leave `Handled` alone so the platform serves it normally (the bundle).
        if (response is null) return;

        try
        {
            // The PORTABLE overload: every header reaches the native response on both platforms, so
            // `e.PlatformArgs` is not needed.
            e.SetResponse(response.StatusCode, response.ReasonPhrase, PlatformHeaders(response.Headers),
                PlatformBody(response.Content));
            e.Handled = true;
        }
        catch (Exception ex)
        {
            // 🔴 THE HANDOVER ITSELF THROWS, and on Android an escape from here crosses JNI out of
            // `shouldInterceptRequest` and KILLS THE PROCESS — the same death `PlatformBody` guards for the
            // READ path, one statement earlier. Measured on Android 12 (SDK 32), a middleware answering
            // 302: `java.lang.IllegalArgumentException: statusCode can't be in the [300, 399] range` from
            // `WebResourceResponse.<init>`, FATAL EXCEPTION, app gone. Android also rejects an empty or
            // non-ASCII reason phrase, and `PlatformHeaders` can throw on a duplicate header key.
            // ⚠ The guard belongs HERE rather than around the pipeline: the try above deliberately covers
            // middleware EXECUTION, and widening it would put a second `SetResponse` on the failure path.
            // The desktop shell has guarded this same seam all along (`WebViewHost.CreateWebResourceResponse`).
            Log(() => $"[Shenora.Mobile] The platform refused the response for '{e.Uri}' "
                    + $"(status {response.StatusCode}) — it will serve this request itself.", ex);

            // `Handled` is set only after a successful handover, so it is still false and the platform
            // takes the request back. The body never reached the platform, so disposing it is OURS to do —
            // otherwise a file-backed response leaks its handle until finalization.
            try { response.Content.Dispose(); }
            catch (Exception disposeFailed) { Log(() => "[Shenora.Mobile] Disposing the refused body", disposeFailed); }
        }
    }

    /// <summary>
    /// The body as the PLATFORM must receive it. Identity on iOS; on Android a thin wrapper that translates
    /// a mid-read failure into the one throwable the webview's own error path can already see.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Without the wrapper, a mid-read throw KILLS THE PROCESS on Android, with nothing in the app's
    /// log.</b> A managed exception marshals into Java as <c>android.runtime.JavaProxyThrowable</c>, which
    /// extends <c>java.lang.Error</c> and so escapes Chromium's <c>catch (IOException)</c> in
    /// <c>InputStreamAdapter.read</c>; a <c>Java.IO.IOException</c> arrives as its PEER and hits that catch.
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
    /// Chromium's <c>InputStreamUtil.read</c> catches; anything else kills the process
    /// (<see cref="PlatformBody"/> has the mechanism).
    /// </summary>
    /// <remarks>
    /// ⚠ It must NOT return 0 to hide the failure — that is the silent short body a bounded stream throws to
    /// avoid (<c>BoundedBodyStream.Read</c>). Translation, not suppression.
    /// </remarks>
    private sealed class AndroidResponseBody(Stream inner, ILogger? log) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        /// <summary>The array overload only: <c>InputStreamAdapter</c> calls it, and
        /// <see cref="Stream.Read(Span{byte})"/>'s base implementation forwards here.</summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return inner.Read(buffer, offset, count);
            }
            catch (Java.IO.IOException)
            {
                // What `InputStreamUtil.read`'s `catch (IOException)` expects, so it passes through with its
                // real type and message. ⚠ The test is "already catchable", NOT "has a Java peer": a peered
                // `Java.Lang.SecurityException` (a SAF permission revoked mid-read) misses that catch and
                // kills the process, so it is translated below instead.
                throw;
            }
            catch (Java.Lang.Error)
            {
                // A genuine `java.lang.Error` (OOM, StackOverflow) SHOULD still take the process. ⚠ Never
                // reached by a wrapped MANAGED exception: the `JavaProxyThrowable` that extends
                // `java.lang.Error` is minted at the marshalling boundary and does not exist on this side.
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

        /// <summary>Forwarded, and load-bearing: Android DISPOSES a response body — including one abandoned
        /// mid-download — and that dispose is what releases the source handle behind a bounded body.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
#endif

    /// <summary>
    /// The kit's repair for a platform that cannot serve its own bundle for a given request shape. Null —
    /// "nothing to repair" — for every other request, and null again when the bundle cannot be read.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Android maps a top-level <c>#fragment</c> URL onto an asset name and answers 404</b>, so every
    /// hash-routed page dies on RELOAD with <c>net::ERR_INVALID_RESPONSE</c>; on iOS the navigation never
    /// completes. The request fails before any script runs, so this interceptor is the only seam that can
    /// answer it (table on <see cref="WebViewResourceRequest.IsRootWithFragment"/>). It answers the SAME
    /// bytes the platform would have served for the fragment-free URL — <c>HybridRoot/DefaultFile</c> from
    /// the app package — so the page boots as on a first load and its router reads <c>location</c> as usual.
    /// </remarks>
    private WebViewResourceResponse? RepairDocumentRequest(Uri uri)
    {
        if (!WebViewResourceRequest.IsRootWithFragment(uri)) return null;

        // ⚠ NEVER BLOCK HERE. `_document` is warmed once at construction; if it is not ready we DECLINE.
        // `.Result`/`.GetAwaiter().GetResult()` deadlocks iOS's main thread — the app stays alive with this
        // as the last line it ever logs.
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
    /// repair, never WHAT to serve, so there is no page input on this path to contain. Read off the WEBVIEW,
    /// since both settings are settable and assuming the defaults would silently stop repairing the apps
    /// that changed them.
    /// </para>
    /// </summary>
    private Task<BundleDocument?> ReadBundleDocument()
    {
        var root = (_webView.HybridRoot ?? string.Empty).Trim('/');
        var file = _defaultFile;
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
    /// ⚠ <b>Android duplicates</b> — measured with a route that varied only which headers the kit supplied:
    /// <code>
    /// kit supplies      page receives content-type                 page receives content-length
    /// ────────────      ─────────────────────────                  ───────────────────────────
    /// type + length     application/x-probe, application/x-probe   0, 32
    /// type only         application/x-probe, application/x-probe   0
    /// length only       application/octet-stream                   0, 32
    /// neither           application/octet-stream                   0
    /// </code>
    /// <para>
    /// So the platform always emits a <c>Content-Type</c> and a <c>Content-Length: 0</c> of its own AND
    /// copies our dictionary verbatim (a custom <c>X-</c> header arrived exactly once in every variant), and
    /// no <c>SetResponse</c> overload takes a content type ALONGSIDE a dictionary.
    /// </para>
    /// <para>
    /// <c>Content-Length</c> is therefore dropped: two DIFFERENT values for it is an invalid HTTP message
    /// (RFC 9110 §8.6) and a consumer taking the first reads the payload as EMPTY. <c>Content-Type</c>
    /// STAYS — dropping it yields <c>application/octet-stream</c>, which no <c>&lt;video&gt;</c> will touch.
    /// ⚠ Android only: iOS builds an <c>NSHTTPURLResponse</c> through different platform code and is
    /// UNMEASURED.
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
    /// event returns, so the metadata cannot be awaited. Laziness belongs in the BODY — the response
    /// carries a <c>Stream</c> the platform reads afterwards — so middleware must not do slow work before
    /// returning (<see cref="IWebViewInterceptor.Use"/>).
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
