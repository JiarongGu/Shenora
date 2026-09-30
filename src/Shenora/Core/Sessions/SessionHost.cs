using System.Drawing;
using Shenora.Core.Shell;

namespace Shenora.Core.Sessions;

/// <summary>
/// The shell's host for the sessions (D91): it makes the browsers they drive, WebView2 in <c>Shenora.Windows</c> and
/// CEF in <c>Shenora.Chromium</c>. <see cref="RenderSessionPool"/>, <see cref="StreamingSession"/> and
/// <see cref="InteractiveSession"/> are written once over it; a shell registers one, and an app hands it to each
/// session's options as their <c>Host</c>.
/// <para>
/// ⚠ Everything here, and every member of what it makes, runs on <see cref="Ui"/>. The sessions marshal there
/// themselves.
/// </para>
/// </summary>
public interface ISessionHost
{
    /// <summary>The thread every browser this makes is created and driven on: the shell's UI thread.</summary>
    IUiDispatcher Ui { get; }

    /// <summary>
    /// A context for browsers that share one profile (a pool's), as Chromium's own browser context is a profile in use:
    /// WebView2 serves them from one environment and CEF from one request context. Owned by whoever made it, never by
    /// the process: disposing it lets the profile go, so its browser process can exit and its folder can be cleared
    /// (<see cref="InteractiveSession.ClearProfile"/>).
    /// </summary>
    ISessionBrowserContext CreateContext();

    /// <summary>
    /// Make a browser for a session, on <see cref="Ui"/>: off-screen, or a visible window when
    /// <see cref="SessionBrowserDefinition.VisibleTitle"/> is set. Its policies (<see cref="SessionBrowserOptions"/>'s hooks,
    /// the request filter) and its events are wired before it is handed over.
    /// </summary>
    /// <param name="definition">The browser, its profile and where its events go.</param>
    /// <param name="cancellationToken">Abandons the wait. A browser that finishes being made after it is abandoned is
    /// torn down, never handed to nobody.</param>
    Task<ISessionBrowser> CreateAsync(SessionBrowserDefinition definition, CancellationToken cancellationToken);

    /// <summary>
    /// Open an interactive session's window with a browser in it, on <see cref="Ui"/>, modal to the app's main
    /// window while it shows. Completes once the window is up and its browser made; <see cref="ISessionWindow.Closed"/>
    /// completes when it is gone.
    /// </summary>
    Task<ISessionWindow> OpenWindowAsync(SessionWindowDefinition definition, CancellationToken cancellationToken);
}

/// <summary>The context of browsers over one profile (see <see cref="ISessionHost.CreateContext"/>). Dispose it to let
/// the profile go.</summary>
public interface ISessionBrowserContext : IDisposable;

/// <summary>What <see cref="ISessionHost.CreateAsync"/> makes.</summary>
public sealed class SessionBrowserDefinition
{
    /// <summary>The profile, the policies and the reporting.</summary>
    public required SessionBrowserOptions Options { get; init; }

    /// <summary>
    /// The scope everything this browser publishes on <see cref="SessionBrowserOptions.Events"/> goes out under,
    /// read at each publish: a pooled browser outlives the lease that borrowed it. ⚠ Null from it is an unscoped
    /// broadcast.
    /// </summary>
    public required Func<string?> Scope { get; init; }

    /// <summary>The context it shares a profile in, or null for a browser of its own.</summary>
    public ISessionBrowserContext? Context { get; init; }

    /// <summary>Called when the page's renderer or the browser itself is gone for good. Every other failure (a GPU
    /// process restart, a busy renderer) heals itself and is only published.</summary>
    public Action<SessionProcessReport>? OnGone { get; init; }

    /// <summary>The off-screen surface's size, in device-independent pixels: the page's window size until something
    /// emulates another.</summary>
    public Size ViewportSize { get; init; } = new(1280, 1600);

    /// <summary>A visible window with this title instead of an off-screen browser: a development mode, to watch a
    /// session work. Null means off-screen.</summary>
    public string? VisibleTitle { get; init; }
}

/// <summary>What <see cref="ISessionHost.OpenWindowAsync"/> opens.</summary>
public sealed class SessionWindowDefinition
{
    /// <summary>The browser in the window.</summary>
    public required SessionBrowserOptions Options { get; init; }

    /// <summary>The scope the window's browser publishes under.</summary>
    public required Func<string?> Scope { get; init; }

    /// <summary>The window's title.</summary>
    public string Title { get; init; } = "Session";

    /// <summary>Its content size, in device-independent pixels.</summary>
    public Size ClientSize { get; init; } = new(680, 780);

    /// <summary>Its minimum size, in device-independent pixels.</summary>
    public Size MinimumSize { get; init; } = new(300, 340);

    /// <summary>What shows before the page paints. Null is the system's.</summary>
    public Color? BackColor { get; init; }

    /// <summary>On screen from the start (true), or made and kept off-screen until <see cref="ISessionWindow.Reveal"/>
    /// (false), which also makes it neither modal nor in the taskbar until then.</summary>
    public bool Revealed { get; init; } = true;
}

/// <summary>
/// One browser a session drives. Every member runs on the shell's UI thread (<see cref="ISessionHost.Ui"/>).
/// </summary>
public interface ISessionBrowser
{
    /// <summary>Where the page is now: empty before the first navigation commits.</summary>
    string Source { get; }

    /// <summary>Start a navigation; <see cref="NavigationCompleted"/> says when the document has loaded.</summary>
    void Navigate(string url);

    /// <summary>A main-frame navigation finished, successfully or not.</summary>
    event Action<SessionNavigationResult>? NavigationCompleted;

    /// <summary>
    /// Asked before every main-frame navigation, with where it is going: true cancels it. The browser guards the call,
    /// and a throw cancels.
    /// </summary>
    Func<string, bool>? CancelNavigation { get; set; }

    /// <summary>Cancel the browser's own downloads, so the app fetches what the page asked for with its own progress.
    /// They are still published as <see cref="SessionEvents.DownloadStarting"/>.</summary>
    bool CancelDownloads { get; set; }

    /// <summary>Run script on the page and return its value JSON-encoded, as <c>JSON.stringify</c> would.</summary>
    Task<string?> ExecuteScriptAsync(string javaScript);

    /// <summary>Call a DevTools protocol method and return its result as JSON. Throws when the protocol refuses it.</summary>
    Task<string> CallDevToolsAsync(string method, string parametersJson);

    /// <summary>Receive a DevTools protocol event's parameters as JSON until the returned handle is disposed.</summary>
    IDisposable OnDevToolsEvent(string eventName, Action<string> onEvent);

    /// <summary>The cookies a request to <paramref name="origin"/> would carry.</summary>
    Task<IReadOnlyList<SessionCookie>> GetCookiesAsync(string origin);

    /// <summary>Give the page keyboard focus.</summary>
    void Focus();

    /// <summary>Tear the browser down, and a window made for it alone with it.</summary>
    void Close();
}

/// <summary>
/// An interactive session's window (see <see cref="ISessionHost.OpenWindowAsync"/>). Every member runs on the
/// shell's UI thread.
/// </summary>
public interface ISessionWindow
{
    /// <summary>The browser in it.</summary>
    ISessionBrowser Browser { get; }

    /// <summary>
    /// Asked as the window is about to close, with whether a person (or code acting for one) asked, rather than the app
    /// or the OS ending: false keeps it open. Null lets every close through.
    /// </summary>
    Func<bool, bool>? Closing { get; set; }

    /// <summary>Whether the window is on screen.</summary>
    bool IsRevealed { get; }

    /// <summary>Bring a window kept off-screen on screen: centred on its display, in front, the page focused.
    /// Idempotent.</summary>
    void Reveal();

    /// <summary>Size the window's content to a box the page measured, in CSS pixels, within its display.</summary>
    void FitToContent(int cssWidth, int cssHeight);

    /// <summary>Close the window, past <see cref="Closing"/>.</summary>
    void Close();

    /// <summary>Completes when the window is gone, and with it the hold its browser had on the profile.</summary>
    Task Closed { get; }
}
