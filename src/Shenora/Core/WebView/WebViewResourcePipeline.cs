namespace Shenora.Core.WebView;

/// <summary>
/// The middleware registry and composition behind every shell's <see cref="IWebViewInterceptor"/> — the
/// portable half, so a shell implementation is only the platform's event glue, and so composition order,
/// decline-and-fall-through, wrapping and removal are all testable with no webview at all.
/// </summary>
public sealed class WebViewResourcePipeline
{
    /// <summary>The terminal step: nothing claimed the request, so the platform should handle it.</summary>
    private static readonly WebViewResourceHandler Decline =
        static (_, _) => Task.FromResult<WebViewResourceResponse?>(null);

    private readonly object _gate = new();

    // Copy-on-write, read WITHOUT a lock: the array is read on the platform's event thread while a route
    // can be added or removed from another, and List<T>.ToArray() reads _size then copies, so an Add in
    // between throws or copies a torn view.
    private volatile WebViewResourceMiddleware[] _middleware = [];

    /// <summary>
    /// True when no middleware is registered — the shell's fast path, worth checking first in an event
    /// handler because on desktop the same handler also serves the app's own bundle.
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
    /// Compose the registered middleware into one handler, or null when there are none — null rather than
    /// an always-declines handler, so a caller cannot pay a thread-pool hop and a deferral per request for
    /// a pipeline that does not exist.
    /// </summary>
    public WebViewResourceHandler? Build()
    {
        // ONE read of the volatile field: re-reading it mid-build could compose half of one snapshot and
        // half of another.
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
        // disposing one must not silently remove the other. And ONE slot, not a filter: one delegate
        // OBJECT registered twice fills two reference-equal slots, so a filter strips both and makes the
        // second dispose a silent no-op.
        lock (_gate)
        {
            var snapshot = _middleware;
            var index = -1;
            for (var i = 0; i < snapshot.Length; i++)
            {
                if (ReferenceEquals(snapshot[i], middleware)) { index = i; break; }
            }
            if (index < 0) return;
            var next = new WebViewResourceMiddleware[snapshot.Length - 1];
            Array.Copy(snapshot, 0, next, 0, index);
            Array.Copy(snapshot, index + 1, next, index, snapshot.Length - index - 1);
            _middleware = next;
        }
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
