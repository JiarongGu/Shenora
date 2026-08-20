namespace Shenora.Windows;

/// <summary>
/// What a session browser REPORTS, published on the app's <see cref="Shenora.Core.Events.IEventBus"/>.
/// Events answer "what happened"; the hooks on <see cref="SessionBrowserOptions"/> answer "what should
/// happen" — an event has many subscribers and no return value, a hook exactly one owner and a decision
/// the browser obeys. Subscribe with
/// <c>bus.SubscribeToModule(SessionEvents.Module, sessionId, handler)</c>, or name one
/// <see cref="Shenora.Core.Events.IEventBus.Subscribe(string, string, string, Func{Shenora.Core.Events.EventMessage, Task})"/>
/// type at a time.
/// <para>
/// 🔴 <b>THE SCOPE IS THE SESSION'S ID, and it is not decoration.</b> A pool runs many browsers against
/// ONE options object and ONE bus, so without a scope two concurrent sessions' events are
/// indistinguishable — a subscriber watching for a login would react to another session's redirect. An
/// unscoped emit is a global broadcast and reaches every subscriber, which is only correct when there
/// is exactly one session.
/// </para>
/// </summary>
public static class SessionEvents
{
    /// <summary>The module every session event is published under.</summary>
    public const string Module = "SHENORA.SESSION";

    /// <summary>
    /// A network response arrived — payload <see cref="SessionResponse"/>.
    /// ⚠ <b>OFF unless <see cref="SessionBrowserOptions.ObserveResponse"/> selects it</b>: this is the
    /// only event here that fires per SUBRESOURCE, and building a header list for each of a page's
    /// hundreds of requests is a cost nothing should pay by accident.
    /// </summary>
    public const string ResponseReceived = "RESPONSE_RECEIVED";

    /// <summary>A top-level navigation began — payload <see cref="SessionSource"/>.</summary>
    public const string NavigationStarting = "NAVIGATION_STARTING";

    /// <summary>
    /// A top-level navigation finished, successfully or not — payload
    /// <see cref="SessionNavigationResult"/>. The one that makes a REDIRECT-driven load visible: a page
    /// that bounced somewhere else on its own is otherwise unannounced.
    /// </summary>
    public const string NavigationCompleted = "NAVIGATION_COMPLETED";

    /// <summary>The document exists and is parsed — payload <see cref="SessionSource"/>. What a driver
    /// otherwise polls for with a script.</summary>
    public const string DomContentLoaded = "DOM_CONTENT_LOADED";

    /// <summary>
    /// The address changed WITHOUT a navigation — payload <see cref="SessionSource"/>. The SPA signal: a
    /// <c>history.pushState</c> route change fires no navigation at all, so an app watching only
    /// <see cref="NavigationCompleted"/> never learns the user moved.
    /// </summary>
    public const string SourceChanged = "SOURCE_CHANGED";

    /// <summary>The document title changed — payload <see cref="SessionSource"/>.</summary>
    public const string TitleChanged = "TITLE_CHANGED";

    /// <summary>The page posted a message via <c>chrome.webview.postMessage</c> — payload
    /// <see cref="SessionWebMessage"/>.</summary>
    public const string WebMessage = "WEB_MESSAGE";

    /// <summary>The page began a download — payload <see cref="DownloadHit"/>. Whether the browser's own
    /// download is cancelled is the session type's policy, not this event's.</summary>
    public const string DownloadStarting = "DOWNLOAD_STARTING";

    /// <summary>The page called <c>window.close()</c> — no payload.</summary>
    public const string WindowCloseRequested = "WINDOW_CLOSE_REQUESTED";

    /// <summary>
    /// A browser process died — payload <see cref="SessionProcessReport"/>. Published for EVERY kind,
    /// with <see cref="SessionProcessReport.Terminal"/> saying whether the session is actually dead;
    /// the per-instance <c>onProcessFailed</c> callback still fires for terminal kinds only.
    /// </summary>
    public const string ProcessFailed = "PROCESS_FAILED";
}

/// <summary>
/// Where the browser is, at the moment an event fired — the payload shared by
/// <see cref="SessionEvents.NavigationStarting"/>, <see cref="SessionEvents.DomContentLoaded"/>,
/// <see cref="SessionEvents.SourceChanged"/> and <see cref="SessionEvents.TitleChanged"/>.
/// <para>
/// ⚠ <b>It is a snapshot of NOW, not of the event's subject.</b> On
/// <see cref="SessionEvents.NavigationStarting"/>, <paramref name="Uri"/> is where the browser is
/// GOING while <paramref name="Title"/> is still the outgoing document's — the new page has no title yet.
/// </para>
/// </summary>
/// <param name="Uri">The address, absolute.</param>
/// <param name="Title">The document title; empty before a document has one.</param>
public sealed record SessionSource(string Uri, string Title);

/// <summary>How a top-level navigation ended (<see cref="SessionEvents.NavigationCompleted"/>).</summary>
/// <param name="Uri">Where the browser ended up — read from the browser, so a redirect chain reports
/// its DESTINATION rather than the address the navigation started for.</param>
/// <param name="Success">Whether the load succeeded.</param>
/// <param name="Status">The browser's own error status (<c>Unknown</c> when it succeeded) — the
/// name of a <c>CoreWebView2WebErrorStatus</c>, carried as a string so an app subscribing through the
/// bus needs no WebView2 reference.</param>
public sealed record SessionNavigationResult(string Uri, bool Success, string Status);

/// <summary>
/// A network response (<see cref="SessionEvents.ResponseReceived"/>).
/// <para>
/// 🔴 <b>This is the honest primitive behind "tell me when a cookie changes".</b> Measured against the
/// SDK: <c>CoreWebView2CookieManager</c> raises NO events at all, so there is nothing to forward. A
/// response carries <c>Set-Cookie</c> as it happens, along with the redirect that usually accompanies it.
/// </para>
/// <para>
/// ⚠ <b>What it does NOT see: a cookie set by JS</b> (<c>document.cookie</c>). Read the actual jar with
/// <see cref="SessionController.GetCookiesAsync"/>.
/// </para>
/// </summary>
/// <param name="Uri">The request's address.</param>
/// <param name="StatusCode">The HTTP status code.</param>
/// <param name="ReasonPhrase">The HTTP reason phrase.</param>
/// <param name="Headers">
/// The response headers as reported, in order. ⚠ <b>A LIST, not a dictionary</b>: <c>Set-Cookie</c>
/// legitimately repeats, and a map keyed by name would keep one and silently drop the rest — losing
/// exactly the header this event exists to carry. (That the SDK's enumerator yields a repeated name as
/// separate entries is INFERRED from its shape, not measured.)
/// </param>
/// <param name="BodySample">
/// A bounded prefix of the response body, or empty. Read only when
/// <see cref="SessionBrowserOptions.ResponseBodySample"/> asks for one, and best-effort even then:
/// content already consumed or still streaming yields empty rather than failing the event.
/// </param>
public sealed record SessionResponse(
    string Uri, int StatusCode, string ReasonPhrase, IReadOnlyList<KeyValuePair<string, string>> Headers,
    string BodySample);

/// <summary>A message the page posted (<see cref="SessionEvents.WebMessage"/>).</summary>
/// <param name="Message">The message as a string; a page posting a non-string is not reported.</param>
public sealed record SessionWebMessage(string Message);

/// <summary>A browser process failure (<see cref="SessionEvents.ProcessFailed"/>).</summary>
/// <param name="Kind">The <c>CoreWebView2ProcessFailedKind</c> name, as a string.</param>
/// <param name="Reason">The <c>CoreWebView2ProcessFailedReason</c> name, as a string.</param>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="Terminal">
/// Whether this kind means the SESSION is dead. False for the routine ones — a GPU-driver reset raises a
/// failure on a perfectly live page — so a subscriber can log everything and act on the two that matter.
/// </param>
public sealed record SessionProcessReport(string Kind, string Reason, int ExitCode, bool Terminal);
