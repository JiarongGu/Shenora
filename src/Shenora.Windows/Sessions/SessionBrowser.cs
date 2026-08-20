using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Shenora.Core.Events;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Windows;

/// <summary>
/// Configuration for a session browser (see <see cref="SessionBrowser"/>). A <c>record</c>, so a session
/// can <c>with</c>-override the one or two fields it OWNS and inherit the rest by construction.
/// </summary>
public sealed record SessionBrowserOptions
{
    /// <summary>
    /// The persistent profile (user-data folder) this browser runs in, and the session's ISOLATION
    /// boundary: an interactive session scopes it per provider and per sub-account (a SECURITY
    /// boundary, see <see cref="InteractiveSession"/>); wiping it discards the session for real.
    /// </summary>
    public required string ProfileDirectory { get; init; }

    /// <summary>
    /// True for an OFF-SCREEN browser: Chromium's occlusion + background-timer throttling would
    /// otherwise pause the page's JS while nothing shows.
    /// </summary>
    public bool KeepAliveInBackground { get; init; }

    /// <summary>Mute all audio and block autoplay without a user gesture (default true).</summary>
    public bool MuteAudio { get; init; } = true;

    /// <summary>Extra Chromium arguments appended after the preset.</summary>
    public string? AdditionalBrowserArguments { get; init; }

    /// <summary>
    /// Request-layer filter: return true to BLOCK a subresource request (answered with an empty 403).
    /// Receives the request URI and the page's current URI. Runs on the UI thread per request — keep it
    /// fast. ⚠ The page URI is null before the first navigation commits; NEVER block then, or the page's
    /// own document can't load.
    /// </summary>
    public Func<Uri, Uri?, bool>? RequestFilter { get; init; }

    /// <summary>
    /// Virtual host the app's OWN packaged bundle is served on inside this session, via
    /// <see cref="ResourceProvider"/> — the same pair, with the same names, as
    /// <c>WebViewHostOptions.VirtualHost</c>/<c>ResourceProvider</c>. Both halves or neither: either
    /// alone throws at initialization. Without it a session can only reach NETWORK-reachable URLs (D38).
    /// </summary>
    public string? VirtualHost { get; init; }

    /// <summary>
    /// The packaged-bundle provider behind <see cref="VirtualHost"/>. Pass the SAME instance the
    /// shell's <c>WebViewHost</c> uses, so the session's requests hit a warm cache.
    /// <para>
    /// ⚠ <b>Only on a session that renders YOUR pages.</b> Bundle responses carry
    /// <c>Access-Control-Allow-Origin: *</c>, so script in whatever page this session is on could
    /// <c>fetch</c> your whole bundle; give a co-browse session its own options without them (D38).
    /// </para>
    /// </summary>
    public IWebViewResourceProvider? ResourceProvider { get; init; }

    /// <summary>
    /// Disk-folder virtual hosts for this session (<c>SetVirtualHostNameToFolderMapping</c>), for an app
    /// whose bundle or media lives on disk rather than embedded.
    /// </summary>
    public IReadOnlyList<WebViewFolderMapping> FolderMappings { get; init; } = [];

    /// <summary>
    /// Budget for environment creation + core attach; normal init is a few seconds. A profile folder
    /// still LOCKED by an orphaned WebView2 process otherwise hangs init FOREVER.
    /// </summary>
    public TimeSpan InitTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// True in development: re-appends <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS</c> so a session browser
    /// is reachable over CDP. Required because setting <c>AdditionalBrowserArguments</c> at all makes
    /// WebView2 IGNORE that env var.
    /// </summary>
    public bool IsDevelopment { get; init; }

    /// <summary>Diagnostics. Null = silent.</summary>
    public ILogger? Log { get; init; }

    /// <summary>
    /// Where to publish what the browser reports (types and payloads: <see cref="SessionEvents"/>);
    /// null = publish nothing. The SCOPE is not here — one options object is shared across a pool's
    /// instances, so the session's identity is a per-instance argument to
    /// <c>SessionBrowser.InitializeAsync</c>.
    /// </summary>
    public IEventBus? Events { get; init; }

    /// <summary>
    /// Which responses raise <see cref="SessionEvents.ResponseReceived"/>. Null (the default) = NONE.
    /// A predicate rather than a bool because this event is per-SUBRESOURCE — it is both the on-switch
    /// and the cost control: <c>uri =&gt; uri.Host == "login.example.com"</c> pays for that host only.
    /// ⚠ Not <see cref="RequestFilter"/>, whose polarity is the opposite: that one answers "block
    /// this?", this one "report this?". Neither can affect the other.
    /// </summary>
    public Func<Uri, bool>? ObserveResponse { get; init; }

    /// <summary>
    /// How many characters of an observed response's BODY to include in
    /// <see cref="SessionResponse.BodySample"/>. 0 (the default) = do not read bodies at all; separate
    /// from <see cref="ObserveResponse"/>, which decides WHICH responses are reported.
    /// <para>
    /// ⚠ A SAMPLE, not a download — clamped to 1,048,576 CHARACTERS, which is about <b>2 MB</b> of memory
    /// because a .NET <c>char</c> is two bytes. The buffer is allocated at the clamped size per observed
    /// response, so a large value costs that much per response and not once.
    /// </para>
    /// </summary>
    public int ResponseBodySample { get; init; }

    /// <summary>
    /// The page opened an <c>alert</c>/<c>confirm</c>/<c>prompt</c>. Null = DISMISS it.
    /// <para>
    /// ⚠ <b>The DEFAULT is the fix here.</b> Leaving <c>ScriptDialogOpening</c> unhandled makes WebView2
    /// show its OWN modal — and a session's window is off-screen, so nothing can ever dismiss it and the
    /// page stops for good.
    /// </para>
    /// </summary>
    public Action<SessionScriptDialog>? OnScriptDialog { get; init; }

    /// <summary>
    /// The server asked for HTTP credentials (a 401 challenge). Null = CANCEL, which lets the load fail
    /// normally. ⚠ Same wedge as <see cref="OnScriptDialog"/>: unhandled, WebView2 raises its own prompt
    /// against a window nobody can see.
    /// </summary>
    public Action<SessionAuthRequest>? OnAuthRequest { get; init; }

    /// <summary>
    /// The server asked for a CLIENT certificate. Null = CANCEL. ⚠ The third of the blocking three, and
    /// the one easiest to miss: mutual-TLS is rare until an app meets an intranet that requires it, and
    /// then the session simply stops.
    /// </summary>
    public Action<SessionCertificateRequest>? OnCertificateRequest { get; init; }

    /// <summary>
    /// The page tried to open a new window (<c>window.open</c>, <c>target="_blank"</c>).
    /// Null = SUPPRESS it.
    /// </summary>
    public Action<SessionWindowRequest>? OnWindowRequest { get; init; }

    /// <summary>
    /// The page asked for a capability (camera, microphone, geolocation, clipboard read…). Null = DENY:
    /// an invisible page cannot meaningfully prompt, and an unanswered request stalls whatever asked.
    /// </summary>
    public Action<SessionPermissionRequest>? OnPermissionRequest { get; init; }
}

/// <summary>A new-window request from the page. Allow it, or leave it to be suppressed.</summary>
/// <param name="Uri">Where the page wanted to open.</param>
/// <param name="UserInitiated">True when a real gesture triggered it, rather than script alone.</param>
public sealed record SessionWindowRequest(string Uri, bool UserInitiated)
{
    /// <summary>True = let the browser open it. False (the default) = suppress.</summary>
    public bool Allow { get; set; }
}

/// <summary>A capability the page asked for. Grant it, or leave it to be denied.</summary>
/// <param name="Kind">The platform's name for what was asked (<c>Camera</c>, <c>ClipboardRead</c>, …).</param>
/// <param name="Uri">The page that asked.</param>
/// <param name="UserInitiated">True when a real gesture triggered it.</param>
public sealed record SessionPermissionRequest(string Kind, string Uri, bool UserInitiated)
{
    /// <summary>True = grant. False (the default) = deny.</summary>
    public bool Allow { get; set; }
}

/// <summary>
/// A script dialog the page opened, and what to do about it. Mutate and return — nothing is awaited.
/// </summary>
/// <param name="Kind">Alert, confirm, prompt or beforeunload, as the platform reports it.</param>
/// <param name="Uri">The page that opened it.</param>
/// <param name="Message">The text the page passed.</param>
/// <param name="DefaultText">A <c>prompt</c>'s pre-filled text; empty otherwise.</param>
public sealed record SessionScriptDialog(string Kind, string Uri, string Message, string DefaultText)
{
    /// <summary>
    /// True = answer as if the user pressed OK. False (the default) = dismiss/cancel.
    /// ⚠ For <c>beforeunload</c>, accepting lets the navigation proceed.
    /// </summary>
    public bool Accept { get; set; }

    /// <summary>What a <c>prompt</c> should answer with. Ignored unless <see cref="Accept"/> is set.</summary>
    public string ResultText { get; set; } = string.Empty;
}

/// <summary>An HTTP authentication challenge, and the credentials to answer it with.</summary>
/// <param name="Uri">The resource being requested.</param>
/// <param name="Challenge">The scheme and realm the server named.</param>
public sealed record SessionAuthRequest(string Uri, string Challenge)
{
    /// <summary>Set both to answer the challenge; leave them null to CANCEL, which is the default.</summary>
    public string? UserName { get; set; }

    /// <inheritdoc cref="UserName"/>
    public string? Password { get; set; }

    /// <summary>
    /// 🔴 <b>REDACTED, because a record's generated <c>ToString()</c> prints every property.</b> This one
    /// holds a password, and the generated version would put it in any log line, exception message or
    /// debugger watch that formats the object.
    /// </summary>
    public override string ToString() =>
        $"{nameof(SessionAuthRequest)} {{ Uri = {Uri}, Challenge = {Challenge}, "
        + $"UserName = {(UserName is null ? "null" : "***")}, Password = {(Password is null ? "null" : "***")} }}";
}

/// <summary>A client-certificate request. Select one, or leave it to cancel.</summary>
/// <param name="Host">The host asking.</param>
/// <param name="Port">The port it asked on.</param>
/// <param name="Subjects">The certificate subjects on offer, in the platform's order.</param>
public sealed record SessionCertificateRequest(string Host, int Port, IReadOnlyList<string> Subjects)
{
    /// <summary>
    /// Index into <see cref="Subjects"/> to present that certificate. Null (the default) CANCELS, which
    /// fails the load rather than hanging it.
    /// </summary>
    public int? SelectedIndex { get; set; }
}

/// <summary>
/// The ONE place a session browser gets configured: creates a per-profile
/// <see cref="CoreWebView2Environment"/>, attaches it, and applies the session hardening (no
/// devtools/status bar/password autosave, muted). Distinct from <c>WebViewHost</c>, which hosts ONE app
/// frontend; sessions are many short-lived browsers over arbitrary pages with per-profile isolation.
/// </summary>
internal static class SessionBrowser
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
    /// Called when this browser's RENDER process dies (crash, OOM, kill). Per-INSTANCE rather than an
    /// options field, because one options object is shared across a pool's instances. ⚠ Sessions run
    /// unattended off-screen, so without it a dead renderer is INVISIBLE.
    /// </param>
    /// <param name="cancellationToken">
    /// Abandons the WAIT for initialization, NOT the creation itself — see the body.
    /// </param>
    /// <param name="sessionScope">
    /// This session's identity, the SCOPE of everything published on
    /// <see cref="SessionBrowserOptions.Events"/>. ⚠ Null = an unscoped GLOBAL broadcast, correct only
    /// when there is exactly one session. A FUNCTION, not a string, because a pooled browser outlives
    /// the lease that borrowed it and is read per emit.
    /// </param>
    internal static Task InitializeAsync(WebView2Control web, SessionBrowserOptions options,
                                         Action<CoreWebView2ProcessFailedEventArgs>? onProcessFailed = null,
                                         Func<string?>? sessionScope = null,
                                         CancellationToken cancellationToken = default) =>
        InitializeAsync(web, options, onProcessFailed, sessionScope, environmentCache: null, cancellationToken);

    /// <summary>A fresh session identity — one shape for every session type.</summary>
    internal static string NewSessionId() => Guid.NewGuid().ToString("n");

    /// <summary>
    /// As the overload above, but reusing <paramref name="environmentCache"/>'s shared environment
    /// when the caller creates SEVERAL browsers on one profile (the render pool). See
    /// <see cref="SessionEnvironmentCache"/> for why the cache is owner-scoped rather than static.
    /// </summary>
    internal static async Task InitializeAsync(WebView2Control web, SessionBrowserOptions options,
                                               Action<CoreWebView2ProcessFailedEventArgs>? onProcessFailed,
                                               Func<string?>? sessionScope,
                                               SessionEnvironmentCache? environmentCache,
                                               CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(web);
        ArgumentNullException.ThrowIfNull(options);

        // A non-positive InitTimeout makes both WaitAsync calls below expire immediately, so init fails
        // instantly with the profile-LOCK diagnosis — sending the caller after a zombie msedgewebview2
        // process that does not exist.
        if (options.InitTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(SessionBrowserOptions.InitTimeout)} must be positive.");

        AssertBundleConfigured(options);

        Directory.CreateDirectory(options.ProfileDirectory);

        // Composed through Shenora.Windows's owner so each features switch appears exactly ONCE (a
        // caller's own --disable-features cannot silently discard the preset) and the dev CDP arguments
        // are re-appended, without which a session browser is unreachable over CDP.
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
            // The timeout wraps BOTH steps: a profile-lock stall most often hangs environment creation,
            // not just the core attach.
            //
            // ⚠ WaitAsync abandons the AWAIT, not the creation. Without a cache the next attempt starts a
            // SECOND CreateAsync against the same locked profile, orphaning another browser process onto
            // the lock its own error message blames; with one, the retry joins the attempt in flight.
            //
            // THE TOKEN GATES THE AWAIT ONLY: with a cache the environment task is SHARED across every
            // instance the pool is building on that profile, so cancelling the creation for one caller
            // would break all the others.
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
        // Script dialogs OFF: a session browser is parked off-screen at opacity 0, so an alert()/confirm()
        // renders inside an invisible window nobody can dismiss — and it BLOCKS the page's JS thread, so
        // every later script or CDP call for that instance never completes.
        settings.AreDefaultScriptDialogsEnabled = false;
        if (options.MuteAudio)
            core.IsMuted = true; // belt-and-suspenders with --mute-audio

        // POLICIES are the hooks — one owner, a decision the browser obeys. EVENTS are observation, with
        // many subscribers and no say. Separate methods keep the next handler from becoming both.
        WireSessionPolicies(core, options, onProcessFailed);
        WireSessionEvents(core, options, sessionScope);
        AttachResourceHandling(core, env, options);
    }

    /// <summary>
    /// <see cref="SessionBrowserOptions.VirtualHost"/> and
    /// <see cref="SessionBrowserOptions.ResourceProvider"/> are both-or-neither: either alone serves
    /// nothing, and the symptom would be the session showing "can't reach this page". Fail at
    /// composition instead. Internal + static so the rule is testable without a live browser process.
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
    /// The request-layer DECISION for a session browser, split out of the event handler so the rule is
    /// unit-testable. THE ORDER IS THE INVARIANT: the filter is consulted BEFORE the bundle, so serving
    /// from our own provider can never override a policy the app stated. ONE decision rather than two
    /// handlers, because two <c>WebResourceRequested</c> subscriptions both assigning
    /// <c>args.Response</c> is last-writer-wins by subscription order.
    /// </summary>
    /// <param name="requestUri">The raw <c>e.Request.Uri</c>.</param>
    /// <param name="pageSource">The raw <c>core.Source</c> — may be empty or <c>about:blank</c>.</param>
    /// <param name="filter">The app's blocking policy, or null.</param>
    /// <param name="bundlePrefix">The virtual-host prefix from <c>WebViewBundleServing.Prefix</c>, or null.</param>
    /// <param name="onFilterError">Receives a throw from <paramref name="filter"/>; the request is then allowed.</param>
    internal static SessionRequestAction DecideRequest(string? requestUri, string? pageSource,
                                                       Func<Uri, Uri?, bool>? filter, string? bundlePrefix,
                                                       Action<Exception>? onFilterError = null)
    {
        if (filter is not null && ShouldBlockRequest(requestUri, pageSource, filter, onFilterError))
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
        // Handled by WebView2 itself — no interception, so no ordering questions against the below.
        foreach (var mapping in options.FolderMappings)
            core.SetVirtualHostNameToFolderMapping(mapping.HostName, mapping.FolderPath, mapping.AccessKind);

        var filter = options.RequestFilter;
        var bundlePrefix = WebViewBundleServing.Prefix(options.VirtualHost, options.ResourceProvider);
        if (filter is null && bundlePrefix is null) return;

        // ONE filter registration, widened only as far as needed: a blocking policy must see every
        // request; a bundle alone only needs its own prefix.
        core.AddWebResourceRequestedFilter(
            filter is not null ? "*" : bundlePrefix + "*", CoreWebView2WebResourceContext.All);

        // ONCE per session. The filter runs on every subresource of every page, so a predicate that
        // throws for one shape would otherwise write a log line per image on the page.
        var filterErrorReported = 0;
        void ReportFilterError(Exception ex)
        {
            if (Interlocked.Exchange(ref filterErrorReported, 1) != 0) return;
            SessionLog.Try(options.Log, l => l.LogError(ex,
                "The session's RequestFilter threw and the request was ALLOWED (fail-open). Every later "
                + "throw from it is also allowed and will not be logged again — if this filter is your "
                + "blocking policy, it is not blocking."));
        }

        core.WebResourceRequested += (_, e) =>
        {
            var uri = e.Request.Uri;
            switch (DecideRequest(uri, core.Source, filter, bundlePrefix, ReportFilterError))
            {
                case SessionRequestAction.Block:
                    e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
                    break;
                case SessionRequestAction.ServeBundle:
                    // The SAME implementation the app shell serves its frontend with. The log sink is the
                    // session's own, guarded, because this body runs inside a WebView2 event.
                    WebViewBundleServing.Serve(e, env, options.ResourceProvider!, uri, bundlePrefix!,
                        message => SessionLog.Try(options.Log, l => l.LogDebug("{Message}", message())));
                    break;
            }
        };
    }

    /// <summary>
    /// The session's own event policies. <c>WebViewHost</c>'s versions must NOT be shared: an app shell
    /// opens external links in the system browser, an unattended session opens nothing at all.
    /// </summary>
    private static void WireSessionPolicies(CoreWebView2 core, SessionBrowserOptions options,
                                            Action<CoreWebView2ProcessFailedEventArgs>? onProcessFailed)
    {
        // A pooled/off-screen page calling window.open() would otherwise get a REAL, visible WebView2
        // popup in an app that has no session UI. Every diagnostic below goes through SessionLog: these
        // bodies run inside WebView2 events with no caller on the stack, so an app logger that throws IS
        // an unhandled UI-thread exception.
        core.NewWindowRequested += (_, e) =>
        {
            var request = Decide(options.OnWindowRequest,
                new SessionWindowRequest(e.Uri ?? string.Empty, e.IsUserInitiated),
                ex => SessionLog.Try(options.Log, l => l.LogError(ex, "OnWindowRequest threw; suppressing.")));

            if (request.Allow) return;   // leaving it unhandled is what lets the browser open it
            e.Handled = true;
            SessionLog.Try(options.Log, l => l.LogDebug("Session browser suppressed a new-window request for {Uri}.", e.Uri));
        };

        // 🔴 THE THREE THAT WEDGE. Each is HANDLED even when the app supplies no hook, and that is the
        // whole point: leaving any of them unhandled makes WebView2 raise its OWN modal prompt, and a
        // session's window is off-screen, so nothing can dismiss it and the page stops for good. The
        // defaults are the safe answer to "nobody is watching" — dismiss, cancel, cancel.
        core.ScriptDialogOpening += (_, e) =>
        {
            var dialog = new SessionScriptDialog(
                e.Kind.ToString(), e.Uri ?? string.Empty, e.Message ?? string.Empty,
                e.DefaultText ?? string.Empty);

            Decide(options.OnScriptDialog, dialog,
                ex => SessionLog.Try(options.Log, l => l.LogError(ex, "OnScriptDialog threw; dismissing.")));

            if (dialog.Accept)
            {
                if (e.Kind == CoreWebView2ScriptDialogKind.Prompt) e.ResultText = dialog.ResultText;
                e.Accept();
            }
            // Not calling Accept() IS the dismiss.
            //
            // 🔴 THERE IS NO `Handled` ON THIS EVENT — measured against the SDK, its members are
            // Kind/Uri/Message/DefaultText/ResultText/Accept/GetDeferral and nothing else. SUBSCRIBING
            // is what suppresses WebView2's own dialog. So a handler that looks like it does nothing is
            // doing the load-bearing thing, and deleting it as dead code brings the wedge back.
        };

        core.BasicAuthenticationRequested += (_, e) =>
        {
            var challenge = new SessionAuthRequest(e.Uri ?? string.Empty, e.Challenge ?? string.Empty);
            Decide(options.OnAuthRequest, challenge,
                ex => SessionLog.Try(options.Log, l => l.LogError(ex, "OnAuthRequest threw; cancelling.")));

            if (challenge.UserName is not null && challenge.Password is not null)
            {
                e.Response.UserName = challenge.UserName;
                e.Response.Password = challenge.Password;
            }
            else
            {
                e.Cancel = true;
            }
            // No `Handled` here either — same rule as the dialog above: subscribing is the suppression.
        };

        core.ClientCertificateRequested += (_, e) =>
        {
            var offered = e.MutuallyTrustedCertificates;
            var request = new SessionCertificateRequest(e.Host ?? string.Empty, e.Port,
                [.. offered.Select(c => c.Subject ?? string.Empty)]);
            Decide(options.OnCertificateRequest, request,
                ex => SessionLog.Try(options.Log, l => l.LogError(ex, "OnCertificateRequest threw; cancelling.")));

            if (request.SelectedIndex is { } index && index >= 0 && index < offered.Count)
            {
                e.SelectedCertificate = offered[index];
            }
            else
            {
                // Cancel rather than "continue without one": continuing prompts the user on some
                // servers, which is the hang again by another route.
                e.Cancel = true;
            }
            e.Handled = true;
        };

        core.PermissionRequested += (_, e) =>
        {
            var request = Decide(options.OnPermissionRequest,
                new SessionPermissionRequest(e.PermissionKind.ToString(), e.Uri ?? string.Empty, e.IsUserInitiated),
                ex => SessionLog.Try(options.Log, l => l.LogError(ex, "OnPermissionRequest threw; denying.")));

            e.State = request.Allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            e.Handled = true;
            SessionLog.Try(options.Log, l => l.LogDebug("Session browser {Decision} permission {Kind}.",
                request.Allow ? "granted" : "denied", e.PermissionKind));
        };

        // A dead renderer is otherwise INVISIBLE here (see onProcessFailed's docs).
        core.ProcessFailed += (_, e) =>
        {
            // 🔴 EVERYTHING is logged; only a TERMINAL kind is reported. `ProcessFailed` fires for the
            // whole Chromium process tree, and most of it is routine and self-healing: a GPU-driver TDR
            // raises GpuProcessExited on a perfectly live page, and RenderProcessUnresponsive fires while
            // a renderer is merely busy. Treating those as "the renderer died" throws away a whole warm
            // pool. The DIAGNOSTIC fields go in the line because sessions run unattended, so this is the
            // only signal an adopter gets.
            SessionLog.Try(options.Log, l => l.LogWarning(
                "Session browser process failed: {Kind} ({Reason}), exit code {ExitCode}, process '{Process}'"
                + ", failing module '{Module}'.",
                e.ProcessFailedKind, e.Reason, e.ExitCode,
                string.IsNullOrWhiteSpace(e.ProcessDescription) ? "(unnamed)" : e.ProcessDescription,
                string.IsNullOrWhiteSpace(e.FailureSourceModulePath) ? "(none)" : e.FailureSourceModulePath));

            if (!IsTerminal(e.ProcessFailedKind)) return;

            try { onProcessFailed?.Invoke(e); }
            catch (Exception ex)
            {
                // Reporting a crash must not itself crash the UI thread.
                SessionLog.Try(options.Log, l => l.LogError(ex, "OnProcessFailed callback threw."));
            }
        };
    }

    /// <summary>
    /// Forward what the browser REPORTS onto the app's bus, scoped by this instance's id. Wires nothing
    /// when no bus is configured.
    /// <para>
    /// ⚠ <b>Observation only — no handler here changes what the browser does.</b> Policy lives in
    /// <see cref="WireSessionPolicies"/>; a session type that also wants to act on an event subscribes
    /// its own handler alongside.
    /// </para>
    /// </summary>
    private static void WireSessionEvents(CoreWebView2 core, SessionBrowserOptions options,
                                          Func<string?>? sessionScope)
    {
        if (options.Events is not { } bus) return;

        // ONE guard for every site below: reading the args can throw (a disposed response view, a
        // non-string web message), and an escape from a WebView2 event is fatal (see SessionLog). The
        // bus isolates a throwing SUBSCRIBER; what it cannot protect is building the payload.
        void Publish(string type, Func<object?> payload, bool coalesce = false)
        {
            try
            {
                bus.Emit(new EventMessage
                {
                    Module = SessionEvents.Module,
                    Type = type,
                    Scope = sessionScope?.Invoke(),
                    Payload = payload(),
                    // Only for the two whose payload is a full SNAPSHOT of "where am I"; the others are
                    // discrete happenings and coalescing them would lose events.
                    CoalesceKey = coalesce ? type : null,
                });
            }
            catch (Exception ex)
            {
                SessionLog.Try(options.Log, l => l.LogError(ex, "Publishing session event {Type} failed.", type));
            }
        }

        // Read from `core`, not the event args: NavigationCompleted's args carry no Uri at all, and
        // after a redirect chain the address the navigation started for is not where the page ended up.
        SessionSource Where() => new(core.Source ?? string.Empty, core.DocumentTitle ?? string.Empty);

        core.NavigationStarting += (_, e) => Publish(SessionEvents.NavigationStarting,
            () => new SessionSource(e.Uri ?? string.Empty, core.DocumentTitle ?? string.Empty));

        core.NavigationCompleted += (_, e) => Publish(SessionEvents.NavigationCompleted,
            () => new SessionNavigationResult(core.Source ?? string.Empty, e.IsSuccess, e.WebErrorStatus.ToString()));

        core.DOMContentLoaded += (_, _) => Publish(SessionEvents.DomContentLoaded, () => Where());

        core.SourceChanged += (_, _) => Publish(SessionEvents.SourceChanged, () => Where(), coalesce: true);

        core.DocumentTitleChanged += (_, _) => Publish(SessionEvents.TitleChanged, () => Where(), coalesce: true);

        core.WindowCloseRequested += (_, _) => Publish(SessionEvents.WindowCloseRequested, () => null);

        core.WebMessageReceived += (_, e) =>
        {
            // Prefer the string form, FALL BACK to raw JSON: a page posting an object rather than a
            // string is ordinary — `postMessage({type:'x'})`.
            string? message = null;
            try { message = e.TryGetWebMessageAsString(); }
            catch (ArgumentException)
            {
                try { message = e.WebMessageAsJson; } catch (ArgumentException) { /* neither form */ }
            }
            if (message is null) return;
            Publish(SessionEvents.WebMessage, () => new SessionWebMessage(message));
        };

        core.DownloadStarting += (_, e) => Publish(SessionEvents.DownloadStarting, () =>
        {
            var name = Path.GetFileName(e.ResultFilePath ?? string.Empty);
            return new DownloadHit(e.DownloadOperation.Uri, string.IsNullOrEmpty(name) ? null : name);
        });

        // Published for EVERY kind, unlike the onProcessFailed callback; `Terminal` separates the two.
        core.ProcessFailed += (_, e) => Publish(SessionEvents.ProcessFailed,
            () => new SessionProcessReport(e.ProcessFailedKind.ToString(), e.Reason.ToString(), e.ExitCode,
                                           IsTerminal(e.ProcessFailedKind)));

        if (options.ObserveResponse is not { } wanted) return;

        core.WebResourceResponseReceived += (_, e) =>
        {
            // The predicate runs OUTSIDE Publish's guard: it is app code on the per-subresource path, so
            // it gets its own guard and a throw means "do not report" rather than "report everything".
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;
            try { if (!wanted(uri)) return; }
            catch (Exception ex)
            {
                SessionLog.Try(options.Log, l => l.LogError(ex, "ObserveResponse threw; not reporting {Uri}.", uri));
                return;
            }

            if (options.ResponseBodySample <= 0)
            {
                Publish(SessionEvents.ResponseReceived, () => Describe(e, string.Empty));
                return;
            }

            // Fire-and-forget: the body arrives asynchronously, so the event is published LATER than the
            // header-only one, and nothing awaits it — the method guards itself throughout.
            _ = PublishWithBodyAsync(e, Math.Min(options.ResponseBodySample, MaxBodySample));
        };

        // Headers materialised EAGERLY: the response view is only valid while the handler (and the
        // synchronous head of the async read) is on the stack, so a lazy sequence handed to a subscriber
        // would read freed COM state.
        SessionResponse Describe(CoreWebView2WebResourceResponseReceivedEventArgs args, string body)
        {
            var response = args.Response;
            List<KeyValuePair<string, string>> headers = [.. response.Headers];
            return new SessionResponse(args.Request.Uri, response.StatusCode,
                                       response.ReasonPhrase ?? string.Empty, headers, body);
        }

        async Task PublishWithBodyAsync(CoreWebView2WebResourceResponseReceivedEventArgs args, int limit)
        {
            try
            {
                // BEFORE the first await, so it still runs inside the event's own window.
                var described = Describe(args, string.Empty);
                var body = string.Empty;
                try
                {
                    await using var stream = await args.Response.GetContentAsync().ConfigureAwait(true);
                    if (stream is not null)
                    {
                        using var reader = new StreamReader(stream);
                        var buffer = new char[limit];
                        var read = await reader.ReadBlockAsync(buffer, 0, limit).ConfigureAwait(true);
                        body = new string(buffer, 0, read);
                    }
                }
                catch (Exception ex)
                {
                    // Content already consumed, or still streaming. Report the response ANYWAY with an
                    // empty sample — the URL, status and headers are what most subscribers came for.
                    SessionLog.Try(options.Log, l => l.LogDebug(ex, "No body sample for {Uri}.", args.Request.Uri));
                }

                Publish(SessionEvents.ResponseReceived, () => described with { BodySample = body });
            }
            catch (Exception ex)
            {
                // Nothing observes this task, so an escape here would be an unobserved exception.
                SessionLog.Try(options.Log, l => l.LogError(ex, "Publishing a response with its body failed."));
            }
        }
    }

    /// <summary>The hard ceiling on <see cref="SessionBrowserOptions.ResponseBodySample"/> — this buffer
    /// is allocated per observed response.</summary>
    private const int MaxBodySample = 1024 * 1024;

    /// <summary>
    /// Ask a hook what to do, with the SAFE DEFAULT when there is no hook or the hook throws.
    /// ⚠ A THROWING hook must land on the default, not escape — these run inside a WebView2 event, and
    /// the default is what keeps the page moving, so a buggy hook degrades to "dismiss/cancel" rather
    /// than to the wedge.
    /// </summary>
    /// <param name="hook">The app's handler, or null.</param>
    /// <param name="args">The event, which the hook mutates in place.</param>
    /// <param name="onError">Receives a throw from the hook.</param>
    internal static T Decide<T>(Action<T>? hook, T args, Action<Exception>? onError = null)
    {
        if (hook is null) return args;
        try
        {
            hook(args);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            // Half-mutated args are still readable: every decision field holds its DEFAULT unless the
            // hook set it before throwing, and a hook that set Accept and then threw meant to accept.
        }
        return args;
    }

    /// <summary>
    /// Does this failure mean the SESSION is dead, as opposed to something Chromium recovers from by
    /// itself? An allow-list of two: everything else in the enum is either auxiliary (GPU, utility,
    /// sandbox helper, plugin) or recoverable — an unresponsive renderer is ALIVE, and an
    /// out-of-process iframe dying leaves the main document running.
    /// </summary>
    internal static bool IsTerminal(CoreWebView2ProcessFailedKind kind) =>
        kind is CoreWebView2ProcessFailedKind.RenderProcessExited
             or CoreWebView2ProcessFailedKind.BrowserProcessExited;

    /// <summary>
    /// The request-filter DECISION, split out of the event handler so the rule is unit-testable — this
    /// one is the app's blocking boundary. Returns true when the request must be answered with the 403.
    /// </summary>
    /// <param name="requestUri">The raw <c>e.Request.Uri</c>.</param>
    /// <param name="pageSource">The raw <c>core.Source</c> — may be empty or <c>about:blank</c>.</param>
    /// <param name="filter">The app's policy: (request, pageUri) → block?</param>
    /// <param name="onFilterError">
    /// Receives a throw from <paramref name="filter"/>. The request is ALLOWED when it throws, so this
    /// is the only signal that the app's blocking policy stopped blocking.
    /// </param>
    internal static bool ShouldBlockRequest(string? requestUri, string? pageSource, Func<Uri, Uri?, bool> filter,
                                            Action<Exception>? onFilterError = null)
    {
        // A request URI we cannot parse is not something we can describe to a policy — pass it.
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var request)) return false;

        // Only an http(s) page source is a real "page host" to compare against. The source is empty
        // before the first navigation commits, and a reset pool instance sits on `about:blank`; passing
        // either through would make a same-host filter treat the page's OWN next document as
        // third-party and 403 it.
        var pageUri = Uri.TryCreate(pageSource, UriKind.Absolute, out var p)
                      && p.Scheme is "http" or "https"
            ? p
            : null;
        try
        {
            return filter(request, pageUri);
        }
        catch (Exception ex)
        {
            // A throwing filter must not break page loading — FAIL OPEN, the opposite of the navigation
            // guard's fail-closed stance, because this runs on every subresource of every page.
            //
            // 🔴 BUT IT MUST NOT BE SILENT, WHICH IT WAS. A single NullReferenceException on one edge
            // case turns an app's blocklist into "allow" for every request that hits it, with nothing
            // anywhere to notice. The caller reports the FIRST one per session; logging each would flood.
            onFilterError?.Invoke(ex);
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
