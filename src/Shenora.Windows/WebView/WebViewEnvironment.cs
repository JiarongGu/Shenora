using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace Shenora.Windows;

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
    /// Extra dev-only switches. Defaults to the <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS</c> env var
    /// when null — ⚠ WebView2 IGNORES that env var once AdditionalBrowserArguments is set, so the
    /// devtools CDP port must be re-appended manually.
    /// </summary>
    public string? DevExtraArguments { get; init; }

    /// <summary>App-specific switches appended in all modes.</summary>
    public string? AdditionalArguments { get; init; }

    /// <summary>Fixed browser binaries folder; null = the Evergreen runtime.</summary>
    public string? BrowserExecutableFolder { get; init; }

    /// <summary>Diagnostics sink (timings, prewarm progress). Null = silent.</summary>
    public ILogger? Log { get; init; }

    /// <summary>
    /// Custom URI schemes this environment serves, e.g. <c>app</c> for <c>app://…</c>.
    /// <para>
    /// 🔴 REQUIRED for every non-http(s) scheme in <see cref="WebViewHostOptions.DeferredSchemes"/>, and
    /// it lives HERE rather than beside the handler because WebView2 accepts scheme registrations only
    /// when the ENVIRONMENT is created. Without it the network stack rejects the request before the
    /// <c>WebResourceRequested</c> filter is consulted, and the page sees a bare
    /// <c>TypeError: Failed to fetch</c> with nothing in the host log. <see cref="WebViewHost"/>
    /// validates the pairing at construction so this fails at composition instead.
    /// </para>
    /// </summary>
    public IReadOnlyList<WebViewCustomScheme> CustomSchemes { get; init; } = [];
}

/// <summary>
/// A custom URI scheme registered with the environment (see
/// <see cref="WebViewEnvironmentOptions.CustomSchemes"/>).
/// </summary>
public sealed class WebViewCustomScheme
{
    /// <summary>Scheme name without the separator (e.g. <c>app</c> for <c>app://…</c>).</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Treat as a SECURE origin (default true). Secure schemes reach the APIs a modern page needs —
    /// service workers, <c>crypto.subtle</c>, loading subresources into an https document; an insecure
    /// custom scheme is mysteriously restricted instead.
    /// </summary>
    public bool TreatAsSecure { get; init; } = true;

    /// <summary>
    /// True (default) when URIs carry a host, i.e. <c>scheme://host/path</c>. False for the opaque
    /// <c>scheme:path</c> form. Get this wrong and paths parse into the wrong component.
    /// </summary>
    public bool HasAuthorityComponent { get; init; } = true;

    /// <summary>
    /// Origins allowed to fetch this scheme cross-origin. Empty (default) = same-origin only, so widen
    /// it when the page's origin differs from the scheme's.
    /// </summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
}

/// <summary>
/// The process-global WebView2 environment: prewarm, shared access, per-thread creation, and a runtime
/// presence check.
///
/// Prewarm: the browser-process spawn + user-data init is the dominant chunk of WebView2 init and needs
/// no control or message loop, so starting it first thing overlaps DI build, window-state load and form
/// creation.
///
/// 🔴 THREAD AFFINITY: a <see cref="CoreWebView2Environment"/> is affine to the thread that created it.
/// ONLY the main UI thread may use <see cref="GetSharedAsync"/>; a secondary window on its own STA
/// thread MUST use <see cref="CreateForCurrentThreadAsync"/> (same options + user-data folder ⇒ the
/// environments share one browser process).
/// </summary>
public static class WebViewEnvironment
{
    private static readonly object Lock = new();
    private static Task<CoreWebView2Environment>? _shared;

    /// <summary>
    /// The installed WebView2 runtime version, or null when NO runtime is available — in which case
    /// show an install prompt rather than letting <c>EnsureCoreWebView2Async</c> fail obscurely later.
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
    /// The shared environment task, started on first call — the main window awaits this instead of
    /// creating its own. Main UI thread only (see class docs). Options are honored on the FIRST call;
    /// later calls return the existing task.
    /// </summary>
    public static Task<CoreWebView2Environment> GetSharedAsync(WebViewEnvironmentOptions options)
    {
        lock (Lock)
        {
            if (_shared is { } existing)
            {
                // PENDING or SUCCEEDED → reuse; that is what prewarming buys.
                if (!existing.IsCompleted || existing.Status == TaskStatus.RanToCompletion) return existing;

                // 🔴 FAULTED or CANCELLED → forget it. `??=` caches a faulted task forever, which makes
                // ONE transient failure (a profile lock since cleared, a runtime update mid-launch)
                // terminal for the whole process — including the retry the init-timeout message asks
                // for. Evicting on observation is what makes a retry real.
                _shared = null;
            }
            return _shared = CreateAsync(options);
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
        AppCallback.Log(options.Log, () => "[WebView2] Creating environment (browser-process spawn)…");
        Directory.CreateDirectory(options.UserDataFolder);

        var devExtra = options.DevExtraArguments
            ?? (options.IsDevelopment ? Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS") : null);
        var browserArguments = BrowserArguments.Build(options.IsDevelopment, devExtra, options.AdditionalArguments);

        // ⚠ Custom schemes go through the CONSTRUCTOR, never the property. `CustomSchemeRegistrations`
        // is NULL on a default-constructed CoreWebView2EnvironmentOptions, so both `.Add(...)` and a
        // `{ ... }` initializer NullReference — and inside an async environment factory the symptom is
        // not a stack trace but a startup that never completes.
        var envOptions = options.CustomSchemes.Count == 0
            ? new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = browserArguments }
            : new CoreWebView2EnvironmentOptions(browserArguments, null, null, false,
                [.. options.CustomSchemes.Select(ToRegistration)]);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var environment = await CoreWebView2Environment.CreateAsync(
            options.BrowserExecutableFolder, options.UserDataFolder, envOptions);
        sw.Stop();
        AppCallback.Log(options.Log, () => $"[WebView2] Environment ready (CreateAsync took {sw.ElapsedMilliseconds}ms)");
        return environment;
    }

    private static CoreWebView2CustomSchemeRegistration ToRegistration(WebViewCustomScheme scheme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme.Name);
        var registration = new CoreWebView2CustomSchemeRegistration(scheme.Name)
        {
            TreatAsSecure = scheme.TreatAsSecure,
            HasAuthorityComponent = scheme.HasAuthorityComponent,
        };
        // AllowedOrigins IS a live list on a constructed registration, unlike the options property
        // above, so adding is correct here.
        foreach (var origin in scheme.AllowedOrigins) registration.AllowedOrigins.Add(origin);
        return registration;
    }
}
