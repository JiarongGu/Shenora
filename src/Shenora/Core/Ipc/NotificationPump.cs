using System.Collections.Concurrent;

namespace Shenora.Core.Ipc;

/// <summary>
/// The transport-neutral half of a host's outbound notification channel: the bounded drop-oldest queue,
/// the ready gate, batch building, and the guarded per-notification serialize (D23).
/// <para>
/// Owns NO TIMER and NO TRANSPORT. Which thread may touch a base's client is a base-specific fact — on
/// WinForms the flush must run on the UI thread, a headless base can use a
/// <see cref="System.Threading.PeriodicTimer"/> — so the base drives the tick, calls
/// <see cref="TryDrainBatch"/>, and decides which of its own events call
/// <see cref="Open"/>/<see cref="Close"/>.
/// </para>
/// </summary>
public sealed class NotificationPump : IDisposable
{
    private readonly NotificationPumpOptions _options;
    private readonly ConcurrentQueue<IpcNotification> _pending = new();
    private int _pendingCount;
    private IDisposable? _busSubscription;
    private volatile bool _open;
    private bool _disposed;

    /// <summary>
    /// Construct BEFORE the base's client can produce anything — buffering starts HERE, not at
    /// <see cref="Open"/>, so an event emitted during a slow host init still reaches the queue.
    /// Options are validated NOW, so a bad value names itself at the call site.
    /// </summary>
    public NotificationPump(NotificationPumpOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // ⚠ MaxQueued = 0 makes Enqueue dequeue what it just enqueued, so every notification for the
        // life of the process vanishes with no error and no log line.
        if (options.MaxQueued < 1)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(NotificationPumpOptions.MaxQueued)} must be at least 1 — 0 would silently discard every notification.");
        if (options.FlushInterval < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(NotificationPumpOptions.FlushInterval)} must be at least 1 ms.");

        if (_options.EventBus is { } bus)
        {
            _busSubscription = bus.SubscribeToAll(message =>
            {
                Enqueue(new IpcNotification
                {
                    Module = message.Module,
                    Type = message.Type,
                    Payload = message.Payload,
                    Scope = message.Scope,
                    // Carried, never derived here — see Coalesce.
                    CoalesceKey = message.CoalesceKey,
                });
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>
    /// The flush cadence the base should drive its own tick at — policy only, since the pump owns no
    /// timer (<see cref="NotificationPumpOptions.FlushInterval"/>).
    /// </summary>
    public TimeSpan FlushInterval => _options.FlushInterval;

    /// <summary>True once the base has opened the gate (its client's ready handshake) — notifications flow.</summary>
    public bool IsOpen => _open;

    /// <summary>Buffered notification count.</summary>
    public int PendingCount => _pendingCount;

    /// <summary>
    /// Queue a notification for the next drained batch (fire-and-forget; delivery starts once
    /// <see cref="Open"/> has been called). Callable from any thread. Apps using
    /// <see cref="NotificationPumpOptions.EventBus"/> rarely call this directly.
    /// </summary>
    public void Enqueue(IpcNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // Applied HERE so a direct Enqueue and a forwarded bus event are the same call site.
        // ⚠ A throwing filter FAILS CLOSED — delivering anyway is the more dangerous direction.
        if (_options.Filter is { } filter && !AppCallback.RunOrDefault(() => filter(notification), fallback: false))
            return;

        _pending.Enqueue(notification);
        // Over the cap, drop the OLDEST to make room.
        if (Interlocked.Increment(ref _pendingCount) > _options.MaxQueued && _pending.TryDequeue(out _))
            Interlocked.Decrement(ref _pendingCount);
    }

    /// <summary>
    /// Open the gate — the base's client can now receive. WHICH event triggers this is the base's
    /// decision.
    /// </summary>
    public void Open() => _open = true;

    /// <summary>
    /// Close the gate — buffer again instead of draining into a client that can no longer receive.
    /// <para>
    /// 🔴 <b>The trap the base's choice of trigger must avoid:</b> a "navigation started" style event
    /// that does not guarantee the document actually changes closes the gate FOREVER — the surviving
    /// client has already spent its one handshake, so nothing will ever call <see cref="Open"/> again.
    /// Trigger on a new document COMMITTING (<c>ContentLoading</c>), never on an attempt.
    /// </para>
    /// </summary>
    public void Close() => _open = false;

    /// <summary>
    /// Drain the queue into a batch envelope; false when the gate is closed, nothing is pending,
    /// or every pending notification failed to serialize. Called by the base's own tick — never
    /// throws, so a base driving this from a UI-thread timer needs no try/catch of its own.
    /// </summary>
    public bool TryDrainBatch(out string? json)
    {
        json = null;

        // Hold delivery (queue intact) until the gate is open — a batch drained before the client's
        // listeners exist is silently lost, which is worse than arriving late.
        if (!_open) return false;
        if (_pending.IsEmpty) return false;

        // Catch-all: called from the base's own tick, so anything escaping here is an unhandled
        // UI-thread exception, repeating every interval.
        try
        {
            var batch = new List<IpcNotification>();
            while (_pending.TryDequeue(out var notification))
            {
                Interlocked.Decrement(ref _pendingCount);
                batch.Add(notification);
            }
            if (batch.Count == 0) return false;

            batch = Coalesce(batch);

            // 🔴 Payloads are APP-supplied, so serialization can throw on a cyclic graph, a delegate
            // member or a throwing getter — and the queue is already drained, so only the OFFENDER may
            // be dropped, never its batch. The whole batch is tried FIRST and isolated only on failure,
            // so the ordinary case pays one serialization rather than 2N.
            try
            {
                json = IpcJson.Serialize(new IpcNotificationBatch { Payload = batch });
                return true;
            }
            catch (Exception)
            {
                // Fall through to find the one that cannot be written.
            }

            var serializable = new List<IpcNotification>(batch.Count);
            foreach (var notification in batch)
            {
                try
                {
                    _ = IpcJson.Serialize(notification);
                    serializable.Add(notification);
                }
                catch (Exception ex)
                {
                    // ⚠ Module/type only — the payload may carry app data and must not be logged.
                    Log(() => "[Shenora.Core.Ipc] Dropped unserializable notification " +
                              $"{notification.Module}/{notification.Type}: {ex.GetType().Name}");
                }
            }
            if (serializable.Count == 0) return false;

            json = IpcJson.Serialize(new IpcNotificationBatch { Payload = serializable });
            return true;
        }
        catch (Exception ex)
        {
            // Through the guarded Log: this catch-all is the tick's last line of defence.
            Log(() => "[Shenora.Core.Ipc] Notification batch drain failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Drop every notification a later one in the SAME batch supersedes — last-write-wins, keyed by
    /// (module, type, scope, <see cref="IpcNotification.CoalesceKey"/>). The survivor keeps the LATEST
    /// position, because a superseding snapshot describes "now".
    /// <para>
    /// 🔴 <b>Opt-in, and it must stay that way.</b> Un-keyed notifications are never touched: the pump
    /// cannot tell a snapshot from a delta, and coalescing deltas silently loses data. Only the emitter
    /// knows, so only the emitter may say (<see cref="Shenora.Core.Events.EventMessage.CoalesceKey"/>).
    /// </para>
    /// </summary>
    private static List<IpcNotification> Coalesce(List<IpcNotification> batch)
    {
        // Allocated only if something actually opted in.
        Dictionary<(string Module, string Type, string? Scope, string Key), int>? lastIndex = null;
        for (var i = 0; i < batch.Count; i++)
        {
            if (batch[i].CoalesceKey is not { } key) continue;
            (lastIndex ??= [])[(batch[i].Module, batch[i].Type, batch[i].Scope, key)] = i;
        }
        if (lastIndex is null) return batch;

        var kept = new List<IpcNotification>(batch.Count);
        for (var i = 0; i < batch.Count; i++)
        {
            var notification = batch[i];
            // ⚠ Ordinal on every part, including the module name routing matches case-INsensitively:
            // dropping is the destructive direction, so two spellings must fail to coalesce.
            if (notification.CoalesceKey is { } key
                && lastIndex[(notification.Module, notification.Type, notification.Scope, key)] != i)
                continue;
            kept.Add(notification);
        }
        return kept;
    }

    /// <summary>
    /// Guarded + lazy, via the one owner (<see cref="AppCallback.Log"/>) — every call site here is a
    /// place with no caller left to catch anything.
    /// </summary>
    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_options.Log, message, exception: failure);

    /// <summary>Unsubscribe from the bus. The base owns its own timer/transport teardown.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The subscription releases itself — no live reference to the bus is needed here.
        _busSubscription?.Dispose();
        _busSubscription = null;
    }
}
