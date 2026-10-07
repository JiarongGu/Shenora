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
    public async Task The_handshake_tells_whoever_asked_once_per_handshake()
    {
        var host = new FakeHost();
        var ready = 0;
        var bridge = new ChromiumIpcBridge(new ChromiumIpcBridgeOptions { Dispatcher = new MessageDispatcher(), OnClientReady = () => ready++ },
            host.Dispatcher, host.Pushed.Add, host.Schedule);
        bridge.Start();
        host.RunUi();

        bridge.Incoming(Request("TEST", "ECHO"));
        await SettleAsync(host);
        Assert.Equal(0, ready);
        bridge.Incoming(Request(IpcHostBridge.HandshakeModule, IpcHostBridge.HandshakeType));
        await SettleAsync(host);
        Assert.Equal(1, ready);
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

    // What follows the pointer cannot wait for the tick: up to 50 ms behind the cursor is visible (measured).
    [Fact]
    public async Task An_immediate_notification_is_pushed_without_waiting_for_the_tick()
    {
        var (bridge, host, _) = Make();
        var state = new IpcNotification { Module = "SHENORA.WINDOW", Type = "CAPTION_BUTTON_STATE", Payload = new { hot = "close" } };

        bridge.Notify(state, immediate: true);
        host.RunUi();
        Assert.Empty(host.Pushed);   // before the handshake it waits, like any other

        bridge.Incoming(Request(IpcHostBridge.HandshakeModule, IpcHostBridge.HandshakeType));
        await SettleAsync(host);
        host.Pushed.Clear();
        bridge.Notify(state, immediate: true);
        host.RunUi();

        Assert.Contains("CAPTION_BUTTON_STATE", Assert.Single(host.Pushed));
        host.Tick();
        Assert.Single(host.Pushed);   // and the tick has nothing left of it to send
    }

    /// <summary>The WebView2 shell's drop-zone module, as an app maps it: under the page's own module name.</summary>
    private sealed class WebView2DropZones : IIpcModule
    {
        public int Calls;
        public string ModuleName => Shenora.Windows.DropZoneManager.Module;

        public Task<IpcResponse> HandleMessageAsync(IpcRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(IpcResponse.CreateSuccess(request.Id));
        }
    }

    // One app, both engines, one dispatcher: whichever was mapped first, a Chromium page's drop zones are the engine's
    // and a WebView2 page's the WebView2 module's. Mapped to the SAME name, a Chromium page's REGISTER once reached the
    // WebView2 module, which would have raised an overlay on the WebView2 form, and mapping it after the engine threw.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task In_an_app_with_both_engines_each_page_is_answered_by_its_own(bool webView2First)
    {
        var dispatcher = new MessageDispatcher();
        var webView2 = new WebView2DropZones();
        if (webView2First) dispatcher.MapModule(webView2);
        Assert.True(dispatcher.TryMapModule(new ChromiumDropZones(() => ChromiumBrowserContext.Current)));
        if (!webView2First) dispatcher.MapModule(webView2);   // never "already mapped"

        var host = new FakeHost();
        var page = ChromiumDropZonesTests.Window("chromium");
        var bridge = new ChromiumIpcBridge(new ChromiumIpcBridgeOptions { Dispatcher = dispatcher, EnterWindow = () => ChromiumBrowserContext.Enter(page) },
            host.Dispatcher, host.Pushed.Add, host.Schedule);
        bridge.Incoming(Request(ChromiumDropZones.Module, ChromiumDropZones.RegisterType, new { zoneId = "z1" }));
        await SettleAsync(host);

        Assert.Contains("\"pageDrop\":true", Assert.Single(host.Pushed));
        Assert.True(page.HasDropZone("z1"));
        Assert.Equal(0, webView2.Calls);

        // A WebView2 page's request reaches the dispatcher as it is.
        var fromWebView2 = await dispatcher.DispatchAsync(new IpcRequest { Id = "w", Module = ChromiumDropZones.Module, Type = ChromiumDropZones.RegisterType });
        Assert.True(fromWebView2.Success);
        Assert.Equal(1, webView2.Calls);
    }

    // The JSON can say `"module": null`, whatever the C# type says: the page's view of the dispatcher passes it on to be
    // refused, never throws on it.
    [Fact]
    public async Task A_request_with_no_module_is_refused_as_before_not_thrown_on()
    {
        var response = await new PageModuleDispatcher(new MessageDispatcher())
            .DispatchAsync(new IpcRequest { Id = "n", Module = null!, Type = "REGISTER" });

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.NoHandler, response.Error?.Code);
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
