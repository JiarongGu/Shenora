using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Shenora.Core.WebView;
using Shenora.Core.Shell;
using Shenora.Core.Ipc;
// `WebView2` alone resolves to the NAMESPACE in here, hence the alias.
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;
using Shenora;

namespace Shenora.Windows;

/// <summary>
/// The ONE place a WebView2 gets configured.
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
    private readonly ILogger? _log;
    private readonly Shenora.Core.Shell.IUiDispatcher _ui;
    // The one open-a-URL implementation (D19).
    private readonly Shenora.Core.Shell.IUrlLauncher _urls = new Shenora.Windows.ShellLauncher();
    private readonly WebView2Interceptor _interceptor = new();
    private DateTime _lastAutoReloadUtc = DateTime.MinValue;
    private int _autoReloadCount;            // terminal state for the crash-reload loop
    private Task? _initialization;

    /// <summary>Wraps <paramref name="webView"/>. Construct, then <see cref="InitializeAsync"/>, then navigate.</summary>
    public WebViewHost(WebView2Control webView, WebViewHostOptions options)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = options.Log ?? options.Environment.Log;

        // The app-level pipeline (D64), applied before anything can navigate. Not guarded: a throwing
        // step is a composition mistake and must fail the window loudly rather than produce one that
        // silently serves nothing.
        options.Pipeline?.ApplyTo(_interceptor);

        // http/https need no registration — those are the browser's own schemes, served by a virtual
        // host rather than a custom one.
        var registered = options.Environment.CustomSchemes.Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unregistered = options.DeferredSchemes
            .Select(s => s.Scheme)
            .Where(s => !s.Equals("http", StringComparison.OrdinalIgnoreCase)
                     && !s.Equals("https", StringComparison.OrdinalIgnoreCase)
                     && !registered.Contains(s))
            .ToArray();
        if (unregistered.Length > 0)
        {
            throw new InvalidOperationException(
                $"DeferredSchemes names {string.Join(", ", unregistered.Select(s => $"'{s}'"))} but "
                + $"{nameof(WebViewEnvironmentOptions)}.{nameof(WebViewEnvironmentOptions.CustomSchemes)} "
                + "does not register them. WebView2 accepts custom schemes only when the ENVIRONMENT is "
                + "created, so without that registration every request to them fails in the page as "
                + "'TypeError: Failed to fetch' with nothing in the host log. Add "
                + $"`CustomSchemes = [new {nameof(WebViewCustomScheme)} {{ Name = \"{unregistered[0]}\" }}]` "
                + "to the environment options.");
        }
        // The one marshalling owner (D19/D20).
        _ui = new Shenora.Windows.WinFormsUiDispatcher(webView,
            ex => Log(() => "[Shenora.Windows] Posted UI work failed", ex));
    }

    /// <summary>
    /// Guarded + lazy, via the one owner (<see cref="Shenora.AppCallback.Log"/>): these sites have no
    /// caller to catch anything, and BUILDING a message may touch a torn-down COM object.
    /// </summary>
    private void Log(Func<string> message, Exception? failure = null) => Shenora.AppCallback.Log(_log, message, exception: failure);

    /// <summary>
    /// Invoke one of the app's event-policy hooks and report whether it HANDLED the event. A hook that
    /// throws counts as "not handled" and is logged, so the caller applies the kit's own default rather
    /// than leaving a WebView2 event unanswered.
    /// </summary>
    private bool AppHandled<T>(Func<T, bool> callback, T args, string hookName) =>
        Shenora.AppCallback.RunOrDefault(() => callback(args), false,
            ex => Log(() => $"[Shenora.Windows] {hookName} threw; applying the built-in policy instead.", ex));

    /// <summary>Dev/prod, from the single source (<see cref="WebViewEnvironmentOptions.IsDevelopment"/>).</summary>
    public bool IsDevelopment => _options.Environment.IsDevelopment;

    /// <summary>
    /// This host's resource-interception pipeline (D45) — the portable seam a feature adds middleware to, e.g.
    /// <c>host.Interceptor.UseFiles(new WebViewFileOptions { … })</c>. Usable before or after
    /// <see cref="InitializeAsync"/>; the pipeline is read per request.
    /// <para>
    /// 🔴 Keep interception paths OFF bundle paths. Middleware see the PAGE'S OWN ORIGIN (the bundle's
    /// virtual host in production, the dev server in development — see
    /// <see cref="WebView2Interceptor.ExtraFilters"/>), and a path the bundle also contains loses to the
    /// bundle here while winning on the mobile shells.
    /// </para>
    /// </summary>
    public IWebViewInterceptor Interceptor => _interceptor;

    /// <summary>
    /// Obtain the environment (shared/prewarmed, or thread-own), ensure the core, then apply
    /// settings, resource serving, scripts, and event policies — the whole sequence under
    /// <see cref="WebViewHostOptions.InitTimeout"/>.
    /// </summary>
    /// <remarks>
    /// IDEMPOTENT: the first call does the work and every later call awaits that same task — nothing in
    /// the sequence is safe to repeat (a second <c>WireEventPolicies</c> double-subscribes every policy
    /// handler). A FAILED attempt is never handed back, so a retry is a real retry. UI thread only, hence
    /// no locking around the cache.
    /// </remarks>
    public Task InitializeAsync()
    {
        // ⚠ Asked HERE rather than cleared from inside the sequence: a failure before the first real
        // suspension completes the task BEFORE `??=` has assigned it, so a `_initialization = null` in
        // the catch nulls a field nothing has written yet and the faulted task is cached on the way out.
        if (_initialization is { IsFaulted: false, IsCanceled: false } inFlight) return inFlight;
        return _initialization = InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        // ONE budget for the WHOLE sequence, not one per await.
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
            // A TimeoutException, not OperationCanceled: the caller never handed us a token.
            throw new TimeoutException(
                $"WebView2 failed to initialize within {_options.InitTimeout.TotalSeconds:0}s. " +
                $"The usual cause is a leftover browser process holding the user-data folder lock " +
                $"('{_options.Environment.UserDataFolder}') — end stray WebView2/msedgewebview2 " +
                "processes for this app, or delete the folder, and start again.");
        }

        Log(() => $"[Shenora.Windows] Host initialized (mode: {(IsDevelopment ? "Development" : "Production")})");
    }

    /// <summary>Navigate to the resolved start URL (see <see cref="ResolveStartUrl"/>).</summary>
    public void Navigate()
    {
        var url = ResolveStartUrl(_options);
        AssertBundleServable(url, _options);
        Log(() => $"[Shenora.Windows] Navigating to {url}");
        _webView.CoreWebView2.Navigate(url);
    }

    /// <summary>
    /// Fail loudly when the START DOCUMENT is the packaged bundle but the provider cannot serve it: a
    /// mistyped <see cref="EmbeddedResourceProviderOptions.ResourcePrefix"/> matches nothing, so every
    /// request 404s and the app opens a BLACK WINDOW with no error anywhere.
    /// <para>
    /// Checked HERE rather than in the provider's constructor, because a provider with nothing to serve
    /// is valid when the page loads from a dev URL — and only this method knows the bundle IS the
    /// document.
    /// </para>
    /// </summary>
    internal static void AssertBundleServable(string url, WebViewHostOptions options)
    {
        if (options.ResourceProvider is not { } provider) return;
        // Only when the start document comes from the virtual host — an app pointing ProductionUrl
        // elsewhere may use the provider for subresources only.
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
    /// <c>index.html</c>. Missing configuration throws an actionable error rather than opening a
    /// blank window.
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

        // The hardening preset: developer surfaces only in dev, everything the app shell doesn't use
        // off, web messages on — they are the IPC transport.
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

        var virtualHostPrefix = WebViewBundleServing.Prefix(_options.VirtualHost, _options.ResourceProvider);
        if (virtualHostPrefix is not null)
        {
            core.AddWebResourceRequestedFilter(virtualHostPrefix + "*", CoreWebView2WebResourceContext.All);
        }
        foreach (var scheme in _options.DeferredSchemes)
        {
            core.AddWebResourceRequestedFilter(scheme.Scheme + "://*", CoreWebView2WebResourceContext.All);
        }
        // What the interceptor needs on top of the above — see WebView2Interceptor.ExtraFilters.
        var interceptorFilters = WebView2Interceptor.ExtraFilters(IsDevelopment, _options.DevUrl);
        foreach (var pattern in interceptorFilters)
        {
            core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All);
        }
        if (virtualHostPrefix is null && _options.DeferredSchemes.Count == 0 && interceptorFilters.Length == 0)
            return;

        // 🔴 TWO SERVING STRATEGIES, AND THE SPLIT IS LOAD-BEARING.
        //
        // Virtual host = the packaged bundle, IN MEMORY, and index.html is the MAIN DOCUMENT the
        //   startup navigation is waiting on. Serve SYNCHRONOUSLY inline — deferring the main
        //   document stalls the initial navigation → "stuck on start" (production only; dev loads
        //   from Vite over http, never here).
        //
        // Deferred schemes = dynamic content (disk reads, remote fetch-and-cache). A burst of
        //   hundreds of requests (thumbnail grids) served inline would block the UI thread → FREEZE.
        //   GetDeferral returns the UI thread immediately, the handler runs on the pool, and the
        //   response is built back on the UI thread (CoreWebView2 is UI-affine).
        core.WebResourceRequested += (_, args) =>
        {
            var uri = args.Request.Uri;
            var intercepting = _interceptor.HasRoutes;

            if (virtualHostPrefix is not null && uri.StartsWith(virtualHostPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!intercepting)
                {
                    WebViewBundleServing.Serve(args, _webView.CoreWebView2.Environment,
                        _options.ResourceProvider!, uri, virtualHostPrefix, message => Log(message));
                    return;
                }

                // TryServe, not Serve: the interceptor shares this origin with the bundle (D45), so a path
                // the bundle does NOT contain falls through to the pipeline instead of 404ing. A path it
                // DOES contain is still served synchronously — the main document never defers.
                if (WebViewBundleServing.TryServe(args, _webView.CoreWebView2.Environment,
                        _options.ResourceProvider!, uri, virtualHostPrefix, message => Log(message)))
                    return;

                ServeInterceptor(args, uri);
                return;
            }

            foreach (var scheme in _options.DeferredSchemes)
            {
                if (uri.StartsWith(scheme.Scheme + "://", StringComparison.OrdinalIgnoreCase))
                {
                    // The scheme owns its whole origin, so declining can only mean 404.
                    ServeAsync(args, uri, (request, _) => scheme.Handler(request), scheme.CacheControl,
                        $"deferred scheme '{scheme.Scheme}'", answerNotFoundWhenDeclined: true);
                    return;
                }
            }

            // Anything else that matched a filter — in practice the dev server's own origin.
            if (intercepting)
            {
                ServeInterceptor(args, uri);
                return;
            }
            // Not ours (e.g. a folder-mapping host) — let WebView2 handle it.
        };
    }

    /// <summary>
    /// Hand a request to the D45 middleware pipeline. Composed once per request, so a route registered
    /// while this one is in flight cannot half-apply; declining leaves the request to WebView2.
    /// </summary>
    private void ServeInterceptor(CoreWebView2WebResourceRequestedEventArgs args, string uri)
    {
        // Re-checked: the caller's HasRoutes read and this build are separate moments.
        if (_interceptor.Build() is not { } pipeline) return;
        ServeAsync(args, uri, pipeline, defaultCacheControl: null, "interceptor");
    }

    /// <summary>
    /// 🔴 Copy the request onto a plain object, ON THE UI THREAD, before handing it to a pool thread.
    /// The WebView2 args and their header collection are COM objects with thread affinity, so reading
    /// <c>args.Request.Headers</c> from inside the handler's <c>Task.Run</c> is a use of a UI-thread
    /// object off the UI thread.
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
            // A torn-down request has no readable headers; an empty set means "no Range", which
            // degrades to serving the whole resource rather than failing.
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

    /// <summary>
    /// Answer <paramref name="args"/> from <paramref name="handler"/> OFF the UI thread — the deferred
    /// path, shared by <see cref="WebViewHostOptions.DeferredSchemes"/> and by the D45 interceptor.
    /// </summary>
    /// <param name="args">The intercepted request, still on the UI thread.</param>
    /// <param name="uri">Its raw URI, already read.</param>
    /// <param name="handler">The handler. Runs on a thread-pool thread.</param>
    /// <param name="defaultCacheControl">Stamped on a 2xx that does not set its own; null for none.</param>
    /// <param name="what">What to name in the log if it fails.</param>
    /// <param name="answerNotFoundWhenDeclined">
    /// True when this origin is EXCLUSIVELY ours (a custom scheme), so declining can only mean 404. False
    /// for the interceptor, whose origin it shares with the page's own content: declining there must
    /// complete the deferral WITHOUT a response and let WebView2 handle the request normally.
    /// </param>
    private void ServeAsync(CoreWebView2WebResourceRequestedEventArgs args, string uri,
                            WebViewResourceHandler handler, string? defaultCacheControl, string what,
                            bool answerNotFoundWhenDeclined = false)
    {
        var deferral = args.GetDeferral();
        var request = SnapshotRequest(args, uri);

        _ = Task.Run(async () =>
        {
            WebViewResourceResponse? response;
            try
            {
                response = await handler(request, CancellationToken.None).ConfigureAwait(false);
                if (response is null && answerNotFoundWhenDeclined) response = WebViewResourceResponse.NotFound();
            }
            catch (Exception ex)
            {
                // 🔴 No exception text in the body — page script can read it, and a handler's message
                // routinely carries a real path or a remote URL. The failure goes to the host log.
                Log(() => $"[Shenora.Windows] {what} failed for '{uri}'", ex);
                // A THROW is a 404 even on a shared origin: falling through would hand a broken route
                // back to WebView2 and the page would see a network error instead of the fixed refusal.
                response = WebViewResourceResponse.NotFound();
            }

            if (response is null)
            {
                // Declined. Completing without a response is how WebView2 is told to carry on normally.
                try { deferral.Complete(); } catch { }
                return;
            }

            var headerLines = new List<string>();
            foreach (var (key, value) in response.Headers) headerLines.Add($"{key}: {value}");
            // A DEFAULT, never an override: a handler answering 206 or 404 has its own caching story.
            if (defaultCacheControl is not null && !response.Headers.ContainsKey("Cache-Control")
                && response.StatusCode is >= 200 and < 300)
                headerLines.Add($"Cache-Control: {defaultCacheControl}");

            // 🔴 CORS by default. An app scheme is a DIFFERENT ORIGIN from the page that loads it, so
            // without this every fetch is refused by the browser — and the refusal is indistinguishable
            // from the scheme not existing (a bare `TypeError: Failed to fetch`) even though the handler
            // ran and answered correctly. Registering the scheme's AllowedOrigins is NOT sufficient:
            // that governs which origins may REQUEST it, this governs whether the browser hands the
            // RESPONSE to script. A handler that sets the header itself wins.
            if (!response.Headers.ContainsKey("Access-Control-Allow-Origin"))
                headerLines.Add("Access-Control-Allow-Origin: *");

            // …and EXPOSE them: on a cross-origin response the browser hands script only the
            // CORS-safelisted headers, so `Content-Range` reads back as null on a perfectly correct 206
            // while the bytes are fine. Same override rule.
            if (!response.Headers.ContainsKey("Access-Control-Expose-Headers"))
                headerLines.Add("Access-Control-Expose-Headers: *");

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

            // 🔴 ALWAYS on a pool thread here, so the response must be marshalled through the ONE
            // marshalling owner — CoreWebView2 is UI-affine, and before the handle exists
            // `InvokeRequired` is FALSE, so an inline build would run on the pool thread. Post returns
            // false when there is no handle or the control is gone; then complete WITHOUT a response.
            // 🔴 IF `Build` NEVER RUNS, NOTHING ELSE WILL EVER CLOSE THE BODY. The body is lazy
            // (`BoundedBodyStream` over a real `FileStream`), so a `Post` that returns false or throws
            // leaks an OS FILE HANDLE per request — which on Windows also blocks deleting or moving the
            // file being served.
            try
            {
                if (!_ui.Post(Build))
                {
                    response.Content.Dispose();
                    deferral.Complete();
                }
            }
            catch
            {
                try { response.Content.Dispose(); } catch { }
                try { deferral.Complete(); } catch { }
            }
        });
    }

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
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                try
                {
                    // The ONE open-a-URL implementation — it enforces the http/https scheme gate itself.
                    _urls.OpenUrl(e.Uri);
                }
                catch (Exception ex)
                {
                    // Rejected scheme, or no default browser — a page must not be able to crash the host.
                    Log(() => $"[Shenora.Windows] Ignoring new-window request for {e.Uri}", ex);
                }
            };
        }

        // All three app policy hooks below run inside a WebView2 event handler, so each is invoked
        // through AppCallback and each FALLS BACK to the kit's own default when the app's hook fails:
        // an un-cancelled download proceeds, an unanswered permission request stalls whatever asked for
        // it, and a renderer crash goes unhandled exactly when things are already wrong.

        core.DownloadStarting += (_, e) =>
        {
            if (_options.OnDownloadStarting is { } onDownload
                && AppHandled(onDownload, e, nameof(WebViewHostOptions.OnDownloadStarting)))
                return;

            e.Cancel = true;
            Log(() => $"[Shenora.Windows] Download canceled by policy: {e.DownloadOperation.Uri}");
        };

        core.PermissionRequested += (_, e) =>
        {
            if (_options.OnPermissionRequested is { } onPermission
                && AppHandled(onPermission, e, nameof(WebViewHostOptions.OnPermissionRequested)))
                return;

            e.State = _options.PermittedPermissions.Contains(e.PermissionKind)
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        };

        core.ProcessFailed += (_, e) =>
        {
            if (_options.OnProcessFailed is { } onFailed
                && AppHandled(onFailed, e, nameof(WebViewHostOptions.OnProcessFailed)))
                return;

            // The three fields that identify a crash rather than merely naming the event: ExitCode (a
            // STATUS_* code names the fault class), ProcessDescription (WHICH utility/GPU process) and
            // FailureSourceModulePath (usually a codec, GPU driver or injected shell extension).
            Log(() =>
            {
                var detail = $"[Shenora.Windows] Process failed: {e.ProcessFailedKind} (reason: {e.Reason}"
                    + $", exitCode: {e.ExitCode})";
                var description = AppCallback.RunOrDefault(() => e.ProcessDescription, null);
                if (!string.IsNullOrWhiteSpace(description)) detail += $" process='{description}'";
                // ⚠ Guarded and read separately: these are newer members on the args and an older runtime
                // can throw, which must not turn a crash REPORT into a second crash inside the handler.
                var module = AppCallback.RunOrDefault(() => e.FailureSourceModulePath, null);
                if (!string.IsNullOrWhiteSpace(module)) detail += $" module='{module}'";
                return detail;
            });
            if (!_options.ReloadOnRenderProcessFailure
                || e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited) return;

            if (DateTime.UtcNow - _lastAutoReloadUtc <= _options.AutoReloadCooldown) return;

            // TERMINAL after MaxAutoReloads — the cooldown alone is not a stopping condition. Log the
            // give-up ONCE, at the cap, or the log becomes the new spin.
            if (_autoReloadCount >= _options.MaxAutoReloads)
            {
                if (_autoReloadCount == _options.MaxAutoReloads)
                {
                    _autoReloadCount++; // past the cap: never log this again
                    Log(() => $"[Shenora.Windows] Renderer crashed {_options.MaxAutoReloads} times — " +
                              "giving up on auto-reload. The page is most likely crashing deterministically; " +
                              "handle it via WebViewHostOptions.OnProcessFailed.");
                }
                return;
            }

            _autoReloadCount++;
            _lastAutoReloadUtc = DateTime.UtcNow;
            Log(() => $"[Shenora.Windows] Renderer crashed — reloading ({_autoReloadCount}/{_options.MaxAutoReloads}).");
            try { _webView.Reload(); } catch { }
        };

        // Only a SUCCESSFUL navigation clears the budget; an error page must not reset it.
        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) _autoReloadCount = 0;
        };
    }
}
