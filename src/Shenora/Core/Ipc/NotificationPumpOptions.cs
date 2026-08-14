using Shenora;
using Shenora.Core.Events;

namespace Shenora.Core.Ipc;

/// <summary>Inputs for <see cref="NotificationPump"/>. Validated at construction — a bad value names itself.</summary>
public sealed record NotificationPumpOptions
{
    /// <summary>
    /// When set, EVERY event emitted on the bus is forwarded to the channel as a batched
    /// notification (the family's wildcard-forward pattern), subject to <see cref="Filter"/>.
    /// Buffering starts at pump CONSTRUCTION so events emitted during a slow host init aren't
    /// lost; delivery starts once <see cref="NotificationPump.Open"/> is called. Null = the app
    /// pushes notifications itself via <see cref="NotificationPump.Enqueue"/>.
    /// </summary>
    public IEventBus? EventBus { get; init; }

    /// <summary>
    /// The flush cadence the base should drive its tick at — "more like a frames-per-second"
    /// figure than a timer setting. ~50 ms is the family's measured sweet spot: a busy backend
    /// can fire hundreds of events a second, and one batched drain beats hundreds of round trips
    /// while staying imperceptible to the UI. Exposed as policy only — the pump owns no timer.
    /// </summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Cap on buffered notifications. If the gate never opens (init stalled or failed) the queue
    /// would otherwise grow without bound until OOM. Over the cap the OLDEST is dropped —
    /// notifications are telemetry-like (progress/status); losing stale ones under overflow is
    /// fine, an OOM isn't. (The family's measured cap.)
    /// </summary>
    public int MaxQueued { get; init; } = 10_000;

    /// <summary>
    /// Per-channel delivery policy, applied at ENQUEUE (from a direct <see cref="NotificationPump.Enqueue"/>
    /// call AND from a forwarded bus event alike). Default: deliver everything. This is the seam
    /// that lets one bridge per window, or a remote/auxiliary channel, receive only the slice of
    /// the app's traffic it should — every channel subscribing with the bus's wildcard forward
    /// otherwise means every event reaches every channel.
    /// </summary>
    public Func<IpcNotification, bool>? Filter { get; init; }

    /// <summary>Diagnostics sink.</summary>
    public Action<string>? Log { get; init; }
}
