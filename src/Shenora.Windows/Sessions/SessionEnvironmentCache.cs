using Microsoft.Web.WebView2.Core;

namespace Shenora.Windows;

/// <summary>
/// ONE <see cref="CoreWebView2Environment"/> shared by the several browsers a single owner creates on
/// ONE profile — held by that owner (today <see cref="RenderSessionPool"/>), never process-globally.
/// <para>
/// WHY IT EXISTS: <see cref="SessionBrowserOptions.InitTimeout"/> abandons the <c>await</c> but NOT the
/// underlying <c>CreateAsync</c>, so with one environment per instance every retried lease against a
/// profile whose folder lock a zombie <c>msedgewebview2</c> was holding started ANOTHER environment
/// creation — each spawning another browser process queued on the very lock the timeout message blames.
/// Reusing the in-flight task means a retry JOINS the first attempt instead of stacking a new one.
/// </para>
/// <para>
/// 🔴 <b>Owner-scoped, never a static profile-keyed cache</b>, for two reasons. A live environment keeps
/// its profile's browser process — and therefore the folder's OS lock — alive, so a process-lifetime
/// cache would make <see cref="InteractiveSession.ClearProfile"/> fail every time instead of only while
/// a window is open. And <see cref="CoreWebView2Environment"/> is THREAD-AFFINE
/// (<c>.claude/knowledge/webview2-hosting.md</c>): an owner marshals all of its browser work to one
/// anchor's UI thread, so an owner-scoped cache is single-threaded by construction and needs neither a
/// thread key nor a lock. A global one would need both.
/// </para>
/// </summary>
internal sealed class SessionEnvironmentCache
{
    // Owner-thread-only state: every read/write happens inside a body marshalled to the owner's UI
    // thread, so no synchronization is needed. Clear() is the one exception — see its docs.
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
            // faulted task makes ONE transient failure terminal for the whole process.
            // `WebViewEnvironment.GetSharedAsync` shares this shape; keep the two in step.
            _pending = null;
        }

        return _pending = create();
    }

    /// <summary>
    /// Forget the cached environment — the owner is disposing, and nothing of ours should keep the
    /// profile's browser process (and its folder lock) alive afterwards.
    /// <para>
    /// Unlike the rest of this type this can run off the owner's UI thread (a pool is disposed from
    /// wherever the app tears down). Dropping a reference is atomic, and the only race — a creation in
    /// flight re-populating the field — yields an instance the pool discards anyway.
    /// </para>
    /// </summary>
    internal void Clear() => _pending = null;
}
