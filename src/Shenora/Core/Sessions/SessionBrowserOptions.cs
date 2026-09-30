using Microsoft.Extensions.Logging;
using Shenora.Core.Events;

namespace Shenora.Core.Sessions;

/// <summary>
/// Configuration for a session's browser, the same on every shell. A <c>record</c>, so a session can <c>with</c>-override
/// the one or two fields it OWNS and inherit the rest by construction; a shell's own options derive from it for what
/// only its engine has (<c>WebView2SessionBrowserOptions</c> in <c>Shenora.Windows</c>).
/// </summary>
public record SessionBrowserOptions
{
    /// <summary>
    /// The persistent profile this browser runs in, and the session's ISOLATION boundary: an interactive session scopes
    /// it per provider and per sub-account (a SECURITY boundary, see <see cref="InteractiveSession"/>); wiping it
    /// discards the session for real.
    /// <para>
    /// ⚠ In the Chromium shell it must lie inside the shell's own data folder (<c>ChromiumHostOptions.UserDataFolder</c>),
    /// where CEF keeps every profile (D91).
    /// </para>
    /// </summary>
    public required string ProfileDirectory { get; init; }

    /// <summary>
    /// True for an OFF-SCREEN browser: Chromium's occlusion and background-timer throttling would otherwise pause the
    /// page's script while nothing shows.
    /// </summary>
    public bool KeepAliveInBackground { get; init; }

    /// <summary>Mute all audio and block autoplay without a user gesture (default true).</summary>
    public bool MuteAudio { get; init; } = true;

    /// <summary>
    /// Request-layer filter: return true to BLOCK a subresource request (answered with an empty 403). Receives the
    /// request URI and the page's current URI. Runs on the UI thread per request, so keep it fast. ⚠ The page URI is
    /// null before the first navigation commits; NEVER block then, or the page's own document can't load.
    /// <para>
    /// 🔴 <b>It FAILS OPEN.</b> A throwing filter lets the request through (the first throw is logged), because it runs
    /// on every subresource of every page. Never make it the only thing between a session and an internal host.
    /// </para>
    /// </summary>
    public Func<Uri, Uri?, bool>? RequestFilter { get; init; }

    /// <summary>
    /// Budget for making the browser; normal is a few seconds. A profile folder still LOCKED by an orphaned browser
    /// process otherwise hangs it FOREVER.
    /// </summary>
    public TimeSpan InitTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Diagnostics. Null = silent.</summary>
    public ILogger? Log { get; init; }

    /// <summary>
    /// Where to publish what the browser reports (types and payloads: <see cref="SessionEvents"/>); null = publish
    /// nothing. The SCOPE is not here: one options object is shared across a pool's instances, so the session's
    /// identity is per instance (<see cref="SessionBrowserDefinition.Scope"/>).
    /// </summary>
    public IEventBus? Events { get; init; }

    /// <summary>
    /// Which responses raise <see cref="SessionEvents.ResponseReceived"/>. Null (the default) = NONE. A predicate rather
    /// than a bool because this event is per-SUBRESOURCE: it is both the on-switch and the cost control,
    /// <c>uri =&gt; uri.Host == "login.example.com"</c> pays for that host only. ⚠ Not <see cref="RequestFilter"/>,
    /// whose polarity is the opposite: that one answers "block this?", this one "report this?".
    /// </summary>
    public Func<Uri, bool>? ObserveResponse { get; init; }

    /// <summary>
    /// How many characters of an observed response's BODY to include in <see cref="SessionResponse.BodySample"/>.
    /// 0 (the default) = do not read bodies at all; separate from <see cref="ObserveResponse"/>, which decides WHICH
    /// responses are reported.
    /// <para>
    /// ⚠ A SAMPLE, not a download: clamped to 1,048,576 CHARACTERS, which is about <b>2 MB</b> of memory because a .NET
    /// <c>char</c> is two bytes. The buffer is allocated at the clamped size per observed response.
    /// </para>
    /// </summary>
    public int ResponseBodySample { get; init; }

    /// <summary>
    /// The page opened an <c>alert</c>/<c>confirm</c>/<c>prompt</c>. Null = DISMISS it.
    /// <para>
    /// ⚠ <b>The DEFAULT is the fix here.</b> Left unanswered, the browser shows its OWN modal, and a session's browser
    /// is off-screen, so nothing can ever dismiss it and the page stops for good.
    /// </para>
    /// </summary>
    public Action<SessionScriptDialog>? OnScriptDialog { get; init; }

    /// <summary>
    /// The server asked for HTTP credentials (a 401 challenge). Null = CANCEL, which lets the load fail normally. ⚠ The
    /// same wedge as <see cref="OnScriptDialog"/>: unanswered, the browser raises its own prompt against a window nobody
    /// can see.
    /// </summary>
    public Action<SessionAuthRequest>? OnAuthRequest { get; init; }

    /// <summary>
    /// The server asked for a CLIENT certificate. Null = CANCEL. ⚠ The third of the blocking three, and the one easiest
    /// to miss: mutual TLS is rare until an app meets an intranet that requires it, and then the session simply stops.
    /// </summary>
    public Action<SessionCertificateRequest>? OnCertificateRequest { get; init; }

    /// <summary>The page tried to open a new window (<c>window.open</c>, <c>target="_blank"</c>). Null = SUPPRESS it.</summary>
    public Action<SessionWindowRequest>? OnWindowRequest { get; init; }

    /// <summary>
    /// The page asked for a capability (camera, microphone, geolocation, clipboard read…). Null = DENY: an invisible page
    /// cannot meaningfully prompt, and an unanswered request stalls whatever asked.
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
/// <param name="Kind">The engine's name for what was asked (<c>Camera</c>, <c>ClipboardRead</c>, …).</param>
/// <param name="Uri">The page that asked.</param>
/// <param name="UserInitiated">True when a real gesture triggered it.</param>
public sealed record SessionPermissionRequest(string Kind, string Uri, bool UserInitiated)
{
    /// <summary>True = grant. False (the default) = deny.</summary>
    public bool Allow { get; set; }
}

/// <summary>A script dialog the page opened, and what to do about it. Mutate and return; nothing is awaited.</summary>
/// <param name="Kind">Alert, confirm, prompt or beforeunload, as the engine reports it.</param>
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
    /// 🔴 <b>REDACTED, because a record's generated <c>ToString()</c> prints every property.</b> This one holds a
    /// password, and the generated version would put it in any log line, exception message or debugger watch that
    /// formats the object.
    /// </summary>
    public override string ToString() =>
        $"{nameof(SessionAuthRequest)} {{ Uri = {Uri}, Challenge = {Challenge}, "
        + $"UserName = {(UserName is null ? "null" : "***")}, Password = {(Password is null ? "null" : "***")} }}";
}

/// <summary>A client-certificate request. Select one, or leave it to cancel.</summary>
/// <param name="Host">The host asking.</param>
/// <param name="Port">The port it asked on.</param>
/// <param name="Subjects">The certificate subjects on offer, in the engine's order.</param>
public sealed record SessionCertificateRequest(string Host, int Port, IReadOnlyList<string> Subjects)
{
    /// <summary>
    /// Index into <see cref="Subjects"/> to present that certificate. Null (the default) CANCELS, which fails the load
    /// rather than hanging it.
    /// </summary>
    public int? SelectedIndex { get; set; }
}

/// <summary>
/// The decisions every shell's session browser makes the same way, for the shells to call (D91). Each is a rule
/// that was wrong once; one copy keeps both engines right.
/// </summary>
public static class SessionPolicy
{
    /// <summary>The ceiling on <see cref="SessionBrowserOptions.ResponseBodySample"/>: the buffer is allocated per
    /// observed response.</summary>
    public const int MaxBodySample = 1024 * 1024;

    /// <summary>
    /// Ask a hook what to do, with the SAFE DEFAULT when there is no hook or the hook throws. ⚠ A THROWING hook must land
    /// on the default, not escape: these run inside a browser's callback, and the default is what keeps the page moving
    /// (dismiss, cancel) or refusing (deny, suppress). A hook that set a field and then threw meant it.
    /// </summary>
    /// <param name="hook">The app's handler, or null.</param>
    /// <param name="args">The request, which the hook mutates in place.</param>
    /// <param name="onError">Receives a throw from the hook.</param>
    public static T Decide<T>(Action<T>? hook, T args, Action<Exception>? onError = null)
    {
        if (hook is null) return args;
        try
        {
            hook(args);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
        return args;
    }

    /// <summary>
    /// The request filter's decision (<see cref="SessionBrowserOptions.RequestFilter"/>): true when the request is to be
    /// answered with the 403. A URI that cannot be parsed passes. Only an http(s) page is a page to compare against, so
    /// the page's own next document is never taken for a third party before the first navigation commits or on a reset
    /// browser's <c>about:blank</c>. A throwing filter ALLOWS the request (fail-open) and reports the throw.
    /// </summary>
    /// <param name="requestUri">The request's URI as the engine gives it.</param>
    /// <param name="pageSource">The page's current address; may be empty or <c>about:blank</c>.</param>
    /// <param name="filter">The app's policy: (request, page) → block?</param>
    /// <param name="onFilterError">Receives a throw from <paramref name="filter"/>: the only sign that the app's blocking
    /// policy stopped blocking.</param>
    public static bool ShouldBlockRequest(string? requestUri, string? pageSource, Func<Uri, Uri?, bool> filter,
                                          Action<Exception>? onFilterError = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var request)) return false;
        var pageUri = Uri.TryCreate(pageSource, UriKind.Absolute, out var p) && p.Scheme is "http" or "https" ? p : null;
        try
        {
            return filter(request, pageUri);
        }
        catch (Exception ex)
        {
            onFilterError?.Invoke(ex);
            return false;
        }
    }
}
