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

        return Compose(args, isDevelopment, devExtraArguments, additionalArguments);
    }

    /// <summary>
    /// Append caller-supplied switches to a <paramref name="preset"/> while keeping this file's two
    /// hard invariants — the single place that knows them, so a second preset (the auxiliary session
    /// browser) cannot get them subtly wrong (P5.5 H4.4).
    /// <list type="number">
    /// <item><b>Each features switch appears EXACTLY ONCE.</b> Chromium keeps only the LAST
    /// occurrence, so an app appending its own <c>--enable-features=</c>/<c>--disable-features=</c>
    /// silently discarded the whole preset — the measured incident this class documents. Caller
    /// feature lists are MERGED into the preset's single occurrence instead of appended.</item>
    /// <item><b>The dev CDP arguments are re-appended by hand.</b> Setting
    /// <c>AdditionalBrowserArguments</c> at all makes WebView2 IGNORE
    /// <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS</c>, which is how the devtools loop passes
    /// <c>--remote-debugging-port</c>. Never in production.</item>
    /// </list>
    /// </summary>
    public static string Compose(string preset, bool isDevelopment,
                                 string? devExtraArguments = null, string? additionalArguments = null)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var args = preset.Trim();

        // Order matters only for the features merge: fold every caller list in, then append what is
        // left over, so the result still carries one --enable-features and one --disable-features.
        var extras = new List<string>();
        if (!string.IsNullOrWhiteSpace(additionalArguments)) extras.Add(additionalArguments.Trim());
        if (isDevelopment && !string.IsNullOrWhiteSpace(devExtraArguments)) extras.Add(devExtraArguments.Trim());

        foreach (var extra in extras)
        {
            var remainder = extra;
            foreach (var switchName in new[] { "--enable-features=", "--disable-features=" })
            {
                remainder = MergeFeatureSwitch(ref args, remainder, switchName);
            }
            if (!string.IsNullOrWhiteSpace(remainder)) args += " " + remainder.Trim();
        }

        return args;
    }

    /// <summary>
    /// Move any <paramref name="switchName"/> occurrence out of <paramref name="remainder"/> and fold
    /// its values into the one already in <paramref name="args"/> (or append it if the preset has
    /// none). Returns the remainder with that switch removed.
    /// </summary>
    private static string MergeFeatureSwitch(ref string args, string remainder, string switchName)
    {
        var at = remainder.IndexOf(switchName, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return remainder;

        var valueStart = at + switchName.Length;
        var valueEnd = remainder.IndexOf(' ', valueStart);
        if (valueEnd < 0) valueEnd = remainder.Length;
        var values = remainder[valueStart..valueEnd];
        remainder = (remainder[..at] + remainder[valueEnd..]).Trim();
        if (string.IsNullOrWhiteSpace(values)) return remainder;

        var presetAt = args.IndexOf(switchName, StringComparison.OrdinalIgnoreCase);
        if (presetAt < 0)
        {
            args += " " + switchName + values;
            return remainder;
        }

        var presetValueStart = presetAt + switchName.Length;
        var presetValueEnd = args.IndexOf(' ', presetValueStart);
        if (presetValueEnd < 0) presetValueEnd = args.Length;
        var existing = args[presetValueStart..presetValueEnd]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        foreach (var value in values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!existing.Contains(value, StringComparer.OrdinalIgnoreCase)) existing.Add(value);
        }
        args = args[..presetValueStart] + string.Join(',', existing) + args[presetValueEnd..];
        return remainder;
    }
}
