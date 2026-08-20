using System.Drawing;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Shenora;
using Shenora.Core.WebView;

namespace Shenora.Windows;

/// <summary>
/// An app-defined scheme served asynchronously OFF the UI thread (disk reads, remote
/// fetch-and-cache…). See <see cref="WebViewHost"/> for the sync-vs-deferred serving split.
/// </summary>
public sealed class WebViewDeferredScheme
{
    /// <summary>Scheme name without the separator (e.g. <c>app</c> handles <c>app://…</c>).</summary>
    public required string Scheme { get; init; }

    /// <summary>
    /// Answers one request. Runs on a thread-pool thread — never touch UI state. Return null or
    /// throw to answer 404; a thrown message never reaches the page (it goes to the host log). Use
    /// <see cref="WebViewByteRange.TryParse"/> plus
    /// <see cref="WebViewResourceResponse.PartialContent"/> for a seekable resource.
    /// </summary>
    public required Func<WebViewResourceRequest, Task<WebViewResourceResponse?>> Handler { get; init; }

    /// <summary>
    /// Default <c>Cache-Control</c> for SUCCESSFUL (2xx) responses that do not set their own; a
    /// handler that sets the header itself wins. Cache-bust with a query token (e.g.
    /// <c>?t=&lt;mtime&gt;</c>) when the content can change.
    /// </summary>
    public string CacheControl { get; init; } = "public, max-age=86400";
}

/// <summary>
/// A virtual-host → disk-folder mapping (<c>SetVirtualHostNameToFolderMapping</c>) — the other way to
/// serve a bundle: folder mapping for disk-backed content, resource interception
/// (<see cref="WebViewHostOptions.ResourceProvider"/>) for embedded content. Both are supported; don't
/// unify one away.
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

/// <summary>Inputs for <see cref="WebViewHost"/>.</summary>
public sealed class WebViewHostOptions
{
    /// <summary>
    /// Environment inputs (user-data folder, dev flag, browser arguments…). The dev flag here is
    /// the single dev/prod source for the host too (settings, scripts, navigation) — wire it from
    /// <c>ShenoraEnvironment.IsDevelopment</c>.
    /// </summary>
    public required WebViewEnvironmentOptions Environment { get; init; }

    /// <summary>
    /// The app-level resource pipeline (<c>app.UseFiles(…)</c>, <c>app.UseMediaPlayer()</c>), applied to
    /// this host's interceptor at construction. Wire it from the built app —
    /// <c>Pipeline = sp.GetRequiredService&lt;WebViewPipeline&gt;()</c> — and every <see cref="WebViewHost"/>
    /// built from these options serves the same routes, a secondary window's included. Null = this host
    /// serves only what is registered on its own interceptor.
    /// <para>
    /// ⚠ <b>NOT a session browser.</b> <c>SessionBrowser</c> builds its own environment and interceptor
    /// and cannot be handed this pipeline, so a page that renders <c>mediaUrl(…)</c> happily in the main
    /// window 404s inside a <c>RenderSession</c> or <c>StreamingSession</c>. <c>docs/ADOPTION.md</c> has
    /// the recipe for serving your own frontend into an off-screen session.
    /// </para>
    /// </summary>
    public Shenora.Core.WebView.WebViewPipeline? Pipeline { get; init; }

    /// <summary>
    /// True (default) = the process-wide shared environment (main window, main UI thread).
    /// False = a fresh environment on the CALLING thread — required for secondary windows on
    /// their own STA thread (see <see cref="WebViewEnvironment"/>'s thread-affinity contract).
    /// </summary>
    public bool UseSharedEnvironment { get; init; } = true;

    /// <summary>
    /// Budget for environment creation + <c>EnsureCoreWebView2Async</c>. ⚠ An orphaned
    /// user-data-folder lock (a zombie browser process) hangs init FOREVER without it — no error, no
    /// window.
    /// </summary>
    public TimeSpan InitTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// The dev server URL (e.g. <c>http://localhost:3517</c>), kept in sync with the frontend's
    /// <c>vite.config.ts</c>. No default on purpose: pick a unique port so parallel dev sessions of
    /// sibling apps can't collide.
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
    /// Explicit production start URL, overriding the <see cref="VirtualHost"/> default
    /// (<c>https://{VirtualHost}/index.html</c>) — the server-backed profile points this at its own
    /// in-process HTTP server.
    /// </summary>
    public string? ProductionUrl { get; init; }

    /// <summary>Async app schemes (see <see cref="WebViewDeferredScheme"/>).</summary>
    public IReadOnlyList<WebViewDeferredScheme> DeferredSchemes { get; init; } = [];

    /// <summary>Disk-folder virtual hosts (see <see cref="WebViewFolderMapping"/>).</summary>
    public IReadOnlyList<WebViewFolderMapping> FolderMappings { get; init; } = [];

    /// <summary>
    /// The control's background before content paints. Set it to the SAME color as the form and the
    /// app's page background, or the window flashes white. Null = leave default.
    /// </summary>
    public Color? BackgroundColor { get; init; }

    /// <summary>
    /// Keep OS drag-drop delivery to the browser enabled so internal HTML5 drag-drop works;
    /// EXTERNAL file drops are neutralized by <see cref="PreventDefaultFileDrop"/> and belong to
    /// the native drop-zone overlay.
    /// </summary>
    public bool AllowExternalDrop { get; init; } = true;

    /// <summary>Inject the script that stops the browser navigating to dropped files.</summary>
    public bool PreventDefaultFileDrop { get; init; } = true;

    /// <summary>Inject the script that blocks browser chrome shortcuts in production.</summary>
    public bool BlockBrowserShortcutsInProduction { get; init; } = true;

    /// <summary>
    /// Globals injected on document created as <c>window.&lt;name&gt; = &lt;json&gt;;</c> (e.g. an
    /// app-metadata object). Values are JSON-serialized camelCase with full escaping.
    /// </summary>
    public IReadOnlyDictionary<string, object?> InjectedGlobals { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>Raw scripts additionally executed on every document created, in order.</summary>
    public IReadOnlyList<string> DocumentCreatedScripts { get; init; } = [];

    /// <summary>
    /// Runs AFTER the hardening preset, so an app can override individual settings without losing
    /// the rest.
    /// </summary>
    public Action<CoreWebView2Settings>? ConfigureSettings { get; init; }

    /// <summary>
    /// <c>window.open</c>/<c>target=_blank</c> goes to the SYSTEM browser (http/https only, anything
    /// else dropped) — never a bare WebView2 popup.
    /// </summary>
    public bool OpenExternalLinksInSystemBrowser { get; init; } = true;

    /// <summary>
    /// Replaces the default download policy (downloads are CANCELED and logged).
    /// <para>
    /// ⚠ <b>Return <c>true</c> when you have handled it</b>; <c>false</c> falls through to the built-in
    /// policy, and so does a throw (which is logged). An unanswered download event proceeds, so
    /// "observe and let the kit decide" must be spelled <c>false</c>.
    /// </para>
    /// </summary>
    public Func<CoreWebView2DownloadStartingEventArgs, bool>? OnDownloadStarting { get; init; }

    /// <summary>
    /// Replaces the default permission policy (kinds in <see cref="PermittedPermissions"/> allowed,
    /// everything else silently denied — no browser-style prompt ever interrupts the app).
    /// <para>
    /// ⚠ <b>Return <c>true</c> when you have set <c>State</c></b>; <c>false</c> (or a throw, which is
    /// logged) falls through to <see cref="PermittedPermissions"/>. An unanswered permission request
    /// stalls whatever asked for it.
    /// </para>
    /// </summary>
    public Func<CoreWebView2PermissionRequestedEventArgs, bool>? OnPermissionRequested { get; init; }

    /// <summary>Permission kinds the default policy allows.</summary>
    public IReadOnlyList<CoreWebView2PermissionKind> PermittedPermissions { get; init; } =
        [CoreWebView2PermissionKind.ClipboardRead];

    /// <summary>
    /// Recover from a crashed renderer by reloading — at most <see cref="MaxAutoReloads"/> times, and
    /// never more than once per <see cref="AutoReloadCooldown"/>. Browser-process kinds are NOT
    /// auto-recovered: the whole control is dead then.
    /// </summary>
    public bool ReloadOnRenderProcessFailure { get; init; } = true;

    /// <summary>Minimum spacing between automatic renderer-crash reloads.</summary>
    public TimeSpan AutoReloadCooldown { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many times a renderer crash may be auto-recovered before the host gives up and leaves the
    /// failure to <see cref="OnProcessFailed"/> (default 3). Rate-limiting alone is not a stopping
    /// condition — a page that faults during load would reload every cooldown FOREVER, burning a
    /// browser process each time. A successful navigation resets the count.
    /// </summary>
    public int MaxAutoReloads { get; init; } = 3;

    /// <summary>
    /// Replaces the default process-failure handling (which logs the failure in detail, then
    /// auto-reloads per <see cref="ReloadOnRenderProcessFailure"/>).
    /// <para>
    /// 🔴 <b>Return <c>false</c> to OBSERVE without replacing.</b> Returning <c>true</c> means "handled",
    /// which suppresses the diagnostic log AND the whole auto-reload path — so
    /// <see cref="ReloadOnRenderProcessFailure"/>, <see cref="AutoReloadCooldown"/> and
    /// <see cref="MaxAutoReloads"/> stay set and do nothing. Crash telemetry wants <c>false</c>.
    /// </para>
    /// </summary>
    public Func<CoreWebView2ProcessFailedEventArgs, bool>? OnProcessFailed { get; init; }

    /// <summary>Diagnostics sink. Null = <see cref="WebViewEnvironmentOptions.Log"/>.</summary>
    public ILogger? Log { get; init; }
}
