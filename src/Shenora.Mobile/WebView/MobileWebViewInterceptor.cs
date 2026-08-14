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
    private readonly Task<BundleDocument?> _document;
    // The registry and composition are PORTABLE and shared with the desktop shell — see
    // WebViewResourcePipeline for why this is not hand-rolled per shell. All that is left here is the glue.
    private readonly WebViewResourcePipeline _pipeline = new();
    private bool _disposed;

    /// <param name="webView">The webview to intercept. Its <c>WebResourceRequested</c> is subscribed here.</param>
    /// <param name="pipeline">
    /// The app-level pipeline (<c>app.UseFiles(…)</c>, <c>app.UseMediaPlayer()</c>), applied to this
    /// interceptor now — routes are read per request, so this is early enough for the first document.
    /// Pass <c>app.Pipeline</c>.
    /// <para>
    /// 🔴 <b>REQUIRED, and the desktop's equivalent is not — the asymmetry is deliberate.</b>
    /// <c>WebViewHostOptions</c> is an object the app already constructs, so a nullable property there
    /// breaks no existing site and a genuinely isolated webview can opt out. Here the app calls this
    /// constructor DIRECTLY, and an optional parameter would be a line every adopter had to remember on
    /// every window — the exact shape that left request tracking inert for a whole release (D63). Making
    /// it required means the compiler names every site instead. Pass a fresh
    /// <see cref="WebViewPipeline"/> for a webview that must serve nothing.
    /// </para>
    /// </param>
    /// <param name="log">Optional diagnostics. Guarded — a throwing sink must not break serving.</param>
    public MobileWebViewInterceptor(HybridWebView webView, WebViewPipeline pipeline, Action<string>? log = null)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        ArgumentNullException.ThrowIfNull(pipeline);
        _log = log;
        _webView.WebResourceRequested += OnWebResourceRequested;

        // Start reading the app's document NOW, so the fragment repair can answer from memory without ever
        // doing I/O on the platform's event thread. A first load is always fragment-free, so this has the
        // whole of that navigation to finish before any reload could need it.
        _document = ReadBundleDocument();

        // Not guarded: a throwing step is a composition mistake and must fail loudly rather than produce
        // a webview that silently serves nothing.
        pipeline.ApplyTo(this);

        // 🔴 THE AUTOPLAY POLICY IS DELIBERATELY NOT LEVELLED HERE — the obvious fix was tried and it
        // BREAKS MEDIA. Android requires a user gesture for `play()` and iOS does not, so one page
        // autoplays on one shell and answers `NotAllowedError` on the other; the documented Android
        // answer is `Settings.MediaPlaybackRequiresUserGesture = false`, and setting it on MAUI's
        // `MauiHybridWebView` at construction made every clip fail to load at all
        // (`MEDIA_ELEMENT_ERROR: Format error`, `readyState=0`), reproduced and A/B'd on 2026-08-09.
        // See TASKS.md — a fix needs the real mechanism, not another attempt from here.
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
            // ⚠ Through `Answer` like every other reply, not a hand-rolled `SetResponse`. There is exactly
            // ONE `SetResponse` call in this type on purpose: it is the seam where a managed body becomes a
            // platform one, so a second call site would be a body that skipped `PlatformBody` — and on
            // Android that is the difference between a failed load and a dead process.
            Answer(e, WebViewResourceResponse.NotFound());
            return;
        }

        Answer(e, response);
    }

    /// <summary>
    /// Hand a response to the platform, or leave the event untouched so it serves the request itself.
    /// <para>
    /// 🔴 <b>THE ONE PLACE a managed <see cref="Stream"/> becomes a platform response body</b> — which is why
    /// the per-platform adjustments (<see cref="PlatformHeaders"/>, <see cref="PlatformBody"/>) belong here
    /// and nowhere else. A body must never know which shell is reading it.
    /// </para>
    /// </summary>
    private void Answer(WebViewWebResourceRequestedEventArgs e, WebViewResourceResponse? response)
    {
        // Nothing claimed it — leave `Handled` alone so the platform serves it normally (the bundle).
        if (response is null) return;

        // ⚠ The PORTABLE overload, with a header dictionary. `e.PlatformArgs` is not needed on either
        // platform: every header reaches the native response, verified on devices after this repo spent a
        // session believing otherwise (D44).
        e.SetResponse(response.StatusCode, response.ReasonPhrase, PlatformHeaders(response.Headers),
            PlatformBody(response.Content));
        e.Handled = true;
    }

    /// <summary>
    /// The body as the PLATFORM must receive it. Identity on iOS; on Android a thin wrapper that translates
    /// a mid-read failure into the one throwable the webview's own error path can already see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>WHY ANDROID NEEDS THIS AT ALL, and it is a marshalling fact rather than a webview limitation.</b>
    /// A response body is read lazily (<c>WebViewFiles.Read</c>, shipped since 0.9.1), so an IO failure — a
    /// shrinking file, an ejected card, a dropped share, a revoked permission — now surfaces from INSIDE a
    /// platform read, after the status line and the <c>Content-Length</c> promise are committed. Chromium
    /// handles that: <c>InputStreamUtil.read</c> wraps the read in <c>catch (IOException)</c>, logs, and
    /// returns <c>EXCEPTION_THROWN_STATUS</c> (<c>-2</c>), a status the native reader distinguishes from
    /// <c>-1</c> (EOF) and turns into a net error — a failed load the page can see. But .NET Android marshals
    /// a managed exception into Java as <c>android.runtime.JavaProxyThrowable</c>, which extends
    /// <c>java.lang.Error</c> — outside <c>catch (IOException)</c> and outside any <c>catch (Exception)</c> by
    /// construction — so it left <c>InputStreamAdapter.read</c> uncaught and JNI's handler KILLED THE PROCESS,
    /// a second or so later (1.4 s in the run that A/B'd this wrapper, 0.4 s in the truncation run that first
    /// found it — the interval is incidental), with nothing in the app's own log. Measured on a device
    /// 2026-08-13 both through the kit's whole stack and through a bare throwing stream, so it belonged to the
    /// marshalling rather than to any seam (<c>.claude/knowledge/mobile-shells.md</c>).
    /// </para>
    /// <para>
    /// ✅ <b>So this hands the shell something its existing catch can already see</b>, and the marshalling half
    /// is MEASURED rather than assumed — a <c>Java.IO.IOException</c> thrown from inside the body
    /// arrives in Java as its PEER (<c>java.io.IOException</c>) rather than being re-wrapped. ⚠ <b>And the
    /// measurement covers all THREE exception shapes the wrapper discriminates</b>, because a branch measured
    /// at one point on the axis it branches on is not measured: a managed <c>System.IO</c> exception, a peered
    /// <c>Java.Lang.SecurityException</c> and a <c>Java.IO.IOException</c> thrown by the body itself. The A/B is
    /// in <c>.claude/knowledge/mobile-shells.md</c>.
    /// </para>
    /// <para>
    /// ⚠ <b>iOS is deliberately UNCHANGED, and that is not an oversight.</b> There the same failure is silent
    /// — a committed <c>200</c> with a body short of its promise, no error anywhere — which is the
    /// silent-truncation outcome this repo refuses; fixing it needs its own measurement and its own answer, so
    /// a symmetric wrapper here would only make the untested shell look handled. See <c>TASKS.md</c>.
    /// </para>
    /// <para>
    /// ✅ <b>Only the READ path is translated, and the others are UNREACHABLE rather than merely unmeasured.</b>
    /// The same device run counted every non-read member the platform touched on a body it was handed:
    /// <c>Length=0 Position=0 CanSeek=0 Seek=0</c>, across four bodies including a 64 MiB one and one abandoned
    /// mid-transfer. So <c>InputStreamAdapter</c> reads and closes, and nothing else — translating a throw from
    /// <c>Length</c> would be dead code with a confident comment, which is the shape this repo keeps finding.
    /// ⚠ The counters covered the PROBE's own bodies, so the claim is about this read path, not a promise about
    /// every future consumer; a <c>Close</c> that throws is still untranslated and still unmeasured.
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
    /// Chromium's <c>InputStreamUtil.read</c> already catches — instead of as anything else, all of which kill
    /// the process. See <see cref="PlatformBody"/> for the mechanism and the measurements.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It forwards everything and decides nothing: it must NOT return 0 to hide the failure, because that is
    /// the silent short body a bounded stream throws precisely to avoid (<c>BoundedBodyStream.Read</c>) — a
    /// film that plays back wrong is worse than a load that fails. Translation, not suppression.
    /// </para>
    /// <para>
    /// 🔴 <b>The three-way branch in <see cref="Read"/> is the whole type</b>, and each arm was measured on a
    /// device rather than reasoned about: a <c>java.io.IOException</c> passes through (already catchable), a
    /// <c>java.lang.Error</c> passes through (genuinely fatal, and SHOULD kill), everything else is translated.
    /// ⚠ The middle and first arms are not decoration — an earlier version rethrew every peered throwable and
    /// left <c>Java.Lang.SecurityException</c> killing the app.
    /// </para>
    /// </remarks>
    private sealed class AndroidResponseBody(Stream inner, Action<string>? log) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        /// <summary>
        /// ⚠ The array overload ONLY, deliberately. <c>InputStreamAdapter</c> calls it, and
        /// <see cref="Stream.Read(Span{byte})"/>'s base implementation forwards here — so overriding the span
        /// overload as well would add a second path to keep in step for no reach at all.
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return inner.Read(buffer, offset, count);
            }
            catch (Java.IO.IOException)
            {
                // 🔴 THE TEST IS "IS IT ALREADY CATCHABLE BY THE PLATFORM", NOT "DOES IT HAVE A JAVA PEER" —
                // and this line said the latter until it was measured. A peered throwable that is not a
                // `java.io.IOException` marshals as its own peer and misses `InputStreamUtil.read`'s
                // `catch (IOException)` just as surely as a `JavaProxyThrowable` does, so the process dies
                // anyway. Reachable, and by this method's OWN named trigger: an adopter serving a user-picked
                // SAF document through `ContentResolver.OpenInputStream` whose URI permission is revoked
                // mid-read gets a `Java.Lang.SecurityException` — a peered `RuntimeException`. Measured on a
                // device 2026-08-13: rethrowing it killed the app; translating it fails the load instead
                // (`.claude/knowledge/mobile-shells.md` has the three-arm A/B).
                //
                // A `java.io.IOException` — and only that — is what the platform's catch already expects, so
                // it passes through unchanged and keeps its real type and message.
                throw;
            }
            catch (Java.Lang.Error)
            {
                // A genuine `java.lang.Error` (OOM, StackOverflow) SHOULD still take the process. Converting
                // one into a failed request would let the app limp on past a fatal condition, which is worse
                // than the crash this wrapper exists to remove — that crash was a correctness bug, this one is
                // the runtime telling the truth. ⚠ Never reached by a wrapped MANAGED exception: the
                // `JavaProxyThrowable` that extends `java.lang.Error` is minted at the marshalling boundary
                // and does not exist on this side of it.
                throw;
            }
            catch (Exception ex)
            {
                // 🔴 THE DIAGNOSIS GOES TO THE HOST'S OWN LOG, and the throwable carries only the TYPE.
                // Chromium logs the throwable's message itself (`Log.e` in `InputStreamUtil.read`), and a
                // file body's failure detail is the likeliest of all of them to carry a real path — the same
                // reasoning as the kit's one fixed 404 body. Without this line the app's log says nothing at
                // all about a load that failed, which is what the crash used to do.
                AppCallback.Log(log, () => $"[Shenora.Mobile] The response body failed mid-read, so the "
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
        /// Forwarded, and it is load-bearing: Android DISPOSES a response body — including one it abandons
        /// mid-download — and that dispose is what releases the source handle behind a bounded body. A wrapper
        /// that swallowed it would leak a file handle per abandoned request.
        /// <para>
        /// ✅ Re-measured THROUGH this wrapper (2026-08-13), not carried over from the pre-wrapper run: a 64 MiB
        /// body whose <c>fetch</c> aborted after 6,144 bytes came back <c>handed=1 drained=0 disposed=1</c> —
        /// disposed 7 reads in, undrained. The forwarding works.
        /// </para>
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
    /// "nothing to repair" — for every request on every platform except the one measured case below.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Android maps a top-level <c>#fragment</c> URL onto an asset name and answers 404</b>, so every
    /// hash-routed page dies on RELOAD with <c>net::ERR_INVALID_RESPONSE</c>. The measurement and the table
    /// are on <see cref="WebViewResourceRequest.IsRootWithFragment"/>; what
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
    /// <para>
    /// 🔴 <b>iOS IS REPAIRED TOO SINCE 2026-08-09, and the reason it was not is the interesting part.</b>
    /// The standing account said a reload at a hash route "simply never produces a second document" there,
    /// and that the Android repair MADE IT WORSE — the adopter measured no document request at all
    /// afterwards, and native evaluation ceasing to answer. Both halves were wrong, and measuring settled it:
    /// </para>
    /// <list type="bullet">
    /// <item><description>iOS <b>does</b> issue the document request — logged arriving at this interceptor as
    /// <c>app://0.0.0.1/#/probe-route</c>, with the navigation still never completing.</description></item>
    /// <item><description>The Android repair's <b>implementation</b> was what broke iOS, not the idea. It read
    /// the bundle with a blocking <c>.GetAwaiter().GetResult()</c> inside this handler, which DEADLOCKS the
    /// main thread there: the app stays alive, the fragment request is the last line it ever logs, and every
    /// later probe simply never reports — exactly what "evaluation stopped answering" looks like from
    /// outside.</description></item>
    /// </list>
    /// <para>
    /// So the read moved to construction and the answer comes from memory, and the same repair now works on
    /// both platforms. ⚠ <b>The lesson is bigger than this defect:</b> a hypothesis discarded because its
    /// first implementation failed was never actually tested — and this one blocked a real adopter bug for
    /// three days.
    /// </para>
    /// </remarks>
    private WebViewResourceResponse? RepairDocumentRequest(Uri uri)
    {
        if (!WebViewResourceRequest.IsRootWithFragment(uri)) return null;

        // 🔴 THE URI DECIDES ONLY *WHETHER* TO ACT, NEVER *WHAT* TO SERVE, and it must stay that way. `uri`
        // is page-controlled — a page can navigate itself to any fragment it likes — so deriving any part of
        // the served path from it would put page input straight into an app-package path. The path is built
        // in `ReadBundleDocument` from app configuration alone; there is no containment check on this route
        // because there is deliberately no input to contain.
        //
        // ⚠ NEVER BLOCK HERE, and this is the line the whole repair turns on. `_document` is warmed once at
        // construction; if it is not ready we DECLINE rather than wait. `.Result`/`.GetAwaiter().GetResult()`
        // is what the Android arm used to do and what deadlocks iOS outright (see the remarks).
        if (_document is not { IsCompletedSuccessfully: true, Result: { } document })
        {
            Log(() => $"[Shenora.Mobile] Fragment document repair declined — the bundle is not readable, or "
                    + $"not yet read, for '{uri.Fragment}'.");
            return null;
        }

        Log(() => $"[Shenora.Mobile] Served the app's document for a fragment document request "
                + $"(fragment '{uri.Fragment}').");
        return WebViewResourceResponse.Ok(new MemoryStream(document.Bytes, writable: false), document.ContentType);
    }

    /// <summary>The bundle's own document, read ONCE so the repair never has to do I/O on the platform's thread.</summary>
    private readonly record struct BundleDocument(byte[] Bytes, string ContentType);

    /// <summary>
    /// Start reading the app's document in the background, at construction, so a later fragment repair can
    /// answer from memory.
    /// <para>
    /// 🔴 <b>The path is built entirely from APP CONFIGURATION.</b> The request URI decides only WHETHER to
    /// repair, never WHAT to serve — a page can navigate itself to any fragment it likes, so deriving any
    /// part of the asset name from it would put page input straight into an app-package path. There is
    /// deliberately no containment check on this path because there is deliberately no input.
    /// </para>
    /// <para>
    /// Read from the WEBVIEW rather than from constants: both are settable, and a kit that assumed the
    /// defaults would silently stop repairing the apps that changed them.
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
                // No bundle to serve. The repair declines — an app that serves its document some other way
                // must be left exactly as it was, and turning a working page into a fixed 404 to repair a
                // defect it does not have would be the worse failure by far.
                Log(() => $"[Shenora.Mobile] The app document '{asset}' could not be read, so the fragment "
                        + $"repair will decline: {ex.Message}");
                return null;
            }
        });
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
