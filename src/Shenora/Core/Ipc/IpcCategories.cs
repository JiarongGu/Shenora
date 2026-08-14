namespace Shenora.Core.Ipc;

/// <summary>
/// Values of the <c>category</c> discriminator on host→client messages. Transports that
/// multiplex responses and notifications over one channel (WebView2 postMessage, the
/// server-backed profile's WebSocket) route on it. Lowercase by design — the source app used
/// SCREAMING values, but the design contract (§5) fixed lowercase to match the
/// camelCase-everything wire convention (<see cref="IpcJson.Options"/>).
/// </summary>
public static class IpcCategories
{
    /// <summary>A response to a client request (<see cref="IpcResponse"/>).</summary>
    public const string Ipc = "ipc";

    /// <summary>A host-pushed notification batch (<see cref="IpcNotificationBatch"/>).</summary>
    public const string Notification = "notification";
}
