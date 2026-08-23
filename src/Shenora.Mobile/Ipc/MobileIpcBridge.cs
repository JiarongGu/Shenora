using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Shenora;
using Shenora.Core.Events;
using Shenora.Core.Shell;
using Shenora.Core.Ipc;

namespace Shenora.Mobile;

/// <summary>Inputs for <see cref="MobileIpcBridge"/>.</summary>
public sealed class MobileIpcBridgeOptions
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
    /// What to tell the client this shell is and can do, answered in the handshake so one page can ship to
    /// every shell. Declared by the APP — it depends on what this app composed, not only on the platform.
    /// Null says nothing, which the client reads as "assume nothing".
    /// </summary>
    public ShellInfo? Shell { get; init; }

    /// <summary>Invoked on each ready handshake — the moment to clear per-page state.</summary>
    public Action<IpcRequest>? OnClientReady { get; init; }

    /// <summary>
    /// Release the webview's platform handler when this bridge is disposed. <b>On by default, and turning
    /// it off on Android costs you the app.</b>
    /// </summary>
    /// <remarks>
    /// 🔴 <b>MEASURED, because the failure is a process death nobody can attribute.</b> Android recreates
    /// the window for a font-scale or locale change, which disposes the OLD window's <c>MauiContext</c>
    /// scope — and MAUI's own <c>MauiHybridWebViewClient.ShouldInterceptRequest</c> then resolves a logger
    /// from that dead scope for a request the outgoing webview is still serving, throwing out of a
    /// JNI-invoked override with nothing managed above it. On an API 36 emulator one font-scale change
    /// killed the app in <b>8 of 10</b> trials; released, <b>0 of 10</b> over ten consecutive changes in
    /// one process, with the rebuilt page handshaking every time.
    /// <para>
    /// ⚠ <b>Stopping the webview is NOT the same remedy</b> — the same experiment with the platform view's
    /// <c>StopLoading()</c> was 10 of 10. The HANDLER is what holds the dead scope.
    /// </para>
    /// <para>
    /// ⚠ <b>Turn it off for a page that unloads and RELOADS the same view instance</b> — an ordinary
    /// navigation, where the handler would be pulled out from under a view that is coming back. That case
    /// is unmeasured; a recreation, where MAUI builds a new page and a new view, is what this is for and
    /// what was measured. Then call <see cref="MobileWindowLifecycle.ReleaseHandler"/> yourself at the
    /// point your page knows it is really going away.
    /// </para>
    /// <para>
    /// ⚠ iOS does not recreate a window for a configuration change, so this is a no-op there.
    /// </para>
    /// </remarks>
    public bool ReleaseHandlerOnDispose { get; init; } = true;

    /// <summary>Diagnostics sink.</summary>
    public ILogger? Log { get; init; }
}

/// <summary>
/// The MAUI <c>HybridWebView</c> IPC transport — the peer of <c>Shenora.Windows.WebViewIpcBridge</c>, and
/// much thinner: <see cref="IpcHostBridge"/> owns parse → handshake-or-dispatch → response and the error
/// boundary, <see cref="NotificationPump"/> owns the queue, the ready gate and batch building. What is left
/// here is what only this platform can do — read a message off <c>RawMessageReceived</c>, write one with
/// <c>SendRawMessage</c>, and tick a dispatcher timer.
/// <para>
/// ⚠ <b>The ready gate never CLOSES on a document change.</b> <c>HybridWebView</c> surfaces no
/// document-lifecycle event to close it on, where WebView2 has <c>ContentLoading</c>. Bounded rather than
/// silent: a reloaded page re-handshakes (<see cref="NotificationPump.Open"/> is idempotent), and the window
/// where a flush could reach a page that is going away is the one WebView2 documents too.
/// </para>
/// </summary>
public sealed class MobileIpcBridge : IDisposable
{
    private readonly HybridWebView _webView;
    private readonly MobileIpcBridgeOptions _options;
    private readonly ILogger? _log;
    private readonly NotificationPump _pump;
    private readonly IpcHostBridge _host;
    private readonly IUiDispatcher _ui;
    private IDispatcherTimer? _flushTimer;
    private bool _attached;
    private bool _disposed;

    /// <summary>
    /// Construct BEFORE the page can send anything — event buffering starts here — then
    /// <see cref="Attach"/> once the control is on screen. Options are validated now, so a bad value names
    /// itself at the call site rather than inside a timer.
    /// </summary>
    public MobileIpcBridge(HybridWebView webView, MobileIpcBridgeOptions options)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // Named against THIS type's option names, not the pump's: an adopter setting
        // MaxQueuedNotifications should not get an error about MaxQueued.
        if (options.MaxQueuedNotifications < 1)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(MobileIpcBridgeOptions.MaxQueuedNotifications)} must be at least 1 — 0 would silently discard every notification.");
        if (options.NotificationInterval < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(MobileIpcBridgeOptions.NotificationInterval)} must be at least 1 ms.");

        _log = options.Log;
        _ui = new MobileUiDispatcher(webView.Dispatcher,
            ex => Log(() => "[Shenora.Mobile] Posted UI work failed", ex));

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

    /// <summary>True once the client has completed the ready handshake (notifications flow).</summary>
    public bool IsClientReady => _pump.IsOpen;

    /// <summary>
    /// What became of the notifications emitted so far — read this FIRST when a page reports that it
    /// receives no events. See <see cref="NotificationPump.Report"/>.
    /// </summary>
    public NotificationPumpReport NotificationReport => _pump.Report();

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

        // A dispatcher timer ticks on the UI thread, so the flush needs no marshalling.
        _flushTimer = _webView.Dispatcher.CreateTimer();
        _flushTimer.Interval = _options.NotificationInterval;
        _flushTimer.IsRepeating = true;
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();

        Log(() => "[Shenora.Mobile] IPC bridge attached");
    }

    /// <summary>
    /// Queue a notification for the next batched push (fire-and-forget; delivery starts once the
    /// client is ready). Callable from any thread.
    /// </summary>
    public void SendNotification(string module, string type, object? payload = null, string? scope = null) =>
        _pump.Enqueue(new IpcNotification { Module = module, Type = type, Payload = payload, Scope = scope });

    /// <summary>
    /// Handle a message from the page. <c>async void</c> because it is an event handler, so the try/catch is
    /// mandatory: anything escaping re-throws on the UI thread's synchronization context with no caller to
    /// observe it.
    /// </summary>
    private async void OnRawMessageReceived(object? sender, HybridWebViewRawMessageReceivedEventArgs e)
    {
        try
        {
            if (e.Message is not { Length: > 0 } json) return;

            // ConfigureAwait(true) — stay on the UI thread: the dispatch pipeline preserves the caller's
            // synchronization context BY DESIGN, and SendRawMessage below is UI-affine.
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
            try { _webView.SendRawMessage(json); }
            catch (Exception ex) { Log(() => "[Shenora.Mobile] SendRawMessage failed", ex); }
        });
    }

    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_log, message, exception: failure);

    /// <summary>Stop the timer, detach the handler, cancel the dispatch lifetime and unsubscribe from the
    /// bus. Without this the timer keeps firing into a torn-down page for the life of the process.</summary>
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
            catch (Exception ex) { Log(() => "[Shenora.Mobile] Bridge dispose: could not detach the message handler", ex); }
        }

        // LAST, and only after this bridge has finished its own teardown: releasing the handler takes the
        // platform view apart, so nothing above may still need it. See the option's remarks for what a
        // recreation does to a webview whose handler is left connected — it is not a leak, it is the app.
        if (_options.ReleaseHandlerOnDispose)
        {
            try
            {
                MobileWindowLifecycle.ReleaseHandler(_webView);
                // ⚠ Said on SUCCESS too, not only on failure. Whether this ran is the difference between
                // an app that survives a configuration change and one that dies on it, and an absence of
                // failure lines is not evidence that it happened.
                Log(() => "[Shenora.Mobile] Bridge dispose: released the webview's platform handler");
            }
            catch (Exception ex) { Log(() => "[Shenora.Mobile] Bridge dispose: could not release the webview handler", ex); }
        }
    }
}
