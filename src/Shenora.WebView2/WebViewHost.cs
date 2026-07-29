using System.Text;
using Microsoft.Web.WebView2.Core;
// Inside namespace Shenora.WebView2 the bare identifier "WebView2" resolves to the namespace, so
// the control type needs an alias.
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.WebView2;

/// <summary>
/// The ONE place a WebView2 gets configured — merged from the three family initializers, with
/// the gaps every source shipped with fixed here (init-timeout guard, new-window/download/
/// permission/process-failure policies, escaped script injection, dev-gated settings hardening).
///
/// Usage: create the control, then
/// <code>
/// var host = new WebViewHost(webView, options);
/// await host.InitializeAsync();
/// host.Navigate();
/// </code>
/// Call on the thread that owns the control (the main UI thread — or a secondary window's own
/// STA thread with <see cref="WebViewHostOptions.UseSharedEnvironment"/> = false).
/// </summary>
public sealed class WebViewHost
{
    /// <summary>Minimum spacing between automatic renderer-crash reloads (see
    /// <see cref="WebViewHostOptions.ReloadOnRenderProcessFailure"/>).</summary>
    public static readonly TimeSpan AutoReloadCooldown = TimeSpan.FromSeconds(10);

    private readonly WebView2Control _webView;
    private readonly WebViewHostOptions _options;
    private readonly Action<string>? _log;
    private DateTime _lastAutoReloadUtc = DateTime.MinValue;

    public WebViewHost(WebView2Control webView, WebViewHostOptions options)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = options.Log ?? options.Environment.Log;
    }

    /// <summary>Dev/prod, from the single source (<see cref="WebViewEnvironmentOptions.IsDevelopment"/>).</summary>
    public bool IsDevelopment => _options.Environment.IsDevelopment;

    /// <summary>
    /// Obtain the environment (shared/prewarmed, or thread-own), ensure the core, then apply
    /// settings, resource serving, scripts, and event policies. The whole sequence runs under
    /// <see cref="WebViewHostOptions.InitTimeout"/>: an orphaned user-data-folder lock (zombie
    /// browser process) otherwise hangs <c>EnsureCoreWebView2Async</c> forever with no window
    /// and no error — the family's measured failure mode.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            var environment = _options.UseSharedEnvironment
                ? await WebViewEnvironment.GetSharedAsync(_options.Environment).WaitAsync(_options.InitTimeout)
                : await WebViewEnvironment.CreateForCurrentThreadAsync(_options.Environment).WaitAsync(_options.InitTimeout);

            await _webView.EnsureCoreWebView2Async(environment).WaitAsync(_options.InitTimeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"WebView2 failed to initialize within {_options.InitTimeout.TotalSeconds:0}s. " +
                $"The usual cause is a leftover browser process holding the user-data folder lock " +
                $"('{_options.Environment.UserDataFolder}') — end stray WebView2/msedgewebview2 " +
                "processes for this app, or delete the folder, and start again.");
        }

        ApplySettings();
        RegisterResourceServing();
        await InjectScriptsAsync();
        WireEventPolicies();
        _log?.Invoke($"[Shenora.WebView2] Host initialized (mode: {(IsDevelopment ? "Development" : "Production")})");
    }

    /// <summary>Navigate to the resolved start URL (see <see cref="ResolveStartUrl"/>).</summary>
    public void Navigate()
    {
        var url = ResolveStartUrl(_options);
        _log?.Invoke($"[Shenora.WebView2] Navigating to {url}");
        _webView.CoreWebView2.Navigate(url);
    }

    /// <summary>
    /// The dev/prod start-URL decision: development → <see cref="WebViewHostOptions.DevUrl"/>;
    /// production → <see cref="WebViewHostOptions.ProductionUrl"/>, else the virtual host's
    /// <c>index.html</c>. Missing configuration throws an actionable error instead of the source
    /// apps' silent blank window.
    /// </summary>
    internal static string ResolveStartUrl(WebViewHostOptions options)
    {
        if (options.Environment.IsDevelopment)
        {
            return options.DevUrl
                ?? throw new InvalidOperationException(
                    "Development mode needs WebViewHostOptions.DevUrl (the frontend dev-server " +
                    "URL, matching its vite.config.ts port).");
        }
        return options.ProductionUrl
            ?? (options.VirtualHost is { Length: > 0 } host
                ? $"https://{host}/index.html"
                : throw new InvalidOperationException(
                    "Production mode needs WebViewHostOptions.ProductionUrl, or VirtualHost + " +
                    "ResourceProvider for a packaged bundle."));
    }

    private void ApplySettings()
    {
        var settings = _webView.CoreWebView2.Settings;
        var isDev = IsDevelopment;

        // The family hardening preset: developer surfaces only in dev; everything the app shell
        // doesn't use switched off (each one shaves startup/attack surface); web messages on —
        // they're the IPC transport.
        settings.AreDevToolsEnabled = isDev;
        settings.AreDefaultContextMenusEnabled = isDev;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsWebMessageEnabled = true;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;

        _webView.AllowExternalDrop = _options.AllowExternalDrop;
        if (_options.BackgroundColor is { } color) _webView.DefaultBackgroundColor = color;

        _options.ConfigureSettings?.Invoke(settings);
    }

    private void RegisterResourceServing()
    {
        var core = _webView.CoreWebView2;

        foreach (var mapping in _options.FolderMappings)
        {
            core.SetVirtualHostNameToFolderMapping(mapping.HostName, mapping.FolderPath, mapping.AccessKind);
        }

        var virtualHostPrefix = _options.VirtualHost is { Length: > 0 } host && _options.ResourceProvider is not null
            ? $"https://{host}/"
            : null;
        if (virtualHostPrefix is not null)
        {
            core.AddWebResourceRequestedFilter(virtualHostPrefix + "*", CoreWebView2WebResourceContext.All);
        }
        foreach (var scheme in _options.DeferredSchemes)
        {
            core.AddWebResourceRequestedFilter(scheme.Scheme + "://*", CoreWebView2WebResourceContext.All);
        }
        if (virtualHostPrefix is null && _options.DeferredSchemes.Count == 0) return;

        // Two serving strategies, and the split is load-bearing (the source app's measured lesson):
        //
        // Virtual host = the packaged bundle, IN MEMORY, and index.html is the MAIN DOCUMENT the
        //   startup navigation is waiting on. Serve SYNCHRONOUSLY inline: an in-memory read is
        //   instant, and deferring the main document stalls the initial navigation → "stuck on
        //   start" (only reproduces in production — dev loads from Vite over http, never here).
        //
        // Deferred schemes = dynamic content (disk reads, remote fetch-and-cache). A burst of
        //   hundreds of requests (thumbnail grids on startup/scroll) served inline would block the
        //   UI thread → FREEZE. GetDeferral returns the UI thread immediately, the handler runs on
        //   the pool, and the response is built back on the UI thread (CoreWebView2 is UI-affine)
        //   via non-blocking BeginInvoke.
        core.WebResourceRequested += (_, args) =>
        {
            var uri = args.Request.Uri;

            if (virtualHostPrefix is not null && uri.StartsWith(virtualHostPrefix, StringComparison.OrdinalIgnoreCase))
            {
                ServeVirtualHost(args, uri, virtualHostPrefix);
                return;
            }

            foreach (var scheme in _options.DeferredSchemes)
            {
                if (uri.StartsWith(scheme.Scheme + "://", StringComparison.OrdinalIgnoreCase))
                {
                    ServeDeferred(args, uri, scheme);
                    return;
                }
            }
            // Not ours (e.g. a folder-mapping host) — let WebView2 handle it.
        };
    }

    private void ServeVirtualHost(CoreWebView2WebResourceRequestedEventArgs args, string uri, string prefix)
    {
        try
        {
            var path = uri[prefix.Length..];
            var queryIndex = path.IndexOf('?');
            if (queryIndex >= 0) path = path[..queryIndex];
            // The request path arrives percent-encoded; bundle filenames with spaces or
            // non-ASCII (CJK asset names are normal in this family) would otherwise miss the
            // manifest and 404 — production-only, since dev serves from Vite.
            path = Uri.UnescapeDataString(path);
            if (path.Length == 0) path = "index.html";

            var stream = _options.ResourceProvider!.GetResourceStream(path);
            if (stream is not null)
            {
                var headers = $"Content-Type: {WebViewContentTypes.FromPath(path)}\n" +
                              $"Cache-Control: {WebViewContentTypes.CacheControlFromPath(path)}\n" +
                              "Access-Control-Allow-Origin: *";
                args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
            }
            else
            {
                args.Response = NotFound($"Resource not found: {path}");
            }
        }
        catch (Exception ex)
        {
            try
            {
                args.Response = NotFound($"Error: {ex.Message}");
            }
            catch
            {
                // the webview may be tearing down
            }
        }
    }

    private void ServeDeferred(CoreWebView2WebResourceRequestedEventArgs args, string uri, WebViewDeferredScheme scheme)
    {
        var deferral = args.GetDeferral();
        _ = Task.Run(async () =>
        {
            byte[] data;
            var status = 200;
            var reason = "OK";
            string headers;
            try
            {
                var (bytes, contentType) = await scheme.Handler(new Uri(uri)).ConfigureAwait(false);
                data = bytes;
                headers = $"Content-Type: {contentType}\nCache-Control: {scheme.CacheControl}";
            }
            catch (Exception ex)
            {
                data = Encoding.UTF8.GetBytes($"Error: {ex.Message}");
                status = 404;
                reason = "Not Found";
                headers = "Content-Type: text/plain";
            }

            void Build()
            {
                try
                {
                    args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        new MemoryStream(data, writable: false), status, reason, headers);
                }
                catch
                {
                    // the webview may be tearing down
                }
                finally
                {
                    deferral.Complete();
                }
            }

            // We are ALWAYS on a thread-pool thread here, so the response must be marshalled to
            // the UI thread — CoreWebView2 is UI-affine. Never build inline: before the handle
            // exists InvokeRequired is false, and an inline build would run on the pool thread
            // (the source app's exact bug). No handle (early-startup race) → complete without a
            // response.
            try
            {
                if (_webView.IsHandleCreated)
                    _webView.BeginInvoke((Action)Build);
                else
                    deferral.Complete();
            }
            catch
            {
                try { deferral.Complete(); } catch { }
            }
        });
    }

    private CoreWebView2WebResourceResponse NotFound(string message) =>
        _webView.CoreWebView2.Environment.CreateWebResourceResponse(
            new MemoryStream(Encoding.UTF8.GetBytes(message), writable: false),
            404, "Not Found", "Content-Type: text/plain");

    private async Task InjectScriptsAsync()
    {
        var core = _webView.CoreWebView2;

        if (_options.PreventDefaultFileDrop)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WebViewScripts.PreventDefaultFileDrop);

        if (_options.BlockBrowserShortcutsInProduction && !IsDevelopment)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WebViewScripts.BlockBrowserShortcuts);

        foreach (var (name, value) in _options.InjectedGlobals)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WebViewScripts.BuildGlobalScript(name, value));

        foreach (var script in _options.DocumentCreatedScripts)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
    }

    private void WireEventPolicies()
    {
        var core = _webView.CoreWebView2;

        if (_options.OpenExternalLinksInSystemBrowser)
        {
            // External links (target=_blank / window.open) go to the SYSTEM browser — never a
            // bare WebView2 popup. Scheme-checked so a page can't shell-execute odd protocols.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                {
                    try
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
                    }
                    catch
                    {
                        // no default browser — nothing sensible to do
                    }
                }
            };
        }

        core.DownloadStarting += (_, e) =>
        {
            if (_options.OnDownloadStarting is { } onDownload)
            {
                onDownload(e);
                return;
            }
            e.Cancel = true;
            _log?.Invoke($"[Shenora.WebView2] Download canceled by policy: {e.DownloadOperation.Uri}");
        };

        core.PermissionRequested += (_, e) =>
        {
            if (_options.OnPermissionRequested is { } onPermission)
            {
                onPermission(e);
                return;
            }
            e.State = _options.PermittedPermissions.Contains(e.PermissionKind)
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        };

        core.ProcessFailed += (_, e) =>
        {
            if (_options.OnProcessFailed is { } onFailed)
            {
                onFailed(e);
                return;
            }
            _log?.Invoke($"[Shenora.WebView2] Process failed: {e.ProcessFailedKind} (reason: {e.Reason})");
            if (_options.ReloadOnRenderProcessFailure
                && e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited
                && DateTime.UtcNow - _lastAutoReloadUtc > AutoReloadCooldown)
            {
                _lastAutoReloadUtc = DateTime.UtcNow;
                _log?.Invoke("[Shenora.WebView2] Renderer crashed — reloading.");
                try { _webView.Reload(); } catch { }
            }
        };
    }
}
