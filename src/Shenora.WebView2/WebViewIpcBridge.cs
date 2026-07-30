using System.Collections.Concurrent;
using Shenora.Core;
using Shenora.Ipc;
// Inside namespace Shenora.WebView2 the bare identifier "WebView2" resolves to the namespace, so
// the control type needs an alias.
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.WebView2;

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
    /// Invoked on the ready handshake with the handshake request (its payload is app-defined).
    /// Fires PER handshake — a reloaded page (renderer-crash recovery, dev hot reload) reports
    /// ready again, which is the moment to clear per-page state (stale overlays, splash).
    /// A callback exception is logged and the handshake still succeeds.
    /// </summary>
    public Action<IpcRequest>? OnClientReady { get; init; }

    /// <summary>Diagnostics sink.</summary>
    public Action<string>? Log { get; init; }
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
    /// <summary>Reserved wire route: the client's ready handshake module (mirrored by the client bridge).</summary>
    public const string HandshakeModule = "SHENORA";

    /// <summary>Reserved wire route: the client's ready handshake type (mirrored by the client bridge).</summary>
    public const string HandshakeType = "READY";

    private readonly WebView2Control _webView;
    private readonly WebViewIpcBridgeOptions _options;
    private readonly Action<string>? _log;
    private readonly ConcurrentQueue<IpcNotification> _pending = new();
    private int _pendingCount;
    private string? _busSubscriptionId;
    private System.Windows.Forms.Timer? _flushTimer;
    private volatile bool _clientReady;
    private bool _attached;
    private bool _disposed;

    public WebViewIpcBridge(WebView2Control webView, WebViewIpcBridgeOptions options)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = options.Log;

        // Subscribe NOW, not at Attach: the bus hands us events from any thread and the queue
        // buffers them until the client is ready — so nothing emitted during WebView2 init or
        // page load is lost (the buffered-startup lesson from the server-backed sibling).
        if (_options.EventBus is { } bus)
        {
            _busSubscriptionId = bus.SubscribeToAll(message =>
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

    /// <summary>True once the client has completed the ready handshake (notifications flow).</summary>
    public bool IsClientReady => _clientReady;

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
        // Any main-document navigation (renderer-crash auto-reload, dev reload) replaces the
        // page and its listeners — close the ready gate so notifications BUFFER again until the
        // new page's handshake, instead of draining into a document with no subscriber.
        _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;

        // A WinForms timer ticks on the UI thread — the only thread allowed to touch
        // CoreWebView2 — so the flush needs no marshalling.
        _flushTimer = new System.Windows.Forms.Timer { Interval = (int)_options.NotificationInterval.TotalMilliseconds };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();

        _log?.Invoke("[Shenora.WebView2] IPC bridge attached");
    }

    /// <summary>
    /// Queue a notification for the next batched push (fire-and-forget; delivery starts once the
    /// client is ready). Callable from any thread. Apps using <see cref="WebViewIpcBridgeOptions.EventBus"/>
    /// rarely call this directly — emitting on the bus reaches every attached bridge.
    /// </summary>
    public void SendNotification(string module, string type, object? payload = null, string? scope = null) =>
        Enqueue(new IpcNotification { Module = module, Type = type, Payload = payload, Scope = scope });

    private void Enqueue(IpcNotification notification)
    {
        _pending.Enqueue(notification);
        // Bound the buffer (see MaxQueuedNotifications): over the cap, drop the oldest to make room.
        if (Interlocked.Increment(ref _pendingCount) > _options.MaxQueuedNotifications && _pending.TryDequeue(out _))
            Interlocked.Decrement(ref _pendingCount);
    }

    /// <summary>
    /// Handle messages from the page. Async ON THE UI THREAD: each <c>await</c> yields the
    /// message pump, so concurrent IPC calls still interleave (the frontend gets concurrency)
    /// WITHOUT consuming a thread-pool thread per call. A previous family version offloaded
    /// every message to <c>Task.Run</c>, but under heavy backend load (bulk work already
    /// saturating the pool) that starved the thread pool and made even trivial IPC time out,
    /// freezing the app. Heavy work belongs in the backend's own bounded queues — the transport
    /// must never fan out onto the pool.
    /// </summary>
    private async void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Last-resort guard: this is an async-void event handler — anything that escaped would
        // re-throw on the UI thread's sync context and take the process down.
        try
        {
            string? json;
            try
            {
                json = e.TryGetWebMessageAsString();
            }
            catch (Exception ex)
            {
                // Non-string message (postMessage of a raw object) — our client always posts strings.
                _log?.Invoke($"[Shenora.WebView2] Ignored non-string web message: {ex.Message}");
                return;
            }
            if (json is null) return;

            var response = await HandleIncomingAsync(json).ConfigureAwait(true); // stay on the UI thread
            if (response is not null)
                PostJson(response);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Shenora.WebView2] Unhandled error in the IPC message handler: {ex}");
        }
    }

    private void OnNavigationStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e) =>
        ResetClientReady();

    /// <summary>Close the ready gate (a new document is coming). Internal seam for tests.</summary>
    internal void ResetClientReady()
    {
        if (!_clientReady) return;
        _clientReady = false;
        _log?.Invoke("[Shenora.WebView2] Navigation started — buffering notifications until the client is ready again");
    }

    /// <summary>
    /// Parse → handshake-or-dispatch → response JSON. Null when the input wasn't a valid request
    /// (nothing to correlate a response to — logged and dropped; the client's own timeout
    /// surfaces it). Internal seam so the protocol is testable without a live WebView2.
    /// </summary>
    internal async Task<string?> HandleIncomingAsync(string json)
    {
        IpcRequest? request;
        try
        {
            request = IpcJson.Deserialize<IpcRequest>(json);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Shenora.WebView2] Invalid IPC message dropped: {ex.Message}");
            return null;
        }
        if (request is null) return null;

        try
        {
            if (string.Equals(request.Module, HandshakeModule, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(request.Type, HandshakeType, StringComparison.OrdinalIgnoreCase))
            {
                return IpcJson.Serialize(HandleHandshake(request));
            }

            var response = await _options.Dispatcher.DispatchAsync(request).ConfigureAwait(true);
            return IpcJson.Serialize(response);
        }
        catch (Exception ex)
        {
            // MessageDispatcher never throws, but IMessageDispatcher is a public seam (an app
            // implementation carries no such guarantee) — and Serialize itself can throw on an
            // unserializable handler result (cycles, Type/delegate members). The client must
            // still get a response, and per design §5 it learns nothing but the code.
            _log?.Invoke($"[Shenora.WebView2] Error handling {request.Module}/{request.Type}: {ex}");
            return IpcJson.Serialize(IpcResponse.CreateError(request.Id, IpcErrorCodes.UnknownError,
                parameters: new Dictionary<string, string> { ["exceptionType"] = ex.GetType().Name }));
        }
    }

    private IpcResponse HandleHandshake(IpcRequest request)
    {
        _clientReady = true;
        _log?.Invoke("[Shenora.WebView2] Client ready");
        try
        {
            _options.OnClientReady?.Invoke(request);
        }
        catch (Exception ex)
        {
            // Per-page glue (splash, overlays) failing must not fail the client's init await.
            _log?.Invoke($"[Shenora.WebView2] OnClientReady callback failed: {ex.Message}");
        }
        return IpcResponse.CreateSuccess(request.Id);
    }

    private void Flush()
    {
        // Catch-all: this runs on a WinForms timer, so ANYTHING that escapes here is an unhandled
        // UI-thread exception — a modal crash dialog under the family bootstrap, repeating every
        // interval. The incoming path has always been guarded; this one was not (found in the P0–P5
        // review).
        try
        {
            var batchJson = TryBuildBatchJson();
            if (batchJson is null) return;
            PostJson(batchJson);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Shenora.WebView2] Notification flush failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Drain the queue into a batch envelope; null when empty or the client isn't ready yet.
    /// Internal seam for tests.
    /// </summary>
    internal string? TryBuildBatchJson()
    {
        // Hold delivery (queue intact) until the page's listeners exist — a batch posted before
        // the client subscribed would be silently lost, which is worse than arriving 50 ms late.
        if (!_clientReady) return null;
        if (_pending.IsEmpty) return null;

        var batch = new List<IpcNotification>();
        while (_pending.TryDequeue(out var notification))
        {
            Interlocked.Decrement(ref _pendingCount);
            batch.Add(notification);
        }
        if (batch.Count == 0) return null;

        // Payloads are APP-supplied objects, so serialization can throw on data the framework never
        // sees until here: a cyclic object graph (parent/child entities), a Type/delegate member, a
        // throwing getter. The queue is already drained at this point, so an unguarded throw lost the
        // WHOLE batch as well as crashing the UI thread. Serialize per-notification and drop only the
        // offender, so one bad event can't take its batch down with it.
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
                // Module/type only — a payload that fails to serialize must not have its contents
                // logged either (it may carry app data).
                _log?.Invoke($"[Shenora.WebView2] Dropped unserializable notification " +
                             $"{notification.Module}/{notification.Type}: {ex.GetType().Name}");
            }
        }
        if (serializable.Count == 0) return null;

        return IpcJson.Serialize(new IpcNotificationBatch { Payload = serializable });
    }

    /// <summary>Buffered notification count. Internal seam for tests.</summary>
    internal int PendingNotificationCount => _pendingCount;

    private void PostJson(string json)
    {
        try
        {
            // IsHandleCreated FIRST: before the handle exists InvokeRequired lies (false on a
            // pool thread), so it must never gate an inline call on its own (the family's
            // measured trap — see .claude/knowledge/webview2-hosting.md). No handle yet or
            // already torn down → drop; the client's timeout/reconnect handles the rest.
            if (!_webView.IsHandleCreated) return;
            if (_webView.InvokeRequired)
            {
                _webView.BeginInvoke(() =>
                {
                    try { _webView.CoreWebView2?.PostWebMessageAsString(json); }
                    catch { /* window tearing down mid-post */ }
                });
            }
            else
            {
                _webView.CoreWebView2?.PostWebMessageAsString(json);
            }
        }
        catch
        {
            // window tearing down mid-post
        }
    }

    /// <summary>
    /// Stop the flush timer, detach the message handler, unsubscribe from the bus. Without this
    /// the source app's 50 ms timer kept firing (and posting to a torn-down WebView) for the
    /// life of the process after the owning window was disposed.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _flushTimer?.Stop();
        _flushTimer?.Dispose();

        if (_busSubscriptionId is { } id)
        {
            _options.EventBus?.Unsubscribe(id);
            _busSubscriptionId = null;
        }

        // Best-effort: CoreWebView2 may already be gone at teardown.
        try
        {
            if (_attached && _webView.CoreWebView2 is not null)
            {
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _webView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Shenora.WebView2] Bridge dispose: could not detach WebView2 handlers ({ex.Message})");
        }
    }
}
