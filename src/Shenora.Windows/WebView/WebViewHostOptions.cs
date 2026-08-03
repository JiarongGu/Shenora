using System.Drawing;
using Microsoft.Web.WebView2.Core;
using Shenora.Core;

namespace Shenora.Windows;

/// <summary>
/// An app-defined scheme served asynchronously OFF the UI thread (disk reads, remote
/// fetch-and-cache…). See <see cref="WebViewHost"/> for the sync-vs-deferred serving split and
/// why it exists.
/// </summary>
public sealed class WebViewDeferredScheme
{
    /// <summary>Scheme name without the separator (e.g. <c>app</c> handles <c>app://…</c>).</summary>
    public required string Scheme { get; init; }

    /// <summary>
    /// Answers one request. Runs on a thread-pool thread — never touch UI state. Return null or
    /// throw to answer 404; a thrown message never reaches the page (it goes to the host log).
    /// <para>
    /// It takes the whole <see cref="WebViewResourceRequest"/> and returns a
    /// <see cref="WebViewResourceResponse"/> — status, headers, and a STREAM — rather than the
    /// <c>(byte[], contentType)</c> pair it used to (P6.6). Two things were impossible before and are
    /// the reason this changed: a handler could not read a request header, so <c>Range</c> was
    /// invisible and a page could not SEEK anything it served; and returning the complete bytes meant
    /// a 4 GB file became 4 GB of memory. Use <see cref="WebViewByteRange.TryParse"/> plus
    /// <see cref="WebViewResourceResponse.PartialContent"/> for the seekable case.
    /// </para>
    /// </summary>
    public required Func<WebViewResourceRequest, Task<WebViewResourceResponse?>> Handler { get; init; }

    /// <summary>
    /// Default <c>Cache-Control</c> for SUCCESSFUL (2xx) responses that do not set their own. The
    /// family default caches one day; callers cache-bust with a query token (e.g.
    /// <c>?t=&lt;mtime&gt;</c>) when the content can change. A handler that sets the header itself
    /// wins — a 206 or a 404 has its own caching story and must not be stamped over.
    /// </summary>
    public string CacheControl { get; init; } = "public, max-age=86400";
}

/// <summary>
/// A virtual-host → disk-folder mapping (<c>SetVirtualHostNameToFolderMapping</c>). The OTHER
/// legitimate way to serve a bundle: folder mapping for disk-backed content, resource
/// interception (<see cref="WebViewHostOptions.ResourceProvider"/>) for embedded content — both
/// are family-proven, so both are supported (don't "unify" one away).
/// </summary>
public sealed class WebViewFolderMapping
{
    /// <summary>Virtual host name (e.g. <c>myapp-media</c>).</summary>
    public required string HostName { get; init; }

    /// <summary>The disk folder served under the host.</summary>
    public required string FolderPath { get; init; }

    /// <summary>Cross-origin access policy for the mapped host.</summary>
    public CoreWebView2HostResourceAccessKind AccessKind { get; init; } = CoreWebView2HostResourceAccessKind.Allow;
}

/// <summary>
/// Inputs for <see cref="WebViewHost"/> — every magic value the source apps hardcoded (dev URL,
/// virtual host, schemes, background color, timeout) as documented options.
/// </summary>
public sealed class WebViewHostOptions
{
    /// <summary>
    /// Environment inputs (user-data folder, dev flag, browser arguments…). The dev flag here is
    /// the single dev/prod source for the host too (settings, scripts, navigation) — wire it from
    /// <c>ShenoraEnvironment.IsDevelopment</c>.
    /// </summary>
    public required WebViewEnvironmentOptions Environment { get; init; }

    /// <summary>
    /// True (default) = the process-wide shared environment (main window, main UI thread).
    /// False = a fresh environment on the CALLING thread — required for secondary windows on
    /// their own STA thread (see <see cref="WebViewEnvironment"/>'s thread-affinity contract).
    /// </summary>
    public bool UseSharedEnvironment { get; init; } = true;

    /// <summary>
    /// Budget for environment creation + <c>EnsureCoreWebView2Async</c>. The family-proven guard:
    /// an orphaned user-data-folder lock (a zombie browser process) hangs init FOREVER without
    /// it; failing loudly with an actionable message beats a silent never-appearing window.
    /// </summary>
    public TimeSpan InitTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// The dev server URL (e.g. <c>http://localhost:3517</c>). No default on purpose: every
    /// family app picks a UNIQUE port (never 3000) so parallel dev sessions of sibling apps
    /// can't collide — keep it in sync with the frontend's <c>vite.config.ts</c>.
    /// </summary>
    public string? DevUrl { get; init; }

    /// <summary>
    /// Virtual host the packaged bundle is served on via <see cref="ResourceProvider"/>
    /// (e.g. <c>app.local</c> ⇒ <c>https://app.local/…</c>).
    /// </summary>
    public string? VirtualHost { get; init; }

    /// <summary>The packaged-bundle provider behind <see cref="VirtualHost"/>.</summary>
    public IWebViewResourceProvider? ResourceProvider { get; init; }

    /// <summary>
    /// Explicit production start URL. Overrides the <see cref="VirtualHost"/> default
    /// (<c>https://{VirtualHost}/index.html</c>) — the server-backed profile points this at its
    /// own in-process HTTP server instead of using a provider at all.
    /// </summary>
    public string? ProductionUrl { get; init; }

    /// <summary>Async app schemes (see <see cref="WebViewDeferredScheme"/>).</summary>
    public IReadOnlyList<WebViewDeferredScheme> DeferredSchemes { get; init; } = [];

    /// <summary>Disk-folder virtual hosts (see <see cref="WebViewFolderMapping"/>).</summary>
    public IReadOnlyList<WebViewFolderMapping> FolderMappings { get; init; } = [];

    /// <summary>
    /// The control's background before content paints. Set it to the SAME color as the form and
    /// the app's page background — the family's no-white-flash contract. Null = leave default.
    /// </summary>
    public Color? BackgroundColor { get; init; }

    /// <summary>
    /// Keep OS drag-drop delivery to the browser enabled so internal HTML5 drag-drop works;
    /// EXTERNAL file drops are neutralized by <see cref="PreventDefaultFileDrop"/> and belong to
    /// the native drop-zone overlay.
    /// </summary>
    public bool AllowExternalDrop { get; init; } = true;

    /// <summary>Inject the family script that stops the browser navigating to dropped files.</summary>
    public bool PreventDefaultFileDrop { get; init; } = true;

    /// <summary>Inject the family script that blocks browser chrome shortcuts in production.</summary>
    public bool BlockBrowserShortcutsInProduction { get; init; } = true;

    /// <summary>
    /// Globals injected on document created as <c>window.&lt;name&gt; = &lt;json&gt;;</c> (e.g. an
    /// app-metadata object). Values are JSON-serialized camelCase with full escaping — the source
    /// apps interpolated raw strings here, which was the audit's injection gap.
    /// </summary>
    public IReadOnlyDictionary<string, object?> InjectedGlobals { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>Raw scripts additionally executed on every document created, in order.</summary>
    public IReadOnlyList<string> DocumentCreatedScripts { get; init; } = [];

    /// <summary>
    /// Runs AFTER the family hardening preset (dev-gated devtools/context menus; status bar,
    /// zoom, autofill, pinch, swipe, password autosave, built-in error page all off; web
    /// messages on) so an app can override individual settings without losing the rest.
    /// </summary>
    public Action<CoreWebView2Settings>? ConfigureSettings { get; init; }

    /// <summary>
    /// <c>window.open</c>/<c>target=_blank</c> goes to the SYSTEM browser (http/https only,
    /// anything else dropped) — never a bare WebView2 popup. The app window only ever hosts the
    /// app.
    /// </summary>
    public bool OpenExternalLinksInSystemBrowser { get; init; } = true;

    /// <summary>
    /// Replaces the default download policy. Default: downloads are CANCELED (and logged) — an
    /// app shell is not a browser; a page-initiated download is almost always a bug or an attack
    /// surface. Apps that want downloads take full control here (e.g. set the result path and
    /// hide the default UI).
    /// </summary>
    public Action<CoreWebView2DownloadStartingEventArgs>? OnDownloadStarting { get; init; }

    /// <summary>
    /// Replaces the default permission policy. Default: kinds in
    /// <see cref="PermittedPermissions"/> are allowed, everything else (camera, mic, location,
    /// notifications…) silently denied — no browser-style prompt ever interrupts the app.
    /// </summary>
    public Action<CoreWebView2PermissionRequestedEventArgs>? OnPermissionRequested { get; init; }

    /// <summary>Permission kinds the default policy allows. Clipboard read is the one web
    /// capability family apps legitimately use from the page.</summary>
    public IReadOnlyList<CoreWebView2PermissionKind> PermittedPermissions { get; init; } =
        [CoreWebView2PermissionKind.ClipboardRead];

    /// <summary>
    /// Recover from a crashed renderer by reloading — at most <see cref="MaxAutoReloads"/> times, and
    /// never more than once per <see cref="AutoReloadCooldown"/>. The browser-process kinds are NOT
    /// auto-recovered: the whole control is dead then, which is an app-level decision.
    /// </summary>
    public bool ReloadOnRenderProcessFailure { get; init; } = true;

    /// <summary>Minimum spacing between automatic renderer-crash reloads.</summary>
    public TimeSpan AutoReloadCooldown { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many times a renderer crash may be auto-recovered before the host gives up and leaves the
    /// failure to <see cref="OnProcessFailed"/> (default 3).
    /// <para>
    /// Having a TERMINAL state is the point (P5.5 H3). Rate-limiting alone is not a stopping condition:
    /// a deterministically-crashing page — one that faults during load — reloaded every cooldown
    /// FOREVER, burning a browser process each time, while
    /// <see cref="ReloadOnRenderProcessFailure"/>'s own documentation promised that "a crash-looping
    /// page must not spin". After the cap the host logs once and stops. A successful navigation resets
    /// the count, so a long-running app is not slowly used up by unrelated crashes.
    /// </para>
    /// </summary>
    public int MaxAutoReloads { get; init; } = 3;

    /// <summary>Replaces the default process-failure handling (which logs + auto-reloads per
    /// <see cref="ReloadOnRenderProcessFailure"/>).</summary>
    public Action<CoreWebView2ProcessFailedEventArgs>? OnProcessFailed { get; init; }

    /// <summary>Diagnostics sink. Null = <see cref="WebViewEnvironmentOptions.Log"/>.</summary>
    public Action<string>? Log { get; init; }
}
