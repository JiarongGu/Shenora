using System.Drawing;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;
using Shenora.Core.WebView;

namespace Shenora.Chromium;

/// <summary>
/// Inputs for <see cref="ChromiumHostExtensions.UseChromium"/>: the Chromium shell of the kit's own (D82),
/// on CEF's Views framework. The names match the WebView2 host's where the concept is the same
/// (<c>DevUrl</c>, <c>VirtualHost</c>, <c>IsDevelopment</c>, <c>UserDataFolder</c>), so changing engines does not
/// change vocabulary.
/// </summary>
public sealed class ChromiumHostOptions
{
    /// <summary>The main window.</summary>
    public ChromiumWindowOptions Window { get; init; } = new();

    /// <summary>
    /// The folder the app's bundle is served from, at <c>https://{VirtualHost}/</c>. Nothing outside it is
    /// reachable. Required unless every page comes from <see cref="DevUrl"/>.
    /// </summary>
    public string? ContentRoot { get; init; }

    /// <summary>
    /// The app's bundle from a provider — an <see cref="EmbeddedResourceProvider"/> serves one built into the app's
    /// assembly — at <c>https://{VirtualHost}/</c>, as <see cref="ContentRoot"/> serves a folder. One or the other. A
    /// file comes whole: byte ranges are <see cref="ContentRoot"/>'s.
    /// </summary>
    public IWebViewResourceProvider? ResourceProvider { get; init; }

    /// <summary>
    /// What a page load that finds nothing shows: this path in the bundle, answered with status 404. Null, or a bundle
    /// without it, shows the kit's page (<see cref="WebViewResourceResponse.NotFoundDocument"/>). A <c>fetch()</c>, a
    /// script or an image that finds nothing still gets the plain 404.
    /// </summary>
    public string? NotFoundPage { get; init; } = "404.html";

    /// <summary>The page in development (a dev server such as Vite). Ignored outside development.</summary>
    public string? DevUrl { get; init; }

    /// <summary>The host of the app's own origin. The page is served from <c>https://{VirtualHost}/</c>.</summary>
    public string VirtualHost { get; init; } = "app.local";

    /// <summary>Development mode. Null means the application's own environment decides.</summary>
    public bool? IsDevelopment { get; init; }

    /// <summary>
    /// A remote-debugging port, honoured in development only. Outside it the shell also disables command-line
    /// switches, because a port on the app's own command line would otherwise reach the page holding the
    /// bridge (measured).
    /// </summary>
    public int DevToolsPort { get; init; }

    /// <summary>Where Chromium keeps its profile, cache and log. Null means the application's <c>chromium</c> data
    /// area.</summary>
    public string? UserDataFolder { get; init; }

    /// <summary>The language of the page (<c>navigator.language</c>, and <c>Intl</c>'s default) and of Chromium's own
    /// menus and dialogs, such as <c>zh-CN</c>. Null means the OS's UI language, as WebView2 follows it.</summary>
    public string? Locale { get; init; }

    /// <summary>What the ready handshake tells the page this shell is and can do (D36).</summary>
    public ShellInfo? Shell { get; init; }

    /// <summary>A tray icon: its menu, and whether closing the main window hides it there. Null means none. The app
    /// reaches it as <see cref="ChromiumTray"/>.</summary>
    public ChromiumTrayOptions? Tray { get; init; }

    /// <summary>A splash over the main window's render area until the app's boot work and the page are ready, drawn by the
    /// operating system rather than Chromium, with the window's own frame live around it; and, with a
    /// <see cref="ChromiumSplashOptions.Card"/>, a card from the moment the app runs until the window exists. Null (the
    /// default) shows none.</summary>
    public ChromiumSplashOptions? Splash { get; init; }

    /// <summary>The app's colour scheme as it starts: whether it follows the OS's light or dark setting
    /// (<see cref="ColorScheme.System"/>, the default) or is held at one. It is the app's own Chromium, so the setting is
    /// Chromium's: the page's <c>prefers-color-scheme</c>, Chrome's own UI, a window's frame on Windows, and the splash
    /// all follow it. Pass the user's saved choice here; change it later through <see cref="IColorScheme"/>, and save it
    /// on its <see cref="IColorScheme.Changed"/>.</summary>
    public ColorScheme ColorScheme { get; init; } = ColorScheme.System;

    /// <summary>When the launcher's startup screen (<see cref="IStartupScreen"/>) is closed: once the splash card, or
    /// with none the main window, is on screen (<see cref="StartupScreenMode.FirstWindow"/>, the default), or by the app
    /// (<see cref="StartupScreenMode.Manual"/>). Nothing happens without a launcher's screen.</summary>
    public StartupScreenMode StartupScreen { get; init; } = StartupScreenMode.FirstWindow;

    /// <summary>
    /// One running instance per install, on by default: a later launch has the running app bring its main window
    /// forward (shown, restored, or opened again if it was closed), hands it its arguments
    /// (<see cref="SingleInstanceHostOptions.OnActivated"/>), and exits. Whether the window then takes the foreground
    /// is the OS's to allow on Linux and macOS; on Windows the later launch hands it over.
    /// <para>
    /// Null turns the gate off, and Chromium's own rule still stands: one process per <see cref="UserDataFolder"/>, so
    /// a second instance needs a folder of its own, and a launch that shares one is handed to the running app, which
    /// brings its window forward with NO arguments, since Chromium does not pass the app's on.
    /// </para>
    /// </summary>
    public SingleInstanceHostOptions? SingleInstance { get; init; } = new();

    /// <summary>
    /// The main window's size, position and maximized state, saved as it closes and restored as it opens; null (the
    /// default) keeps none. Device-independent pixels, as the window's own <see cref="ChromiumWindowOptions.Width"/>
    /// and <see cref="ChromiumWindowOptions.Height"/>, which it opens at, centred, when nothing is saved. A saved
    /// position no display can show any more is dropped, and the window is centred. The options'
    /// <see cref="WindowStateOptions.MinWidth"/> and <see cref="WindowStateOptions.MinHeight"/> are the window's
    /// minimum size while it runs too, so it never reopens larger than it was left.
    /// </summary>
    public WindowStateHostOptions? WindowState { get; init; }

    /// <summary>
    /// Receives every unhandled exception, as the WinForms shell's <c>WinFormsBootstrapOptions</c> does: work posted to
    /// the UI thread (an <c>async void</c> continuation there included), which the loop survives; any other thread,
    /// where the process is usually ending (<see cref="UnhandledExceptionReport.IsTerminating"/>); and a faulted task
    /// nobody observed. Log it here. A throwing handler is swallowed. No dialog is shown; what the user sees is the
    /// app's.
    /// </summary>
    public Action<UnhandledExceptionReport>? OnUnhandledException { get; init; }

    /// <summary>Mark an unobserved faulted task observed, so it never escalates; it still reaches
    /// <see cref="OnUnhandledException"/>.</summary>
    public bool ObserveUnobservedTaskExceptions { get; init; } = true;

    /// <summary>
    /// Let the app's sessions that render off-screen run here (<c>RenderSessionPool</c>, <c>StreamingSession</c>, D91):
    /// CEF's windowless rendering, which it starts with or not at all. Off by default, as CEF advises for an app that
    /// does not use it, since it can cost rendering performance; a session asked for without it is refused.
    /// </summary>
    public bool OffscreenSessions { get; init; }
}

/// <summary>A Chromium window: CEF's own window around one browser view (D82).</summary>
public sealed class ChromiumWindowOptions
{
    /// <summary>The window title. Null means the page's own title.</summary>
    public string? Title { get; init; }

    /// <summary>The initial width, in device-independent pixels.</summary>
    public int Width { get; init; } = 1200;

    /// <summary>The initial height, in device-independent pixels.</summary>
    public int Height { get; init; } = 800;

    /// <summary>
    /// No native frame: the page draws its own title bar, and its <c>-webkit-app-region: drag</c> regions move
    /// the window natively.
    /// </summary>
    public bool FramelessChrome { get; init; } = true;

    /// <summary>What shows before the page paints, so a dark page never flashes white.</summary>
    public Color? BackgroundColor { get; init; }

    /// <summary>
    /// Whether the window plays the system's animations as it opens and closes (<see cref="WindowAnimations.System"/>,
    /// the default), or appears and goes at once. On Windows minimize and maximize go with them; on Linux the window
    /// manager decides. Per window: one opened with <see cref="ChromiumWindows.Open"/> takes its own options' value,
    /// not the main window's. Off, a splash's cover reaches a frameless window sooner as it appears, on Windows.
    /// </summary>
    public WindowAnimations Animations { get; init; } = WindowAnimations.System;

    /// <summary>The page to open, relative to the app's origin. Null means its root.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// The window paints the caption buttons itself, as the system does, over the rectangles the page reserves
    /// with <c>SET_CAPTION_BUTTONS</c>: the platform's glyphs and colours, repainted as the pointer moves. The page
    /// draws nothing there; an idle button is transparent, so the page's title bar shows through. On Windows.
    /// <para>
    /// On macOS the window shows the system's own traffic lights at the top left of the frameless window instead, and
    /// the page leaves that corner clear; <c>SET_CAPTION_BUTTONS</c> and the theme do not apply there.
    /// </para>
    /// <para>
    /// Light or dark follows the page's <c>SET_THEME</c>. Until the page sends one it is the system's app theme, read
    /// as the window opens and not followed afterwards, so a page whose theme can differ from the system's sends it.
    /// </para>
    /// <para>Requires <see cref="FramelessChrome"/>: a framed window has the system's own buttons.</para>
    /// </summary>
    public bool NativeCaptionButtons { get; init; }

    /// <summary>
    /// This window's size, position and maximized state, saved as it closes and restored as it opens, as the main
    /// window's are through <see cref="ChromiumHostOptions.WindowState"/>; null (the default) keeps none. For a window
    /// opened with <see cref="ChromiumWindows.Open"/>, with one store per window name (a
    /// <see cref="JsonFileWindowStateStore"/> per file, say). The main window keeps its through
    /// <see cref="ChromiumHostOptions.WindowState"/> and refuses this.
    /// </summary>
    public IWindowStateStore? StateStore { get; init; }

    /// <summary>The minimum size and the visibility rule for <see cref="StateStore"/>. Null means the defaults, whose
    /// minimum is 800 × 600: a smaller window sets its own.</summary>
    public WindowStateOptions? StateOptions { get; init; }

    /// <summary>Refuse a combination that cannot work, where the caller can see why.</summary>
    internal void Validate(string parameter)
    {
        if (NativeCaptionButtons && !FramelessChrome)
            throw new ArgumentException($"{nameof(NativeCaptionButtons)} requires {nameof(FramelessChrome)}: a framed window has the system's own caption buttons.", parameter);
    }
}
