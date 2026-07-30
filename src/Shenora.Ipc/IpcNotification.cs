using System.Text.Json.Serialization;

namespace Shenora.Ipc;

/// <summary>
/// One host→client event inside an <see cref="IpcNotificationBatch"/>: <c>{ module, type,
/// payload?, scope? }</c>. Fire-and-forget — notifications are not correlated and have no
/// response.
/// </summary>
public sealed class IpcNotification
{
    /// <summary>The module the event originates from (e.g. <c>"APP"</c>).</summary>
    [JsonPropertyName("module")]
    public required string Module { get; init; }

    /// <summary>The event type within the module (e.g. <c>"UPDATED"</c>).</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Event data; null for signal-only events (omitted on the wire).</summary>
    [JsonPropertyName("payload")]
    public object? Payload { get; init; }

    /// <summary>Optional app-defined scope, mirroring <see cref="IpcRequest.Scope"/>.</summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}
