using Microsoft.Web.WebView2.Core;

namespace Shenora.Windows;

/// <summary>
/// ONE <see cref="CoreWebView2Environment"/> shared by the several browsers a single owner creates on
/// ONE profile — held by that owner (today <see cref="RenderSessionPool"/>), never process-globally.
/// <para>
/// WHY IT EXISTS (P5.5 H2, the item H4.4 left open). <see cref="SessionBrowser"/> created a fresh
/// environment per instance, and <see cref="SessionBrowserOptions.InitTimeout"/> abandons the
/// <c>await</c> but NOT the underlying <c>CreateAsync</c>. So against a profile whose folder lock a
/// zombie <c>msedgewebview2</c> was holding, every retried lease started ANOTHER environment
/// creation — each spawning another browser process queued on the same lock, i.e. adding to the very
/// lock the timeout message blames. Reusing the in-flight task means a retry JOINS the first attempt
/// instead of stacking a new one.
/// </para>
/// <para>
/// WHY IT IS OWNER-SCOPED AND NOT A STATIC, PROFILE-KEYED CACHE. Two reasons, both load-bearing:
/// </para>
/// <list type="number">
/// <item>A live environment keeps its profile's browser process — and therefore the folder's OS lock
/// — alive. A process-lifetime cache would root the lock for every profile ever opened, so
/// <see cref="InteractiveSession.ClearProfile"/> (the call that REALLY discards a session) would fail
/// every time instead of only while a window is open. An interactive session opens one profile once, so it
/// gains nothing from caching; a pool creates N instances on ONE profile, which is the case that
/// does.</item>
/// <item><see cref="CoreWebView2Environment"/> is THREAD-AFFINE (see
/// <c>.claude/knowledge/webview2-hosting.md</c> — mixing threads broke every secondary window in the
/// source app). An owner marshals all of its browser work to one anchor's UI thread, so an
/// owner-scoped cache is single-threaded by construction and needs no thread key and no lock. A
/// global cache would need both.</item>
/// </list>
/// </summary>
internal sealed class SessionEnvironmentCache
{
    // Owner-thread-only state: every read/write happens inside a body marshalled to the owner's UI
    // thread (RenderSessionPool.CreateInstanceAsync), so no synchronization is needed. Clear() is the
    // one exception — see its docs.
    private Task<CoreWebView2Environment>? _pending;

    /// <summary>
    /// The shared environment: the cached one if it is still usable, otherwise a new creation from
    /// <paramref name="create"/>. Call on the owner's UI thread.
    /// </summary>
    internal Task<CoreWebView2Environment> GetOrCreate(Func<Task<CoreWebView2Environment>> create)
    {
        ArgumentNullException.ThrowIfNull(create);

        if (_pending is { } existing)
        {
            // PENDING or SUCCEEDED → reuse. Pending is the important half: that is the retry-against-
            // a-locked-profile case above, where starting a second creation is what orphaned a second
            // browser process.
            if (!existing.IsCompleted || existing.Status == TaskStatus.RanToCompletion) return existing;

            // FAULTED or CANCELLED → forget it, so the next attempt can genuinely retry. Caching a
            // faulted task makes ONE transient failure terminal for the whole process. This comment
            // used to say Shenora.Windows's own WebViewEnvironment "still has" that trap and cite
            // TASKS.md H3 — both stale: H3 fixed WebViewEnvironment.GetSharedAsync the same way
            // (it evicts a faulted/cancelled entry on observation) and that task is long closed.
            // The two now share ONE shape deliberately; keep them in step.
            _pending = null;
        }

        return _pending = create();
    }

    /// <summary>
    /// Forget the cached environment — the owner is disposing, and nothing of ours should keep the
    /// profile's browser process (and its folder lock) alive afterwards.
    /// <para>
    /// Unlike the rest of this type this can run off the owner's UI thread (a pool is disposed from
    /// wherever the app tears down). Dropping a reference is atomic, so the only race is a creation
    /// in flight at that moment re-populating the field; that instance is discarded by the pool
    /// anyway and the field dies with the cache, so it costs nothing to leave unsynchronized.
    /// </para>
    /// </summary>
    internal void Clear() => _pending = null;
}
