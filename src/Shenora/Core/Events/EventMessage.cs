namespace Shenora.Core.Events;

/// <summary>
/// An in-process pub/sub event on the <see cref="IEventBus"/>: which module it originates from,
/// what happened, optionally for which app-defined scope, with what data. The HOST-side event type — a
/// transport bridge converts it into the <c>Shenora.Core.Ipc</c> notification envelope, so this carries
/// no wire attributes.
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

    /// <summary>
    /// Optional: this event SUPERSEDES an earlier undelivered one carrying the same module, type, scope
    /// and key. Null (the default) means every emit is its own event and none is ever dropped.
    /// <para>
    /// 🔴 <b>Only a FULL-SNAPSHOT payload may set it.</b> Keying a delta ("+3 bytes") coalesces two
    /// increments into one and loses the other.
    /// </para>
    /// <para>
    /// ⚠ <b>Nothing coalesces on the BUS itself</b> — a bus handler runs immediately, so every subscriber
    /// sees every emit. The key is honoured by a buffering consumer, today
    /// <see cref="Shenora.Core.Ipc.NotificationPump"/>, whose flush interval IS the window.
    /// </para>
    /// </summary>
    public string? CoalesceKey { get; init; }

    /// <summary>Emit time.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
