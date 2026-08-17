using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Shenora.Core.Ipc;

/// <summary>
/// The transport-neutral half of a host's outbound notification channel: the bounded drop-oldest queue,
/// the ready gate, batch building, and the guarded per-notification serialize (D23). It is separate from
/// any transport so that a second, non-WinForms base inherits these invariants rather than re-earning
/// them one incident at a time.
/// <para>
/// Owns NO TIMER and NO TRANSPORT. Which thread may touch a base's client is a base-specific
/// fact — on WinForms the flush must run on the UI thread (a <c>System.Windows.Forms.Timer</c>,
/// which is why one is used today); a headless base would use a <see cref="System.Threading.PeriodicTimer"/>.
/// So the base drives the tick and calls <see cref="TryDrainBatch"/>; the pump only exposes
/// <see cref="FlushInterval"/> as policy. The base also decides WHICH of its own events call
/// <see cref="Open"/>/<see cref="Close"/> — the pump only owns what those two do once called.
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
    /// Construct BEFORE the base's client can produce anything — buffering starts HERE, at
    /// construction, not at <see cref="Open"/>: subscribing to the bus now means an event
    /// emitted during a slow host init (WebView2 spin-up, a mobile shell's bridge handshake)
    /// still reaches the queue instead of being lost before anything existed to receive it.
    /// Options are validated NOW, not on first use, so a bad value names itself at the call site.
    /// </summary>
    public NotificationPump(NotificationPumpOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // ⚠ MaxQueued = 0 makes Enqueue dequeue what it just enqueued, so every notification for the
        // life of the process vanishes with no error and no log line. Both are checked at CONSTRUCTION
        // so a bad value names itself here rather than far from its cause.
        if (options.MaxQueued < 1)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(NotificationPumpOptions.MaxQueued)} must be at least 1 — 0 would silently discard every notification.");
        if (options.FlushInterval < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(NotificationPumpOptions.FlushInterval)} must be at least 1 ms.");

        // Subscribe NOW, not at Open(): the queue buffers until the gate opens, so nothing emitted
        // during host init or page load is lost.
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
                    // Carried, not derived: only the EMITTER knows whether its payload is a whole
                    // snapshot (safe to supersede) or a delta (never safe). See EventMessage.CoalesceKey.
                    CoalesceKey = message.CoalesceKey,
                });
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>
    /// The flush cadence the base should drive its own tick at (policy only — see the class doc:
    /// the pump owns no timer). ~50 ms / 20 fps is the family's measured default
    /// (<see cref="NotificationPumpOptions.FlushInterval"/>).
    /// </summary>
    public TimeSpan FlushInterval => _options.FlushInterval;

    /// <summary>True once the base has opened the gate (its client's ready handshake) — notifications flow.</summary>
    public bool IsOpen => _open;

    /// <summary>Buffered notification count.</summary>
    public int PendingCount => _pendingCount;

    /// <summary>
    /// Queue a notification for the next drained batch (fire-and-forget; delivery starts once
    /// <see cref="Open"/> has been called). Callable from any thread. Apps using
    /// <see cref="NotificationPumpOptions.EventBus"/> rarely call this directly — emitting on the
    /// bus reaches every pump subscribed to it, each through its own <see cref="NotificationPumpOptions.Filter"/>.
    /// </summary>
    public void Enqueue(IpcNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // Applied HERE so a direct Enqueue and a forwarded bus event are the same call site.
        // ⚠ A throwing filter FAILS CLOSED: the filter exists to keep traffic off a channel, so
        // delivering anyway is the more dangerous direction.
        if (_options.Filter is { } filter && !AppCallback.RunOrDefault(() => filter(notification), fallback: false))
            return;

        _pending.Enqueue(notification);
        // Bound the buffer (see MaxQueued): over the cap, drop the OLDEST to make room.
        if (Interlocked.Increment(ref _pendingCount) > _options.MaxQueued && _pending.TryDequeue(out _))
            Interlocked.Decrement(ref _pendingCount);
    }

    /// <summary>
    /// Open the gate — the base's client can now receive (its ready handshake completed). Call
    /// from the base; WHICH event triggers this is the base's own decision.
    /// </summary>
    public void Open() => _open = true;

    /// <summary>
    /// Close the gate — buffer again instead of draining into a client that can no longer receive. Call
    /// from the base; WHICH event triggers it is the base's own decision.
    /// <para>
    /// 🔴 <b>The trap that decision must avoid:</b> a "navigation started" style event that does not
    /// guarantee the document actually changes closes the gate FOREVER — the surviving client has already
    /// spent its one handshake, so nothing will ever call <see cref="Open"/> again. Trigger on the event
    /// that means a new document is committing (<c>ContentLoading</c>), never on the one that means a
    /// navigation was merely attempted.
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

        // Hold delivery (queue intact) until the gate is open — a batch drained before the
        // client's listeners exist would be silently lost, which is worse than arriving late.
        if (!_open) return false;
        if (_pending.IsEmpty) return false;

        // Catch-all: this is called from the base's own tick (a WinForms timer today), so
        // ANYTHING that escapes here would be an unhandled UI-thread exception — a modal crash
        // dialog under the family bootstrap, repeating every interval. The incoming IPC path has
        // always been guarded this way; the outgoing one was not, until the P0–P5 review found it.
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
            // be dropped, never its batch. ⚠ Try the whole batch FIRST and isolate only on failure: the
            // ordinary case is every case, and it should pay one serialization rather than 2N.
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
                    // Module/type only — a payload that fails to serialize must not have its
                    // contents logged either (it may carry app data).
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
            // Through the guarded Log: this catch-all IS the tick's last line of defence, so a
            // throwing app sink here would defeat the very thing it is reporting from.
            Log(() => "[Shenora.Core.Ipc] Notification batch drain failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Drop every notification a later one in the SAME batch supersedes — last-write-wins, keyed by
    /// (module, type, scope, <see cref="IpcNotification.CoalesceKey"/>). Order is otherwise untouched:
    /// the survivor keeps the LATEST position, because a superseding snapshot describes "now", not the
    /// moment the first of its run was queued.
    /// <para>
    /// 🔴 <b>Opt-in, and it must stay that way.</b> Un-keyed notifications are never touched: the pump
    /// cannot tell a snapshot from a delta, and coalescing deltas silently loses data. Only the emitter
    /// knows, so only the emitter may say (<see cref="Shenora.Core.Events.EventMessage.CoalesceKey"/>).
    /// Legal because folding all of a run and folding only its last reach the same state.
    /// </para>
    /// </summary>
    private static List<IpcNotification> Coalesce(List<IpcNotification> batch)
    {
        // Allocated only if something actually opted in — which, for an app that never sets a key, is
        // never. The scan itself is one pass over a list already in hand.
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
            // Ordinal on every part, including the module name that routing matches case-INsensitively:
            // dropping a notification is the destructive direction, so two spellings must fail to
            // coalesce rather than coalesce by accident.
            if (notification.CoalesceKey is { } key
                && lastIndex[(notification.Module, notification.Type, notification.Scope, key)] != i)
                continue;
            kept.Add(notification);
        }
        return kept;
    }

    /// <summary>
    /// Guarded + lazy, via the one owner (<see cref="AppCallback.Log"/>) — the sink is app code
    /// invoked from the per-notification guard above and the base's tick, i.e. places with no caller
    /// left to catch anything.
    /// </summary>
    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_options.Log, message, exception: failure);

    /// <summary>Unsubscribe from the bus. The base owns its own timer/transport teardown.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The subscription releases itself. The id version needed BOTH the id and a live reference to
        // the bus here — so a pump torn down after its bus went away leaked the subscription, silently.
        // That whole failure mode is gone rather than fixed.
        _busSubscription?.Dispose();
        _busSubscription = null;
    }
}
