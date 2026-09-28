using System.Text.Json;
using Shenora.Chromium.Host;
using Shenora.Core.Events;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Chromium;

/// <summary>
/// One Chromium window's IPC over the kit's own host bridge and pump, against a fake UI thread and a fake
/// page, so the WebViewIpcBridge rules it follows are checked without CEF.
/// </summary>
public class ChromiumIpcBridgeTests
{
    private sealed class FakeHost
    {
        public readonly Queue<Action> Ui = new();
        public readonly List<string> Pushed = new();
        public readonly Queue<Action> Ticks = new();
        public readonly CefUiDispatcher Dispatcher;
        private bool _onUi;

        public FakeHost()
        {
            Dispatcher = new CefUiDispatcher(work => { Ui.Enqueue(work); return true; }, () => _onUi);
            Dispatcher.MarkReady();
        }

        public bool Schedule(TimeSpan delay, Action work) { Ticks.Enqueue(work); return true; }

        public void RunUi()
        {
            _onUi = true;
            while (Ui.TryDequeue(out var work)) work();
            _onUi = false;
        }

        public void Tick()
        {
            _onUi = true;
            if (Ticks.TryDequeue(out var tick)) tick();
            _onUi = false;
        }
    }

    private static (ChromiumIpcBridge Bridge, FakeHost Host, EventBus Bus) Make()
    {
        var host = new FakeHost();
        var bus = new EventBus();
        var dispatcher = new MessageDispatcher();
        dispatcher.MapRoute("TEST", "ECHO", request => new { echoed = request.Payload });
        var bridge = new ChromiumIpcBridge(new ChromiumIpcBridgeOptions { Dispatcher = dispatcher, EventBus = bus },
            host.Dispatcher, host.Pushed.Add, host.Schedule);
        bridge.Start();
        host.RunUi();   // the tick starts on the bridge's thread
        return (bridge, host, bus);
    }

    private static string Request(string module, string type, object? payload = null) =>
        JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString("N"), category = "ipc", module, type, payload });

    private static async Task SettleAsync(FakeHost host)
    {
        // A route's awaits resume through the UI queue; drain until the response has been pushed.
        for (var i = 0; i < 50 && host.Ui.Count + host.Pushed.Count == 0; i++) await Task.Delay(10);
        for (var i = 0; i < 20; i++) { host.RunUi(); await Task.Delay(5); }
    }

    [Fact]
    public async Task A_request_is_dispatched_on_the_UI_thread_and_its_response_pushed_to_the_page()
    {
        var (bridge, host, _) = Make();

        bridge.Incoming(Request("TEST", "ECHO", new { n = 1 }));
        Assert.Empty(host.Pushed);   // not inline on the caller's (CEF's IO) thread
        await SettleAsync(host);

        var response = Assert.Single(host.Pushed);
        Assert.Contains("\"echoed\":{\"n\":1}", response);
    }

    [Fact]
    public async Task Notifications_wait_for_the_handshake_then_arrive_batched_on_the_tick()
    {
        var (bridge, host, bus) = Make();
        bus.Emit(new EventMessage { Module = "TEST", Type = "TICKED", Payload = new { n = 1 } });

        host.Tick();
        Assert.Empty(host.Pushed);   // buffered: nobody has said ready

        bridge.Incoming(Request(IpcHostBridge.HandshakeModule, IpcHostBridge.HandshakeType));
        await SettleAsync(host);
        host.Pushed.Clear();
        host.Tick();

        var batch = Assert.Single(host.Pushed);
        Assert.Contains("TICKED", batch);
        Assert.True(bridge.IsClientReady);
    }

    [Fact]
    public async Task A_request_is_dispatched_as_its_windows_across_its_awaits()
    {
        var host = new FakeHost();
        var window = ChromiumDropZonesTests.Window("second");
        var dispatcher = new MessageDispatcher();
        var seen = new List<bool>();
        dispatcher.UseRoute("TEST", "WHO", async (request, _) =>
        {
            seen.Add(ReferenceEquals(ChromiumBrowserContext.Current, window));
            await Task.Yield();
            seen.Add(ReferenceEquals(ChromiumBrowserContext.Current, window));
            return IpcResponse.CreateSuccess(request.Id, null);
        });
        var bridge = new ChromiumIpcBridge(new ChromiumIpcBridgeOptions { Dispatcher = dispatcher, EnterWindow = () => ChromiumBrowserContext.Enter(window) },
            host.Dispatcher, host.Pushed.Add, host.Schedule);

        bridge.Incoming(Request("TEST", "WHO"));
        await SettleAsync(host);

        Assert.Equal([true, true], seen);
        Assert.Null(ChromiumBrowserContext.Current);   // and nothing leaks out of the dispatch
    }

    [Fact]
    public async Task A_window_local_notification_reaches_this_page_and_never_the_bus()
    {
        var (bridge, host, bus) = Make();
        // A second window on the SAME bus, as every window of the shell is: a bus event would reach it.
        var otherHost = new FakeHost();
        var other = new ChromiumIpcBridge(new ChromiumIpcBridgeOptions { Dispatcher = new MessageDispatcher(), EventBus = bus },
            otherHost.Dispatcher, otherHost.Pushed.Add, otherHost.Schedule);
        other.Start();
        var onBus = 0;
        using var _ = bus.SubscribeToAll(_ => { onBus++; return Task.CompletedTask; });
        foreach (var (b, h) in new[] { (bridge, host), (other, otherHost) })
        {
            b.Incoming(Request(IpcHostBridge.HandshakeModule, IpcHostBridge.HandshakeType));
            await SettleAsync(h);
            h.Pushed.Clear();
        }

        bridge.Notify(new IpcNotification { Module = "SHENORA.WINDOW", Type = "CAPTION_BUTTON_STATE", Payload = new { hot = "close" } });
        host.Tick();
        otherHost.Tick();

        Assert.Contains("CAPTION_BUTTON_STATE", Assert.Single(host.Pushed));
        Assert.Empty(otherHost.Pushed);
        Assert.Equal(0, onBus);
    }

    [Fact]
    public async Task A_new_document_closes_the_gate_so_nothing_drains_into_a_page_that_left()
    {
        var (bridge, host, bus) = Make();
        bridge.Incoming(Request(IpcHostBridge.HandshakeModule, IpcHostBridge.HandshakeType));
        await SettleAsync(host);

        bridge.DocumentReplaced();
        host.RunUi();
        bus.Emit(new EventMessage { Module = "TEST", Type = "LATE" });
        host.Pushed.Clear();
        host.Tick();

        Assert.False(bridge.IsClientReady);
        Assert.Empty(host.Pushed);
    }

    [Fact]
    public async Task What_CEF_reports_on_its_own_thread_runs_on_the_bridges()
    {
        // An embedded browser: the bridge lives on the host's thread and CEF reports a new document on its own.
        var (bridge, host, _) = Make();
        bridge.Incoming(Request(IpcHostBridge.HandshakeModule, IpcHostBridge.HandshakeType));
        await SettleAsync(host);

        bridge.DocumentReplaced();   // off the bridge's thread
        Assert.True(bridge.IsClientReady);   // not touched from CEF's
        host.RunUi();

        Assert.False(bridge.IsClientReady);
    }

    [Fact]
    public void A_throwing_push_neither_escapes_nor_stops_the_tick()
    {
        var host = new FakeHost();
        var bridge = new ChromiumIpcBridge(new ChromiumIpcBridgeOptions { Dispatcher = new MessageDispatcher() },
            host.Dispatcher, _ => throw new InvalidOperationException("frame gone"), host.Schedule);
        bridge.Start();
        host.RunUi();

        host.Tick();

        Assert.Single(host.Ticks);   // re-scheduled after the failure
    }

    [Fact]
    public void Disposing_stops_the_tick_and_drops_later_messages()
    {
        var (bridge, host, _) = Make();
        bridge.Dispose();
        bridge.Incoming(Request("TEST", "ECHO"));   // dropped at once, before the teardown has run
        host.RunUi();

        host.Tick();

        Assert.Empty(host.Ticks);
        Assert.Empty(host.Ui);
    }
}
