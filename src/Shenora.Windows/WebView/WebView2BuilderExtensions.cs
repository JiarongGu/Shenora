using Shenora;

namespace Shenora.Windows;

/// <summary>WebView2 composition on a <see cref="ShenoraApplicationBuilder"/>.</summary>
public static class WebView2BuilderExtensions
{
    /// <summary>
    /// Kick off the shared WebView2 environment as a startup hook (see
    /// <see cref="WebViewEnvironment.Prewarm"/>). 🔴 A lifecycle hook rather than an immediate call so
    /// it runs AFTER the single-instance gate: the environment takes the user-data-folder OS lock,
    /// which a losing second launch must never touch. <paramref name="options"/> is evaluated at that
    /// point too, so it can use the built app (e.g. <c>app.Paths.DataArea(…)</c>).
    /// </summary>
    public static ShenoraApplicationBuilder PrewarmWebView2(this ShenoraApplicationBuilder builder,
        Func<ShenoraApplication, WebViewEnvironmentOptions> options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.OnStarting(app => WebViewEnvironment.Prewarm(options(app)));
    }
}
