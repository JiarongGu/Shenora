using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.WebView2.Sessions;

/// <summary>One JSON API response intercepted from a session's page (see <see cref="RenderSession.OnNetwork"/>).</summary>
public sealed record SessionApiCall(string Url, string Method, string ContentType, string BodySample);

/// <summary>
/// A leased, driveable off-screen browser session, ported from the server-backed sibling: the
/// caller navigates it, runs its OWN JS on the live page, reads the HTML, calls CDP methods, and
/// installs live interceptors for the page's JSON API responses + posted messages. The pool owns
/// the WebView2 + the UI thread; the caller owns ALL interpretation (its settle poll, its DOM
/// analysis). Every WebView2 touch marshals onto the pool's UI-thread anchor (a
/// <c>BeginInvoke</c> + a <see cref="TaskCompletionSource{TResult}"/>); the event handlers fire
/// on that same UI thread, so interceptor bookkeeping needs no cross-thread locking.
/// <see cref="DisposeAsync"/> returns the instance to the pool — idempotent and guarded: after
/// dispose (or if the message loop is gone) every op fails gracefully.
/// </summary>
public sealed class RenderSession : IAsyncDisposable
{
    private const int MaxBodySample = 4096; // bounded JSON body sample per intercepted API call

    private readonly RenderSessionPool _pool;
    private readonly RenderSessionPool.PoolInstance _instance;
    private readonly Control _anchor;
    private readonly WebView2Control _web;
    private readonly Func<Uri, CancellationToken, Task<bool>>? _navigationGuard;

    // Live interceptors the caller installed (subscribe on the UI thread; the returned handle
    // unsubscribes there too).
    private readonly List<EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs>> _netHandlers = [];
    private readonly List<EventHandler<CoreWebView2WebMessageReceivedEventArgs>> _msgHandlers = [];
    private int _disposed; // 0 live, 1 disposed — dispose is idempotent + gates every op

    internal RenderSession(RenderSessionPool pool, RenderSessionPool.PoolInstance instance, Control anchor,
        Func<Uri, CancellationToken, Task<bool>>? navigationGuard)
    {
        _pool = pool;
        _instance = instance;
        _anchor = anchor;
        _web = instance.Web;
        _navigationGuard = navigationGuard;
    }

    /// <summary>
    /// Navigate to an absolute http(s) URL and wait for the DOCUMENT to load only
    /// (NavigationCompleted) — NOT for JS to settle; the caller decides "settled" itself via
    /// script polling and its interceptors. A 30 s hard cap keeps a hung load from wedging the
    /// leased instance. When <see cref="RenderSessionPoolOptions.NavigationGuard"/> is set,
    /// every navigation must pass it first — wire your SSRF/allowlist policy there: a session
    /// often navigates DATA-DRIVEN URLs, and services also reachable from localhost make an
    /// unguarded navigate a server-side request forgery.
    /// </summary>
    public Task NavigateAsync(string url, CancellationToken cancellationToken = default) => OnUiAsync(async () =>
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("url must be an absolute http(s) URL", nameof(url));
        if (_navigationGuard is { } guard && !await guard(uri, cancellationToken).ConfigureAwait(true))
            throw new InvalidOperationException($"Navigation refused by the navigation guard: {uri.Host}");

        var navDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e) => navDone.TrySetResult(e.IsSuccess);
        _web.CoreWebView2.NavigationCompleted += OnNav;
        try
        {
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            overall.CancelAfter(TimeSpan.FromSeconds(30)); // hard cap so a hung load can't wedge the lease
            _web.CoreWebView2.Navigate(uri.ToString());
            // WhenAny never throws; the cap firing and the CALLER cancelling both complete the
            // Delay task — but they mean different things. The 30 s cap is a soft "return what's
            // there" (the caller polls); the caller's own token means "I gave up", which MUST
            // surface so it can't be mistaken for a completed load.
            await Task.WhenAny(navDone.Task, Task.Delay(Timeout.Infinite, overall.Token)).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _web.CoreWebView2.NavigationCompleted -= OnNav;
        }
        return true;
    }, cancellationToken);

    /// <summary>
    /// Run JS on the live page and return WebView2's JSON-encoded result (a quoted string / a
    /// JSON value) — the caller deserializes it.
    /// </summary>
    public Task<string?> ExecuteScriptAsync(string javaScript, CancellationToken cancellationToken = default) =>
        OnUiAsync<string?>(async () => await _web.ExecuteScriptAsync(javaScript).ConfigureAwait(true), cancellationToken);

    /// <summary>The current rendered HTML (JSON-decoded), or null.</summary>
    public Task<string?> GetHtmlAsync(CancellationToken cancellationToken = default) =>
        OnUiAsync(() => SessionBrowser.GetHtmlAsync(_web), cancellationToken);

    /// <summary>
    /// Call a CDP method on the live page and return its JSON result. Guarded: any failure (an
    /// unsupported method, a disposed session) surfaces as null, never a wedge, so the caller
    /// degrades gracefully.
    /// </summary>
    public Task<string?> CallDevToolsAsync(string method, string parametersJson, CancellationToken cancellationToken = default) =>
        OnUiAsync<string?>(async () =>
        {
            try
            {
                return await _web.CoreWebView2.CallDevToolsProtocolMethodAsync(
                    method, string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson).ConfigureAwait(true);
            }
            catch
            {
                return null;
            }
        }, cancellationToken);

    /// <summary>
    /// Install a live interceptor for the page's JSON API responses: for each response whose
    /// content-type contains "json", a bounded body sample is read (best-effort) and
    /// <paramref name="handler"/> is invoked with a <see cref="SessionApiCall"/>. The event
    /// fires on the UI thread, so the handler must be quick / non-blocking. The returned handle
    /// unsubscribes.
    /// </summary>
    public IDisposable OnNetwork(Action<SessionApiCall> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        void OnResp(object? s, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                var headers = e.Response.Headers;
                var contentType = headers.Contains("content-type") ? headers.GetHeader("content-type") : "";
                if (contentType is null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return;
                // Fire-and-forget the bounded body read; a failed read still delivers the
                // endpoint with an empty sample so the caller at least sees the URL + shape.
                _ = DeliverAsync(e.Response, e.Request.Uri, e.Request.Method ?? "GET", contentType, handler);
            }
            catch
            {
                // skip this response — interception is best-effort, never breaks the page
            }
        }

        OnUiFireAndForget(() =>
        {
            _web.CoreWebView2.WebResourceResponseReceived += OnResp;
            _netHandlers.Add(OnResp);
        });
        return new Unsubscriber(() => OnUiFireAndForget(() =>
        {
            try { _web.CoreWebView2.WebResourceResponseReceived -= OnResp; } catch { }
            _netHandlers.Remove(OnResp);
        }));
    }

    /// <summary>
    /// Install a listener for messages the page posts via <c>chrome.webview.postMessage(...)</c>
    /// — so JS the caller injects (a MutationObserver, an event hook) can stream DOM events
    /// back. Prefers the string form, falls back to raw JSON. The returned handle unsubscribes.
    /// </summary>
    public IDisposable OnMessage(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        void OnMsg(object? s, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string payload;
            try { payload = e.TryGetWebMessageAsString(); }
            catch
            {
                try { payload = e.WebMessageAsJson; } catch { return; } // neither form → drop it
            }
            try { handler(payload); }
            catch
            {
                // isolate the caller's handler — one throw can't break the listener
            }
        }

        OnUiFireAndForget(() =>
        {
            _web.CoreWebView2.WebMessageReceived += OnMsg;
            _msgHandlers.Add(OnMsg);
        });
        return new Unsubscriber(() => OnUiFireAndForget(() =>
        {
            try { _web.CoreWebView2.WebMessageReceived -= OnMsg; } catch { }
            _msgHandlers.Remove(OnMsg);
        }));
    }

    /// <summary>
    /// Return the leased instance to the pool. Idempotent (only the first call does anything):
    /// unsubscribes every still-installed interceptor on the UI thread, then hands the instance
    /// back (the pool resets it to about:blank + releases the capacity slot). Never throws — an
    /// <c>await using</c> must be safe.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        OnUiFireAndForget(() =>
        {
            foreach (var h in _netHandlers) { try { _web.CoreWebView2.WebResourceResponseReceived -= h; } catch { } }
            foreach (var h in _msgHandlers) { try { _web.CoreWebView2.WebMessageReceived -= h; } catch { } }
            _netHandlers.Clear();
            _msgHandlers.Clear();
        });
        _pool.Return(_instance); // resets + re-pools + releases the slot (best-effort, on the UI thread)
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Run <paramref name="work"/> ON THE UI THREAD and await its result — the one marshal every
    /// op uses. Fails gracefully: a disposed session, a dead message loop, or a thrown delegate
    /// all surface as the delegate's own exception path, never a wedge.
    /// </summary>
    private Task<T> OnUiAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Task.FromException<T>(new ObjectDisposedException(nameof(RenderSession)));
        var anchor = _anchor;
        if (anchor.IsDisposed)
            return Task.FromException<T>(new InvalidOperationException("The session host is gone."));
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            anchor.BeginInvoke(new Action(async () =>
            {
                if (cancellationToken.IsCancellationRequested) { tcs.TrySetCanceled(cancellationToken); return; }
                try { tcs.TrySetResult(await work().ConfigureAwait(true)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }));
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        return tcs.Task;
    }

    /// <summary>
    /// Marshal an interceptor (un)subscribe onto the UI thread WITHOUT awaiting — the returned
    /// IDisposable must be non-blocking. Best-effort: if the loop is gone it's a no-op.
    /// </summary>
    private void OnUiFireAndForget(Action work)
    {
        var anchor = _anchor;
        if (anchor.IsDisposed || !anchor.IsHandleCreated) return;
        try { anchor.BeginInvoke(work); } catch { }
    }

    /// <summary>Bounded body-sample read, then deliver — best-effort throughout (UI thread).</summary>
    private static async Task DeliverAsync(CoreWebView2WebResourceResponseView response, string url, string method,
        string contentType, Action<SessionApiCall> handler)
    {
        var sample = "";
        try
        {
            await using var stream = await response.GetContentAsync().ConfigureAwait(true);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                var buffer = new char[MaxBodySample];
                var read = await reader.ReadBlockAsync(buffer, 0, MaxBodySample).ConfigureAwait(true);
                sample = new string(buffer, 0, read);
            }
        }
        catch
        {
            // content unavailable (already consumed / streamed) — deliver the endpoint, drop the sample
        }
        try { handler(new SessionApiCall(url, method, contentType, sample)); }
        catch
        {
            // the caller's handler threw — isolate it; one bad call can't break interception
        }
    }

    /// <summary>A one-shot IDisposable that runs an unsubscribe action once.</summary>
    private sealed class Unsubscriber(Action off) : IDisposable
    {
        private Action? _off = off;

        public void Dispose() => Interlocked.Exchange(ref _off, null)?.Invoke();
    }
}
