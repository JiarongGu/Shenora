using Shenora.Core.Shell;

namespace Shenora.Core.WebView;

/// <summary>
/// The middleware registry and composition behind every shell's <see cref="IWebViewInterceptor"/> — the
/// portable half, so a shell implementation is only the platform's event glue.
/// <para>
/// It lives here rather than being written once per shell because it is where this shape gets subtly wrong:
/// the chain must be built back-to-front so index 0 runs FIRST, the array must be copy-on-write because it is
/// read on a platform event thread while a route is added or removed from another, and removing a route must
/// remove exactly the one registration rather than every equal-looking delegate. Three shells hand-rolling
/// that is three chances to get one of them wrong, and the same reasoning that made
/// <c>WebViewBundleServing</c> and <c>IUiDispatcher</c> single owners.
/// </para>
/// <para>
/// It is also the only way any of this is TESTABLE: composition order, decline-and-fall-through, wrapping, and
/// removal are all provable here with no webview at all. A rule reachable only through a live browser is a
/// rule nothing tests (P5.5 H7).
/// </para>
/// </summary>
public sealed class WebViewResourcePipeline
{
    /// <summary>The terminal step: nothing claimed the request, so the platform should handle it.</summary>
    private static readonly WebViewResourceHandler Decline =
        static (_, _) => Task.FromResult<WebViewResourceResponse?>(null);

    private readonly object _gate = new();

    // Copy-on-write, read WITHOUT a lock. The array is read on the platform's event thread while a route can
    // be added or removed from another — the same reason SessionController's tap arrays are copy-on-write:
    // List<T>.ToArray() reads _size then copies, so an Add in between throws or copies a torn view.
    private volatile WebViewResourceMiddleware[] _middleware = [];

    /// <summary>
    /// True when no middleware is registered — the shell's fast path. Worth checking before anything else in
    /// an event handler: with no routes registered the interceptor must cost as close to nothing as possible,
    /// because on desktop the same handler also serves the app's own bundle.
    /// </summary>
    public bool IsEmpty => _middleware.Length == 0;

    /// <summary>
    /// Add a middleware. Runs in registration order; dispose the return value to remove just this one.
    /// </summary>
    public IDisposable Use(WebViewResourceMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        lock (_gate) _middleware = [.. _middleware, middleware];
        return new Registration(this, middleware);
    }

    /// <summary>
    /// Compose the registered middleware into one handler, or null when there are none.
    /// <para>
    /// Null rather than a handler that always declines, so a caller cannot accidentally pay for a pipeline
    /// that does not exist — on desktop that difference is a thread-pool hop and a deferral per request.
    /// </para>
    /// </summary>
    public WebViewResourceHandler? Build()
    {
        // ONE read of the volatile field: re-reading it mid-build could compose half of one snapshot and half
        // of another.
        var snapshot = _middleware;
        if (snapshot.Length == 0) return null;

        // Back-to-front, so snapshot[0] is the outermost layer and therefore runs first.
        var next = Decline;
        for (var i = snapshot.Length - 1; i >= 0; i--)
        {
            var middleware = snapshot[i];
            var downstream = next;
            next = (request, cancellationToken) => middleware(request, downstream, cancellationToken);
        }
        return next;
    }

    /// <summary>Drop every registration — a disposing shell, so an outlived route cannot answer for the next page.</summary>
    public void Clear()
    {
        lock (_gate) _middleware = [];
    }

    private void Remove(WebViewResourceMiddleware middleware)
    {
        // ReferenceEquals, not Equals: two registrations of the SAME method group are equal delegates, and
        // disposing one of them must not silently remove the other.
        lock (_gate) _middleware = [.. _middleware.Where(m => !ReferenceEquals(m, middleware))];
    }

    /// <summary>The handle returned by <see cref="Use"/>. Idempotent: disposing twice removes one route.</summary>
    private sealed class Registration(WebViewResourcePipeline owner, WebViewResourceMiddleware middleware)
        : IDisposable
    {
        private bool _removed;

        public void Dispose()
        {
            if (_removed) return;
            _removed = true;
            owner.Remove(middleware);
        }
    }
}
