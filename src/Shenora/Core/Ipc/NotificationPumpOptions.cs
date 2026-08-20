using Microsoft.Extensions.Logging;
using Shenora.Core.Events;

namespace Shenora.Core.Ipc;

/// <summary>Inputs for <see cref="NotificationPump"/>. Validated at construction — a bad value names itself.</summary>
public sealed record NotificationPumpOptions
{
    /// <summary>
    /// When set, EVERY event emitted on the bus is forwarded to the channel as a batched notification,
    /// subject to <see cref="Filter"/>. Buffering starts at pump CONSTRUCTION; delivery starts once
    /// <see cref="NotificationPump.Open"/> is called. Null = the app pushes notifications itself via
    /// <see cref="NotificationPump.Enqueue"/>.
    /// </summary>
    public IEventBus? EventBus { get; init; }

    /// <summary>
    /// The flush cadence the base should drive its tick at — a frames-per-second figure rather than a
    /// timer setting, since one batched drain replaces hundreds of round trips. Policy only: the pump
    /// owns no timer.
    /// </summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Cap on buffered notifications, so a gate that never opens cannot grow the queue until OOM. Over
    /// the cap the OLDEST is dropped: notifications are telemetry-like, and losing stale ones under
    /// overflow beats an OOM.
    /// </summary>
    public int MaxQueued { get; init; } = 10_000;

    /// <summary>
    /// Per-channel delivery policy, applied at ENQUEUE (from a direct
    /// <see cref="NotificationPump.Enqueue"/> call and from a forwarded bus event alike). Default:
    /// deliver everything. The seam that lets one bridge per window, or an auxiliary channel, receive
    /// only the slice of the app's traffic it should.
    /// </summary>
    public Func<IpcNotification, bool>? Filter { get; init; }

    /// <summary>Diagnostics sink.</summary>
    public ILogger? Log { get; init; }
}
