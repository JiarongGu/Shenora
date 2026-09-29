using Shenora.Core.Ipc;
// `WebView2` alone resolves to the NAMESPACE in here, hence the alias.
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Windows;

/// <summary>
/// The IPC entry for <see cref="DropZoneManager"/> — module <see cref="DropZoneManager.Module"/>,
/// routes REGISTER / UPDATE / UNREGISTER / SHOW (what the client's <c>useDropZone</c> sends).
/// <para>
/// REGISTRATION: map it LATE, from wherever the window is created —
/// <c>dispatcher.MapModule(new DropZoneModule(manager))</c> on the plain
/// <see cref="IMessageDispatcher"/> resolved from DI (no cast; late mapping is safe while requests
/// are in flight). ⚠ NOT <c>UseMessageDispatcher</c>'s configure callback, which runs at
/// provider-build time, before the live <c>WebView2</c> and <see cref="Form"/> a
/// <see cref="DropZoneManager"/> needs exist.
/// </para>
/// <para>
/// ONCE, over the main web view's manager. A page in another web view, as <see cref="WebViewIpcBridge"/> reports it
/// (a <see cref="SecondaryWindows"/> window's, or a second one in the main window), gets a manager of its own over
/// that web view and its top-level form, made on its first request and disposed with the web view: its overlays land
/// over its own page. Its drop events go out on the same bus, told apart by their <c>zoneId</c>, which
/// <c>useDropZone</c> makes unique unless the page names its zones. A page whose web view is gone registers nothing.
/// </para>
/// </summary>
public sealed class DropZoneModule : ModuleBase
{
    /// <summary>Route: declare a zone at <c>{ zoneId, x, y, width, height }</c> (page coordinates).</summary>
    public const string RegisterType = "REGISTER";

    /// <summary>Route: move a zone to new bounds; same payload as <see cref="RegisterType"/>.</summary>
    public const string UpdateType = "UPDATE";

    /// <summary>Route: forget a zone: <c>{ zoneId }</c>.</summary>
    public const string UnregisterType = "UNREGISTER";

    /// <summary>Route: raise the drop overlay over a zone: <c>{ zoneId }</c>.</summary>
    public const string ShowType = "SHOW";

    private readonly DropZoneManager _manager;
    private readonly Dictionary<WebView2Control, DropZoneManager> _pages = [];   // other web views', on their threads
    private readonly object _lock = new();

    /// <summary>The IPC face of <paramref name="manager"/>. Map it late — it needs the live control.</summary>
    public DropZoneModule(DropZoneManager manager, Microsoft.Extensions.Logging.ILogger<DropZoneModule>? logger = null)
        : base(logger)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    // The manager for the page that sent this: the main one for its own web view and for a send from no page; none for
    // a page whose web view is gone. A new one only on the sending web view's own thread, the one a manager over it
    // must be made on: work a page's request started runs elsewhere, as that page's.
    private DropZoneManager? For(bool isPage, Control? sender)
    {
        if (!isPage) return _manager;
        if (sender is null) return null;   // collected
        if (sender is not WebView2Control web || ReferenceEquals(web, _manager.WebView)) return _manager;
        lock (_lock)
        {
            if (_pages.TryGetValue(web, out var existing)) return existing;
            if (web.IsDisposed || web.InvokeRequired || web.TopLevelControl is not Form form) return null;
            var manager = new DropZoneManager(new DropZoneManagerOptions { WebView = web, ParentForm = form, EventBus = _manager.EventBus }, _manager.Logger);
            _pages[web] = manager;
            web.Disposed += (_, _) =>
            {
                lock (_lock) _pages.Remove(web);
                manager.Dispose();
            };
            return manager;
        }
    }

    /// <inheritdoc />
    public override string ModuleName => DropZoneManager.Module;

    /// <inheritdoc />
    protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        var manager = For(PageSender.IsPage(out var sender), sender);
        switch (request.Type.ToUpperInvariant())
        {
            case RegisterType:
            case UpdateType: // updating is registering with new bounds
                manager?.RegisterZone(
                    PayloadHelper.GetRequiredValue<string>(request.Payload, "zoneId"),
                    PayloadHelper.GetRequiredValue<int>(request.Payload, "x"),
                    PayloadHelper.GetRequiredValue<int>(request.Payload, "y"),
                    PayloadHelper.GetRequiredValue<int>(request.Payload, "width"),
                    PayloadHelper.GetRequiredValue<int>(request.Payload, "height"));
                return Done();

            case UnregisterType:
                manager?.UnregisterZone(PayloadHelper.GetRequiredValue<string>(request.Payload, "zoneId"));
                return Done();

            case ShowType:
                manager?.ShowOverlay(PayloadHelper.GetRequiredValue<string>(request.Payload, "zoneId"));
                return Done();

            default:
                throw UnknownType(request);   // ModuleBase owns the shape
        }
    }
}
