using Microsoft.Web.WebView2.Core;

namespace Shenora.WebView2;

/// <summary>Inputs for <see cref="WebViewEnvironment"/>. One instance per app, built once at startup.</summary>
public sealed class WebViewEnvironmentOptions
{
    /// <summary>
    /// The WebView2 user-data folder (holds the profile + an OS lock — one running app per
    /// folder). Typically a data area from the app's paths authority, e.g. <c>data/webview2</c>.
    /// </summary>
    public required string UserDataFolder { get; init; }

    /// <summary>Dev mode appends <see cref="DevExtraArguments"/> — see <see cref="BrowserArguments.Build"/>.</summary>
    public bool IsDevelopment { get; init; }

    /// <summary>
    /// Extra dev-only switches. Defaults to the <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS</c> env
    /// var when null — WebView2 ignores that env var once AdditionalBrowserArguments is set, so
    /// the devtools CDP port must be re-appended manually (the family's measured gotcha).
    /// </summary>
    public string? DevExtraArguments { get; init; }

    /// <summary>App-specific switches appended in all modes.</summary>
    public string? AdditionalArguments { get; init; }

    /// <summary>Fixed browser binaries folder; null = the Evergreen runtime.</summary>
    public string? BrowserExecutableFolder { get; init; }

    /// <summary>Diagnostics sink (timings, prewarm progress). Null = silent.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// The process-global WebView2 environment: prewarm, shared access, per-thread creation, and the
/// runtime presence check no source app had.
///
/// Prewarm: the browser-process spawn + user-data init is the dominant chunk of WebView2 init
/// (~1–2 s measured) and needs no control or message loop — so kick it off first thing at
/// startup and it overlaps DI build, window-state load, and form creation; by the time the
/// window needs it the task is usually already complete.
///
/// Thread affinity (the source app's hard-won rule): a <see cref="CoreWebView2Environment"/> is
/// affine to the thread that created it. ONLY the main UI thread may use
/// <see cref="GetSharedAsync"/>; a secondary window on its own STA thread MUST use
/// <see cref="CreateForCurrentThreadAsync"/> (same options + user-data folder ⇒ the environments
/// share one browser process).
/// </summary>
public static class WebViewEnvironment
{
    private static readonly object Lock = new();
    private static Task<CoreWebView2Environment>? _shared;

    /// <summary>
    /// The installed WebView2 runtime version, or null when NO runtime is available — in which
    /// case show an actionable install prompt instead of letting <c>EnsureCoreWebView2Async</c>
    /// fail obscurely later (the gap every source app shipped with).
    /// </summary>
    public static string? GetAvailableRuntimeVersion(string? browserExecutableFolder = null)
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString(browserExecutableFolder);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return null;
        }
    }

    /// <summary>True when a WebView2 runtime (or the configured fixed version) is present.</summary>
    public static bool IsRuntimeAvailable(string? browserExecutableFolder = null) =>
        GetAvailableRuntimeVersion(browserExecutableFolder) is not null;

    /// <summary>Kick off shared-environment creation early (fire-and-forget). Idempotent.</summary>
    public static void Prewarm(WebViewEnvironmentOptions options) => _ = GetSharedAsync(options);

    /// <summary>
    /// The shared environment task, started on first call — the main window awaits this instead
    /// of creating its own, paying only the remaining (often zero) prewarm time. Main UI thread
    /// only (see class docs). Options are honored on the FIRST call; later calls return the
    /// existing task.
    /// </summary>
    public static Task<CoreWebView2Environment> GetSharedAsync(WebViewEnvironmentOptions options)
    {
        lock (Lock)
        {
            return _shared ??= CreateAsync(options);
        }
    }

    /// <summary>
    /// Create a FRESH environment on the CALLING thread (secondary windows on their own STA
    /// thread). Not cached — each such window gets its own, affine to its thread.
    /// </summary>
    public static Task<CoreWebView2Environment> CreateForCurrentThreadAsync(WebViewEnvironmentOptions options) =>
        CreateAsync(options);

    private static async Task<CoreWebView2Environment> CreateAsync(WebViewEnvironmentOptions options)
    {
        options.Log?.Invoke("[WebView2] Creating environment (browser-process spawn)…");
        Directory.CreateDirectory(options.UserDataFolder);

        var devExtra = options.DevExtraArguments
            ?? (options.IsDevelopment ? Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS") : null);

        var envOptions = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = BrowserArguments.Build(options.IsDevelopment, devExtra, options.AdditionalArguments),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var environment = await CoreWebView2Environment.CreateAsync(
            options.BrowserExecutableFolder, options.UserDataFolder, envOptions);
        sw.Stop();
        options.Log?.Invoke($"[WebView2] Environment ready (CreateAsync took {sw.ElapsedMilliseconds}ms)");
        return environment;
    }
}
