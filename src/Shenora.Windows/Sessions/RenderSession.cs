using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;
using Shenora.Core.Shell;
using Shenora.Core.Ipc;

namespace Shenora.Windows;

/// <summary>
/// A leased, driveable off-screen browser session, ported from the server-backed sibling: the
/// caller navigates it, runs its OWN JS on the live page, reads the HTML and calls CDP methods. The
/// pool owns the WebView2 + the UI thread; the caller owns ALL interpretation (its settle poll, its
/// DOM analysis). Every WebView2 touch marshals onto the pool's UI-thread anchor (a
/// <c>BeginInvoke</c> + a <see cref="TaskCompletionSource{TResult}"/>).
/// <see cref="DisposeAsync"/> returns the instance to the pool — idempotent and guarded: after
/// dispose (or if the message loop is gone) every op fails gracefully.
///
/// 🔴 It DRIVES; it does not report. What the page does — its API responses, its posted messages, its
/// navigations — arrives on the app's <see cref="Shenora.Core.Events.IEventBus"/> as
/// <see cref="SessionEvents"/>, scoped by <see cref="Id"/>.
/// </summary>
public sealed class RenderSession : IAsyncDisposable
{
    private readonly RenderSessionPool _pool;
    private readonly RenderSessionPool.PoolInstance _instance;
    private readonly Shenora.Core.Shell.IUiDispatcher _ui;   // the one marshal owner (D19/D20)
    private readonly WebView2Control _web;
    private readonly Func<Uri, CancellationToken, Task<bool>>? _navigationGuard;
    private readonly TimeSpan _opTimeout;
    private readonly TimeSpan _navigationTimeout;
    private readonly Microsoft.Extensions.Logging.ILogger? _log;

    private int _disposed; // 0 live, 1 disposed — dispose is idempotent + gates every op

    internal RenderSession(RenderSessionPool pool, RenderSessionPool.PoolInstance instance,
        RenderSessionPoolOptions options)
    {
        _pool = pool;
        _instance = instance;
        _ui = new Shenora.Windows.WinFormsUiDispatcher(options.Anchor);
        _web = instance.Web;
        _navigationGuard = options.NavigationGuard;
        _opTimeout = options.OpTimeout;
        _navigationTimeout = options.NavigationTimeout;
        _log = options.Log;
        Id = instance.Scope;
    }

    /// <summary>
    /// This lease's identity — the SCOPE its browser publishes every <see cref="SessionEvents"/> under.
    /// Subscribe with it to hear only this session:
    /// <c>bus.SubscribeToModule(SessionEvents.Module, session.Id, handler)</c>.
    /// <para>
    /// ⚠ <b>It belongs to the LEASE, not to the pooled browser.</b> The same browser gets a different id
    /// next time it is leased, so a subscription outliving this session stops receiving anything rather
    /// than silently picking up the next tenant's pages. Dispose it with the session.
    /// </para>
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Navigate to an absolute http(s) URL and wait for the DOCUMENT to load only
    /// (NavigationCompleted) — NOT for JS to settle; the caller decides "settled" itself via
    /// script polling and its interceptors. <see cref="RenderSessionPoolOptions.NavigationTimeout"/>
    /// caps the wait so a hung load can't wedge the leased instance. When
    /// <see cref="RenderSessionPoolOptions.NavigationGuard"/> is set,
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

        // Record what the guard actually vetted. The pool's NavigationStarting policy cancels an
        // unvetted CROSS-ORIGIN hop from here on, so a 302 to somewhere the guard never saw (the classic
        // "redirect me to 127.0.0.1" SSRF step) can't be followed. See
        // RenderSessionPool.WireNavigationPolicy for why the event can enforce only a sync rule.
        //
        // 🔴 AUTHORITY, NOT HOST — the PORT is half the identity here. `Uri.Host` drops it, so approving
        // `127.0.0.1:3000` (an app's own dev origin, and `IsLoopback` approves all of loopback) also
        // approved a redirect to `127.0.0.1:8080/admin`: the exact hop the policy's own doc gives as the
        // thing it closes. `Uri.Authority` keeps the port and still omits it when it is the scheme's
        // default, so the documented http -> https allowance is unaffected.
        _instance.ApprovedOrigin = uri.Authority;

        var navDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e) => navDone.TrySetResult(e.IsSuccess);
        _web.CoreWebView2.NavigationCompleted += OnNav;
        try
        {
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            overall.CancelAfter(_navigationTimeout); // cap so a hung load can't wedge the lease
            _web.CoreWebView2.Navigate(uri.ToString());
            // WhenAny never throws; the cap firing and the CALLER cancelling both complete the
            // Delay task — but they mean different things. The cap is a soft "return what's
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
    /// Return the leased instance to the pool. Idempotent (only the first call does anything): hands the
    /// instance back, and the pool resets it to about:blank + releases the capacity slot. Never throws —
    /// an <c>await using</c> must be safe.
    /// <para>
    /// It used to also unsubscribe this lease's interceptors, and no longer needs to: observation moved
    /// to <see cref="SessionEvents"/>, where the SCOPE does that job. The next lease of this browser gets
    /// a new <see cref="Id"/>, so a subscription left behind simply stops receiving — rather than, as the
    /// taps risked, streaming the next tenant's traffic to the previous caller.
    /// </para>
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _pool.Return(_instance); // resets + re-pools + releases the slot (best-effort, on the UI thread)
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Run <paramref name="work"/> ON THE UI THREAD and await its result — the one marshal every
    /// op uses. Fails gracefully: a disposed session, a dead message loop, or a thrown delegate
    /// all surface as the delegate's own exception path, never a wedge. Bounded by
    /// <see cref="RenderSessionPoolOptions.OpTimeout"/>, and an operation the UI thread never
    /// finishes POISONS the instance (see <see cref="RunBoundedAsync{T}"/>).
    /// </summary>
    private Task<T> OnUiAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Task.FromException<T>(new ObjectDisposedException(nameof(RenderSession)));

        // The ONE marshal owner (P5.5 H4.2). This replaced a hand-rolled BeginInvoke + TCS that
        // checked the cancellation token ONCE, inside the posted delegate, and then awaited the body
        // with no way to observe it again — so an op against a page whose JS thread is blocked (an
        // alert(), a spin loop) could never be cancelled, the lease never returned, and the pool's
        // permit was gone for the process lifetime. The dispatcher observes the token via WaitAsync,
        // so the CALLER always escapes even when the UI thread never runs the body.
        return RunBoundedAsync(work, cancellationToken);
    }

    /// <summary>
    /// The other half of that fix (P5.5 H2). Escaping the await was never enough on its own:
    /// <c>WaitAsync</c> hands the CALLER back, but the wedged page is still sitting in the pool, so
    /// <see cref="DisposeAsync"/> re-pooled it and the next lease inherited the same dead instance.
    /// So this adds the two missing pieces:
    /// <list type="bullet">
    /// <item>a BOUNDED wait — <see cref="RenderSessionPoolOptions.OpTimeout"/> — because a caller that
    /// passes no token (every parameterless overload does) had no escape at all; and</item>
    /// <item>POISONING the instance when the body never completed, so
    /// <see cref="RenderSessionPool.Return"/> discards it instead of re-pooling it.</item>
    /// </list>
    /// "Never completed" is tracked rather than inferred, and that distinction matters: a body that
    /// ran and threw (a bad URL, a guard refusal, a caller token observed INSIDE the body) leaves the
    /// instance perfectly reusable, and discarding it would cost seconds of browser startup on every
    /// ordinary error.
    /// </summary>
    private async Task<T> RunBoundedAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        var finished = 0;
        async Task<T> Tracked()
        {
            try { return await work().ConfigureAwait(true); }
            finally { Interlocked.Exchange(ref finished, 1); } // ran to an outcome — success or throw
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(_opTimeout);
        try
        {
            return await _ui.InvokeAsync(Tracked, bounded.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Only a token that actually tripped means "we walked away while it was still running".
            // A NotReady/Gone dispatcher failure is a composition problem, not a wedged page.
            if (Volatile.Read(ref finished) == 0 && bounded.IsCancellationRequested)
            {
                _instance.Poisoned = true;
                // Guarded: a throwing app logger here would REPLACE the diagnosis below with its own
                // exception, so the caller would never learn the operation was abandoned.
                SessionLog.Try(_log, l => l.LogWarning(
                    "A render-session operation was abandoned after {Timeout}s with the operation still " +
                    "outstanding; the instance is poisoned and will be discarded when the lease returns.",
                    _opTimeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));

                // Report the WEDGE as a timeout, not as the caller's own cancellation — unless the
                // caller really did cancel, in which case its OperationCanceledException must survive.
                if (!cancellationToken.IsCancellationRequested)
                {
                    // Keep the original cancellation as the inner exception — the replacement message
                    // is the diagnosis, not a reason to lose the stack that produced it.
                    throw new TimeoutException(
                        $"The render-session operation did not complete within {_opTimeout.TotalSeconds:0}s. " +
                        "The page's script thread is most likely blocked; the session instance has been discarded.",
                        ex);
                }
            }
            throw;
        }
    }

}
