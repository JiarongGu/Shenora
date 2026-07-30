using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.WebView2.Sessions;

/// <summary>Inputs for <see cref="SessionBrowser.InitializeAsync(WebView2Control, SessionBrowserOptions, Action{CoreWebView2ProcessFailedEventArgs})"/>.</summary>
public sealed class SessionBrowserOptions
{
    /// <summary>
    /// The persistent profile (user-data folder) this browser runs in. Sessions key their whole
    /// isolation story on this directory: a login window scopes it per provider (and per
    /// sub-account — a SECURITY boundary, see <see cref="LoginWindow"/> once it ships), a pool
    /// shares one across its instances, and wiping it is what makes a logout real.
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
    /// are for fetch/render/login work, never playback — a page that autoplays media must not
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
    public static Task InitializeAsync(WebView2Control web, SessionBrowserOptions options,
                                       Action<CoreWebView2ProcessFailedEventArgs>? onProcessFailed = null) =>
        InitializeAsync(web, options, onProcessFailed, environmentCache: null);

    /// <summary>
    /// As the public overload, but reusing <paramref name="environmentCache"/>'s shared environment
    /// when the caller creates SEVERAL browsers on one profile (the render pool). Internal because
    /// the cache is an ownership detail of that caller, not a consumer concept — see
    /// <see cref="SessionEnvironmentCache"/> for why it is owner-scoped rather than static.
    /// </summary>
    internal static async Task InitializeAsync(WebView2Control web, SessionBrowserOptions options,
                                               Action<CoreWebView2ProcessFailedEventArgs>? onProcessFailed,
                                               SessionEnvironmentCache? environmentCache)
    {
        ArgumentNullException.ThrowIfNull(web);
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.ProfileDirectory);

        // Compose through Shenora.WebView2's owner (P5.5 H4.4 — the edge D14 declared but nothing
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
            var creation = environmentCache is null ? CreateEnvironment() : environmentCache.GetOrCreate(CreateEnvironment);
            env = await creation.WaitAsync(options.InitTimeout).ConfigureAwait(true);
            await web.EnsureCoreWebView2Async(env).WaitAsync(options.InitTimeout).ConfigureAwait(true);
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

        if (options.RequestFilter is { } filter)
            AttachRequestFilter(core, env, filter);
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
    /// Block filtered requests with an empty 403 so they never load — the request-layer
    /// mechanism the app's blocking policy plugs into (UI-thread hot path).
    /// </summary>
    private static void AttachRequestFilter(CoreWebView2 core, CoreWebView2Environment env, Func<Uri, Uri?, bool> filter)
    {
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var requestUri)) return;
            // Only an http(s) page source is a real "page host" to compare against. Before the
            // first navigation commits the source is empty, and a returned/reset pool instance
            // sits on `about:blank` — both parse as a non-web Uri (or none), and passing them
            // through would make a same-host filter treat the page's OWN next document as
            // third-party and 403 it. Pass null so a sane filter never blocks the document.
            var pageUri = Uri.TryCreate(core.Source, UriKind.Absolute, out var p) && p.Scheme is "http" or "https" ? p : null;
            try
            {
                if (filter(requestUri, pageUri))
                    e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
            }
            catch
            {
                // a throwing filter must not break page loading — fail open
            }
        };
    }

    /// <summary>
    /// The current rendered HTML (<c>document.documentElement.outerHTML</c>), or null.
    /// ExecuteScriptAsync returns the value JSON-encoded (a quoted string) — this decodes it
    /// back to raw HTML. Call on the UI thread.
    /// </summary>
    public static async Task<string?> GetHtmlAsync(WebView2Control web)
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
