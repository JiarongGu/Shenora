using Microsoft.Extensions.Logging;
using Shenora;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;

namespace Shenora.Mobile;

/// <summary>
/// The part of <see cref="MobileIpcBridge"/> that is not MAUI: option validation, the host bridge and the pump
/// wired together, the message handler, the flush, and the order the teardown runs in. The bridge hands it the
/// three things only a <c>HybridWebView</c> can do — post to the UI thread, write a raw message, tick a timer.
/// <para>
/// Exists so a test can reach it. <c>src/Shenora.Mobile/</c> is source with no csproj, and a MAUI type cannot be
/// referenced from the test project, so this file is compiled into <c>Shenora.Tests</c> as well as into the
/// shells, like <c>AppForegroundTracker</c>: it must stay free of any MAUI type. What stays in the bridge, and is
/// proven only on a device, is the wiring to the control itself — the event hook, the dispatcher timer, the handler release.
/// </para>
/// </summary>
internal sealed class MobileIpcCore : IDisposable
{
    private readonly MobileIpcBridgeOptions _options;
    private readonly ILogger? _log;
    private readonly NotificationPump _pump;
    private readonly IpcHostBridge _host;
    private readonly IUiDispatcher _ui;
    private readonly Action<string> _sendRaw;
    private readonly Func<TimeSpan, Action, Action> _startTimer;
    private Action? _stopTimer;
    private bool _started;
    private bool _disposed;

    /// <param name="options">Validated now, so a bad value names itself at the call site rather than inside a timer.</param>
    /// <param name="ui">Where a message is written to the page, which is UI-affine.</param>
    /// <param name="sendRaw">Write one message to the page. Called on the UI thread.</param>
    /// <param name="startTimer">Run the action on the UI thread at the interval until the returned action is called.</param>
    public MobileIpcCore(MobileIpcBridgeOptions options, IUiDispatcher ui, Action<string> sendRaw,
                         Func<TimeSpan, Action, Action> startTimer)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _sendRaw = sendRaw ?? throw new ArgumentNullException(nameof(sendRaw));
        _startTimer = startTimer ?? throw new ArgumentNullException(nameof(startTimer));

        // Named against THIS type's option names, not the pump's: an adopter setting
        // MaxQueuedNotifications should not get an error about MaxQueued.
        if (options.MaxQueuedNotifications < 1)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(MobileIpcBridgeOptions.MaxQueuedNotifications)} must be at least 1 — 0 would silently discard every notification.");
        if (options.NotificationInterval < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(MobileIpcBridgeOptions.NotificationInterval)} must be at least 1 ms.");

        _log = options.Log;

        _pump = new NotificationPump(new NotificationPumpOptions
        {
            EventBus = options.EventBus,
            FlushInterval = options.NotificationInterval,
            MaxQueued = options.MaxQueuedNotifications,
            Filter = options.NotificationFilter,
            Log = options.Log,
        });

        _host = new IpcHostBridge(new IpcHostBridgeOptions
        {
            Dispatcher = options.Dispatcher,
            Pump = _pump,
            Shell = options.Shell,
            OnClientReady = options.OnClientReady,
            Log = options.Log,
            // ⚠ This bridge's lifetime is the PAGE's, and on mobile the page dies on every activity
            // recreation. Cancelling in-flight dispatches aborts work whose effects are HOST-side — a save
            // mid-picker died OPERATION_CANCELLED with the user's chosen file left empty (measured). The
            // work completes; the response is dropped with the page.
            CancelInFlightOnDispose = false,
        });
    }

    public bool IsClientReady => _pump.IsOpen;

    public NotificationPumpReport NotificationReport => _pump.Report();

    /// <summary>Start the flush timer. Once; the bridge hooks the message event right after, on the same thread.</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException("MobileIpcBridge");
        if (_started) throw new InvalidOperationException("Attach() can only be called once.");
        _started = true;

        _stopTimer = _startTimer(_options.NotificationInterval, Flush);
    }

    public void SendNotification(string module, string type, object? payload, string? scope) =>
        _pump.Enqueue(new IpcNotification { Module = module, Type = type, Payload = payload, Scope = scope });

    /// <summary>
    /// Handle a message from the page. Never throws, because its caller is an event handler: anything escaping
    /// re-throws on the UI thread's synchronization context with no caller to observe it.
    /// </summary>
    public async Task HandleMessageAsync(string? message)
    {
        try
        {
            if (message is not { Length: > 0 } json) return;

            // ConfigureAwait(true) — stay on the UI thread: the dispatch pipeline preserves the caller's
            // synchronization context BY DESIGN, and the write below is UI-affine.
            var response = await _host.HandleIncomingAsync(json).ConfigureAwait(true);
            if (response is not null) Send(response);
        }
        catch (Exception ex)
        {
            Log(() => "[Shenora.Mobile] Unhandled error in the IPC message handler", ex);
        }
    }

    private void Flush()
    {
        // The tick has no caller, so anything escaping becomes an unhandled UI-thread exception repeating
        // every interval.
        try
        {
            if (_pump.TryDrainBatch(out var batchJson) && batchJson is not null) Send(batchJson);
        }
        catch (Exception ex)
        {
            Log(() => "[Shenora.Mobile] Notification flush failed", ex);
        }
    }

    private void Send(string json)
    {
        // Through the ONE marshalling owner. A false return means there is nowhere to post, and dropping is
        // correct — the client's own timeout and re-handshake cover it.
        _ui.Post(() =>
        {
            try { _sendRaw(json); }
            catch (Exception ex) { Log(() => "[Shenora.Mobile] SendRawMessage failed", ex); }
        });
    }

    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_log, message, exception: failure);

    /// <summary>
    /// The host bridge FIRST, before the transport and subscriptions go (it cancels nothing here —
    /// CancelInFlightOnDispose is off), then the timer, then the pump.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _host.Dispose();

        _stopTimer?.Invoke();
        _stopTimer = null;

        _pump.Dispose();
    }
}
