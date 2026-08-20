using System.Text.Json.Serialization;

namespace Shenora.Core.Ipc;

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

    /// <summary>
    /// HOST-SIDE ONLY — never serialized, and deliberately absent from the TS mirror, because the
    /// coalescing has already happened by the time a batch leaves. While this notification is still
    /// buffered, a later one with the same <see cref="Module"/>, <see cref="Type"/>,
    /// <see cref="Scope"/> and key REPLACES it (<see cref="NotificationPump.TryDrainBatch"/>); null means
    /// it is never dropped.
    /// <para>
    /// ⚠ Only a FULL-SNAPSHOT payload may set it — see
    /// <see cref="Shenora.Core.Events.EventMessage.CoalesceKey"/>, where a forwarded bus notification
    /// gets it from.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public string? CoalesceKey { get; init; }
}
