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
    private readonly WebView2Control _webView;
    private readonly WebViewHostOptions _options;
    private readonly Action<string>? _log;
    private readonly Shenora.Core.IUiDispatcher _ui;
    // The one open-a-URL implementation, reachable since D19 — see the NewWindowRequested policy.
    private readonly Shenora.Core.IUrlLauncher _urls = new Shenora.WinForms.ShellLauncher();
    private DateTime _lastAutoReloadUtc = DateTime.MinValue;
    private int _autoReloadCount;            // terminal state for the crash-reload loop (see WireEventPolicies)
    private Task? _initialization;           // InitializeAsync is idempotent — see its remarks

    public WebViewHost(WebView2Control webView, WebViewHostOptions options)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = options.Log ?? options.Environment.Log;
        // The one marshalling owner (D19/D20, P5.5 H4.2).
        _ui = new Shenora.WinForms.WinFormsUiDispatcher(webView,
            ex => Log(() => $"[Shenora.WebView2] Posted UI work failed: {ex.Message}"));
    }

    /// <summary>
    /// Write a diagnostic through the app's sink — GUARDED and LAZY (P5.5 H2).
    /// <para>
    /// <see cref="WebViewHostOptions.Log"/> is an app-supplied delegate, and almost every call site
    /// below sits inside a WebView2 event handler or a posted UI-thread body, where an escaping
    /// exception has no caller to catch it and becomes an unhandled UI-thread exception (a modal crash
    /// dialog under the family bootstrap). Routing every one through
    /// <see cref="Shenora.Core.AppCallback"/> makes that structurally impossible instead of relying on
    /// each site remembering.
    /// </para>
    /// <para>
    /// The <see cref="Func{TResult}"/> is not ceremony: the guard has to cover BUILDING the message as
    /// well as writing it, because several messages read WebView2/COM properties (a download
    /// operation's URI, a process-failed reason) that can throw once the underlying object is gone —
    /// and that read would otherwise happen at the call site, outside the guard. It also makes the
    /// interpolation free when no sink is configured.
    /// </para>
    /// </summary>
    private void Log(Func<string> message)
    {
        if (_log is null) return;
        Shenora.Core.AppCallback.Run(() => _log(message()));
    }

    /// <summary>
    /// Invoke one of the app's event-policy hooks and report whether it HANDLED the event: true only
    /// when it ran to completion. A hook that throws returns false, so the caller applies the kit's own
    /// default rather than leaving a WebView2 event unanswered (P5.5 H2).
    /// </summary>
    private bool AppCallbackRan<T>(Action<T> callback, T args, string hookName) =>
        Shenora.Core.AppCallback.Run(() => callback(args),
            ex => Log(() => $"[Shenora.WebView2] {hookName} threw ({ex.GetType().Name}: {ex.Message}); " +
                            "applying the built-in policy instead."));

    /// <summary>Dev/prod, from the single source (<see cref="WebViewEnvironmentOptions.IsDevelopment"/>).</summary>
    public bool IsDevelopment => _options.Environment.IsDevelopment;

    /// <summary>
    /// Obtain the environment (shared/prewarmed, or thread-own), ensure the core, then apply
    /// settings, resource serving, scripts, and event policies. The whole sequence runs under
    /// <see cref="WebViewHostOptions.InitTimeout"/>: an orphaned user-data-folder lock (zombie
    /// browser process) otherwise hangs <c>EnsureCoreWebView2Async</c> forever with no window
    /// and no error — the family's measured failure mode.
    /// </summary>
    /// <remarks>
    /// IDEMPOTENT (P5.5 H3): the first call does the work and every later call awaits that same task.
    /// The timeout message itself advises "start again", so a Retry button is the expected recovery —
    /// and a second call used to re-run <c>WireEventPolicies</c>, double-subscribing every policy
    /// handler: from then on each external link opened TWICE, each download decision ran twice, and the
    /// renderer auto-reload raced itself. Nothing in the sequence was safe to repeat. A FAILED
    /// initialization clears the cached task, so a retry is still a real retry. UI thread only, like the
    /// rest of this type — hence no locking around the cache.
    /// </remarks>
    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        // ONE budget for the WHOLE sequence, not one per await (P5.5 H3). Each step used to get its own
        // full InitTimeout, so the documented "25 s" was really 50 s before the sequence even reached
        // ApplySettings — and ApplySettings/RegisterResourceServing/InjectScriptsAsync were unbounded on
        // top of that, which matters because script injection is a real round-trip to the browser.
        using var budget = new CancellationTokenSource(_options.InitTimeout);
        try
        {
            var environment = await (_options.UseSharedEnvironment
                ? WebViewEnvironment.GetSharedAsync(_options.Environment)
                : WebViewEnvironment.CreateForCurrentThreadAsync(_options.Environment))
                .WaitAsync(budget.Token);

            await _webView.EnsureCoreWebView2Async(environment).WaitAsync(budget.Token);

            ApplySettings();
            RegisterResourceServing();
            await InjectScriptsAsync().WaitAsync(budget.Token);
            WireEventPolicies();
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            // Re-throw as a TimeoutException: the budget expiring is a timeout, and the caller never
            // handed us a token, so an OperationCanceledException would be a lie about who gave up.
            throw new TimeoutException(
                $"WebView2 failed to initialize within {_options.InitTimeout.TotalSeconds:0}s. " +
                $"The usual cause is a leftover browser process holding the user-data folder lock " +
                $"('{_options.Environment.UserDataFolder}') — end stray WebView2/msedgewebview2 " +
                "processes for this app, or delete the folder, and start again.");
        }
        catch
        {
            // A failed init must be retryable — otherwise the "start again" the message advises would
            // hand back the same faulted task forever.
            _initialization = null;
            throw;
        }

        Log(() => $"[Shenora.WebView2] Host initialized (mode: {(IsDevelopment ? "Development" : "Production")})");
    }

    /// <summary>Navigate to the resolved start URL (see <see cref="ResolveStartUrl"/>).</summary>
    public void Navigate()
    {
        var url = ResolveStartUrl(_options);
        AssertBundleServable(url, _options);
        Log(() => $"[Shenora.WebView2] Navigating to {url}");
        _webView.CoreWebView2.Navigate(url);
    }

    /// <summary>
    /// Fail loudly when the START DOCUMENT is the packaged bundle but the provider cannot serve it
    /// (P5.5 H3).
    /// <para>
    /// A mistyped or stale <see cref="EmbeddedResourceProviderOptions.ResourcePrefix"/> — a string that
    /// depends on MSBuild's manifest-name mangling — matches nothing, so every request 404s and the app
    /// opens a BLACK WINDOW with no error anywhere. <see cref="ResolveStartUrl"/> already throws
    /// actionably for the neighbouring class of mistake (missing URL configuration); this closes the gap
    /// where the URL is fine and the content behind it is not.
    /// </para>
    /// <para>
    /// It is checked HERE, not in the provider's constructor, because a provider with nothing to serve
    /// is perfectly valid when the page loads from a dev URL — which is the normal state of a fresh
    /// clone, whose bundle has not been built yet. The condition is "the bundle IS the document", and
    /// only this method knows that. The probe is <see cref="IWebViewResourceProvider.Exists"/> on
    /// <c>index.html</c>, which also catches a bundle that is present but incomplete.
    /// </para>
    /// <para>Internal + static (like <see cref="ResolveStartUrl"/>) so it is testable without a live
    /// browser process — <c>Navigate</c> itself needs one.</para>
    /// </summary>
    internal static void AssertBundleServable(string url, WebViewHostOptions options)
    {
        if (options.ResourceProvider is not { } provider) return;
        // Only when the start document comes from the virtual host — an app pointing ProductionUrl
        // elsewhere may use the provider for subresources only, and that is its business.
        if (options.VirtualHost is not { Length: > 0 } host) return;
        if (!url.StartsWith($"https://{host}/", StringComparison.OrdinalIgnoreCase)) return;

        if (provider.Exists("index.html")) return;

        throw new InvalidOperationException(
            $"The start document is '{url}', but the resource provider has no 'index.html' to serve — " +
            "every request would 404 and the window would come up blank. Check the bundle was built " +
            "into the app (an empty output folder embeds nothing) and that the provider's resource " +
            "prefix matches the assembly's actual manifest names; the provider logs what it found on " +
            "construction.");
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
                // The path is the page's own request, so echoing it leaks nothing — but keep the shape
                // uniform with the catch below and let the log carry the detail.
                Log(() => $"[Shenora.WebView2] 404 for bundle resource '{path}'");
                args.Response = NotFound();
            }
        }
        catch (Exception ex)
        {
            try
            {
                // The BODY says nothing about the exception (P5.5 H3). These responses carry
                // `Access-Control-Allow-Origin: *`, so page script can fetch any of them and read the
                // text — `ex.Message` there routinely means a full local filesystem path, or an inner
                // provider's message. Same rule as the IPC error boundary: the diagnosis goes to the
                // host log, the wire gets a code.
                Log(() => $"[Shenora.WebView2] Serving '{uri}' failed: {ex}");
                args.Response = NotFound();
            }
            catch
            {
                // the webview may be tearing down
            }
        }
    }

    /// <summary>
    /// Copy the request onto a plain object, ON THE UI THREAD, before handing it to a pool thread.
    /// The WebView2 args and their header collection are COM objects with thread affinity, so
    /// reading <c>args.Request.Headers</c> from inside the handler's <c>Task.Run</c> is a use of a
    /// UI-thread object off the UI thread — the kind that works until it doesn't.
    /// </summary>
    private static WebViewResourceRequest SnapshotRequest(CoreWebView2WebResourceRequestedEventArgs args, string uri)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var header in args.Request.Headers)
            {
                // Last one wins for a repeated name; none of the headers a handler acts on repeat.
                headers[header.Key] = header.Value;
            }
        }
        catch
        {
            // A torn-down request has no readable headers; an empty set simply means "no Range",
            // which degrades to serving the whole resource rather than failing.
        }

        var method = "GET";
        try { method = args.Request.Method ?? "GET"; } catch { /* same */ }

        return new WebViewResourceRequest
        {
            Uri = new Uri(uri),
            Method = method.ToUpperInvariant(),
            Headers = headers,
        };
    }

    private void ServeDeferred(CoreWebView2WebResourceRequestedEventArgs args, string uri, WebViewDeferredScheme scheme)
    {
        var deferral = args.GetDeferral();
        // Snapshot the request on THIS thread: the args object belongs to the UI thread and its
        // Headers collection must not be walked from the pool thread the handler runs on.
        var request = SnapshotRequest(args, uri);

        _ = Task.Run(async () =>
        {
            WebViewResourceResponse response;
            try
            {
                response = await scheme.Handler(request).ConfigureAwait(false)
                           ?? WebViewResourceResponse.NotFound();
            }
            catch (Exception ex)
            {
                // No exception text in the body (P5.5 H3) — an app scheme handler's message is the most
                // likely of all of these to carry a real path or a remote URL, and page script can read
                // this body. The handler's failure goes to the host log instead.
                Log(() => $"[Shenora.WebView2] Deferred scheme '{scheme.Scheme}' failed for '{uri}': {ex}");
                response = WebViewResourceResponse.NotFound();
            }

            var headerLines = new List<string>();
            foreach (var (key, value) in response.Headers) headerLines.Add($"{key}: {value}");
            // The scheme's Cache-Control is a DEFAULT, not an override: a handler answering 206 or 404
            // has its own caching story, and stamping "cache for a day" over it would be wrong.
            if (!response.Headers.ContainsKey("Cache-Control") && response.StatusCode is >= 200 and < 300)
                headerLines.Add($"Cache-Control: {scheme.CacheControl}");

            void Build()
            {
                try
                {
                    args.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        response.Content, response.StatusCode, response.ReasonPhrase,
                        string.Join("\n", headerLines));
                }
                catch
                {
                    // the webview may be tearing down
                    response.Content.Dispose();
                }
                finally
                {
                    deferral.Complete();
                }
            }

            // We are ALWAYS on a thread-pool thread here, so the response must be marshalled to
            // the UI thread — CoreWebView2 is UI-affine. Never build inline: before the handle
            // exists InvokeRequired is false, and an inline build would run on the pool thread
            // (the source app's exact bug). The one marshalling owner encodes that rule (P5.5 H4.2);
            // it returns false when there is no handle (early-startup race) or the control is gone,
            // and then we complete WITHOUT a response rather than serving from the wrong thread.
            try
            {
                if (!_ui.Post(Build)) deferral.Complete();
            }
            catch
            {
                try { deferral.Complete(); } catch { }
            }
        });
    }

    /// <summary>
    /// The one 404 body served to the page — deliberately CONSTANT. Every response here carries
    /// <c>Access-Control-Allow-Origin: *</c>, so page script can read whatever is in it; the reason a
    /// request failed belongs in the host log, not in a body a compromised or third-party script can
    /// fetch (P5.5 H3 — this used to be <c>$"Error: {ex.Message}"</c>).
    /// </summary>
    private static readonly byte[] NotFoundBody = Encoding.UTF8.GetBytes("Not Found");

    private CoreWebView2WebResourceResponse NotFound() =>
        _webView.CoreWebView2.Environment.CreateWebResourceResponse(
            new MemoryStream(NotFoundBody, writable: false),
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
                try
                {
                    // Delegate to the ONE open-a-URL implementation (P5.5 H4.5) — reachable since the
                    // re-layer (D19). This used to be a hand-copied duplicate of
                    // ShellLauncher.OpenUrl that had drifted: it was missing the Win11 process-handle
                    // Dispose, so every external link click leaked a handle. It also re-implemented
                    // the http/https scheme gate, which the launcher enforces itself.
                    _urls.OpenUrl(e.Uri);
                }
                catch (Exception ex)
                {
                    // Rejected scheme, or no default browser — a page must not be able to crash the
                    // host by asking to open something odd.
                    Log(() => $"[Shenora.WebView2] Ignoring new-window request for {e.Uri}: {ex.GetType().Name}");
                }
            };
        }

        // ALL THREE app policy hooks below run inside a WebView2 event handler, so a throw from one
        // has no caller on its stack and becomes an unhandled UI-thread exception (P5.5 H2). Each is
        // therefore invoked through AppCallback, and each FALLS BACK TO THE KIT'S OWN DEFAULT when the
        // app's hook fails — because leaving the event unanswered is its own bug: an un-cancelled
        // download proceeds, an unanswered permission request stalls whatever asked for it, and a
        // renderer crash goes unhandled at the exact moment things are already going wrong.

        core.DownloadStarting += (_, e) =>
        {
            if (_options.OnDownloadStarting is { } onDownload
                && AppCallbackRan(onDownload, e, nameof(WebViewHostOptions.OnDownloadStarting)))
                return;

            e.Cancel = true;
            Log(() => $"[Shenora.WebView2] Download canceled by policy: {e.DownloadOperation.Uri}");
        };

        core.PermissionRequested += (_, e) =>
        {
            if (_options.OnPermissionRequested is { } onPermission
                && AppCallbackRan(onPermission, e, nameof(WebViewHostOptions.OnPermissionRequested)))
                return;

            e.State = _options.PermittedPermissions.Contains(e.PermissionKind)
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        };

        core.ProcessFailed += (_, e) =>
        {
            if (_options.OnProcessFailed is { } onFailed
                && AppCallbackRan(onFailed, e, nameof(WebViewHostOptions.OnProcessFailed)))
                return;

            Log(() => $"[Shenora.WebView2] Process failed: {e.ProcessFailedKind} (reason: {e.Reason})");
            if (!_options.ReloadOnRenderProcessFailure
                || e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited) return;

            if (DateTime.UtcNow - _lastAutoReloadUtc <= _options.AutoReloadCooldown) return;

            // TERMINAL after MaxAutoReloads (P5.5 H3). The cooldown alone only slowed the loop down: a
            // page that faults during load kept crash-reload-crashing every 10 s for the process
            // lifetime, spawning a renderer each time. Log the give-up ONCE — at the cap, not on every
            // later crash — so the log says what happened without becoming the new spin.
            if (_autoReloadCount >= _options.MaxAutoReloads)
            {
                if (_autoReloadCount == _options.MaxAutoReloads)
                {
                    _autoReloadCount++; // past the cap: never log this again
                    Log(() => $"[Shenora.WebView2] Renderer crashed {_options.MaxAutoReloads} times — " +
                              "giving up on auto-reload. The page is most likely crashing deterministically; " +
                              "handle it via WebViewHostOptions.OnProcessFailed.");
                }
                return;
            }

            _autoReloadCount++;
            _lastAutoReloadUtc = DateTime.UtcNow;
            Log(() => $"[Shenora.WebView2] Renderer crashed — reloading ({_autoReloadCount}/{_options.MaxAutoReloads}).");
            try { _webView.Reload(); } catch { }
        };

        // A page that actually loads clears the budget, so a long-running app is not slowly used up by
        // unrelated crashes hours apart — the cap is meant to catch a CRASH LOOP, not to ration a
        // session. Only a successful navigation counts; an error page must not reset it.
        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) _autoReloadCount = 0;
        };
    }
}
