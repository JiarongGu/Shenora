using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Shenora;
using Shenora.Core.Ipc;

namespace Shenora.Mobile;

/// <summary>
/// The MAUI <c>HybridWebView</c> IPC transport — the peer of <c>Shenora.Windows.WebViewIpcBridge</c>, and
/// much thinner: <see cref="IpcHostBridge"/> owns parse → handshake-or-dispatch → response and the error
/// boundary, <see cref="NotificationPump"/> owns the queue, the ready gate and batch building. What is left
/// here is what only this platform can do — read a message off <c>RawMessageReceived</c>, write one with
/// <c>SendRawMessage</c>, and tick a dispatcher timer.
/// <para>
/// Everything that is not the control itself lives in <c>MobileIpcCore</c>, which a test compiles; this class is
/// the wiring to the <c>HybridWebView</c>, proven on a device.
/// </para>
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
    private readonly MobileIpcCore _core;
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
        _log = options.Log;

        _core = new MobileIpcCore(options,
            new MobileUiDispatcher(webView.Dispatcher, ex => Log(() => "[Shenora.Mobile] Posted UI work failed", ex)),
            webView.SendRawMessage,
            StartTimer);
    }

    /// <summary>True once the client has completed the ready handshake (notifications flow).</summary>
    public bool IsClientReady => _core.IsClientReady;

    /// <summary>
    /// What became of the notifications emitted so far — read this FIRST when a page reports that it
    /// receives no events. See <see cref="NotificationPump.Report"/>.
    /// </summary>
    public NotificationPumpReport NotificationReport => _core.NotificationReport;

    /// <summary>
    /// Hook <c>RawMessageReceived</c> and start the flush timer. Call once, on the UI thread, before
    /// the page loads — hooking afterwards loses the handshake and every message before it.
    /// </summary>
    public void Attach()
    {
        // Throws when disposed or already attached, before the handler is hooked a second time.
        _core.Start();
        _attached = true;

        _webView.RawMessageReceived += OnRawMessageReceived;

        Log(() => "[Shenora.Mobile] IPC bridge attached");
    }

    // A dispatcher timer ticks on the UI thread, so the flush needs no marshalling.
    private Action StartTimer(TimeSpan interval, Action tick)
    {
        var timer = _webView.Dispatcher.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = true;
        timer.Tick += (_, _) => tick();
        timer.Start();
        return timer.Stop;
    }

    /// <summary>
    /// Queue a notification for the next batched push (fire-and-forget; delivery starts once the
    /// client is ready). Callable from any thread.
    /// </summary>
    public void SendNotification(string module, string type, object? payload = null, string? scope = null) =>
        _core.SendNotification(module, type, payload, scope);

    // Fire and forget is safe: the core's handler never throws, which an async void event handler would need.
    private void OnRawMessageReceived(object? sender, HybridWebViewRawMessageReceivedEventArgs e) =>
        _ = _core.HandleMessageAsync(e.Message);

    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_log, message, exception: failure);

    /// <summary>Stop the timer, detach the handler and unsubscribe from the bus. Without this the timer keeps firing
    /// into a torn-down page for the life of the process. ⚠ In-flight requests are NOT cancelled: they run to the end,
    /// and their responses are dropped with the page (see the constructor).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // FIRST, before the transport and subscriptions go: the host bridge, the timer, then the pump.
        _core.Dispose();

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
