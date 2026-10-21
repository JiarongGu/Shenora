using System.Drawing;
using Shenora.Chromium.Host;
using Shenora.Core.Sessions;

namespace Shenora.Chromium;

/// <summary>
/// Inputs for <see cref="ChromiumWindows.CreateWebPanelAsync"/>: a web-content browser embedded as a child of one of the
/// shell's windows (D97), in a profile of the caller's choosing.
/// </summary>
public sealed class ChromiumWebPanelOptions
{
    /// <summary>
    /// The browser's profile, policies and reporting, the same shape every session browser takes (D91): the
    /// <see cref="SessionBrowserOptions.ProfileDirectory"/> is the panel's sign-in and isolation boundary, and its hooks
    /// (dialogs, permissions, new windows, request filter, observed responses, events) drive the embedded content exactly
    /// as they drive an off-screen session.
    /// <para>
    /// ⚠ As for any Chromium session, the profile must be a folder directly inside the shell's own data folder
    /// (<see cref="ChromiumSessionHost.ProfilesDirectory"/>) and not the app's own, the only place CEF keeps a profile
    /// (D91). Point it at the SAME folder a signed-in <see cref="ChromiumBrowserProcess"/> uses to reuse that sign-in —
    /// but never while that process has the folder open, since Chromium allows one process per data folder.
    /// </para>
    /// </summary>
    public required SessionBrowserOptions Session { get; init; }

    /// <summary>The first page to load. Null opens <c>about:blank</c>, and the host navigates it with
    /// <see cref="ChromiumWebPanel.Browser"/>'s <see cref="ISessionBrowser.Navigate"/>.</summary>
    public string? Url { get; init; }

    /// <summary>Where the panel sits in the host window's client area, in the window's client pixels. The host moves and
    /// resizes it afterwards with <see cref="ChromiumWebPanel.SetBounds"/>.</summary>
    public Rectangle Bounds { get; init; }

    /// <summary>What shows before the page paints, so a dark page never flashes white. Null is the system's.</summary>
    public Color? BackgroundColor { get; init; }

    /// <summary>
    /// Keep cookies that have no expiry across a restart (default true), which is how a site's sign-in usually survives
    /// one. Cookies WITH an expiry persist regardless, because the profile has its own on-disk cache; this is the
    /// request context's <c>persist_session_cookies</c> for the rest.
    /// </summary>
    public bool PersistSessionCookies { get; init; } = true;

    /// <summary>
    /// A proxy for this profile's requests, as a Chromium proxy server spec (<c>socks5://127.0.0.1:1080</c>,
    /// <c>http://host:port</c>). Null routes directly. Set through the profile's own request context, so it binds to this
    /// panel's profile and no other.
    /// </summary>
    public string? ProxyServer { get; init; }

    /// <summary>Called when the page's renderer or the browser itself is gone for good. Every other failure heals itself
    /// and is only published on <see cref="SessionBrowserOptions.Events"/>.</summary>
    public Action<SessionProcessReport>? OnGone { get; init; }
}

/// <summary>
/// A web-content browser embedded as a child of one of the shell's windows (D97): a CEF browser as a native child of a
/// window the shell owns, running in the shell's own CEF process (not a second <see cref="ChromiumEngine"/>, which cannot
/// coexist), in a caller-chosen profile's request context. The host places it (<see cref="SetBounds"/>) in a region its
/// page reserves and drives it through <see cref="Browser"/>; the host draws its own chrome (address bar, back/forward)
/// in the page. Windows only, today.
/// <para>
/// <see cref="Dispose"/> closes the browser and leaves its parent window open; destroying the parent closes it too.
/// </para>
/// </summary>
public sealed class ChromiumWebPanel : IDisposable
{
    private readonly ChromiumWebPanelBrowser _panel;
    private readonly CefUiDispatcher _ui;
    private int _disposed;

    internal ChromiumWebPanel(ChromiumWebPanelBrowser panel, CefUiDispatcher ui)
    {
        _panel = panel;
        _ui = ui;
    }

    // The async orchestration, off ChromiumWindows' unsafe class (which cannot await): the host window is resolved on the
    // UI thread, the browser-view panel is made in the profile's request context and floated over the window, and the
    // first page is navigated.
    internal static async Task<ChromiumWebPanel> CreateAsync(CefUiDispatcher ui, ChromiumSessionHost sessionHost, string windowName,
        Func<ChromiumWindow?> host, ChromiumWebPanelOptions options, CancellationToken cancellationToken)
    {
        var window = await ui.InvokeAsync(() => Task.FromResult(host()), cancellationToken).ConfigureAwait(false);
        if (window is null) throw new InvalidOperationException($"No window named '{windowName}' is open to host a web panel.");
        var panel = await sessionHost.CreateChildAsync(options.Session, options.OnGone, window, options.Bounds,
            options.BackgroundColor, options.PersistSessionCookies, options.ProxyServer, cancellationToken).ConfigureAwait(false);
        if (options.Url is { } url) await ui.InvokeAsync(() => panel.Browser.Navigate(url), cancellationToken).ConfigureAwait(false);
        return new ChromiumWebPanel(panel, ui);
    }

    /// <summary>
    /// The embedded browser, driven exactly as any session browser is: <see cref="ISessionBrowser.Navigate"/>,
    /// <see cref="ISessionBrowser.ExecuteScriptAsync"/>, <see cref="ISessionBrowser.GetCookiesAsync"/>,
    /// <see cref="ISessionBrowser.NavigationCompleted"/> and the rest (D91). Its members run on the shell's UI thread,
    /// which is the thread the app runs on.
    /// </summary>
    public ISessionBrowser Browser => _panel.Browser;

    /// <summary>
    /// 0 for a web panel. The panel is a CEF Views overlay, placed through <see cref="SetBounds"/> in the window's view
    /// hierarchy — it has no native child window of its own to hand out, unlike the raw <c>WS_CHILD</c> browser this once
    /// was (D97). Kept for source compatibility; nothing a consumer needs reads it. Any thread.
    /// </summary>
    public nint WindowHandle => _panel.Browser.WindowHandle;

    /// <summary>Completes once the browser has closed, however it closed.</summary>
    public Task Closed => _panel.Browser.Closed;

    /// <summary>
    /// Place the panel in its host window, in the window's client pixels — the same coordinates this took before (D97).
    /// Applied on the shell's UI thread as a CEF Views overlay (converted to the window's DIP there), so it is a post, not
    /// an immediate move. Any thread.
    /// </summary>
    /// <param name="bounds">The panel's rectangle, in the host window's client pixels.</param>
    public void SetBounds(Rectangle bounds) => _ui.Queue(() => _panel.SetBounds(bounds));

    /// <summary>
    /// Show or hide the panel in place, without moving it off-screen. A hide stays until the next
    /// <see cref="SetVisible"/>(true) or <see cref="SetBounds"/> (placing the panel shows it). Applied on the shell's
    /// UI thread, like <see cref="SetBounds"/>. Any thread.
    /// </summary>
    /// <param name="visible">True shows the panel; false hides it where it is.</param>
    public void SetVisible(bool visible) => _ui.Queue(() => _panel.SetVisible(visible));

    /// <summary>Navigate back in this panel's history, if it can. The shell's UI thread.</summary>
    public void GoBack() => _panel.Browser.GoBack();

    /// <summary>Navigate forward in this panel's history, if it can. The shell's UI thread.</summary>
    public void GoForward() => _panel.Browser.GoForward();

    /// <summary>Reload the current page. The shell's UI thread.</summary>
    public void Reload() => _panel.Browser.Reload();

    /// <summary>Stop the current load. The shell's UI thread.</summary>
    public void Stop() => _panel.Browser.Stop();

    /// <summary>Give the page keyboard focus. The shell's UI thread.</summary>
    public void Focus() => _panel.Browser.Focus();

    /// <summary>Close the panel's browser; <see cref="Closed"/> completes once it has. Any thread; idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _ui.Queue(_panel.Close);
    }
}
