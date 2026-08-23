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
    private long _accepted;
    private long _filtered;
    private long _overflowed;
    private long _unserializable;
    private long _delivered;
    private readonly ConcurrentDictionary<string, byte> _saidOnce = new();
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
        {
            Interlocked.Increment(ref _filtered);
            SayOnce("was rejected by this channel's Filter", notification);
            return;
        }

        _pending.Enqueue(notification);
        Interlocked.Increment(ref _accepted);
        // Over the cap, drop the OLDEST to make room.
        if (Interlocked.Increment(ref _pendingCount) > _options.MaxQueued && _pending.TryDequeue(out var evicted))
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _overflowed);
            SayOnce($"was dropped — the queue is at its {_options.MaxQueued} cap and the client gate is "
                    + (_open ? "open" : "still CLOSED, so nothing has ever been delivered"), evicted);
        }
    }

    /// <summary>
    /// What became of the notifications this pump was handed — the answer to "my page receives no
    /// events", which cannot be worked out from the page: a request answering proves nothing, because
    /// requests do not come through here.
    /// </summary>
    /// <remarks>
    /// Read it when something is missing: <c>Accepted</c> at 0 means nothing was ever emitted (the
    /// host half is not wired), a rising <c>Filtered</c> names the app's own
    /// <see cref="NotificationPumpOptions.Filter"/>, and <c>Delivered</c> at 0 with a gate that is not
    /// open means the client never handshook.
    /// </remarks>
    public NotificationPumpReport Report() => new(
        _open,
        _pendingCount,
        Interlocked.Read(ref _accepted),
        Interlocked.Read(ref _filtered),
        Interlocked.Read(ref _overflowed),
        Interlocked.Read(ref _unserializable),
        Interlocked.Read(ref _delivered));

    /// <summary>
    /// Say a DROP once per reason and per module/type, then leave it to <see cref="Report"/>'s counters.
    /// <para>
    /// 🔴 A silent drop is the whole failure: from the page, "the host never emitted it", "your filter
    /// rejected it" and "the gate never opened" are the same nothing. ⚠ But a filter rejecting a busy
    /// module would write a line per event, so only the first of each kind is logged — and the key set
    /// is capped, because a pathological app could otherwise mint keys for ever.
    /// </para>
    /// </summary>
    private void SayOnce(string what, IpcNotification notification)
    {
        // ⚠ Module/type only — the payload may carry app data and must not be logged.
        if (_saidOnce.Count >= 32 || !_saidOnce.TryAdd($"{what}|{notification.Module}/{notification.Type}", 0)) return;
        Log(() => $"[Shenora.Core.Ipc] Notification {notification.Module}/{notification.Type} {what}. "
                  + "Later ones are counted rather than logged — see NotificationPump.Report().");
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
                Interlocked.Add(ref _delivered, batch.Count);
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
                    Interlocked.Increment(ref _unserializable);
                    // ⚠ Module/type only — the payload may carry app data and must not be logged.
                    Log(() => "[Shenora.Core.Ipc] Dropped unserializable notification " +
                              $"{notification.Module}/{notification.Type}: {ex.GetType().Name}");
                }
            }
            if (serializable.Count == 0) return false;

            json = IpcJson.Serialize(new IpcNotificationBatch { Payload = serializable });
            Interlocked.Add(ref _delivered, serializable.Count);
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

/// <summary>
/// A snapshot of what a <see cref="NotificationPump"/> did with the notifications it was handed —
/// see <see cref="NotificationPump.Report"/> for how to read it.
/// </summary>
/// <param name="IsOpen">
/// Whether the client gate is open. False means the client never handshook (or the base closed it), and
/// nothing has been delivered however much was emitted.
/// </param>
/// <param name="Pending">Buffered right now, waiting for the next drain.</param>
/// <param name="Accepted">Passed the filter and entered the queue. Zero means nothing was ever emitted.</param>
/// <param name="Filtered">
/// Rejected by <see cref="NotificationPumpOptions.Filter"/> — the app's own policy, including a filter
/// that THREW, since that fails closed.
/// </param>
/// <param name="Overflowed">Dropped as the oldest, because the queue was at its cap.</param>
/// <param name="Unserializable">Dropped at drain time because the payload could not be written.</param>
/// <param name="Delivered">Written into a batch the base then sent.</param>
public readonly record struct NotificationPumpReport(
    bool IsOpen,
    int Pending,
    long Accepted,
    long Filtered,
    long Overflowed,
    long Unserializable,
    long Delivered);
