using Microsoft.Extensions.Logging;
using Shenora.Core.Events;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>Inputs for <see cref="ChromiumIpcBridge"/>.</summary>
internal sealed class ChromiumIpcBridgeOptions
{
    public required IMessageDispatcher Dispatcher { get; init; }

    /// <summary>When set, every event on the bus reaches the page as a batched notification.</summary>
    public IEventBus? EventBus { get; init; }

    public ShellInfo? Shell { get; init; }

    public Action<IpcRequest>? OnClientReady { get; init; }

    public TimeSpan NotificationInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    public int MaxQueuedNotifications { get; init; } = 10_000;

    public ILogger? Log { get; init; }

    /// <summary>Entered around each dispatch, so a module learns which window's page asked
    /// (<see cref="ChromiumWindowContext"/>).</summary>
    public Func<IDisposable>? EnterWindow { get; init; }
}

/// <summary>
/// One Chromium window's IPC: the kit's own <see cref="IpcHostBridge"/> and <see cref="NotificationPump"/>,
/// which both other shells wrap too, over the renderer-free transport (D83). It follows
/// <c>WebViewIpcBridge</c> rule for rule: the pump buffers from construction and the handshake opens it;
/// a new document closes it (CEF's page-load START, the counterpart of <c>ContentLoading</c>), and so
/// does a dead renderer; disposing cancels in-flight dispatch FIRST.
/// <para>
/// CEF's two primitives are injected, pushing a message into the page and scheduling the flush tick, so
/// the bridge is tested without CEF. Everything here runs on CEF's UI thread.
/// </para>
/// </summary>
internal sealed class ChromiumIpcBridge : IDisposable
{
    private readonly ChromiumIpcBridgeOptions _options;
    private readonly IUiDispatcher _ui;
    private readonly Action<string> _push;
    private readonly Func<TimeSpan, Action, bool> _schedule;
    private readonly NotificationPump _pump;
    private readonly IpcHostBridge _host;
    private bool _disposed;

    /// <param name="options">The dispatcher, the bus and the handshake's shell description.</param>
    /// <param name="ui">CEF's UI thread, where every message is dispatched.</param>
    /// <param name="push">Deliver one serialized message to the page's main frame. UI thread.</param>
    /// <param name="schedule">Run the action on the UI thread after the delay; false once CEF is gone.</param>
    public ChromiumIpcBridge(ChromiumIpcBridgeOptions options, IUiDispatcher ui, Action<string> push, Func<TimeSpan, Action, bool> schedule)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ui = ui;
        _push = push;
        _schedule = schedule;
        _pump = new NotificationPump(new NotificationPumpOptions
        {
            EventBus = options.EventBus,
            FlushInterval = options.NotificationInterval,
            MaxQueued = options.MaxQueuedNotifications,
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

    public bool IsClientReady => _pump.IsOpen;

    public NotificationPumpReport NotificationReport => _pump.Report();

    /// <summary>Start the flush tick. UI thread, once the page's browser exists.</summary>
    public void Start() => Tick();

    /// <summary>
    /// A page's envelope, from any thread (CEF delivers it on its IO thread). Dispatched on the UI thread,
    /// where a route's awaits resume, which is the kit's context-preserving model.
    /// </summary>
    public void Incoming(string json)
    {
        if (_disposed) return;
        if (!_ui.Post(() => HandleAsync(json)))
            AppCallback.Log(_options.Log, () => "[Shenora.Chromium] An IPC message arrived with no UI thread to run it; dropped", LogLevel.Debug);
    }

    private async Task HandleAsync(string json)
    {
        string? response;
        using (_options.EnterWindow?.Invoke())
            response = await _host.HandleIncomingAsync(json);   // never throws, by its contract
        if (response is not null && !_disposed) Push(response);
    }

    /// <summary>Queue a notification for THIS window's page only. The bus cannot address one: its events reach
    /// every window.</summary>
    public void Notify(IpcNotification notification)
    {
        if (!_disposed) _pump.Enqueue(notification);
    }

    /// <summary>The main frame started loading a new document: whoever handshook can no longer receive.</summary>
    public void DocumentReplaced() => CloseGate("a new document is loading");

    /// <summary>The renderer died: a flush now would drain the queue into nothing.</summary>
    public void RendererGone() => CloseGate("the renderer process terminated");

    private void CloseGate(string reason)
    {
        if (!_pump.IsOpen) return;
        _pump.Close();
        AppCallback.Log(_options.Log, () => $"[Shenora.Chromium] Buffering notifications until the page is ready again — {reason}");
    }

    private void Tick()
    {
        if (_disposed) return;
        try
        {
            if (_pump.TryDrainBatch(out var batch) && batch is not null) Push(batch);
        }
        catch (Exception ex)
        {
            AppCallback.Log(_options.Log, () => "[Shenora.Chromium] Notification flush failed", LogLevel.Warning, ex);
        }
        if (!_schedule(_options.NotificationInterval, Tick))
            AppCallback.Log(_options.Log, () => "[Shenora.Chromium] The flush tick stopped: CEF's UI thread is gone", LogLevel.Debug);
    }

    private void Push(string json) =>
        AppCallback.Run(() => _push(json), ex => AppCallback.Log(_options.Log, () => "[Shenora.Chromium] Pushing to the page failed", LogLevel.Warning, ex));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // FIRST: an in-flight handler learns the page is gone while its await can still act on it.
        _host.Dispose();
        _pump.Dispose();
    }
}
