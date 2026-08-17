using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Shenora.Core.Events;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Windows;

/// <summary>
/// Configuration for a session browser (see <see cref="SessionBrowser"/>).
/// <para>
/// 🔴 <b>A <c>record</c> so a session can <c>with</c>-override the one or two fields it OWNS and inherit
/// the rest by construction.</b> <see cref="InteractiveSession"/> used to build its own options object
/// and forward two fields by hand, so the hooks, the request filter, bundle serving and the logger could
/// not reach it at all — and copying field-by-field would have fixed that once while silently missing the
/// NEXT field added here, a defect that ages in rather than failing.
/// </para>
/// </summary>
public sealed record SessionBrowserOptions
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

    /// <summary>
    /// Where to publish what the browser reports. Null = publish nothing, which is what every session
    /// did before the catalogue existed. See <see cref="SessionEvents"/> for the types and their
    /// payloads.
    /// <para>
    /// ⚠ <b>The BUS belongs here; the SCOPE does not.</b> One options object is shared across a pool's
    /// instances, so the session's identity is a per-instance argument to
    /// <c>SessionBrowser.InitializeAsync</c> — the same reasoning that keeps <c>onProcessFailed</c> out
    /// of this record. Putting the id here would give every pooled browser the same scope and make the
    /// events useless for telling them apart.
    /// </para>
    /// </summary>
    public IEventBus? Events { get; init; }

    /// <summary>
    /// Which responses raise <see cref="SessionEvents.ResponseReceived"/>. Null (the default) = NONE.
    /// <para>
    /// 🔴 <b>A predicate rather than a bool, because this event is per-SUBRESOURCE.</b> One page load is
    /// hundreds of images, stylesheets and XHRs; emitting all of them — and building a header list for
    /// each — is a cost that should be asked for, not defaulted into. The predicate is both the
    /// on-switch and the way to keep the cost proportionate: a cookie-capture app writes
    /// <c>uri =&gt; uri.Host == "login.example.com"</c> and pays for that host only.
    /// </para>
    /// <para>
    /// ⚠ Not to be confused with <see cref="RequestFilter"/>, whose polarity is the opposite: that one
    /// answers "block this?", this one answers "report this?". Neither can affect the other.
    /// </para>
    /// </summary>
    public Func<Uri, bool>? ObserveResponse { get; init; }

    /// <summary>
    /// How many characters of an observed response's BODY to include in
    /// <see cref="SessionResponse.BodySample"/>. 0 (the default) = do not read bodies at all.
    /// <para>
    /// Separate from <see cref="ObserveResponse"/> because the two costs are different: that one decides
    /// WHICH responses are reported, this one decides whether each report also pays for a content read.
    /// An app watching an API for its JSON payloads sets both; one watching for <c>Set-Cookie</c> sets
    /// only the first and never touches a body.
    /// </para>
    /// <para>
    /// ⚠ A SAMPLE, not a download — clamped to 1 MB. Anything larger wants the app's own HTTP client,
    /// not a copy of the page's traffic held in memory on the UI thread.
    /// </para>
    /// </summary>
    public int ResponseBodySample { get; init; }

    /// <summary>
    /// The page opened a <c>alert</c>/<c>confirm</c>/<c>prompt</c>. Null = DISMISS it.
    /// <para>
    /// 🔴 <b>A HOOK, not an event: exactly one owner, and it decides.</b> Shaped like a web event — the
    /// handler is given the dialog and acts ON it — because that is what an adopter already knows and
    /// what WebView2's own events look like.
    /// </para>
    /// <para>
    /// ⚠ <b>The DEFAULT is the fix here.</b> Leaving <c>ScriptDialogOpening</c> unhandled makes WebView2
    /// show its OWN modal — and a session's window is off-screen, so nothing can ever dismiss it and the
    /// page stops for good. Dismissing silently is not ideal; waiting forever is worse, and was what
    /// shipped.
    /// </para>
    /// </summary>
    public Action<SessionScriptDialog>? OnScriptDialog { get; init; }

    /// <summary>
    /// The server asked for HTTP credentials (a 401 challenge). Null = CANCEL, which lets the load fail
    /// normally.
    /// <para>
    /// ⚠ Same wedge as <see cref="OnScriptDialog"/>: unhandled, WebView2 raises its own prompt against a
    /// window nobody can see.
    /// </para>
    /// </summary>
    public Action<SessionAuthRequest>? OnAuthRequest { get; init; }

    /// <summary>
    /// The server asked for a CLIENT certificate. Null = CANCEL.
    /// <para>
    /// ⚠ The third of the blocking three, and the one easiest to miss: mutual-TLS is rare until an app
    /// meets an intranet that requires it, and then the session simply stops.
    /// </para>
    /// </summary>
    public Action<SessionCertificateRequest>? OnCertificateRequest { get; init; }

    /// <summary>
    /// The page tried to open a new window (<c>window.open</c>, <c>target="_blank"</c>). Null = SUPPRESS
    /// it, which is what has always happened.
    /// <para>
    /// ⚠ Unlike the three above, the default here is not a hang — it is a POLICY the kit was making
    /// silently. A co-browse session showing a page whose flow legitimately opens a popup had no way to
    /// say so.
    /// </para>
    /// </summary>
    public Action<SessionWindowRequest>? OnWindowRequest { get; init; }

    /// <summary>
    /// The page asked for a capability (camera, microphone, geolocation, clipboard read…). Null = DENY,
    /// which is what has always happened.
    /// <para>
    /// ⚠ Denying remains the right default for an unattended session — an invisible page cannot
    /// meaningfully prompt, and an unanswered request stalls whatever asked. What was missing is the app's
    /// say: a session driving the app's OWN page may legitimately want clipboard read or geolocation.
    /// </para>
    /// </summary>
    public Action<SessionPermissionRequest>? OnPermissionRequest { get; init; }
}

/// <summary>A new-window request from the page. Allow it, or leave it to be suppressed.</summary>
/// <param name="Uri">Where the page wanted to open.</param>
/// <param name="UserInitiated">True when a real gesture triggered it, rather than script alone.</param>
public sealed record SessionWindowRequest(string Uri, bool UserInitiated)
{
    /// <summary>
    /// True = let the browser open it. False (the default) = suppress, the long-standing behaviour.
    /// </summary>
    public bool Allow { get; set; }
}

/// <summary>A capability the page asked for. Grant it, or leave it to be denied.</summary>
/// <param name="Kind">The platform's name for what was asked (<c>Camera</c>, <c>ClipboardRead</c>, …).</param>
/// <param name="Uri">The page that asked.</param>
/// <param name="UserInitiated">True when a real gesture triggered it.</param>
public sealed record SessionPermissionRequest(string Kind, string Uri, bool UserInitiated)
{
    /// <summary>True = grant. False (the default) = deny, the long-standing behaviour.</summary>
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
    /// debugger watch that formats the object — none of which looks like a place a credential goes. The
    /// override is the only thing standing between "we accepted a credential" and "we printed it".
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
/// The ONE place a session browser gets configured — ported from the server-backed sibling's
/// shared browser primitive (before it existed, every window re-did the same env creation +
/// hardening + HTML read). Creates a per-profile <see cref="CoreWebView2Environment"/>, attaches
/// it, and applies the session hardening (no devtools/status bar/password autosave, muted).
/// Distinct from <c>WebViewHost</c> on purpose: app shells host ONE app frontend; sessions are
/// many short-lived browsers over arbitrary pages with per-profile isolation.
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
    /// <param name="sessionScope">
    /// This session's identity, used as the SCOPE of everything published on
    /// <see cref="SessionBrowserOptions.Events"/>. Per-instance for the same reason
    /// <paramref name="onProcessFailed"/> is: a pool builds many browsers from one options object, and
    /// events that cannot be told apart are events nobody can act on. Null = a global (unscoped)
    /// broadcast, which only reads correctly when there is exactly one session.
    /// <para>
    /// 🔴 <b>A FUNCTION, not a string, and the pool is why.</b> Handlers are wired once at init but a
    /// pooled browser outlives the lease that borrowed it — so a captured constant id would publish a
    /// LATER lease's events under the earlier one's scope, which is the exact
    /// two-sessions-indistinguishable problem the scope exists to solve, only displaced in time.
    /// Reading it per emit lets the pool hand each lease its own identity.
    /// </para>
    /// </param>
    internal static Task InitializeAsync(WebView2Control web, SessionBrowserOptions options,
                                         Action<CoreWebView2ProcessFailedEventArgs>? onProcessFailed = null,
                                         Func<string?>? sessionScope = null,
                                         CancellationToken cancellationToken = default) =>
        InitializeAsync(web, options, onProcessFailed, sessionScope, environmentCache: null, cancellationToken);

    /// <summary>A fresh session identity — one shape for every session type, so a scope is recognisable
    /// as one wherever it surfaces.</summary>
    internal static string NewSessionId() => Guid.NewGuid().ToString("n");

    /// <summary>
    /// As the overload above, but reusing <paramref name="environmentCache"/>'s shared environment
    /// when the caller creates SEVERAL browsers on one profile (the render pool). The cache is an
    /// ownership detail of that caller, not a consumer concept — see
    /// <see cref="SessionEnvironmentCache"/> for why it is owner-scoped rather than static.
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

        // Two passes on purpose, and the split is the design: POLICIES are the hooks — one owner, a
        // decision the browser obeys — while EVENTS are observation with many subscribers and no say.
        // Keeping them in separate methods stops the next handler from quietly becoming both.
        WireSessionPolicies(core, options, onProcessFailed);
        WireSessionEvents(core, options, sessionScope);
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

            // Guarded: this runs inside a WebView2 event, where an escaping exception is an unhandled
            // UI-thread crash. A throwing hook falls back to the safe default rather than the wedge.
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

        // Deny every permission by default: an invisible page cannot meaningfully prompt, and an
        // un-answered permission request stalls the feature that asked.
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

        // A dead renderer is otherwise INVISIBLE here (see OnProcessFailed's docs).
        core.ProcessFailed += (_, e) =>
        {
            // 🔴 EVERYTHING is logged; only a TERMINAL kind is reported. `ProcessFailed` fires for the
            // whole Chromium process tree, and most of it is routine and self-healing: a GPU-driver TDR
            // raises GpuProcessExited on a perfectly live page, and RenderProcessUnresponsive fires while
            // a renderer is merely busy. Treating those as "the renderer died" threw away a whole warm
            // pool at seconds-per-instance, and permanently killed a co-browse pane over a page that was
            // still running. `WebViewHost` in this same package already filters on the kind; the session
            // stack did not.
            //
            // ⚠ The DIAGNOSTIC fields go in the log line. `WebViewHost` records why under a 🔴 comment:
            // "RenderProcessExited (reason: Crashed)" names the event and nothing about the cause, and
            // naming a failure while withholding its identity reads as a diagnostic and is not one.
            // Sessions run unattended, so this line is the only signal an adopter gets.
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
                // Reporting a crash must not itself crash the UI thread — which is also why the report
                // goes through SessionLog: an app logger that throws in this handler has no caller.
                SessionLog.Try(options.Log, l => l.LogError(ex, "OnProcessFailed callback threw."));
            }
        };
    }

    /// <summary>
    /// Forward what the browser REPORTS onto the app's bus, scoped by this instance's id. Wires nothing
    /// when no bus is configured, so an app that never asked pays nothing.
    /// <para>
    /// ⚠ <b>Observation only — not one handler here changes what the browser does.</b> No <c>Cancel</c>,
    /// no <c>Handled</c>, no <c>State</c>. Policy lives in <see cref="WireSessionPolicies"/>, and a
    /// session type that also wants to act on an event (the interactive controller cancels downloads)
    /// subscribes its own handler alongside — the two do not interfere, and the emit is unaffected by
    /// whatever the other one decides.
    /// </para>
    /// </summary>
    private static void WireSessionEvents(CoreWebView2 core, SessionBrowserOptions options,
                                          Func<string?>? sessionScope)
    {
        if (options.Events is not { } bus) return;

        // ONE guard for every site below. These bodies run inside WebView2 events with no caller on the
        // stack, so an escaping exception is an unhandled UI-thread crash — and reading the args can
        // genuinely throw (a disposed response view, a non-string web message). The bus already isolates
        // a throwing SUBSCRIBER; what it cannot protect is building the payload.
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
                    // Only for the two whose payload is a full SNAPSHOT of "where am I" — a buffering
                    // consumer may drop an intermediate one without changing the state anyone renders.
                    // The others are discrete happenings and coalescing them would lose events.
                    CoalesceKey = coalesce ? type : null,
                });
            }
            catch (Exception ex)
            {
                SessionLog.Try(options.Log, l => l.LogError(ex, "Publishing session event {Type} failed.", type));
            }
        }

        // The browser's position, as it is RIGHT NOW. Deliberately read from `core` rather than from the
        // event args: NavigationCompleted's args carry no Uri at all, and after a redirect chain the
        // address the navigation started for is not where the page ended up.
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
            // Prefer the string form, FALL BACK to raw JSON. A page posting an object rather than a
            // string is ordinary — `postMessage({type:'x'})` — and dropping it would have made this
            // event strictly weaker than the tap it replaces, which is the one thing a replacement
            // must not be.
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

        // Published for EVERY kind, unlike the onProcessFailed callback — a subscriber that wants to log
        // a recoverable GPU reset should be able to, and `Terminal` is what separates the two.
        core.ProcessFailed += (_, e) => Publish(SessionEvents.ProcessFailed,
            () => new SessionProcessReport(e.ProcessFailedKind.ToString(), e.Reason.ToString(), e.ExitCode,
                                           IsTerminal(e.ProcessFailedKind)));

        if (options.ObserveResponse is not { } wanted) return;

        core.WebResourceResponseReceived += (_, e) =>
        {
            // The predicate runs OUTSIDE Publish's guard on purpose: it is app code on the per-subresource
            // path, so it gets its own guard and a throw means "do not report" rather than "report
            // everything". Failing closed here matches the filter's polarity — a broken predicate must not
            // turn into the firehose it exists to prevent.
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

            // Fire-and-forget, exactly as the tap this replaces did: the body arrives asynchronously, so
            // the event is published LATER than the header-only one would be. Nothing awaits it — an
            // event handler has no caller to return a task to — so the method guards itself throughout.
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
                    // empty sample — the URL, status and headers are what most subscribers came for, and
                    // withholding all of them because the body was unavailable is the worse answer.
                    SessionLog.Try(options.Log, l => l.LogDebug(ex, "No body sample for {Uri}.", args.Request.Uri));
                }

                Publish(SessionEvents.ResponseReceived, () => described with { BodySample = body });
            }
            catch (Exception ex)
            {
                // Nothing observes this task, so an escape here would be an unobserved exception rather
                // than a visible failure.
                SessionLog.Try(options.Log, l => l.LogError(ex, "Publishing a response with its body failed."));
            }
        }
    }

    /// <summary>The hard ceiling on <see cref="SessionBrowserOptions.ResponseBodySample"/> — a sample is
    /// a diagnostic aid, not a download, and this buffer is allocated per observed response.</summary>
    private const int MaxBodySample = 1024 * 1024;

    /// <summary>
    /// Ask a hook what to do, with the SAFE DEFAULT when there is no hook or the hook throws.
    /// <para>
    /// 🔴 Split out of the WebView2 handler for the reason the request filter was: a rule reachable only
    /// through a live <c>CoreWebView2</c> is a rule nothing tests, and these three decide whether an
    /// unattended session carries on or stops forever.
    /// </para>
    /// <para>
    /// ⚠ A THROWING hook must land on the default, not escape. These run inside a WebView2 event, where
    /// an escaping exception is an unhandled UI-thread crash — and the default is what keeps the page
    /// moving, so a buggy hook degrades to "dismiss/cancel" rather than to the wedge.
    /// </para>
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
            // The args may be half-mutated, so hand back a pristine one? No — the caller reads only the
            // decision fields, and every one of them still holds its DEFAULT unless the hook set it
            // before throwing. A hook that set Accept and then threw meant to accept.
        }
        return args;
    }

    /// <summary>
    /// Does this failure mean the SESSION is dead, as opposed to something Chromium recovers from by
    /// itself?
    /// <para>
    /// An allow-list of two, because the consequences of reporting are terminal — a discarded pool
    /// instance, a completed frame channel — and everything else in the enum is either auxiliary (GPU,
    /// utility, sandbox helper, plugin) or explicitly recoverable (an unresponsive renderer is ALIVE, and
    /// an out-of-process iframe dying leaves the main document running).
    /// </para>
    /// </summary>
    internal static bool IsTerminal(CoreWebView2ProcessFailedKind kind) =>
        kind is CoreWebView2ProcessFailedKind.RenderProcessExited
             or CoreWebView2ProcessFailedKind.BrowserProcessExited;

    /// <summary>
    /// The request-filter DECISION, split out of the event handler so the real rule is unit-testable
    /// (P5.5 H7 — the same lesson as the pool's reset probe: a rule reachable only through a live
    /// <c>CoreWebView2</c> is a rule nothing tests, and this one is the app's blocking boundary).
    /// Returns true when the request must be answered with the 403.
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
        catch (Exception ex)
        {
            // A throwing filter must not break page loading — FAIL OPEN. Deliberate, and the opposite
            // of the navigation guard's fail-closed stance: this runs on every subresource of every
            // page, so failing closed on a buggy app predicate would present as a blank page with no
            // diagnosis.
            //
            // 🔴 BUT IT MUST NOT BE SILENT, WHICH IT WAS. Failing open and saying nothing is the worse
            // half of the same problem the comment above worries about: a single NullReferenceException
            // on one edge case turned an app's blocklist into "allow" for every request that hit it,
            // with nothing anywhere to notice. The caller reports the FIRST one per session — see the
            // wiring — because this runs per subresource and logging each would flood.
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
