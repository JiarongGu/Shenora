namespace Shenora.Core.WebView;

/// <summary>
/// The resource pipeline an application declares ONCE and every webview it hosts receives — the
/// <c>app.UseFiles(…)</c> half of the ASP.NET minimal-hosting shape (D64).
///
/// <para>
/// 🔴 <b>What this replaces, and why the old shape was wrong.</b> Routes used to be registered on ONE
/// interceptor instance, by the app, at the construction site of each webview:
/// <c>interceptor.UseMediaPlayer(services)</c> — the caller fetching an inner object and handing the
/// service provider BACK in, which <c>ADOPTION.md</c> had to spend a paragraph defending. The constraint
/// behind it is real (an interceptor is created WITH its webview, so it cannot exist at registration
/// time) and it is exactly the split ASP.NET draws — but ASP.NET's second phase is <c>app.Use*()</c>,
/// where the app already holds the provider. So the pipeline moves to the application and the app hands
/// it to each webview instead.
/// </para>
/// <para>
/// <b>The semantic change that comes with it, adopted deliberately:</b> a step describes the pipeline for
/// EVERY webview the app hosts, not one instance. Secondary windows and auxiliary session browsers used
/// to get nothing unless the app wired them again by hand — a gap nobody could see, because a window
/// serving no routes looks like a window whose routes were not needed. The per-interceptor call stays for
/// the case that genuinely wants ONE pipeline to differ.
/// </para>
/// </summary>
public sealed class WebViewPipeline
{
    private readonly object _lock = new();
    private readonly List<Action<IWebViewInterceptor>> _steps = [];
    private bool _applied;

    /// <summary>
    /// Append a step. Order is significant and preserved, exactly like an ASP.NET pipeline.
    /// <para>
    /// 🔴 <b>THROWS once any webview has been configured, and that is the point.</b> A step added after
    /// the first <see cref="ApplyTo"/> could not reach the webviews already built, so it would serve some
    /// windows and not others — with nothing to see, because a route that was never registered is
    /// indistinguishable from a route nothing requested. Freezing turns a silent partial pipeline into a
    /// loud composition error, and costs nothing real: a window opened later still gets every step,
    /// because the list is frozen rather than emptied.
    /// </para>
    /// </summary>
    /// <param name="step">Applied to each interceptor, in the order added.</param>
    public WebViewPipeline Use(Action<IWebViewInterceptor> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        lock (_lock)
        {
            if (_applied)
                throw new InvalidOperationException(
                    "The webview pipeline is already serving a webview, so this step could not reach it. " +
                    "Declare every app.Use…() before the first window is created — the same rule as an " +
                    "ASP.NET pipeline, which cannot be changed once the app is running. To give ONE webview " +
                    "a different pipeline, call the route directly on that interceptor instead.");
            _steps.Add(step);
        }
        return this;
    }

    // ⚠ No public `Count`. It was written, it reads like a reasonable diagnostic, and nothing consumes
    // it — which is exactly what "every public type earns its keep" refuses (generic-library.md). Public
    // surface is SemVer surface; add it when a consumer scenario needs it, not for flexibility.

    /// <summary>
    /// Apply every step to <paramref name="interceptor"/>, in order, and freeze further declarations.
    /// Called by the shell as it builds a webview; an app never calls this.
    /// <para>
    /// ⚠ <b>Deliberately NOT guarded.</b> Everywhere else the kit wraps app-supplied callbacks, because
    /// they run on a UI-thread event path with no caller left to catch anything. This runs during
    /// CONSTRUCTION, where a caller does exist and where a throwing step is a composition mistake — the
    /// same class as <c>IModuleContext.Publish</c> without a bus. Swallowing it would produce a window
    /// that silently serves nothing, which is the exact failure this type exists to remove.
    /// </para>
    /// <para>
    /// The <see cref="IDisposable"/> each route returns is intentionally dropped: a step's registration
    /// lives as long as the interceptor it was applied to, and the interceptor owns its own teardown.
    /// </para>
    /// </summary>
    public void ApplyTo(IWebViewInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);

        Action<IWebViewInterceptor>[] steps;
        lock (_lock)
        {
            _applied = true;
            steps = [.. _steps];
        }

        foreach (var step in steps) step(interceptor);
    }
}
