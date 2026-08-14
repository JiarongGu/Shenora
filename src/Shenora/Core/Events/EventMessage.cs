namespace Shenora.Core.Events;

/// <summary>
/// An in-process pub/sub event on the <see cref="IEventBus"/>: which module it originates from,
/// what happened, optionally for which app-defined scope, with what data. This is the HOST-side
/// event type — when an event is forwarded to a client it travels as the Shenora.Core.Ipc
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

    /// <summary>
    /// Optional: this event SUPERSEDES an earlier undelivered one carrying the same
    /// module, type, scope and key. Null (the default) means every emit is its own event and none is
    /// ever dropped.
    /// <para>
    /// It is a declaration about the PAYLOAD, and only a full-snapshot payload may make it: keying a
    /// delta ("+3 bytes") coalesces two increments into one and loses the other. The kit sets it on
    /// <see cref="Shenora.Core.Ipc.IpcRequestEvents.Updated"/>, whose payload is a whole
    /// <see cref="Shenora.Core.Ipc.IpcRequestStatus"/> that the client already folds last-write-wins —
    /// so dropping an intermediate snapshot cannot change the state anyone ends up rendering.
    /// </para>
    /// <para>
    /// ⚠ <b>Nothing coalesces on the BUS itself</b> — every subscriber sees every emit, because a bus
    /// handler runs immediately and there is no window in which to supersede anything. The key is
    /// honoured by a buffering consumer, which today means
    /// <see cref="Shenora.Core.Ipc.NotificationPump"/>: it batches on a flush interval, and that
    /// interval IS the window.
    /// </para>
    /// </summary>
    public string? CoalesceKey { get; init; }

    /// <summary>Emit time.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
