using Microsoft.Web.WebView2.Core;
using Shenora.Core.WebView;
using Shenora.Core.Shell;
using Shenora.Core.Ipc;
// Inside namespace Shenora.Windows the bare identifier "WebView2" resolves to the namespace, so
// the control type needs an alias.
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;
using Shenora;

namespace Shenora.Windows;

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
    private readonly Shenora.Core.Shell.IUiDispatcher _ui;
    // The one open-a-URL implementation, reachable since D19 — see the NewWindowRequested policy.
    private readonly Shenora.Core.Shell.IUrlLauncher _urls = new Shenora.Windows.ShellLauncher();
    private readonly WebView2Interceptor _interceptor = new();
    private DateTime _lastAutoReloadUtc = DateTime.MinValue;
    private int _autoReloadCount;            // terminal state for the crash-reload loop (see WireEventPolicies)
    private Task? _initialization;           // InitializeAsync is idempotent — see its remarks

    /// <summary>Wraps <paramref name="webView"/>. Construct, then <see cref="InitializeAsync"/>, then navigate.</summary>
    public WebViewHost(WebView2Control webView, WebViewHostOptions options)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = options.Log ?? options.Environment.Log;

        // Fail at COMPOSITION rather than degrading to silence (the P5.5 H3 convention). A deferred
        // scheme gets a WebResourceRequested filter below, but WebView2 accepts the SCHEME itself only
        // at environment-creation time — so an unregistered custom scheme is rejected by the network
        // stack before the filter is consulted, and all the page ever sees is
        // `TypeError: Failed to fetch`, with nothing logged host-side to explain it. That was true for
        // as long as the feature existed (P7.1); this guard is what stops it recurring.
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
        // The one marshalling owner (D19/D20, P5.5 H4.2).
        _ui = new Shenora.Windows.WinFormsUiDispatcher(webView,
            ex => Log(() => $"[Shenora.Windows] Posted UI work failed: {ex.Message}"));
    }

    /// <summary>
    /// Guarded + lazy, via the one owner (<see cref="Shenora.AppCallback.Log"/>). Almost every
    /// call site below sits inside a WebView2 event handler or a posted UI-thread body, where an
    /// escaping exception has no caller and becomes a modal crash dialog; and several messages read
    /// WebView2/COM properties (a download's URI, a process-failed reason) that throw once the
    /// underlying object is gone, which is why BUILDING the message must be inside the guard too.
    /// </summary>
    private void Log(Func<string> message) => Shenora.AppCallback.Log(_log, message);

    /// <summary>
    /// Invoke one of the app's event-policy hooks and report whether it HANDLED the event: true only
    /// when it ran to completion. A hook that throws returns false, so the caller applies the kit's own
    /// default rather than leaving a WebView2 event unanswered (P5.5 H2).
    /// </summary>
    private bool AppCallbackRan<T>(Action<T> callback, T args, string hookName) =>
        Shenora.AppCallback.Run(() => callback(args),
            ex => Log(() => $"[Shenora.Windows] {hookName} threw ({ex.GetType().Name}: {ex.Message}); " +
                            "applying the built-in policy instead."));

    /// <summary>Dev/prod, from the single source (<see cref="WebViewEnvironmentOptions.IsDevelopment"/>).</summary>
    public bool IsDevelopment => _options.Environment.IsDevelopment;

    /// <summary>
    /// This host's resource-interception pipeline (D45) — the portable seam a feature adds middleware to, e.g.
    /// <c>host.Interceptor.UseFiles(new WebViewFileOptions { … })</c> to let the page load local files.
    /// <para>
    /// Available immediately, BEFORE <see cref="InitializeAsync"/>, because routes are registered at
    /// composition time while the webview initializes later. Registering after init works too — the pipeline is
    /// read per request.
    /// </para>
    /// <para>
    /// Middleware see requests to the PAGE'S OWN ORIGIN (the bundle's virtual host in production, the dev
    /// server in development) — see <see cref="WebView2Interceptor.ExtraFilters"/> for exactly which, and why
    /// not everything. A route whose path also exists in the packaged bundle loses to the bundle here, so keep
    /// interception paths off bundle paths: relying on either winner is relying on a difference between shells.
    /// </para>
    /// <para>
    /// This does not replace <see cref="WebViewHostOptions.DeferredSchemes"/>, which stays for what it is good
    /// at: a whole custom scheme of the app's own, on its own origin. The interceptor is the portable one —
    /// the same code compiles against the mobile shells.
    /// </para>
    /// </summary>
    public IWebViewInterceptor Interceptor => _interceptor;

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

        var virtualHostPrefix = WebViewBundleServing.Prefix(_options.VirtualHost, _options.ResourceProvider);
        if (virtualHostPrefix is not null)
        {
            core.AddWebResourceRequestedFilter(virtualHostPrefix + "*", CoreWebView2WebResourceContext.All);
        }
        foreach (var scheme in _options.DeferredSchemes)
        {
            core.AddWebResourceRequestedFilter(scheme.Scheme + "://*", CoreWebView2WebResourceContext.All);
        }
        // What the interceptor needs on top: in production its origin IS the bundle's, already filtered above;
        // in development the page lives on the dev server instead. See WebView2Interceptor.ExtraFilters.
        var interceptorFilters = WebView2Interceptor.ExtraFilters(IsDevelopment, _options.DevUrl);
        foreach (var pattern in interceptorFilters)
        {
            core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All);
        }
        if (virtualHostPrefix is null && _options.DeferredSchemes.Count == 0 && interceptorFilters.Length == 0)
            return;

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
            // A cheap array-length read, and worth doing before anything else: with no route registered this
            // handler must cost as close to nothing as possible, because it also serves the app's own bundle.
            var intercepting = _interceptor.HasRoutes;

            if (virtualHostPrefix is not null && uri.StartsWith(virtualHostPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // The shared implementation, also used by an off-screen SessionBrowser (E1) so a
                // session can render the app's OWN packaged frontend. One copy on purpose.
                if (!intercepting)
                {
                    // Exactly the pre-D45 behaviour, one call: serve it or 404.
                    WebViewBundleServing.Serve(args, _webView.CoreWebView2.Environment,
                        _options.ResourceProvider!, uri, virtualHostPrefix, Log);
                    return;
                }

                // TryServe rather than Serve: since D45 the interceptor shares this origin with the bundle
                // (a relative media URL is `https://app.local/media?…`), so a path the bundle does NOT contain
                // has to fall through to the pipeline instead of 404ing. A path it DOES contain is still served
                // synchronously and inline — the main document never reaches the deferred path.
                if (WebViewBundleServing.TryServe(args, _webView.CoreWebView2.Environment,
                        _options.ResourceProvider!, uri, virtualHostPrefix, Log))
                    return;

                // The pipeline may decline too, and then WebView2 handles it — which is a 404 from a virtual
                // host with no mapping, i.e. the same outcome by a different route.
                ServeInterceptor(args, uri);
                return;
            }

            foreach (var scheme in _options.DeferredSchemes)
            {
                if (uri.StartsWith(scheme.Scheme + "://", StringComparison.OrdinalIgnoreCase))
                {
                    // The scheme owns its whole origin, so declining can only mean 404 — the behaviour this
                    // path has always had.
                    ServeAsync(args, uri, (request, _) => scheme.Handler(request), scheme.CacheControl,
                        $"deferred scheme '{scheme.Scheme}'", answerNotFoundWhenDeclined: true);
                    return;
                }
            }

            // Anything else that matched a filter — in practice the dev server's own origin, where the
            // interceptor is the only reason we are listening at all.
            if (intercepting)
            {
                ServeInterceptor(args, uri);
                return;
            }
            // Not ours (e.g. a folder-mapping host) — let WebView2 handle it.
        };
    }

    /// <summary>
    /// Hand a request to the D45 middleware pipeline. Composed HERE, once per request, so a route registered
    /// while this one is in flight cannot half-apply; declining leaves the request to WebView2.
    /// </summary>
    private void ServeInterceptor(CoreWebView2WebResourceRequestedEventArgs args, string uri)
    {
        // Re-checked rather than assumed: the caller's HasRoutes read and this build are separate moments, and
        // the last route can be disposed in between.
        if (_interceptor.Build() is not { } pipeline) return;
        ServeAsync(args, uri, pipeline, defaultCacheControl: null, "interceptor");
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

    /// <summary>
    /// Answer <paramref name="args"/> from <paramref name="handler"/> OFF the UI thread — the deferred path,
    /// shared by <see cref="WebViewHostOptions.DeferredSchemes"/> and by the D45 interceptor pipeline because
    /// every hard-won rule in it applies identically: snapshot the COM request before leaving the UI thread,
    /// never leak handler exception text into a body page script can read, default the cache policy without
    /// overriding one the handler set, add CORS and EXPOSE the headers, and marshal the response build back to
    /// the UI thread through the one dispatcher.
    /// </summary>
    /// <param name="args">The intercepted request, still on the UI thread.</param>
    /// <param name="uri">Its raw URI, already read.</param>
    /// <param name="handler">The handler. Runs on a thread-pool thread.</param>
    /// <param name="defaultCacheControl">
    /// Stamped on a 2xx that does not set its own, or null for none. Null for the interceptor deliberately: a
    /// middleware serving a local file is serving something that can change under it, and "cache for a day"
    /// would be a policy the kit invented rather than one the app chose.
    /// </param>
    /// <param name="what">What to name in the log if it fails.</param>
    /// <param name="answerNotFoundWhenDeclined">
    /// True when this origin is EXCLUSIVELY ours (a custom scheme), so nothing declining it can mean anything
    /// but 404. False for the interceptor, whose origin it shares with the page's own content: declining there
    /// must complete the deferral WITHOUT a response and let WebView2 handle the request normally.
    /// </param>
    private void ServeAsync(CoreWebView2WebResourceRequestedEventArgs args, string uri,
                            WebViewResourceHandler handler, string? defaultCacheControl, string what,
                            bool answerNotFoundWhenDeclined = false)
    {
        var deferral = args.GetDeferral();
        // Snapshot the request on THIS thread: the args object belongs to the UI thread and its
        // Headers collection must not be walked from the pool thread the handler runs on.
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
                // No exception text in the body (P5.5 H3) — an app handler's message is the most likely of all
                // of these to carry a real path or a remote URL, and page script can read this body. The
                // handler's failure goes to the host log instead.
                Log(() => $"[Shenora.Windows] {what} failed for '{uri}': {ex}");
                // A THROW is a 404 even on a shared origin: falling through would hand a broken route back to
                // WebView2, and the page would see a network error instead of the fixed refusal every other
                // failure path here produces.
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
            // The caller's Cache-Control is a DEFAULT, not an override: a handler answering 206 or 404
            // has its own caching story, and stamping "cache for a day" over it would be wrong.
            if (defaultCacheControl is not null && !response.Headers.ContainsKey("Cache-Control")
                && response.StatusCode is >= 200 and < 300)
                headerLines.Add($"Cache-Control: {defaultCacheControl}");

            // CORS, by default, for the same reason the bundle path already sets it. An app scheme is a
            // DIFFERENT ORIGIN from the page that loads it (page on https://app.local, resource on
            // app://…), so without this every fetch/XHR is refused by the browser — and the refusal
            // looks identical to the scheme not existing: a bare `TypeError: Failed to fetch`, with the
            // handler having already run and answered correctly. That is exactly how this cost an
            // afternoon (P7.1): `handlerHits=3` while the page saw three failures.
            // Registering the scheme's AllowedOrigins is NOT sufficient — that governs which origins
            // may REQUEST the scheme; this governs whether the browser hands the RESPONSE to script.
            // A handler that sets the header itself wins, so a stricter policy stays expressible.
            if (!response.Headers.ContainsKey("Access-Control-Allow-Origin"))
                headerLines.Add("Access-Control-Allow-Origin: *");

            // …and EXPOSE the response headers, which is a separate rule people meet second. On a
            // cross-origin response the browser hands script only the CORS-safelisted headers, so
            // `Content-Range` reads back as null even on a perfectly correct 206 — the bytes arrive,
            // the status is right, and the metadata describing them is invisible. (A media element
            // seeking does not care, because the browser reads the header internally; anything doing
            // its own ranged fetch in JS does.) Same override rule as above.
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
                    Log(() => $"[Shenora.Windows] Ignoring new-window request for {e.Uri}: {ex.GetType().Name}");
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
            Log(() => $"[Shenora.Windows] Download canceled by policy: {e.DownloadOperation.Uri}");
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

            // 🔴 EVERYTHING WebView2 KNOWS, not just the kind. "RenderProcessExited (reason: Crashed)"
            // names the event and nothing about the cause, so an adopter — and this repo — could stare at
            // it without a next step. The three fields below are the ones that actually identify a crash:
            // ExitCode (a STATUS_* code names the fault class), ProcessDescription (WHICH utility/GPU
            // process, since those kinds cover several), and FailureSourceModulePath (the module the
            // crash came from — usually a codec, a GPU driver or a shell extension injected into the
            // renderer, and the single most useful field there is).
            // ⚠ Same defect shape as a WinRT COMException reported without its HRESULT: naming a failure
            // while withholding its identity reads as a diagnostic and is not one.
            Log(() =>
            {
                var detail = $"[Shenora.Windows] Process failed: {e.ProcessFailedKind} (reason: {e.Reason}"
                    + $", exitCode: {e.ExitCode})";
                var description = AppCallback.RunOrDefault(() => e.ProcessDescription, null);
                if (!string.IsNullOrWhiteSpace(description)) detail += $" process='{description}'";
                // Guarded and read separately: these are newer members on the args, and an older runtime
                // can throw rather than return empty — which must not turn a crash REPORT into a second
                // crash inside the handler.
                var module = AppCallback.RunOrDefault(() => e.FailureSourceModulePath, null);
                if (!string.IsNullOrWhiteSpace(module)) detail += $" module='{module}'";
                return detail;
            });
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

        // A page that actually loads clears the budget, so a long-running app is not slowly used up by
        // unrelated crashes hours apart — the cap is meant to catch a CRASH LOOP, not to ration a
        // session. Only a successful navigation counts; an error page must not reset it.
        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) _autoReloadCount = 0;
        };
    }
}
