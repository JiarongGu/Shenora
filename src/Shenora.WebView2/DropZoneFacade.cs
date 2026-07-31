using Shenora.Ipc;

namespace Shenora.WebView2;

/// <summary>
/// The IPC entry for <see cref="DropZoneManager"/> — module <see cref="DropZoneManager.Module"/>,
/// routes REGISTER / UPDATE / UNREGISTER / SHOW (what the client's <c>useDropZone</c> sends).
/// Map it like any facade: <c>dispatcher.MapModule(new DropZoneFacade(manager))</c> — or through
/// <c>AddMessageDispatcher</c>'s configure callback once the manager exists.
/// </summary>
public sealed class DropZoneFacade : BaseFacade
{
    private readonly DropZoneManager _manager;

    /// <summary>The IPC face of <paramref name="manager"/>. Map it late — it needs the live control.</summary>
    public DropZoneFacade(DropZoneManager manager, Microsoft.Extensions.Logging.ILogger<DropZoneFacade>? logger = null)
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
            case "REGISTER":
            case "UPDATE": // updating is registering with new bounds
                _manager.RegisterZone(
                    PayloadHelper.GetRequiredValue<string>(request.Payload, "zoneId"),
                    PayloadHelper.GetRequiredValue<int>(request.Payload, "x"),
                    PayloadHelper.GetRequiredValue<int>(request.Payload, "y"),
                    PayloadHelper.GetRequiredValue<int>(request.Payload, "width"),
                    PayloadHelper.GetRequiredValue<int>(request.Payload, "height"));
                return Done();

            case "UNREGISTER":
                _manager.UnregisterZone(PayloadHelper.GetRequiredValue<string>(request.Payload, "zoneId"));
                return Done();

            case "SHOW":
                _manager.ShowOverlay(PayloadHelper.GetRequiredValue<string>(request.Payload, "zoneId"));
                return Done();

            default:
                throw UnknownType(request);   // BaseFacade owns the shape (P5.5 H4.5)
        }
    }
}
