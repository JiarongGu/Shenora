namespace Shenora.Core.Events;

/// <summary>
/// An in-process pub/sub event on the <see cref="IEventBus"/>: which module it originates from,
/// what happened, optionally for which app-defined scope, with what data. This is the HOST-side
/// event type — when an event is forwarded to a client it travels as the Shenora.Ipc
/// notification envelope (a transport bridge does the conversion), so this type deliberately
/// carries no wire attributes.
/// </summary>
public sealed class EventMessage
{
    /// <summary>Event id, for tracing/diagnostics only.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>The module the event originates from (e.g. <c>"APP"</c>).</summary>
    public required string Module { get; init; }

    /// <summary>The event type within the module (e.g. <c>"UPDATED"</c>).</summary>
    public required string Type { get; init; }

    /// <summary>
    /// Optional app-defined scope; null = a global event. See <see cref="IEventBus"/> for how
    /// scope participates in subscription matching.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>Event data; null for signal-only events.</summary>
    public object? Payload { get; init; }

    /// <summary>Emit time.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
