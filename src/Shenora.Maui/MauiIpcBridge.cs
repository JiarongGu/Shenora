using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Shenora.Core;
using Shenora.Ipc;

namespace Shenora.Maui;

/// <summary>Inputs for <see cref="MauiIpcBridge"/>.</summary>
public sealed class MauiIpcBridgeOptions
{
    /// <summary>The pipeline incoming requests are dispatched into.</summary>
    public required IMessageDispatcher Dispatcher { get; init; }

    /// <summary>
    /// When set, every event emitted on the bus is forwarded to the page as a batched notification.
    /// Buffering starts at bridge CONSTRUCTION, so events emitted while the page is still loading
    /// are not lost; delivery starts once the client reports ready.
    /// </summary>
    public IEventBus? EventBus { get; init; }

    /// <summary>Notification flush interval — the family's measured ~50 ms / 20 fps default.</summary>
    public TimeSpan NotificationInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Cap on buffered notifications; over the cap the OLDEST is dropped.</summary>
    public int MaxQueuedNotifications { get; init; } = 10_000;

    /// <summary>Per-channel delivery policy, applied at enqueue. Default: deliver everything.</summary>
    public Func<IpcNotification, bool>? NotificationFilter { get; init; }

    /// <summary>
    /// What to tell the client this shell is and can do, answered in the handshake so one page can
    /// ship to every shell. Declared by the APP — it depends on what this app composed, not only on
    /// the platform. Null says nothing, which the client reads as "assume nothing".
    /// </summary>
    public ShellInfo? Shell { get; init; }

    /// <summary>Invoked on each ready handshake — the moment to clear per-page state.</summary>
    public Action<IpcRequest>? OnClientReady { get; init; }

    /// <summary>Diagnostics sink.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// The MAUI <c>HybridWebView</c> IPC transport — the peer of
/// <c>Shenora.WebView2.WebViewIpcBridge</c>, and deliberately much thinner than it, because
/// everything that is not transport already moved into <c>Shenora.Ipc</c>:
/// <see cref="IpcHostBridge"/> owns parse → handshake-or-dispatch → response and the error boundary,
/// <see cref="NotificationPump"/> owns the queue, the ready gate and batch building. What is left
/// here is what only this platform can do: read a message off <c>RawMessageReceived</c>, write one
/// with <c>SendRawMessage</c>, and tick a dispatcher timer.
/// <para>
/// That thinness is the whole point of the D3 spike's finding — a second base should inherit the
/// already-fixed bugs rather than re-earn them.
/// </para>
/// <para>
/// <b>One capability the WebView2 bridge has and this one cannot:</b> closing the ready gate when the
/// document changes. WebView2 exposes <c>ContentLoading</c>/<c>ProcessFailed</c>; <c>HybridWebView</c>
/// surfaces no document-lifecycle event, so there is nothing to close the gate ON. The consequence is
/// bounded rather than silent: a reloaded page simply re-handshakes (<see cref="NotificationPump.Open"/>
/// is idempotent), and the window where a flush could reach a page that is going away is the same
/// deliberate one WebView2 documents between navigation start and content loading. Recorded here
/// rather than papered over.
/// </para>
/// </summary>
public sealed class MauiIpcBridge : IDisposable
{
    private readonly HybridWebView _webView;
    private readonly MauiIpcBridgeOptions _options;
    private readonly Action<string>? _log;
    private readonly NotificationPump _pump;
    private readonly IpcHostBridge _host;
    private readonly IUiDispatcher _ui;
    private IDispatcherTimer? _flushTimer;
    private bool _attached;
    private bool _disposed;

    /// <summary>
    /// Construct BEFORE the page can send anything — event buffering starts here — then
    /// <see cref="Attach"/> once the control is on screen. Options are validated now, so a bad value
    /// names itself at the call site rather than inside a timer.
    /// </summary>
    public MauiIpcBridge(HybridWebView webView, MauiIpcBridgeOptions options)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // Named against THIS type's option names, not the pump's — an adopter setting
        // MaxQueuedNotifications should not get an error about MaxQueued (the same self-naming rule
        // WebViewIpcBridge follows). The pump re-validates too; that is defence in depth.
        if (options.MaxQueuedNotifications < 1)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(MauiIpcBridgeOptions.MaxQueuedNotifications)} must be at least 1 — 0 would silently discard every notification.");
        if (options.NotificationInterval < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(MauiIpcBridgeOptions.NotificationInterval)} must be at least 1 ms.");

        _log = options.Log;
        _ui = new MauiUiDispatcher(webView.Dispatcher,
            ex => Log(() => $"[Shenora.Maui] Posted UI work failed: {ex.Message}"));

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
        });
    }

    /// <summary>True once the client has completed the ready handshake (notifications flow).</summary>
    public bool IsClientReady => _pump.IsOpen;

    /// <summary>
    /// Hook <c>RawMessageReceived</c> and start the flush timer. Call once, on the UI thread, before
    /// the page loads — hooking afterwards loses the handshake and every message before it.
    /// </summary>
    public void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attached) throw new InvalidOperationException("Attach() can only be called once.");
        _attached = true;

        _webView.RawMessageReceived += OnRawMessageReceived;

        // A dispatcher timer ticks on the UI thread — the only thread allowed to touch the control —
        // so the flush needs no marshalling, exactly like the WinForms Forms.Timer it mirrors.
        _flushTimer = _webView.Dispatcher.CreateTimer();
        _flushTimer.Interval = _options.NotificationInterval;
        _flushTimer.IsRepeating = true;
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();

        Log(() => "[Shenora.Maui] IPC bridge attached");
    }

    /// <summary>
    /// Queue a notification for the next batched push (fire-and-forget; delivery starts once the
    /// client is ready). Callable from any thread.
    /// </summary>
    public void SendNotification(string module, string type, object? payload = null, string? scope = null) =>
        _pump.Enqueue(new IpcNotification { Module = module, Type = type, Payload = payload, Scope = scope });

    /// <summary>
    /// Handle a message from the page. <c>async void</c> because it is an event handler, so the
    /// try/catch is mandatory rather than defensive: anything escaping here re-throws on the UI
    /// thread's synchronization context with no caller to observe it.
    /// </summary>
    private async void OnRawMessageReceived(object? sender, HybridWebViewRawMessageReceivedEventArgs e)
    {
        try
        {
            if (e.Message is not { Length: > 0 } json) return;

            // ConfigureAwait(true) — stay on the UI thread. The dispatch pipeline preserves the
            // caller's synchronization context BY DESIGN (§5), and SendRawMessage below is
            // UI-affine, so hopping off here would break both.
            var response = await _host.HandleIncomingAsync(json).ConfigureAwait(true);
            if (response is not null) Send(response);
        }
        catch (Exception ex)
        {
            Log(() => $"[Shenora.Maui] Unhandled error in the IPC message handler: {ex}");
        }
    }

    private void Flush()
    {
        // TryDrainBatch never throws by contract, but this stays for the same reason the WinForms
        // bridge keeps it: the tick has no caller, so anything escaping becomes an unhandled
        // UI-thread exception repeating every interval.
        try
        {
            if (_pump.TryDrainBatch(out var batchJson) && batchJson is not null) Send(batchJson);
        }
        catch (Exception ex)
        {
            Log(() => $"[Shenora.Maui] Notification flush failed: {ex.Message}");
        }
    }

    private void Send(string json)
    {
        // Through the ONE marshalling owner: a false return means there is nowhere to post, and
        // dropping is correct — the client's own timeout and re-handshake cover it.
        _ui.Post(() =>
        {
            try { _webView.SendRawMessage(json); }
            catch (Exception ex) { Log(() => $"[Shenora.Maui] SendRawMessage failed: {ex.Message}"); }
        });
    }

    private void Log(Func<string> message) => AppCallback.Log(_log, message);

    /// <summary>
    /// Stop the timer, detach the handler, cancel the dispatch lifetime and unsubscribe from the bus.
    /// Without this the timer keeps firing into a torn-down page for the life of the process.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Signal FIRST, so an in-flight handler learns the page is gone while its await can still
        // act on it — IpcHostBridge.Dispose owns the cancellation and its guard.
        _host.Dispose();

        _flushTimer?.Stop();
        _flushTimer = null;

        _pump.Dispose();

        if (_attached)
        {
            try { _webView.RawMessageReceived -= OnRawMessageReceived; }
            catch (Exception ex) { Log(() => $"[Shenora.Maui] Bridge dispose: could not detach the message handler ({ex.Message})"); }
        }
    }
}
