using Microsoft.Extensions.Logging;
using Shenora;
using Shenora.Core.Events;
using Shenora.Core.Shell;
using Shenora.Core.Ipc;
// `WebView2` alone resolves to the NAMESPACE in here, hence the alias.
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Windows;

/// <summary>Inputs for <see cref="WebViewIpcBridge"/>.</summary>
public sealed class WebViewIpcBridgeOptions
{
    /// <summary>The pipeline incoming requests are dispatched into.</summary>
    public required IMessageDispatcher Dispatcher { get; init; }

    /// <summary>
    /// When set, EVERY event emitted on the bus is forwarded to the page as a batched
    /// notification (the family's wildcard-forward pattern). Buffering starts at bridge
    /// CONSTRUCTION so events emitted during the (slow) WebView2 init aren't lost; delivery
    /// starts once the client reports ready. Null = the app pushes notifications itself via
    /// <see cref="WebViewIpcBridge.SendNotification"/>.
    /// </summary>
    public IEventBus? EventBus { get; init; }

    /// <summary>
    /// Notification flush interval. ~50 ms is the family's measured sweet spot: a busy backend
    /// can fire hundreds of events a second, and one batched post beats hundreds of round trips
    /// while staying imperceptible to the UI.
    /// </summary>
    public TimeSpan NotificationInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Cap on buffered notifications. If the client never becomes ready (init stalled or failed)
    /// the queue would otherwise grow without bound until OOM. Over the cap the OLDEST is
    /// dropped — notifications are telemetry-like (progress/status); losing stale ones under
    /// overflow is fine, an OOM isn't. (The family's measured cap.)
    /// </summary>
    public int MaxQueuedNotifications { get; init; } = 10_000;

    /// <summary>
    /// Per-channel delivery policy, applied at ENQUEUE (a direct <see cref="WebViewIpcBridge.SendNotification"/>
    /// call AND a forwarded bus event alike). Default: deliver everything. This is the seam that lets
    /// one bridge per window, or an auxiliary/remote session, receive only the slice of the app's
    /// traffic it should — every bridge subscribing with the bus's wildcard forward otherwise means
    /// every event reaches every window. Forwarded to <see cref="Shenora.Core.Ipc.NotificationPumpOptions.Filter"/>.
    /// </summary>
    public Func<IpcNotification, bool>? NotificationFilter { get; init; }

    /// <summary>
    /// What to tell the client this shell is and can do, answered in the handshake so one page can
    /// ship to every shell. Declared by the APP because it depends on what this app composed — a
    /// desktop host that never mapped <c>WindowCommandModule</c> has no window chrome to advertise,
    /// whatever platform it is on. A typical frameless composition here declares
    /// <c>WindowChrome</c>, <c>DropZones</c> and the picker capabilities.
    /// </summary>
    public ShellInfo? Shell { get; init; }

    /// <summary>
    /// Invoked on the ready handshake with the handshake request (its payload is app-defined).
    /// Fires PER handshake — a reloaded page (renderer-crash recovery, dev hot reload) reports
    /// ready again, which is the moment to clear per-page state (stale overlays, splash).
    /// A callback exception is logged and the handshake still succeeds.
    /// </summary>
    public Action<IpcRequest>? OnClientReady { get; init; }

    /// <summary>Diagnostics sink.</summary>
    public ILogger? Log { get; init; }
}

/// <summary>
/// The WebView2 postMessage IPC transport: client requests come in over
/// <c>WebMessageReceived</c> and are dispatched through the app's
/// <see cref="IMessageDispatcher"/>; responses and batched <see cref="IpcNotificationBatch"/>
/// pushes go back via <c>PostWebMessageAsString</c>. Merged from the two family transports
/// (the correlated postMessage handler of the primary desktop sibling; the bounded buffered
/// event push of Sonora) with their post-mortem comments kept.
///
/// Composition order (the sample app is the reference): construct the bridge BEFORE
/// <see cref="WebViewHost.InitializeAsync"/> (event buffering starts at construction, so events
/// emitted during the slow WebView2 init survive), call <see cref="Attach"/> after it, then
/// <see cref="WebViewHost.Navigate"/>. Construct and attach on the UI thread that owns the
/// control. Dispose with the owning window — the source app's transport once kept its flush
/// timer firing for the life of the process, posting into a torn-down WebView.
/// </summary>
public sealed class WebViewIpcBridge : IDisposable
{
    /// <summary>
    /// Reserved wire route: the client's ready handshake module (mirrored by the client bridge).
    /// Forwards to <see cref="IpcHostBridge.HandshakeModule"/>, which is where the wire contract
    /// lives now that a non-WebView2 base needs it too — a <c>const</c> forward, so the literal
    /// every consumer compiled against is unchanged.
    /// </summary>
    public const string HandshakeModule = IpcHostBridge.HandshakeModule;

    /// <summary>Reserved wire route: the client's ready handshake type (mirrored by the client bridge).</summary>
    public const string HandshakeType = IpcHostBridge.HandshakeType;

    private readonly WebView2Control _webView;
    private readonly WebViewIpcBridgeOptions _options;
    private readonly ILogger? _log;
    private readonly Shenora.Core.Shell.IUiDispatcher _ui;
    private readonly NotificationPump _pump;
    private readonly IpcHostBridge _host;
    private System.Windows.Forms.Timer? _flushTimer;
    private bool _attached;
    private bool _disposed;

    /// <summary>
    /// Construct BEFORE <see cref="WebViewHost.InitializeAsync"/> — event buffering starts here, so
    /// anything emitted during the slow WebView2 init survives — then <see cref="Attach"/> after it.
    /// Options are validated now, not at <see cref="Attach"/>, so a bad value names itself.
    /// </summary>
    public WebViewIpcBridge(WebView2Control webView, WebViewIpcBridgeOptions options)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // Validated against the BRIDGE's option names, not the pump's — an adopter who set
        // MaxQueuedNotifications should not get an error naming NotificationPumpOptions.MaxQueued.
        if (options.MaxQueuedNotifications < 1)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(WebViewIpcBridgeOptions.MaxQueuedNotifications)} must be at least 1 — 0 would silently discard every notification.");
        if (options.NotificationInterval < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(WebViewIpcBridgeOptions.NotificationInterval)} must be at least 1 ms.");

        // A WinForms Timer's Interval is an int32 millisecond count — a fact belonging to whichever base
        // constructs the timer. Checked before it is constructed, so the failure names this option.
        if (options.NotificationInterval.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(WebViewIpcBridgeOptions.NotificationInterval)} must fit in an int32 millisecond count (the WinForms timer's limit).");

        _log = options.Log;
        // The one marshalling owner (D19/D20): a posted body that throws is reported rather than
        // becoming an unhandled UI-thread exception.
        _ui = new Shenora.Windows.WinFormsUiDispatcher(webView,
            ex => AppCallback.Log(options.Log, () => "[Shenora.Windows] Posted UI work failed",
                                  LogLevel.Warning, ex));

        _pump = new NotificationPump(new NotificationPumpOptions
        {
            EventBus = options.EventBus,
            FlushInterval = options.NotificationInterval,
            MaxQueued = options.MaxQueuedNotifications,
            Filter = options.NotificationFilter,
            Log = options.Log,
        });

        // The pump goes to the host bridge so the HANDSHAKE opens the gate in one place — that pairing
        // is protocol. CLOSING it stays here, because which events mean "the page can no longer receive"
        // is WebView2 vocabulary. See docs/design/ipc.md for the split.
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
    /// Hook <c>WebMessageReceived</c> and start the flush timer. Call on the UI thread after
    /// <see cref="WebViewHost.InitializeAsync"/> (the core must exist) and BEFORE
    /// <see cref="WebViewHost.Navigate"/> — hooking after navigation loses early messages.
    /// </summary>
    public void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attached) throw new InvalidOperationException("Attach() can only be called once.");
        if (_webView.CoreWebView2 is null)
            throw new InvalidOperationException(
                "CoreWebView2 is not initialized — call WebViewHost.InitializeAsync (or " +
                "EnsureCoreWebView2Async) before Attach().");
        _attached = true;

        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        // 🔴 ContentLoading, NEVER NavigationStarting. The latter also fires for navigations that never
        // replace the document, and the surviving page has already spent its one READY — so the gate
        // would close FOREVER. The window between the two is deliberate: those listeners are still
        // attached, so live delivery beats buffering for a document that may never load.
        _webView.CoreWebView2.ContentLoading += OnContentLoading;
        // A dead renderer leaves the gate open, so the next tick drains a batch into a process that
        // cannot receive it — and the queue is already emptied. Watched here rather than relying on the
        // host's auto-reload policy, which is optional.
        _webView.CoreWebView2.ProcessFailed += OnProcessFailed;

        // A WinForms timer ticks on the UI thread, the only one allowed to touch CoreWebView2.
        _flushTimer = new System.Windows.Forms.Timer { Interval = (int)_options.NotificationInterval.TotalMilliseconds };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();

        Log(() => "[Shenora.Windows] IPC bridge attached");
    }

    /// <summary>
    /// Guarded + lazy, via the one owner (<see cref="Shenora.AppCallback.Log"/>). Every site
    /// here has no caller to catch anything — a WebView2 event handler, the flush timer's tick,
    /// dispose — and several sit inside a <c>catch</c> that exists to stop a failure escaping, so a
    /// throwing sink would defeat the very catch it reports from.
    /// </summary>
    private void Log(Func<string> message, Exception? failure = null) => Shenora.AppCallback.Log(_log, message, exception: failure);

    /// <summary>
    /// Queue a notification for the next batched push (fire-and-forget; delivery starts once the
    /// client is ready). Callable from any thread. Apps using <see cref="WebViewIpcBridgeOptions.EventBus"/>
    /// rarely call this directly — emitting on the bus reaches every attached bridge.
    /// </summary>
    public void SendNotification(string module, string type, object? payload = null, string? scope = null) =>
        _pump.Enqueue(new IpcNotification { Module = module, Type = type, Payload = payload, Scope = scope });

    /// <summary>
    /// Handle messages from the page. Async ON THE UI THREAD, so concurrent IPC calls interleave
    /// without a thread-pool thread each.
    /// <para>
    /// 🔴 <b>Never <c>Task.Run</c> per message.</b> Under load that starves the pool and makes even
    /// trivial IPC time out. Heavy work belongs in the backend's own bounded queues.
    /// </para>
    /// </summary>
    private async void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        // async void: anything that escapes re-throws on the UI thread's sync context and kills the process.
        try
        {
            string? json;
            try
            {
                json = e.TryGetWebMessageAsString();
            }
            catch (Exception ex)
            {
                // Non-string message — our client always posts strings.
                Log(() => "[Shenora.Windows] Ignored non-string web message", ex);
                return;
            }
            if (json is null) return;

            var response = await HandleIncomingAsync(json).ConfigureAwait(true); // stay on the UI thread
            if (response is not null)
                PostJson(response);
        }
        catch (Exception ex)
        {
            Log(() => "[Shenora.Windows] Unhandled error in the IPC message handler", ex);
        }
    }

    private void OnContentLoading(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2ContentLoadingEventArgs e) =>
        ResetClientReady("a new document is loading");

    private void OnProcessFailed(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedEventArgs e) =>
        ResetClientReady($"the browser process failed ({e.ProcessFailedKind})");

    /// <summary>Close the ready gate — the page that handshook can no longer receive. Internal seam for tests.</summary>
    internal void ResetClientReady(string reason = "the page is being replaced")
    {
        if (!_pump.IsOpen) return;
        _pump.Close();
        // The pump itself no longer logs this transition — ContentLoading/ProcessFailed are WebView2
        // vocabulary that belongs here, not in a base-agnostic type. Kept so the diagnostic survives:
        // a gate that closes silently is very hard to debug (P5.5 H3 was found the hard way without it).
        Log(() => $"[Shenora.Windows] Buffering notifications until the client is ready again — {reason}");
    }

    /// <summary>
    /// Parse → handshake-or-dispatch → response JSON. Null when the input wasn't a valid request
    /// (nothing to correlate a response to — logged and dropped; the client's own timeout
    /// surfaces it). Internal seam so the protocol is testable without a live WebView2.
    /// <para>
    /// The protocol itself lives in <see cref="IpcHostBridge"/> now; this forward is kept so the
    /// bridge's own suite still exercises the WebView2 composition end to end rather than the
    /// neutral piece in isolation — which is what makes it a regression test for the move.
    /// </para>
    /// </summary>
    internal Task<string?> HandleIncomingAsync(string json) => _host.HandleIncomingAsync(json);

    private void Flush()
    {
        // Belt-and-braces around a call that already guards itself: this runs on a WinForms timer, so
        // anything escaping is an unhandled UI-thread exception repeating every interval.
        try
        {
            if (_pump.TryDrainBatch(out var batchJson) && batchJson is not null)
                PostJson(batchJson);
        }
        catch (Exception ex)
        {
            Log(() => "[Shenora.Windows] Notification flush failed", ex);
        }
    }

    /// <summary>
    /// Drain the queue into a batch envelope; null when empty or the client isn't ready yet. Thin
    /// wrapper over <see cref="NotificationPump.TryDrainBatch"/>. Internal seam for tests.
    /// </summary>
    internal string? TryBuildBatchJson() => _pump.TryDrainBatch(out var json) ? json : null;

    /// <summary>Buffered notification count. Internal seam for tests.</summary>
    internal int PendingNotificationCount => _pump.PendingCount;

    private void PostJson(string json)
    {
        try
        {
            // No handle yet, or already torn down → Post returns false and we DROP; the client's
            // timeout handles the rest.
            _ui.Post(() => _webView.CoreWebView2?.PostWebMessageAsString(json));
        }
        catch
        {
            // window tearing down mid-post
        }
    }

    /// <summary>
    /// Stop the flush timer, detach the message handler, unsubscribe from the bus. Without it a 50 ms
    /// timer goes on posting into a torn-down WebView for the life of the process.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // ⚠ Signal the lifetime FIRST: an in-flight handler should learn the page is gone while its
        // await can still act on it, not after the timer and subscriptions are pulled out from under it.
        _host.Dispose();

        _flushTimer?.Stop();
        _flushTimer?.Dispose();
        _pump.Dispose();

        // Best-effort: CoreWebView2 may already be gone at teardown.
        try
        {
            if (_attached && _webView.CoreWebView2 is not null)
            {
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _webView.CoreWebView2.ContentLoading -= OnContentLoading;
                _webView.CoreWebView2.ProcessFailed -= OnProcessFailed;
            }
        }
        catch (Exception ex)
        {
            Log(() => "[Shenora.Windows] Bridge dispose: could not detach WebView2 handlers", ex);
        }
    }
}
