using Shenora.Core.Ipc;

namespace Shenora.Chromium.Host;

/// <summary>
/// <c>useDropZone</c> on a Chromium window: the WebView2 shell's <c>SHENORA.DROPZONE</c> routes, with no overlay.
/// CEF hands the host the dragged files' real paths as the drag enters (<see cref="ChromiumWindow.TakeDraggedFiles"/>),
/// and the page's own DOM drop says WHICH zone received it, so the page delivers the drop and asks for the paths.
/// <c>REGISTER</c> answers <c>{ pageDrop: true }</c> to say so; the WebView2 shell answers nothing, and the hook
/// keeps its overlay protocol there.
/// <para>
/// ONE module for every window: the zones, and the drag, are the window's whose page sent the request
/// (<see cref="ChromiumWindowContext"/>), and the window forgets its zones when a new document starts.
/// </para>
/// </summary>
/// <param name="current">The window whose page sent the request being handled.</param>
internal sealed class ChromiumDropZones(Func<ChromiumWindow?> current) : ModuleBase
{
    public const string Module = "SHENORA.DROPZONE";
    public const string RegisterType = "REGISTER";
    public const string UpdateType = "UPDATE";
    public const string UnregisterType = "UNREGISTER";
    public const string ShowType = "SHOW";

    /// <summary>Route, this shell only: <c>{ zoneId }</c>, the page's drop on a zone, answered with
    /// <c>{ files }</c>, the real paths of the drag it ended.</summary>
    public const string DropType = "DROP";

    public override string ModuleName => Module;

    // Dispatched on CEF's UI thread, where the window's drag state lives.
    protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        var window = current();
        switch (request.Type.ToUpperInvariant())
        {
            case RegisterType or UpdateType:
                window?.DropZones.Add(ZoneId(request));
                return Task.FromResult<object?>(new { PageDrop = true });
            case UnregisterType:
                window?.DropZones.Remove(ZoneId(request));
                return Done();
            case ShowType:
                return Done();   // an overlay to raise is the WebView2 shell's protocol
            case DropType:
                // Only for a zone this page declared: a drop anywhere else is not the kit's to deliver.
                var files = window is not null && window.DropZones.Contains(ZoneId(request)) ? window.TakeDraggedFiles() : [];
                return Task.FromResult<object?>(new { Files = files });
            default:
                throw UnknownType(request);
        }
    }

    private static string ZoneId(IpcRequest request) => PayloadHelper.GetRequiredValue<string>(request.Payload, "zoneId");
}
