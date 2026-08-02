using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Windows;

/// <summary>Configuration for a session browser (see <see cref="SessionBrowser"/>).</summary>
public sealed class SessionBrowserOptions
{
    /// <summary>
    /// The persistent profile (user-data folder) this browser runs in. Sessions key their whole
    /// isolation story on this directory: an interactive session scopes it per provider (and per
    /// sub-account — a SECURITY boundary, see <see cref="InteractiveSession"/>), a pool
    /// shares one across its instances, and wiping it is what really discards the session.
    /// </summary>
    public required string ProfileDirectory { get; init; }

    /// <summary>
    /// True for an OFF-SCREEN browser: Chromium's occlusion + background-timer throttling would
    /// otherwise pause the page's JS while nothing shows — the whole point of these sessions is
    /// that it keeps running.
    /// </summary>
    public bool KeepAliveInBackground { get; init; }

    /// <summary>
    /// Mute all audio + block autoplay without a user gesture (default true): session browsers
    /// are for fetch/render/interaction work, never playback — a page that autoplays media must not
    /// make noise or waste decode while it renders off-screen.
    /// </summary>
    public bool MuteAudio { get; init; } = true;

    /// <summary>Extra Chromium arguments appended after the preset.</summary>
    public string? AdditionalBrowserArguments { get; init; }

    /// <summary>
    /// Request-layer filter: return true to BLOCK a subresource request (answered with an empty
    /// 403 so it never loads). Receives the request URI and the page's current URI (null before
    /// the first navigation commits — NEVER block then, or the page's own document can't load).
    /// This is the seam the source app's ad/tracker blocking hung off — the policy (blocklists,
    /// third-party heuristics, captcha allowlists) is the app's; the mechanism lives here.
    /// Runs on the UI thread per request — keep it fast.
    /// </summary>
    public Func<Uri, Uri?, bool>? RequestFilter { get; init; }

    /// <summary>
    /// Virtual host the app's OWN packaged bundle is served on inside this session, via
    /// <see cref="ResourceProvider"/> — the same pair, with the same names, as
    /// <c>WebViewHostOptions.VirtualHost</c>/<c>ResourceProvider</c>, so an app that wires both reads
    /// the same words twice. Both halves or neither: either alone throws at initialization.
    /// <para>
    /// Without this a session could only reach NETWORK-reachable URLs (E1, 2026-08-03). A session
    /// browser gets its own environment with none of the shell's serving set up, so a packaged
    /// desktop app that pointed an off-screen session at its own <c>https://app.local/…</c> got
    /// WebView2's "can't reach this page" — "co-browse or off-screen-render MY OWN UI" did not work
    /// at all, and could not be bolted on from outside either. A SERVER-BACKED app never saw it: its
    /// pages sit on a real loopback origin.
    /// </para>
    /// </summary>
    public string? VirtualHost { get; init; }

    /// <summary>
    /// The packaged-bundle provider behind <see cref="VirtualHost"/>. Pass the SAME instance the
    /// shell's <c>WebViewHost</c> uses — <see cref="EmbeddedResourceProvider"/> caches what it has
    /// read, so sharing it means the session's requests hit a warm cache rather than re-reading the
    /// manifest into a second copy of the whole bundle.
    /// <para>
    /// ⚠ <b>Configure this only on a session that renders YOUR pages — not one that renders
    /// third-party ones.</b> Bundle responses are served <c>Access-Control-Allow-Origin: *</c>, which
    /// is nearly moot in the app shell (the bundle IS the document's own origin) and materially
    /// different here: whatever page this session is on can be ANY origin, and script in it could
    /// <c>fetch</c> your whole bundle. Your shipped frontend is not a secret, so this is an unintended
    /// read channel rather than a breach — but it is a real one, and the mitigation is free because
    /// these options are per-session: give a co-browse session over other people's pages its own
    /// <see cref="SessionBrowserOptions"/> without them, exactly as it already gets its own
    /// <see cref="ProfileDirectory"/>.
    /// </para>
    /// </summary>
    public IWebViewResourceProvider? ResourceProvider { get; init; }

    /// <summary>
    /// Disk-folder virtual hosts for this session (<c>SetVirtualHostNameToFolderMapping</c>) — the
    /// OTHER family-proven way to serve content, for an app whose bundle or media lives on disk
    /// rather than embedded. Shipped alongside <see cref="ResourceProvider"/> for the same reason
    /// <c>WebViewHostOptions</c> carries both: serving half the mechanisms would leave a disk-backed
    /// app with exactly the gap this closes for an embedded one.
    /// </summary>
    public IReadOnlyList<WebViewFolderMapping> FolderMappings { get; init; } = [];

    /// <summary>
    /// Budget for environment creation + core attach. The family guard: a profile folder still
    /// LOCKED by an orphaned WebView2 process (a prior crash, a subprocess that hasn't exited)
    /// otherwise hangs init FOREVER, wedging the whole request behind it. Normal init is a few
    /// seconds.
    /// </summary>
    public TimeSpan InitTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// True in development: re-appends <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS</c> so a session
    /// browser is reachable over CDP. Required because setting <c>AdditionalBrowserArguments</c> at
    /// all makes WebView2 IGNORE that env var — the gotcha in
    /// <c>.claude/rules/windows-dev-gotchas.md</c>, which the sessions package used to re-introduce
    /// by hand-building its argument string (P5.5 H4.4).
    /// </summary>
    public bool IsDevelopment { get; init; }

    /// <summary>Diagnostics. Null = silent (the package shipped with no logging at all — P5.5 H4.7).</summary>
    public ILogger? Log { get; init; }
}

/// <summary>
/// The ONE place a session browser gets configured — ported from the server-backed sibling's
/// shared browser primitive (before it existed, every window re-did the same env creation +
/// hardening + HTML read). Creates a per-profile <see cref="CoreWebView2Environment"/>, attaches
/// it, and applies the session hardening (no devtools/status bar/password autosave, muted).
/// Distinct from <c>WebViewHost</c> on purpose: app shells host ONE app frontend; sessions are
/// many short-lived browsers over arbitrary pages with per-profile isolation.
/// </summary>
public static class SessionBrowser
{
    // Standard Chromium quiet-start flags: no first-run noise, no default-browser nag, no
    // SmartScreen/Translate UI popups over an off-screen page.
    private const string BaseArgs =
        "--no-first-run --no-default-browser-check --disable-features=msSmartScreenProtection,TranslateUI";

    private const string MuteArgs = " --mute-audio --autoplay-policy=document-user-activation-required";

    // For an OFF-SCREEN browser: keep JS running while nothing shows (occlusion + timer
    // throttling would pause it).
    private const string BackgroundArgs =
        " --disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding";

    /// <summary>
    /// Create the profile's environment, attach the core, and harden the settings. Call on the
    /// UI thread that owns the control.
    /// </summary>
    /// <param name="web">The control to attach a configured browser to.</param>
    /// <param name="options">Profile, hardening and diagnostics configuration.</param>
    /// <param name="onProcessFailed">
    /// Called when this browser's RENDER process dies (crash, OOM, kill). A per-INSTANCE callback
    /// rather than an options field, because one options object is shared across a pool's instances
    /// while this state belongs to one of them. Sessions run unattended off-screen, so without it a
    /// dead renderer is INVISIBLE: a pooled instance was reset, re-pooled and re-leased forever, and
    /// a co-browse frame channel simply stopped with its reader still waiting (P5.5 H4.4).
    /// </param>
    /// <param name="cancellationToken">
    /// Abandons the WAIT for initialization. NOT the creation itself — see the body for why that
    /// distinction is load-bearing.
    /// </param>
    internal static Task InitializeAsync(WebView2Control web, SessionBrowserOptions options,
                                         Action<CoreWebView2ProcessFailedEventArgs>? onProcessFailed = null,
                                         CancellationToken cancellationToken = default) =>
        InitializeAsync(web, options, onProcessFailed, environmentCache: null, cancellationToken);

    /// <summary>
    /// As the overload above, but reusing <paramref name="environmentCache"/>'s shared environment
    /// when the caller creates SEVERAL browsers on one profile (the render pool). The cache is an
    /// ownership detail of that caller, not a consumer concept — see
    /// <see cref="SessionEnvironmentCache"/> for why it is owner-scoped rather than static.
    /// </summary>
    internal static async Task InitializeAsync(WebView2Control web, SessionBrowserOptions options,
                                               Action<CoreWebView2ProcessFailedEventArgs>? onProcessFailed,
                                               SessionEnvironmentCache? environmentCache,
                                               CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(web);
        ArgumentNullException.ThrowIfNull(options);

        // A non-positive InitTimeout makes both WaitAsync calls below expire immediately, so init fails
        // instantly with the profile-LOCK diagnosis — sending the caller after a zombie msedgewebview2
        // process that does not exist (P5.5 H3). Reject the option instead of blaming the environment.
        if (options.InitTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(SessionBrowserOptions.InitTimeout)} must be positive.");

        AssertBundleConfigured(options);

        Directory.CreateDirectory(options.ProfileDirectory);

        // Compose through Shenora.Windows's owner (P5.5 H4.4 — the edge D14 declared but nothing
        // crossed). It keeps the two invariants this package used to re-implement and get wrong:
        // each features switch appears exactly ONCE (so a caller's own --disable-features cannot
        // silently discard the preset), and in dev the CDP arguments are re-appended by hand,
        // without which a session browser is unreachable over CDP.
        var arguments = BrowserArguments.Compose(
            preset: BaseArgs
                + (options.MuteAudio ? MuteArgs : string.Empty)
                + (options.KeepAliveInBackground ? BackgroundArgs : string.Empty),
            isDevelopment: options.IsDevelopment,
            devExtraArguments: Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"),
            additionalArguments: options.AdditionalBrowserArguments);

        var envOptions = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = arguments };
        Task<CoreWebView2Environment> CreateEnvironment() =>
            CoreWebView2Environment.CreateAsync(null, options.ProfileDirectory, envOptions);

        CoreWebView2Environment env;
        try
        {
            // The timeout wraps BOTH steps: a profile-lock stall most often hangs environment
            // creation, not just the core attach — either must surface the same guidance, never
            // a bare "The operation has timed out."
            //
            // WaitAsync abandons the AWAIT, not the creation. Without a cache the next attempt
            // therefore started a SECOND CreateAsync against the same locked profile, orphaning
            // another browser process onto the lock its own error message blames (P5.5 H2); with one,
            // the retry joins the attempt already in flight.
            //
            // The TOKEN GATES THE AWAIT ONLY, and that is a correctness requirement, not a style
            // choice (P5.5 H9.6): with a cache the environment task is SHARED across every instance
            // the pool is building on that profile, so cancelling the creation for one caller would
            // break all the others. A cancelled lease walks away from the wait; the creation finishes
            // for whoever still wants it, and the cache hands it over. Before this, a cancelled lease
            // could not escape DURING init at all — it waited out the full InitTimeout twice over.
            var creation = environmentCache is null ? CreateEnvironment() : environmentCache.GetOrCreate(CreateEnvironment);
            env = await creation.WaitAsync(options.InitTimeout, cancellationToken).ConfigureAwait(true);
            await web.EnsureCoreWebView2Async(env).WaitAsync(options.InitTimeout, cancellationToken).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Session browser failed to initialize within {options.InitTimeout.TotalSeconds:0}s. " +
                $"The usual cause is a leftover WebView2 process holding the profile lock " +
                $"('{options.ProfileDirectory}') — end stray msedgewebview2 processes, or delete the folder.");
        }

        var core = web.CoreWebView2;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        // Script dialogs OFF (P5.5 H2). A session browser is parked off-screen at opacity 0, so an
        // alert()/confirm() renders inside an invisible window that nobody can dismiss — and it
        // BLOCKS the page's JS thread, so every later script or CDP call for that instance never
        // completes. The app shell can afford dialogs; an unattended session cannot.
        settings.AreDefaultScriptDialogsEnabled = false;
        if (options.MuteAudio)
            core.IsMuted = true; // belt-and-suspenders with --mute-audio

        WireSessionPolicies(core, options, onProcessFailed);
        AttachResourceHandling(core, env, options);
    }

    /// <summary>
    /// <see cref="SessionBrowserOptions.VirtualHost"/> and
    /// <see cref="SessionBrowserOptions.ResourceProvider"/> are both-or-neither. Either alone is a
    /// composition mistake that would otherwise do NOTHING — a host with no provider has nothing
    /// behind it, a provider with no host has no address — and the symptom would be the session
    /// showing "can't reach this page" exactly as it did before the seam existed. Fail at composition
    /// (the P5.5 H3 convention, and the same call <c>WebViewHost</c>'s constructor makes about an
    /// unregistered deferred scheme).
    /// <para>Internal + static so the rule is testable without a live browser process.</para>
    /// </summary>
    internal static void AssertBundleConfigured(SessionBrowserOptions options)
    {
        var hasHost = options.VirtualHost is { Length: > 0 };
        var hasProvider = options.ResourceProvider is not null;
        if (hasHost == hasProvider) return;

        var missing = hasHost
            ? nameof(SessionBrowserOptions.ResourceProvider)
            : nameof(SessionBrowserOptions.VirtualHost);
        var present = hasHost
            ? nameof(SessionBrowserOptions.VirtualHost)
            : nameof(SessionBrowserOptions.ResourceProvider);
        throw new InvalidOperationException(
            $"{nameof(SessionBrowserOptions)}.{present} is set but {missing} is not, so this session "
            + "would serve no bundle at all and a navigation to the app's own origin would come up as "
            + "WebView2's 'can't reach this page'. Set both (pass the same provider instance the "
            + "shell's WebViewHost uses), or neither.");
    }

    /// <summary>What a session browser does with one intercepted request.</summary>
    internal enum SessionRequestAction
    {
        /// <summary>Not ours — let WebView2 fetch it.</summary>
        Pass,

        /// <summary>The app's <see cref="SessionBrowserOptions.RequestFilter"/> refused it: empty 403.</summary>
        Block,

        /// <summary>It addresses the app's own bundle: serve it from the resource provider.</summary>
        ServeBundle,
    }

    /// <summary>
    /// The request-layer DECISION for a session browser, split out of the event handler so the real
    /// rule is unit-testable (P5.5 H7 — a rule reachable only through a live <c>CoreWebView2</c> is a
    /// rule nothing tests, and this one carries the app's blocking boundary).
    /// <para>
    /// THE ORDER IS THE INVARIANT: the filter is consulted BEFORE the bundle. An app that blocks a
    /// request has stated a policy, and serving it from our own provider anyway would override that
    /// policy with a code path the app cannot see. It also matters that this is ONE decision rather
    /// than two handlers: two <c>WebResourceRequested</c> subscriptions both assigning
    /// <c>args.Response</c> is last-writer-wins by subscription order, which is not a contract
    /// anything should rest on.
    /// </para>
    /// </summary>
    /// <param name="requestUri">The raw <c>e.Request.Uri</c>.</param>
    /// <param name="pageSource">The raw <c>core.Source</c> — may be empty or <c>about:blank</c>.</param>
    /// <param name="filter">The app's blocking policy, or null.</param>
    /// <param name="bundlePrefix">The virtual-host prefix from <c>WebViewBundleServing.Prefix</c>, or null.</param>
    internal static SessionRequestAction DecideRequest(string? requestUri, string? pageSource,
                                                       Func<Uri, Uri?, bool>? filter, string? bundlePrefix)
    {
        if (filter is not null && ShouldBlockRequest(requestUri, pageSource, filter))
            return SessionRequestAction.Block;

        if (bundlePrefix is not null && requestUri is not null
            && requestUri.StartsWith(bundlePrefix, StringComparison.OrdinalIgnoreCase))
            return SessionRequestAction.ServeBundle;

        return SessionRequestAction.Pass;
    }

    /// <summary>
    /// Wire the ONE <c>WebResourceRequested</c> handler this session needs: the app's blocking filter
    /// and/or its own packaged bundle, plus any disk-folder mappings. UI-thread hot path.
    /// </summary>
    private static void AttachResourceHandling(CoreWebView2 core, CoreWebView2Environment env,
                                               SessionBrowserOptions options)
    {
        // Folder mappings are handled by WebView2 itself — no interception, so they compose with
        // everything below without ordering questions.
        foreach (var mapping in options.FolderMappings)
            core.SetVirtualHostNameToFolderMapping(mapping.HostName, mapping.FolderPath, mapping.AccessKind);

        var filter = options.RequestFilter;
        var bundlePrefix = WebViewBundleServing.Prefix(options.VirtualHost, options.ResourceProvider);
        if (filter is null && bundlePrefix is null) return;

        // ONE filter registration, widened only as far as needed. A blocking policy must see every
        // request; a bundle alone only needs its own prefix — and registering both patterns would
        // raise a question about double-firing that not registering them simply does not have.
        core.AddWebResourceRequestedFilter(
            filter is not null ? "*" : bundlePrefix + "*", CoreWebView2WebResourceContext.All);

        core.WebResourceRequested += (_, e) =>
        {
            var uri = e.Request.Uri;
            switch (DecideRequest(uri, core.Source, filter, bundlePrefix))
            {
                case SessionRequestAction.Block:
                    // Blocked requests are answered with an empty 403 so they never load.
                    e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
                    break;
                case SessionRequestAction.ServeBundle:
                    // The SAME implementation the app shell serves its frontend with — see
                    // WebViewBundleServing for why this is not a second copy. The log sink is the
                    // session's own, guarded, because this body runs inside a WebView2 event.
                    WebViewBundleServing.Serve(e, env, options.ResourceProvider!, uri, bundlePrefix!,
                        message => SessionLog.Try(options.Log, l => l.LogDebug("{Message}", message())));
                    break;
            }
        };
    }

    /// <summary>
    /// The three event policies <c>.claude/knowledge/extraction-sources.md</c> lists as must-fix
    /// during a port, and which the P5 port shipped WITHOUT for pooled and co-browse instances
    /// (P5.5 H4.4). <c>WebViewHost</c> has its own versions, but the POLICY differs and must not be
    /// shared: an app shell opens external links in the system browser, whereas an unattended session
    /// must open nothing at all.
    /// </summary>
    private static void WireSessionPolicies(CoreWebView2 core, SessionBrowserOptions options,
                                            Action<CoreWebView2ProcessFailedEventArgs>? onProcessFailed)
    {
        // A pooled/off-screen page calling window.open() used to get a REAL, visible WebView2 popup
        // in an app that has no session UI. Suppress it; a session navigates where it is told.
        // Every diagnostic below goes through SessionLog: these bodies run inside WebView2 events with
        // no caller on the stack, so an app logger that throws IS an unhandled UI-thread exception.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            SessionLog.Try(options.Log, l => l.LogDebug("Session browser suppressed a new-window request for {Uri}.", e.Uri));
        };

        // Deny every permission by default: an invisible page cannot meaningfully prompt, and an
        // un-answered permission request stalls the feature that asked.
        core.PermissionRequested += (_, e) =>
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
            SessionLog.Try(options.Log, l => l.LogDebug("Session browser denied permission {Kind}.", e.PermissionKind));
        };

        // A dead renderer is otherwise INVISIBLE here (see OnProcessFailed's docs).
        core.ProcessFailed += (_, e) =>
        {
            SessionLog.Try(options.Log, l => l.LogWarning("Session browser process failed: {Kind} ({Reason}).",
                e.ProcessFailedKind, e.Reason));
            try { onProcessFailed?.Invoke(e); }
            catch (Exception ex)
            {
                // Reporting a crash must not itself crash the UI thread — which is also why the report
                // goes through SessionLog: an app logger that throws in this handler has no caller.
                SessionLog.Try(options.Log, l => l.LogError(ex, "OnProcessFailed callback threw."));
            }
        };
    }

    /// <summary>
    /// The request-filter DECISION, split out of the event handler so the real rule is unit-testable
    /// (P5.5 H7 — the same lesson as the pool's reset probe: a rule reachable only through a live
    /// <c>CoreWebView2</c> is a rule nothing tests, and this one is the app's blocking boundary).
    /// Returns true when the request must be answered with the 403.
    /// </summary>
    /// <param name="requestUri">The raw <c>e.Request.Uri</c>.</param>
    /// <param name="pageSource">The raw <c>core.Source</c> — may be empty or <c>about:blank</c>.</param>
    /// <param name="filter">The app's policy: (request, pageUri) → block?</param>
    internal static bool ShouldBlockRequest(string? requestUri, string? pageSource, Func<Uri, Uri?, bool> filter)
    {
        // A request URI we cannot parse is not something we can describe to a policy — pass it.
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var request)) return false;

        // Only an http(s) page source is a real "page host" to compare against. Before the first
        // navigation commits the source is empty, and a returned/reset pool instance sits on
        // `about:blank` — both parse as a non-web Uri (or none), and passing them through would make
        // a same-host filter treat the page's OWN next document as third-party and 403 it. Pass null
        // so a sane filter never blocks the document.
        var pageUri = Uri.TryCreate(pageSource, UriKind.Absolute, out var p)
                      && p.Scheme is "http" or "https"
            ? p
            : null;
        try
        {
            return filter(request, pageUri);
        }
        catch
        {
            // A throwing filter must not break page loading — FAIL OPEN. Deliberate, and the opposite
            // of the navigation guard's fail-closed stance: this runs on every subresource of every
            // page, so failing closed on a buggy app predicate would present as a blank page with no
            // diagnosis. The guard is the boundary that must hold; this is the sieve.
            return false;
        }
    }

    /// <summary>
    /// The current rendered HTML (<c>document.documentElement.outerHTML</c>), or null.
    /// ExecuteScriptAsync returns the value JSON-encoded (a quoted string) — this decodes it
    /// back to raw HTML. Call on the UI thread.
    /// </summary>
    internal static async Task<string?> GetHtmlAsync(WebView2Control web)
    {
        try
        {
            var json = await web.ExecuteScriptAsync("document.documentElement.outerHTML").ConfigureAwait(true);
            return json is null or "null" ? null : JsonSerializer.Deserialize<string>(json);
        }
        catch
        {
            return null;
        }
    }
}
