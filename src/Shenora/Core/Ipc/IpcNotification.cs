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
    /// HOST-SIDE ONLY — never serialized, and deliberately absent from the TS mirror. While this
    /// notification is still buffered, a later one with the same <see cref="Module"/>,
    /// <see cref="Type"/>, <see cref="Scope"/> and key REPLACES it
    /// (<see cref="NotificationPump.TryDrainBatch"/>); null means it is never dropped.
    /// <para>
    /// It does not cross the wire because by the time a batch leaves, the coalescing has already
    /// happened — the client receives the survivor and has nothing left to decide. Sending it would
    /// publish an internal buffering hint as wire contract and invite a client to re-implement a
    /// policy the host already applied.
    /// </para>
    /// <para>
    /// Only a FULL-SNAPSHOT payload may set it — see
    /// <see cref="Shenora.Core.Events.EventMessage.CoalesceKey"/>, which is where it comes from for a
    /// notification forwarded off the bus.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public string? CoalesceKey { get; init; }
}
