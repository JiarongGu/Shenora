using Shenora.Core.Ipc;

namespace Shenora.Chromium.Host;

/// <summary>
/// The page's window commands on a Chromium window: the SAME module and routes as the WebView2 shell's
/// <c>WindowCommandModule</c>, so a page's title bar works unchanged on either engine (a test pins the names).
/// A drag or a resize is a no-op here: Chromium moves the window natively from the page's
/// <c>-webkit-app-region</c>. <c>SET_CAPTION_BUTTONS</c> and <c>SHOW_SYSTEM_MENU</c> are always wired on Windows,
/// because the shell owns the window, and the page learns hover and press from <see cref="CaptionButtonStateEvent"/>.
/// <c>SET_THEME</c> and <c>SET_CAPTION_BUTTON_COLORS</c> are wired for a window that paints its caption buttons, which
/// follow them. Otherwise they, and the other two on another OS, answer <c>NO_ROUTE</c> (the module exists, the type
/// does not), as the WebView2 module does for a route that is not wired.
/// <para>
/// ONE module for every window, mapped once: each command acts on the window whose page sent it
/// (<see cref="ChromiumBrowserContext"/>), so a secondary window's close closes that window, and a main window
/// opened again is served like the first. Outside a window's dispatch every command is a no-op.
/// </para>
/// </summary>
/// <param name="current">The window whose page sent the request being handled.</param>
internal sealed class ChromiumWindowCommands(Func<ChromiumWindow?> current) : ModuleBase
{
    public const string Module = "SHENORA.WINDOW";
    public const string MinimizeType = "MINIMIZE";
    public const string ToggleMaximizeType = "TOGGLE_MAXIMIZE";
    public const string CloseType = "CLOSE";
    public const string IsMaximizedType = "IS_MAXIMIZED";
    public const string StartDragType = "START_DRAG";
    public const string StartResizeType = "START_RESIZE";
    public const string SetCaptionButtonsType = "SET_CAPTION_BUTTONS";
    public const string ShowSystemMenuType = "SHOW_SYSTEM_MENU";

    /// <summary>Route: <c>{ dark }</c>, the page's theme, for a window that paints its caption buttons.</summary>
    public const string SetThemeType = "SET_THEME";

    /// <summary>Route: <c>{ colors? }</c>, the page's own colours for a window that paints its caption buttons, which win
    /// over its theme; no <c>colors</c> goes back to the theme.</summary>
    public const string SetCaptionButtonColorsType = "SET_CAPTION_BUTTON_COLORS";

    /// <summary>
    /// The event this window's page receives when the OS changes what it is doing to a caption button:
    /// <c>{ hot?, pressed? }</c>, each a button kind or absent. A page that drew its buttons loses every mouse
    /// event over them to the hit-test, CSS <c>:hover</c> included, so this is how it learns what to render.
    /// Sent to that window's page alone.
    /// </summary>
    public const string CaptionButtonStateEvent = "CAPTION_BUTTON_STATE";

    public override string ModuleName => Module;

    // Dispatched on CEF's UI thread (the bridge dispatches there), where the window may be touched.
    protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        var window = current();
        switch (request.Type.ToUpperInvariant())
        {
            case MinimizeType: window?.Minimize(); return Done();
            case ToggleMaximizeType: window?.ToggleMaximize(); return Done();
            case CloseType: window?.Close(); return Done();
            case IsMaximizedType: return Task.FromResult<object?>(new { Maximized = window?.IsMaximized ?? false });
            case StartDragType or StartResizeType: return Done();
            case ShowSystemMenuType when ChromiumWindow.SupportsSystemMenu:
                window?.ShowSystemMenu();
                return Done();
            case SetCaptionButtonsType when ChromiumWindow.SupportsCaptionButtons:
                window?.SetCaptionButtons(request.Payload);
                return Done();
            case SetThemeType when window is { PaintsCaptionButtons: true }:
                // As the WebView2 shell's: `dark` optional, default true, so the same page works on either engine.
                window.SetTheme(PayloadHelper.GetOptionalValue<bool?>(request.Payload, "dark") ?? true);
                return Done();
            case SetCaptionButtonColorsType when window is { PaintsCaptionButtons: true }:
                window.SetCaptionButtonColors(CaptionButtonPalette.FromPayload(request.Payload));
                return Done();
            default: throw UnknownType(request);
        }
    }
}
