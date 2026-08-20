namespace Shenora.Core.WebView;

/// <summary>
/// The resource pipeline an application declares ONCE and every webview it hosts receives — the
/// <c>app.UseFiles(…)</c> half of the ASP.NET minimal-hosting shape (D64).
/// <para>
/// A step describes the pipeline for EVERY webview the app hosts, not one instance: a secondary window or
/// an auxiliary session browser otherwise gets nothing, and a window serving no routes looks exactly like
/// a window whose routes were not needed. The per-interceptor call stays for the case that genuinely
/// wants ONE pipeline to differ.
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
    /// 🔴 <b>THROWS once any webview has been configured.</b> A step added after the first
    /// <see cref="ApplyTo"/> could not reach the webviews already built, so it would serve some windows
    /// and not others — invisibly, because a route that was never registered is indistinguishable from a
    /// route nothing requested. A window opened LATER still gets every step: the list is frozen, not
    /// emptied.
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

    /// <summary>
    /// Apply every step to <paramref name="interceptor"/>, in order, and freeze further declarations.
    /// Called by the shell as it builds a webview; an app never calls this.
    /// <para>
    /// ⚠ <b>NOT guarded</b>, unlike the app callbacks the kit wraps elsewhere: this runs during
    /// CONSTRUCTION, where a throwing step is a composition mistake and a caller exists to see it.
    /// Swallowing it would produce a window that silently serves nothing.
    /// </para>
    /// <para>
    /// The <see cref="IDisposable"/> each route returns is intentionally dropped: a step's registration
    /// lives as long as the interceptor it was applied to, which owns its own teardown.
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
