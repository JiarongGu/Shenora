using Shenora;
using Shenora.Core.Events;
using Shenora.Core.Shell;
using Shenora.Core.Ipc;
// Inside namespace Shenora.Windows the bare identifier "WebView2" resolves to the namespace, so
// the control type needs an alias.
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
    private readonly Action<string>? _log;
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

        // Lower bounds, validated HERE and named against the BRIDGE's own options (ALSO IN THIS
        // BATCH, whole-branch review). Before this, both surfaced ONLY from NotificationPump's own
        // constructor below, naming NotificationPumpOptions.MaxQueued/FlushInterval — a type the
        // adopter setting WebViewIpcBridgeOptions.MaxQueuedNotifications/NotificationInterval never
        // touched, the same self-naming defect the upper-bound check right below already avoids.
        // The pump's own checks still run too (constructing it with these values), so this is
        // defense-in-depth with a better message, not a replacement for the pump's validation.
        if (options.MaxQueuedNotifications < 1)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(WebViewIpcBridgeOptions.MaxQueuedNotifications)} must be at least 1 — 0 would silently discard every notification.");
        if (options.NotificationInterval < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(WebViewIpcBridgeOptions.NotificationInterval)} must be at least 1 ms.");

        // Bridge-specific, NOT carried by NotificationPump (P5.5 H3, re-added here): a
        // System.Windows.Forms.Timer's Interval is an int32 millisecond count, a fact that belongs to
        // whichever base actually constructs that timer — this one. NotificationInterval below 1 ms
        // (checked just above) truncates to 0 and used to throw out of Attach() instead, as an opaque
        // ArgumentOutOfRangeException from the WinForms Timer's own setter, at a call site that has
        // nothing to do with the option that caused it. Checked HERE, before the timer is ever
        // constructed, for the same reason.
        if (options.NotificationInterval.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(WebViewIpcBridgeOptions.NotificationInterval)} must fit in an int32 millisecond count (the WinForms timer's limit).");

        _log = options.Log;
        // The one marshalling owner (D19/D20) — reachable because Shenora.Windows layers on
        // Shenora.Windows. A posted body that throws is reported here instead of becoming an
        // unhandled UI-thread exception.
        _ui = new Shenora.Windows.WinFormsUiDispatcher(webView,
            ex => options.Log?.Invoke($"[Shenora.Windows] Posted UI work failed: {ex.Message}"));

        // Everything transport-neutral — the bounded drop-oldest queue, the ready gate, batch
        // building, the per-notification serialize guard, and the bus subscription itself (which
        // starts buffering at CONSTRUCTION, not at Attach, so nothing emitted during the slow WebView2
        // init is lost) — lives in NotificationPump now (design §5). The pump's own construction
        // re-validates MaxQueuedNotifications/NotificationInterval (its MaxQueued/FlushInterval) with
        // its own self-naming messages.
        _pump = new NotificationPump(new NotificationPumpOptions
        {
            EventBus = options.EventBus,
            FlushInterval = options.NotificationInterval,
            MaxQueued = options.MaxQueuedNotifications,
            Filter = options.NotificationFilter,
            Log = options.Log,
        });

        // The inbound protocol — the dispatch lifetime, deserialize, the handshake, the error
        // boundary — is transport-neutral and moved to Shenora.Ipc for the same reason the outbound
        // half did: the D3 spike proved a second base rewrites it identically. What stays HERE is
        // everything WebView2: the Forms.Timer, the event wiring, PostWebMessageAsString.
        //
        // The pump is handed over so the HANDSHAKE opens the gate in one place — that pairing is
        // protocol. Closing it stays below, because which events mean "the page can no longer
        // receive" is WebView2 vocabulary (ContentLoading, ProcessFailed) and getting that choice
        // wrong is P5.5 H3.
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
        // A NEW document is loading, so the old page's listeners are gone — close the ready gate so
        // notifications BUFFER until the new page's handshake instead of draining into a document with
        // no subscriber.
        //
        // ContentLoading, NOT NavigationStarting (P5.5 H3). NavigationStarting fires for navigations
        // that never replace the document — one an app tap or a policy CANCELS, one that fails before
        // committing — and the surviving page has already sent its one READY, so the gate closed
        // FOREVER: notifications buffered to the 10 000 cap and then silently dropped the oldest, for
        // the life of the process. ContentLoading is raised only when a new document actually begins
        // loading, which is exactly the condition the gate cares about.
        //
        // The trade, stated plainly: between NavigationStarting and ContentLoading the gate is still
        // open, so a flush tick in that window delivers to the OUTGOING page rather than buffering for
        // the incoming one. That is the better outcome — those listeners are still attached, and these
        // notifications are progress/status (see MaxQueuedNotifications), so live delivery to the page
        // that is still on screen beats holding them for a document that may never load.
        _webView.CoreWebView2.ContentLoading += OnContentLoading;
        // A dead renderer leaves the gate OPEN, so the next tick drained a whole batch into a process
        // that cannot receive it — the queue was already emptied, so those notifications were simply
        // gone (P5.5 H3). The bridge watches this itself rather than relying on the host's auto-reload
        // policy, which is optional and may be off.
        _webView.CoreWebView2.ProcessFailed += OnProcessFailed;

        // A WinForms timer ticks on the UI thread — the only thread allowed to touch
        // CoreWebView2 — so the flush needs no marshalling.
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
    private void Log(Func<string> message) => Shenora.AppCallback.Log(_log, message);

    /// <summary>
    /// Queue a notification for the next batched push (fire-and-forget; delivery starts once the
    /// client is ready). Callable from any thread. Apps using <see cref="WebViewIpcBridgeOptions.EventBus"/>
    /// rarely call this directly — emitting on the bus reaches every attached bridge.
    /// </summary>
    public void SendNotification(string module, string type, object? payload = null, string? scope = null) =>
        _pump.Enqueue(new IpcNotification { Module = module, Type = type, Payload = payload, Scope = scope });

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
                Log(() => $"[Shenora.Windows] Ignored non-string web message: {ex.Message}");
                return;
            }
            if (json is null) return;

            var response = await HandleIncomingAsync(json).ConfigureAwait(true); // stay on the UI thread
            if (response is not null)
                PostJson(response);
        }
        catch (Exception ex)
        {
            Log(() => $"[Shenora.Windows] Unhandled error in the IPC message handler: {ex}");
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
        // NotificationPump.TryDrainBatch never throws (its own doc guarantees it — a catch-all moved
        // in with it), but this try/catch stays: it runs on a WinForms timer, so ANYTHING that escaped
        // here would be an unhandled UI-thread exception — a modal crash dialog under the family
        // bootstrap, repeating every interval. Belt-and-suspenders around a call that also guards
        // itself is cheap; a hole here was expensive once (found in the P0–P5 review).
        try
        {
            if (_pump.TryDrainBatch(out var batchJson) && batchJson is not null)
                PostJson(batchJson);
        }
        catch (Exception ex)
        {
            // Through the guarded Log: this catch-all IS the timer's last line of defence, so a
            // throwing app sink here would defeat the very thing it is reporting from.
            Log(() => $"[Shenora.Windows] Notification flush failed: {ex.Message}");
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
            // One owner for the marshal (P5.5 H4.2): it holds IsHandleCreated-before-InvokeRequired
            // (pre-handle, InvokeRequired lies — false on a pool thread — so it must never gate an
            // inline call on its own; see .claude/knowledge/webview2-hosting.md), the non-blocking
            // post, and the guarded body. No handle yet or already torn down → the post returns false
            // and we DROP; the client's timeout/reconnect handles the rest.
            _ui.Post(() => _webView.CoreWebView2?.PostWebMessageAsString(json));
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

        // Signal FIRST, before anything is torn down: an in-flight handler should learn the page is
        // gone while its await can still act on it, not after the timer and the subscriptions have
        // already been pulled out from under it. The cancellation itself (and the guard around it,
        // since Cancel runs app continuations synchronously) lives in IpcHostBridge.Dispose.
        _host.Dispose();

        _flushTimer?.Stop();
        _flushTimer?.Dispose();

        _pump.Dispose(); // unsubscribes from the bus; idempotent

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
            Log(() => $"[Shenora.Windows] Bridge dispose: could not detach WebView2 handlers ({ex.Message})");
        }
    }
}
