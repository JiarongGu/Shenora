namespace Shenora.WebView2;

/// <summary>
/// Builds the Chromium command line passed via
/// <c>CoreWebView2EnvironmentOptions.AdditionalBrowserArguments</c> — the family's measured
/// display-optimization preset, extracted verbatim.
///
/// IMPORTANT: <c>--enable-features</c> / <c>--disable-features</c> are each given EXACTLY ONCE
/// with a comma-separated list. Chromium keeps only the LAST occurrence of a repeated switch,
/// which previously silently dropped IsolatedCodeCache (the V8 code-cache feature relied on for
/// fast subsequent loads) and the draggable-regions feature in a source app.
///
/// Measured-and-REJECTED flags (don't re-add): <c>SpareRendererForSitePerProcess</c> (+150 ms
/// startup), <c>msWebView2CancelInitialNavigation</c> (no gain), and
/// <c>--js-flags=--no-lazy --always-opt</c> (<c>--no-lazy</c> regresses startup by force-compiling
/// everything; <c>--always-opt</c> no longer exists in V8 — code caching comes from
/// IsolatedCodeCache/msWebView2CodeCache instead).
/// </summary>
public static class BrowserArguments
{
    // msWebView2CodeCache makes JS served via WebResourceRequested (an embedded bundle) eligible
    // for V8 bytecode caching → faster 3rd+ React mount in production. No effect in dev (Vite
    // serves over http, not through the handler). Pairs with IsolatedCodeCache.
    private const string EnableFeatures =
        "msWebView2EnableDraggableRegions,IsolatedCodeCache,ScriptStreaming,msWebView2CodeCache";

    private const string DisableFeatures = "msSmartScreenProtection,TranslateUI";

    /// <summary>
    /// Build the argument string.
    /// </summary>
    /// <param name="isDevelopment">
    /// Dev mode appends <paramref name="devExtraArguments"/> — needed because setting
    /// <c>AdditionalBrowserArguments</c> at all makes WebView2 IGNORE the
    /// <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS</c> env var, which the devtools loop uses to pass
    /// <c>--remote-debugging-port</c> for CDP. Never appended in production.
    /// </param>
    /// <param name="devExtraArguments">
    /// Normally <c>Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS")</c>.
    /// </param>
    /// <param name="additionalArguments">App-specific extra switches, appended in ALL modes.</param>
    public static string Build(bool isDevelopment, string? devExtraArguments = null, string? additionalArguments = null)
    {
        var args =
            $"--enable-features={EnableFeatures} " +
            $"--disable-features={DisableFeatures} " +
            "--enable-gpu-rasterization " +
            "--enable-zero-copy " +
            "--enable-accelerated-2d-canvas " +
            "--enable-hardware-overlays " +
            "--force-color-profile=srgb " +
            "--disable-background-timer-throttling " +
            "--disable-renderer-backgrounding " +
            "--disable-ipc-flooding-protection " +
            "--disable-gpu-driver-bug-workarounds " +
            "--disable-component-update " +
            "--disable-default-apps " +
            "--disable-domain-reliability " +
            "--disable-sync " +
            "--no-first-run " +
            "--no-default-browser-check " +
            "--disable-background-networking " +
            "--disable-breakpad";

        if (!string.IsNullOrWhiteSpace(additionalArguments))
        {
            args += " " + additionalArguments.Trim();
        }

        if (isDevelopment && !string.IsNullOrWhiteSpace(devExtraArguments))
        {
            args += " " + devExtraArguments.Trim();
        }

        return args;
    }
}
