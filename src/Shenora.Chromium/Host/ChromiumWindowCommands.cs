using Shenora.Core.Ipc;

namespace Shenora.Chromium.Host;

/// <summary>
/// The page's window commands on a Chromium window: the SAME module and routes as the WebView2 shell's
/// <c>WindowCommandModule</c>, so a page's title bar works unchanged on either engine (a test pins the names).
/// A drag or a resize is a no-op here: Chromium moves the window natively from the page's
/// <c>-webkit-app-region</c>. The two opt-in routes (<c>SET_THEME</c>, <c>SET_CAPTION_BUTTONS</c>) answer
/// <c>NO_ROUTE</c> (the module exists, the type does not), as the WebView2 module does when they are not
/// wired (measured through the shell).
/// </summary>
internal sealed class ChromiumWindowCommands(ChromiumWindow window) : ModuleBase
{
    public const string Module = "SHENORA.WINDOW";
    public const string MinimizeType = "MINIMIZE";
    public const string ToggleMaximizeType = "TOGGLE_MAXIMIZE";
    public const string CloseType = "CLOSE";
    public const string IsMaximizedType = "IS_MAXIMIZED";
    public const string StartDragType = "START_DRAG";
    public const string StartResizeType = "START_RESIZE";

    public override string ModuleName => Module;

    // Dispatched on CEF's UI thread (the bridge dispatches there), where the window may be touched.
    protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        switch (request.Type.ToUpperInvariant())
        {
            case MinimizeType: window.Minimize(); return Done();
            case ToggleMaximizeType: window.ToggleMaximize(); return Done();
            case CloseType: window.Close(); return Done();
            case IsMaximizedType: return Task.FromResult<object?>(new { Maximized = window.IsMaximized });
            case StartDragType or StartResizeType: return Done();
            default: throw UnknownType(request);
        }
    }
}
