using System.Drawing;
using Shenora.Core.Ipc;

namespace Shenora.Chromium;

/// <summary>
/// Inputs for <see cref="ChromiumHostExtensions.UseChromium"/>: the Chromium shell of the kit's own (D82),
/// on CEF's Views framework. The names match the WebView2 host's where the concept is the same
/// (<c>DevUrl</c>, <c>VirtualHost</c>, <c>IsDevelopment</c>), so changing engines does not change vocabulary.
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

    /// <summary>The page in development (a dev server such as Vite). Ignored outside development.</summary>
    public string? DevUrl { get; init; }

    /// <summary>The host of the app's own origin. The page is served from <c>https://{VirtualHost}/</c>.</summary>
    public string VirtualHost { get; init; } = "app.local";

    /// <summary>Development mode. Null means the application's own environment decides.</summary>
    public bool? IsDevelopment { get; init; }

    /// <summary>
    /// A remote-debugging port, honoured in DEVELOPMENT only. 🔴 Outside it the shell also disables command-line
    /// switches, because a port on the app's own command line would otherwise reach the page holding the
    /// bridge (measured on CEF 152).
    /// </summary>
    public int DevToolsPort { get; init; }

    /// <summary>Where Chromium keeps its profile. Null means the application's <c>chromium</c> data area.</summary>
    public string? CachePath { get; init; }

    /// <summary>What the ready handshake tells the page this shell is and can do (D36).</summary>
    public ShellInfo? Shell { get; init; }
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
    public bool Frameless { get; init; } = true;

    /// <summary>What shows before the page paints, so a dark page never flashes white.</summary>
    public Color? BackgroundColor { get; init; }

    /// <summary>The page to open, relative to the app's origin. Null means its root.</summary>
    public string? Path { get; init; }
}
