namespace Shenora.Chromium.Serving;

/// <summary>Where one request goes. Decided before any byte is read, from facts CEF supplies.</summary>
internal enum ChromiumRoute
{
    /// <summary>Not the app's: Chromium's own network stack answers it.</summary>
    Network,

    /// <summary>The app's origin: the bundle, then the app's interceptor pipeline on a miss (D45's order).</summary>
    Bundle,

    /// <summary>The page's IPC post, from the app's own page.</summary>
    Ipc,

    /// <summary>Aimed at the IPC route by anything else, and answered with a fixed refusal.</summary>
    Refused,

    /// <summary>The dev server's top-level document, fetched by the shell so it can be marked (D83).</summary>
    DevDocument,
}

/// <summary>The origins a Chromium window serves: the app's own, and the dev server's in development.</summary>
internal sealed record ChromiumOrigins(Uri App, Uri? Dev, string IpcPath)
{
    public static ChromiumOrigins For(string virtualHost, string? devUrl, bool isDevelopment, string ipcPath = ChromiumTransport.DefaultIpcPath) =>
        new(new Uri($"https://{virtualHost}/"), isDevelopment && devUrl is not null ? new Uri(devUrl) : null, ipcPath);
}

/// <summary>
/// The routing decision, kept pure so its security rules are tested without CEF.
/// <para>
/// 🔴 The IPC route is answered only for a POST, from the app's OWN browser, from its MAIN frame, and
/// initiated by the SAME origin. "Main frame" alone is not enough: a page in another browser that shares
/// the handler can make a cross-origin, no-cors POST, and a missing initiator is refused rather than
/// trusted.
/// </para>
/// </summary>
internal static class ChromiumRouting
{
    public static ChromiumRoute Classify(Uri url, string method, bool fromAppBrowser, bool isMainFrame, bool isNavigation,
        string? initiator, ChromiumOrigins origins)
    {
        foreach (var origin in new[] { origins.App, origins.Dev })
        {
            if (origin is null || !SameOrigin(url, origin)) continue;
            if (string.Equals(url.AbsolutePath, origins.IpcPath, StringComparison.Ordinal))
            {
                var trusted = string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
                              && fromAppBrowser && isMainFrame && InitiatedBy(initiator, origin);
                return trusted ? ChromiumRoute.Ipc : ChromiumRoute.Refused;
            }
            if (origin == origins.App) return ChromiumRoute.Bundle;
            return isNavigation && isMainFrame && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                ? ChromiumRoute.DevDocument
                : ChromiumRoute.Network;
        }
        return ChromiumRoute.Network;
    }

    /// <summary>
    /// True when the page's MAIN frame must not navigate to <paramref name="url"/>: a local file. Chromium opens a
    /// file dropped anywhere the page did not claim, which navigates the app's own window away from the app
    /// (measured). No app page means to leave for a file on disk.
    /// </summary>
    public static bool RefusesNavigation(string url, bool isMainFrame) =>
        isMainFrame && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    /// <summary>CEF's <c>CEF_PERMISSION_TYPE_CLIPBOARD</c> flag (a test pins it to the generated enum).</summary>
    public const uint ClipboardPermission = 1u << 4;

    /// <summary>
    /// The answer to a permission prompt, and there must always be one: an Alloy page's prompt left to CEF is
    /// IGNORED, and the page's promise never settles (measured: a clipboard read and a notification request
    /// both hung). The WebView2 shell's policy, so an app sees one: a clipboard read from the app's own page
    /// is allowed, and everything else is denied.
    /// </summary>
    /// <param name="requested">The prompt's permission flags.</param>
    /// <param name="requestingOrigin">The origin CEF names, e.g. <c>https://app.local</c>.</param>
    /// <param name="origins">The app's origins.</param>
    public static bool AllowsPermission(uint requested, string? requestingOrigin, ChromiumOrigins origins) =>
        requested == ClipboardPermission
        && Uri.TryCreate(requestingOrigin, UriKind.Absolute, out var from)
        && new[] { origins.App, origins.Dev }.Any(origin => origin is not null && SameOrigin(from, origin));

    private static bool SameOrigin(Uri url, Uri origin) =>
        string.Equals(url.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(url.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
        && url.Port == origin.Port;

    /// <summary>CEF names a request's initiator as an origin (<c>https://app.local</c>); absent means untrusted.</summary>
    private static bool InitiatedBy(string? initiator, Uri origin) =>
        !string.IsNullOrEmpty(initiator) && Uri.TryCreate(initiator, UriKind.Absolute, out var from) && SameOrigin(from, origin);
}
