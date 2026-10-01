using Microsoft.Extensions.Logging;
using System.Text.Json;
using Shenora.Core.Shell;

namespace Shenora.Core.Sessions;

/// <summary>
/// A leased, driveable off-screen browser session: the caller navigates it, runs its OWN script on the live page, reads
/// the HTML and calls DevTools protocol methods. The pool owns the browser and the UI thread; the caller owns ALL
/// interpretation (its settle poll, its DOM analysis). Every browser touch marshals onto the shell's UI thread.
/// <see cref="DisposeAsync"/> returns the instance to the pool: idempotent and guarded, and after it, or once the
/// shell's loop is gone, every op fails gracefully.
///
/// 🔴 It DRIVES; it does not report. What the page does (its API responses, its posted messages, its navigations)
/// arrives on the app's <see cref="Shenora.Core.Events.IEventBus"/> as <see cref="SessionEvents"/>, scoped by
/// <see cref="Id"/>.
/// </summary>
public sealed class RenderSession : IAsyncDisposable
{
    private readonly RenderSessionPool _pool;
    private readonly RenderSessionPool.PoolInstance _instance;
    private readonly IUiDispatcher _ui;   // the one marshal owner (D19/D20)
    private readonly ISessionBrowser _browser;
    private readonly Func<Uri, CancellationToken, Task<bool>>? _navigationGuard;
    private readonly TimeSpan _opTimeout;
    private readonly TimeSpan _navigationTimeout;
    private readonly ILogger? _log;

    private int _disposed; // 0 live, 1 disposed: dispose is idempotent + gates every op

    internal RenderSession(RenderSessionPool pool, RenderSessionPool.PoolInstance instance, RenderSessionPoolOptions options)
    {
        _pool = pool;
        _instance = instance;
        _ui = options.Host.Ui;
        _browser = instance.Browser;
        _navigationGuard = options.NavigationGuard;
        _opTimeout = options.OpTimeout;
        _navigationTimeout = options.NavigationTimeout;
        _log = options.Log;
        Id = instance.Scope;
    }

    /// <summary>
    /// This lease's identity: the SCOPE its browser publishes every <see cref="SessionEvents"/> under. Subscribe with it
    /// to hear only this session: <c>bus.SubscribeToModule(SessionEvents.Module, session.Id, handler)</c>.
    /// ⚠ <b>It belongs to the LEASE, not to the pooled browser</b>: the same browser gets a different id next time it is
    /// leased, so a subscription outliving this session stops receiving anything rather than picking up the next
    /// tenant's pages.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Navigate to an absolute http(s) URL and wait for the DOCUMENT to load only, NOT for script to settle; the caller
    /// decides "settled" itself via script polling. <see cref="RenderSessionPoolOptions.NavigationTimeout"/> caps the wait
    /// so a hung load can't wedge the leased instance, and every navigation must first pass
    /// <see cref="RenderSessionPoolOptions.NavigationGuard"/> when one is set.
    /// </summary>
    public Task NavigateAsync(string url, CancellationToken cancellationToken = default) => OnUiAsync(async () =>
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("url must be an absolute http(s) URL", nameof(url));
        if (_navigationGuard is { } guard && !await guard(uri, cancellationToken).ConfigureAwait(true))
            throw new InvalidOperationException($"Navigation refused by the navigation guard: {uri.Host}");

        // Record what the guard actually vetted. The pool's navigation policy cancels an unvetted hop to another
        // authority from here on, so a 302 to somewhere the guard never saw can't be followed.
        //
        // 🔴 AUTHORITY, NOT HOST: the PORT is half the identity here. `Uri.Host` drops it, so approving `127.0.0.1:3000`
        // (an app's own dev origin, and `IsLoopback` approves all of loopback) also approved a redirect to
        // `127.0.0.1:8080/admin`: the exact hop the policy exists to close. `Uri.Authority` keeps the port and still
        // omits it when it is the scheme's default, so an http → https hop on the same host is unaffected.
        _instance.ApprovedOrigin = uri.Authority;

        var navDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNav(SessionNavigationResult result) => navDone.TrySetResult(result.Success);
        _browser.NavigationCompleted += OnNav;
        try
        {
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            overall.CancelAfter(_navigationTimeout); // cap so a hung load can't wedge the lease
            _browser.Navigate(uri.ToString());
            // WhenAny never throws, and the cap firing and the CALLER cancelling both complete the Delay task, but they
            // mean different things. The cap is a soft "return what's there"; the caller's own token means "I gave up",
            // which MUST surface so it can't be mistaken for a completed load.
            await Task.WhenAny(navDone.Task, Task.Delay(Timeout.Infinite, overall.Token)).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _browser.NavigationCompleted -= OnNav;
        }
        return true;
    }, cancellationToken);

    /// <summary>
    /// Run script on the live page and return its value JSON-encoded (a quoted string, a JSON value); the caller
    /// deserializes it.
    /// </summary>
    public Task<string?> ExecuteScriptAsync(string javaScript, CancellationToken cancellationToken = default) =>
        OnUiAsync(() => _browser.ExecuteScriptAsync(javaScript), cancellationToken);

    /// <summary>The current rendered HTML, or null.</summary>
    public Task<string?> GetHtmlAsync(CancellationToken cancellationToken = default) =>
        OnUiAsync(async () =>
        {
            try
            {
                var json = await _browser.ExecuteScriptAsync("document.documentElement.outerHTML").ConfigureAwait(true);
                return json is null or "null" ? null : JsonSerializer.Deserialize<string>(json);
            }
            catch
            {
                return null;
            }
        }, cancellationToken);

    /// <summary>
    /// Call a DevTools protocol method on the live page and return its JSON result. Guarded: any failure (an unsupported
    /// method, a disposed session) surfaces as null, never a wedge, so the caller degrades gracefully.
    /// </summary>
    public Task<string?> CallDevToolsAsync(string method, string parametersJson, CancellationToken cancellationToken = default) =>
        OnUiAsync<string?>(async () =>
        {
            try
            {
                return await _browser.CallDevToolsAsync(method,
                    string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson).ConfigureAwait(true);
            }
            catch
            {
                return null;
            }
        }, cancellationToken);

    /// <summary>
    /// Return the leased instance to the pool. Idempotent (only the first call does anything): hands the instance back,
    /// and the pool resets it to about:blank and releases the capacity slot. Never throws, so an <c>await using</c> is
    /// safe.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _pool.Return(_instance); // resets + re-pools + releases the slot (best-effort, on the UI thread)
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Run <paramref name="work"/> ON THE UI THREAD and await its result: the one marshal every op uses. A disposed
    /// session, a dead loop or a thrown delegate all surface as the delegate's own exception path, never a wedge. Bounded
    /// by <see cref="RenderSessionPoolOptions.OpTimeout"/>, and an operation the UI thread never finishes POISONS the
    /// instance (see <see cref="RunBoundedAsync{T}"/>).
    /// </summary>
    private Task<T> OnUiAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return Task.FromException<T>(new ObjectDisposedException(nameof(RenderSession)));

        // The ONE marshal owner. It observes the token via WaitAsync, so the CALLER always escapes even when the UI
        // thread never runs the body: an op against a page whose script thread is blocked otherwise never cancels, the
        // lease never returns, and the pool's permit is gone for good.
        return RunBoundedAsync(work, cancellationToken);
    }

    /// <summary>
    /// The other half: escaping the await hands the CALLER back but leaves the wedged page in the pool, so this adds a
    /// BOUNDED wait (<see cref="RenderSessionPoolOptions.OpTimeout"/>, because a caller that passes no token has no escape
    /// at all) and POISONS the instance when the body never completed, so the pool discards it instead of re-pooling it.
    /// <para>
    /// ⚠ "Never completed" is TRACKED rather than inferred: a body that ran and threw (a bad URL, a guard refusal, a
    /// caller token observed INSIDE the body) leaves the instance perfectly reusable, and discarding it would cost
    /// seconds of browser startup on every ordinary error.
    /// </para>
    /// </summary>
    private async Task<T> RunBoundedAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        var finished = 0;
        async Task<T> Tracked()
        {
            try { return await work().ConfigureAwait(true); }
            finally { Interlocked.Exchange(ref finished, 1); } // ran to an outcome: success or throw
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(_opTimeout);
        try
        {
            return await _ui.InvokeAsync(Tracked, bounded.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Only a token that actually tripped means "we walked away while it was still running". A NotReady/Gone
            // dispatcher failure is a composition problem, not a wedged page.
            if (Volatile.Read(ref finished) == 0 && bounded.IsCancellationRequested)
            {
                _instance.Poisoned = true;
                // Guarded: a throwing app logger here would REPLACE the diagnosis below with its own exception, so the
                // caller would never learn the operation was abandoned.
                SessionLog.Try(_log, l => l.LogWarning(
                    "A render-session operation was abandoned after {Timeout}s with the operation still outstanding; the " +
                    "instance is poisoned and will be discarded when the lease returns.",
                    _opTimeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));

                // Report the WEDGE as a timeout, not as the caller's own cancellation, unless the caller really did
                // cancel, in which case its OperationCanceledException must survive. The original stays as the inner
                // exception.
                if (!cancellationToken.IsCancellationRequested)
                {
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
