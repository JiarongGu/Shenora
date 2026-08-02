using Microsoft.Web.WebView2.Core;
using Shenora.Windows;

namespace Shenora.Tests.WebView2Sessions;

/// <summary>
/// The shared-environment policy (P5.5 H2 / the item H4.4 left open). Real environment creation needs
/// a browser process, so these drive the cache's DECISIONS through a creation delegate — which is all
/// the type owns: reuse-in-flight (the anti-orphan half), don't-cache-a-failure, and release.
/// </summary>
public class SessionEnvironmentCacheTests
{
    private static Task<CoreWebView2Environment> NeverCompletes() =>
        new TaskCompletionSource<CoreWebView2Environment>().Task;

    [Fact]
    public void An_in_flight_creation_is_reused_instead_of_started_again()
    {
        var cache = new SessionEnvironmentCache();
        var created = 0;
        Task<CoreWebView2Environment> Create() { created++; return NeverCompletes(); }

        var first = cache.GetOrCreate(Create);
        var second = cache.GetOrCreate(Create);

        // This is the fix, not an optimization: InitTimeout abandons the AWAIT, never the creation, so
        // starting a second CreateAsync queued another browser process onto the same locked profile —
        // adding to the very lock the timeout's error message blames.
        Assert.Same(first, second);
        Assert.Equal(1, created);
    }

    [Fact]
    public void A_completed_environment_is_reused()
    {
        var cache = new SessionEnvironmentCache();
        var done = new TaskCompletionSource<CoreWebView2Environment>();
        done.SetResult(null!); // the pool's N instances share ONE environment per profile
        var other = NeverCompletes();

        Assert.Same(done.Task, cache.GetOrCreate(() => done.Task));
        Assert.Same(done.Task, cache.GetOrCreate(() => other));
    }

    [Fact]
    public void A_faulted_creation_is_not_cached_forever()
    {
        var cache = new SessionEnvironmentCache();
        var faulted = new TaskCompletionSource<CoreWebView2Environment>();
        faulted.SetException(new InvalidOperationException("profile locked"));
        var retry = NeverCompletes();

        Assert.Same(faulted.Task, cache.GetOrCreate(() => faulted.Task));
        // One transient failure must not be terminal for the process — the trap Shenora.Windows's own
        // WebViewEnvironment still has (TASKS.md H3), deliberately not copied here.
        Assert.Same(retry, cache.GetOrCreate(() => retry));
    }

    [Fact]
    public void A_cancelled_creation_is_not_cached_forever()
    {
        var cache = new SessionEnvironmentCache();
        var cancelled = new TaskCompletionSource<CoreWebView2Environment>();
        cancelled.SetCanceled();
        var retry = NeverCompletes();

        Assert.Same(cancelled.Task, cache.GetOrCreate(() => cancelled.Task));
        Assert.Same(retry, cache.GetOrCreate(() => retry));
    }

    [Fact]
    public void Clear_releases_the_shared_environment()
    {
        var cache = new SessionEnvironmentCache();
        var first = NeverCompletes();
        var second = NeverCompletes();

        Assert.Same(first, cache.GetOrCreate(() => first));
        // The owner is disposing: holding the environment would keep the profile's browser process —
        // and its folder OS lock — alive, so a caller that disposes the pool and then wipes the profile
        // would always fail. This is also why the cache is owner-scoped rather than static.
        cache.Clear();
        Assert.Same(second, cache.GetOrCreate(() => second));
    }
}
