using Shenora.Core.Ipc;

namespace Shenora.Windows;

/// <summary>
/// The IPC entry for <see cref="DropZoneManager"/> — module <see cref="DropZoneManager.Module"/>,
/// routes REGISTER / UPDATE / UNREGISTER / SHOW (what the client's <c>useDropZone</c> sends).
/// <para>
/// REGISTRATION: map it LATE, from wherever the window is created —
/// <c>dispatcher.MapModule(new DropZoneModule(manager))</c> on the plain
/// <see cref="IMessageDispatcher"/> resolved from DI (no cast; late mapping is safe while requests
/// are in flight). This doc used to add "or through <c>UseMessageDispatcher</c>'s configure callback
/// once the manager exists", which CANNOT work and is the same wrong advice
/// <c>WindowCommandModule</c>'s doc already records having carried (P5.5 H6): that callback runs at
/// provider-build time, and a <see cref="DropZoneManager"/> requires a live <c>WebView2</c> control
/// and <see cref="Form"/>, neither of which exists yet.
/// </para>
/// </summary>
public sealed class DropZoneModule : ModuleBase
{
    /// <summary>Route: declare a zone at <c>{ zoneId, x, y, width, height }</c> (page coordinates).</summary>
    public const string RegisterType = "REGISTER";

    /// <summary>Route: same payload as <see cref="RegisterType"/> — updating IS registering with new
    /// bounds, which is why they share a case.</summary>
    public const string UpdateType = "UPDATE";

    /// <summary>Route: forget a zone: <c>{ zoneId }</c>.</summary>
    public const string UnregisterType = "UNREGISTER";

    /// <summary>Route: raise the drop overlay over a zone: <c>{ zoneId }</c>.</summary>
    public const string ShowType = "SHOW";

    private readonly DropZoneManager _manager;

    /// <summary>The IPC face of <paramref name="manager"/>. Map it late — it needs the live control.</summary>
    public DropZoneModule(DropZoneManager manager, Microsoft.Extensions.Logging.ILogger<DropZoneModule>? logger = null)
        : base(logger)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    /// <inheritdoc />
    public override string ModuleName => DropZoneManager.Module;

    /// <inheritdoc />
    protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        switch (request.Type.ToUpperInvariant())
        {
            case RegisterType:
            case UpdateType: // updating is registering with new bounds
                _manager.RegisterZone(
                    PayloadHelper.GetRequiredValue<string>(request.Payload, "zoneId"),
                    PayloadHelper.GetRequiredValue<int>(request.Payload, "x"),
                    PayloadHelper.GetRequiredValue<int>(request.Payload, "y"),
                    PayloadHelper.GetRequiredValue<int>(request.Payload, "width"),
                    PayloadHelper.GetRequiredValue<int>(request.Payload, "height"));
                return Done();

            case UnregisterType:
                _manager.UnregisterZone(PayloadHelper.GetRequiredValue<string>(request.Payload, "zoneId"));
                return Done();

            case ShowType:
                _manager.ShowOverlay(PayloadHelper.GetRequiredValue<string>(request.Payload, "zoneId"));
                return Done();

            default:
                throw UnknownType(request);   // ModuleBase owns the shape (P5.5 H4.5)
        }
    }
}
