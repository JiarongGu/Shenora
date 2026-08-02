using Shenora.Core;

namespace Shenora.Windows;

/// <summary>WebView2 composition on a <see cref="ShenoraApplicationBuilder"/>.</summary>
public static class WebView2BuilderExtensions
{
    /// <summary>
    /// Kick off the shared WebView2 environment as a startup hook (see
    /// <see cref="WebViewEnvironment.Prewarm"/> for why: the browser-process spawn is the
    /// dominant ~1–2 s of WebView2 init and overlaps window creation when started first).
    /// Runs AFTER the single-instance gate — the environment takes the user-data-folder OS lock,
    /// which a losing second launch must never touch — hence a lifecycle hook, not an immediate
    /// call. <paramref name="options"/> is evaluated at that point too, so it can use the built
    /// app (e.g. <c>app.Paths.DataArea(…)</c>, <c>app.Environment.IsDevelopment</c>).
    /// </summary>
    public static ShenoraApplicationBuilder PrewarmWebView2(this ShenoraApplicationBuilder builder,
        Func<ShenoraApplication, WebViewEnvironmentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.OnStarting(app => WebViewEnvironment.Prewarm(options(app)));
    }
}
