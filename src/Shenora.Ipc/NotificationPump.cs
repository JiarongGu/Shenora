using System.Collections.Concurrent;
using Shenora.Core;

namespace Shenora.Ipc;

/// <summary>
/// The transport-neutral half of a host's outbound notification channel: the bounded
/// drop-oldest queue, the ready gate, batch building, and the guarded per-notification
/// serialize. Moved out of <c>Shenora.Windows.WebViewIpcBridge</c> (design
/// <c>docs/2026-08-01-shenora-communication-core-design.md</c> §5) so a second, non-WinForms
/// base inherits these already-fixed bugs instead of re-earning them — every one of the
/// invariants below was a real incident (P5.5 H2/H3) before it was a comment.
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

        // Validate at CONSTRUCTION (P5.5 H3), the kit's convention. Both of these otherwise fail
        // far from their cause:
        //
        // MaxQueued = 0 makes Enqueue dequeue the item it just enqueued, so EVERY notification
        // for the life of the process vanishes with no error and no log line — the worst
        // possible shape for a misconfiguration.
        //
        // FlushInterval below 1 ms is nonsensical for ANY periodic drain, WinForms Forms.Timer or
        // otherwise — the original (WebViewIpcBridge) version of this check let it truncate to 0
        // and throw an opaque ArgumentOutOfRangeException out of the WinForms Timer's own setter,
        // at a call site that has nothing to do with the option that caused it.
        if (options.MaxQueued < 1)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(NotificationPumpOptions.MaxQueued)} must be at least 1 — 0 would silently discard every notification.");
        if (options.FlushInterval < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(NotificationPumpOptions.FlushInterval)} must be at least 1 ms.");

        // Subscribe NOW, not at Open(): the bus hands us events from any thread and the queue
        // buffers them until the gate opens — so nothing emitted during host init or page load is
        // lost (the buffered-startup lesson from the server-backed sibling).
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

        // The filter is applied HERE so it governs every notification uniformly — a direct
        // Enqueue call and one forwarded from the bus subscription above are the same call site.
        // Guarded (AppCallback.RunOrDefault), like every other app-supplied callback this kit
        // hands to a UI-thread event path: a throwing predicate must resolve to a policy decision,
        // not propagate. FAILING CLOSED (drop) is the right default HERE specifically — the filter
        // exists so a channel receives only its own slice of the app's traffic (a per-window
        // bridge, an auxiliary/remote session); failing OPEN on an exception would deliver a
        // notification the app explicitly meant to keep off this channel, which is the more
        // dangerous direction to get wrong.
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
    /// Close the gate — buffer again instead of draining into a client/page/renderer that can no
    /// longer receive. Call from the base; WHICH event triggers this is the base's own decision,
    /// but see the class doc for the trap that decision must avoid: a "navigation started" style
    /// event that does not guarantee the document/session actually changes closes the gate
    /// FOREVER, because the surviving client has already spent its one handshake and nothing else
    /// will ever call <see cref="Open"/> again (P5.5 H3 — <c>WebViewIpcBridge</c> learned this the
    /// hard way with <c>NavigationStarting</c> vs <c>ContentLoading</c>).
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

            // Payloads are APP-supplied objects, so serialization can throw on data the framework
            // never sees until here: a cyclic object graph (parent/child entities), a
            // Type/delegate member, a throwing getter. The queue is already drained at this
            // point, so an unguarded throw would lose the WHOLE batch as well as escaping to the
            // caller. Serialize per-notification and drop only the offender, so one bad event
            // can't take its batch down with it.
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
                    Log(() => $"[Shenora.Ipc] Dropped unserializable notification " +
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
            Log(() => $"[Shenora.Ipc] Notification batch drain failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Guarded + lazy, via the one owner (<see cref="AppCallback.Log"/>) — the sink is app code
    /// invoked from the per-notification guard above and the base's tick, i.e. places with no caller
    /// left to catch anything.
    /// </summary>
    private void Log(Func<string> message) => AppCallback.Log(_options.Log, message);

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
