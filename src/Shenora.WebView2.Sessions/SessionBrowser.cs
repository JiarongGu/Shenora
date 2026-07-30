using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.WebView2.Sessions;

/// <summary>Inputs for <see cref="SessionBrowser.InitializeAsync"/>.</summary>
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
    public static async Task InitializeAsync(WebView2Control web, SessionBrowserOptions options)
    {
        ArgumentNullException.ThrowIfNull(web);
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.ProfileDirectory);

        var arguments = BaseArgs
            + (options.MuteAudio ? MuteArgs : string.Empty)
            + (options.KeepAliveInBackground ? BackgroundArgs : string.Empty)
            + (string.IsNullOrWhiteSpace(options.AdditionalBrowserArguments)
                ? string.Empty
                : " " + options.AdditionalBrowserArguments);

        var envOptions = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = arguments };
        CoreWebView2Environment env;
        try
        {
            // The timeout wraps BOTH steps: a profile-lock stall most often hangs environment
            // creation, not just the core attach — either must surface the same guidance, never
            // a bare "The operation has timed out."
            env = await CoreWebView2Environment.CreateAsync(null, options.ProfileDirectory, envOptions)
                .WaitAsync(options.InitTimeout).ConfigureAwait(true);
            await web.EnsureCoreWebView2Async(env).WaitAsync(options.InitTimeout).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Session browser failed to initialize within {options.InitTimeout.TotalSeconds:0}s. " +
                $"The usual cause is a leftover WebView2 process holding the profile lock " +
                $"('{options.ProfileDirectory}') — end stray msedgewebview2 processes, or delete the folder.");
        }

        var settings = web.CoreWebView2.Settings;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        if (options.MuteAudio)
            web.CoreWebView2.IsMuted = true; // belt-and-suspenders with --mute-audio

        if (options.RequestFilter is { } filter)
            AttachRequestFilter(web.CoreWebView2, env, filter);
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
