using System.Text.Json;
using Shenora.Core;
using Shenora.Ipc;
using Shenora.WebView2;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Tests.WebView2;

/// <summary>
/// Protocol tests over the internal seams (an uninitialized WebView2 control is inert — no core,
/// no handle). The live transport path (WebMessageReceived → PostWebMessageAsString) is proven by
/// the sample-app e2e, the family precedent for real-WebView2 behavior.
/// </summary>
public class WebViewIpcBridgeTests
{
    private static WebViewIpcBridge CreateBridge(WebViewIpcBridgeOptions options) =>
        new(new WebView2Control(), options);

    private static string ReadyJson(string id = "h1") =>
        IpcJson.Serialize(new IpcRequest
        {
            Id = id,
            Module = WebViewIpcBridge.HandshakeModule,
            Type = WebViewIpcBridge.HandshakeType,
        });

    [Fact]
    public async Task Handshake_sets_ready_and_replies_success()
    {
        var readyRequests = new List<IpcRequest>();
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions
        {
            Dispatcher = new MessageDispatcher(),
            OnClientReady = readyRequests.Add,
        });

        Assert.False(bridge.IsClientReady);
        var response = await bridge.HandleIncomingAsync(ReadyJson());

        using var doc = JsonDocument.Parse(response!);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("h1", doc.RootElement.GetProperty("id").GetString());
        Assert.True(bridge.IsClientReady);
        Assert.Single(readyRequests);
    }

    [Fact]
    public async Task Handshake_fires_the_callback_per_occurrence()
    {
        // A reloaded page (crash recovery, dev hot reload) reports ready again — each is the
        // app's cue to reset per-page state.
        var count = 0;
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions
        {
            Dispatcher = new MessageDispatcher(),
            OnClientReady = _ => count++,
        });

        await bridge.HandleIncomingAsync(ReadyJson("h1"));
        await bridge.HandleIncomingAsync(ReadyJson("h2"));

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Handshake_callback_failure_does_not_fail_the_handshake()
    {
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions
        {
            Dispatcher = new MessageDispatcher(),
            OnClientReady = _ => throw new InvalidOperationException("splash glue broke"),
        });

        var response = await bridge.HandleIncomingAsync(ReadyJson());

        using var doc = JsonDocument.Parse(response!);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(bridge.IsClientReady);
    }

    [Fact]
    public async Task Handshake_never_reaches_the_dispatcher()
    {
        var reached = false;
        var dispatcher = new MessageDispatcher()
            .UseModule(WebViewIpcBridge.HandshakeModule, _ =>
            {
                reached = true;
                return Task.FromResult<IpcResponse?>(null);
            });
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions { Dispatcher = dispatcher });

        await bridge.HandleIncomingAsync(ReadyJson());

        Assert.False(reached);
    }

    [Fact]
    public async Task Requests_dispatch_through_the_pipeline()
    {
        var dispatcher = new MessageDispatcher().MapRoute("APP", "PING", _ => "pong");
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions { Dispatcher = dispatcher });

        var response = await bridge.HandleIncomingAsync("""{"id":"r1","module":"APP","type":"PING"}""");

        using var doc = JsonDocument.Parse(response!);
        Assert.Equal(IpcCategories.Ipc, doc.RootElement.GetProperty("category").GetString());
        Assert.Equal("r1", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("pong", doc.RootElement.GetProperty("data").GetString());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"id":"1","type":"PING"}""")] // missing required module
    [InlineData("42")]
    public async Task Malformed_input_is_dropped_without_a_response(string json)
    {
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions { Dispatcher = new MessageDispatcher() });

        Assert.Null(await bridge.HandleIncomingAsync(json));
    }

    [Fact]
    public async Task Notifications_hold_until_the_client_is_ready()
    {
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions { Dispatcher = new MessageDispatcher() });
        bridge.SendNotification("APP", "TICK");

        Assert.Null(bridge.TryBuildBatchJson()); // gate closed — queue intact
        Assert.Equal(1, bridge.PendingNotificationCount);

        await bridge.HandleIncomingAsync(ReadyJson());

        Assert.NotNull(bridge.TryBuildBatchJson()); // buffered events delivered in the first batch
    }

    [Fact]
    public async Task Batch_has_the_wire_shape_and_preserves_order()
    {
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions { Dispatcher = new MessageDispatcher() });
        await bridge.HandleIncomingAsync(ReadyJson());

        bridge.SendNotification("APP", "FIRST", payload: new { n = 1 });
        bridge.SendNotification("APP", "SECOND", scope: "s1");

        using var doc = JsonDocument.Parse(bridge.TryBuildBatchJson()!);
        var root = doc.RootElement;
        Assert.Equal(IpcCategories.Notification, root.GetProperty("category").GetString());
        var items = root.GetProperty("payload");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("FIRST", items[0].GetProperty("type").GetString());
        Assert.Equal(1, items[0].GetProperty("payload").GetProperty("n").GetInt32());
        Assert.Equal("s1", items[1].GetProperty("scope").GetString());

        Assert.Null(bridge.TryBuildBatchJson()); // drained
        Assert.Equal(0, bridge.PendingNotificationCount);
    }

    [Fact]
    public async Task Queue_drops_the_oldest_over_the_cap()
    {
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions
        {
            Dispatcher = new MessageDispatcher(),
            MaxQueuedNotifications = 3,
        });

        for (var i = 1; i <= 5; i++)
            bridge.SendNotification("APP", $"T{i}");

        Assert.Equal(3, bridge.PendingNotificationCount);

        await bridge.HandleIncomingAsync(ReadyJson());
        using var doc = JsonDocument.Parse(bridge.TryBuildBatchJson()!);
        var items = doc.RootElement.GetProperty("payload");
        Assert.Equal(["T3", "T4", "T5"],
            Enumerable.Range(0, items.GetArrayLength()).Select(i => items[i].GetProperty("type").GetString()));
    }

    [Fact]
    public async Task Bus_events_forward_from_construction_and_stop_on_dispose()
    {
        var bus = new EventBus();
        var bridge = CreateBridge(new WebViewIpcBridgeOptions
        {
            Dispatcher = new MessageDispatcher(),
            EventBus = bus,
        });

        // Buffering starts at construction (before Attach/ready) so startup events survive.
        await bus.EmitAsync("APP", "STARTED", payload: 1, scope: "s1");
        Assert.Equal(1, bridge.PendingNotificationCount);

        await bridge.HandleIncomingAsync(ReadyJson());
        using var doc = JsonDocument.Parse(bridge.TryBuildBatchJson()!);
        var item = doc.RootElement.GetProperty("payload")[0];
        Assert.Equal("APP", item.GetProperty("module").GetString());
        Assert.Equal("STARTED", item.GetProperty("type").GetString());
        Assert.Equal(1, item.GetProperty("payload").GetInt32());
        Assert.Equal("s1", item.GetProperty("scope").GetString());

        bridge.Dispose();
        await bus.EmitAsync("APP", "AFTER");
        Assert.Equal(0, bridge.PendingNotificationCount); // unsubscribed — nothing new buffered
        Assert.Equal(0, bus.GetHandlerCount());
    }

    [Fact]
    public void Attach_requires_an_initialized_core()
    {
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions { Dispatcher = new MessageDispatcher() });

        var ex = Assert.Throws<InvalidOperationException>(bridge.Attach);
        Assert.Contains("InitializeAsync", ex.Message);
    }

    [Fact]
    public async Task A_throwing_dispatcher_still_yields_a_structured_response()
    {
        // MessageDispatcher never throws, but IMessageDispatcher is a public seam — an app
        // implementation might. The client must still get a response (and no raw details).
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions { Dispatcher = new ThrowingDispatcher() });

        var response = await bridge.HandleIncomingAsync("""{"id":"r1","module":"APP","type":"PING"}""");

        using var doc = JsonDocument.Parse(response!);
        Assert.Equal("r1", doc.RootElement.GetProperty("id").GetString());
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(IpcErrorCodes.UnknownError, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("secret detail", response);
    }

    [Fact]
    public async Task Unserializable_response_data_becomes_a_structured_error()
    {
        // A handler returning something STJ can't serialize (System.Type here) must not escape
        // the async-void message handler — the client gets UNKNOWN_ERROR instead of a timeout.
        var dispatcher = new MessageDispatcher().MapRoute("APP", "BAD", _ => typeof(string));
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions { Dispatcher = dispatcher });

        var response = await bridge.HandleIncomingAsync("""{"id":"r1","module":"APP","type":"BAD"}""");

        using var doc = JsonDocument.Parse(response!);
        Assert.Equal("r1", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(IpcErrorCodes.UnknownError, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Navigation_closes_the_ready_gate_until_the_next_handshake()
    {
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions { Dispatcher = new MessageDispatcher() });
        await bridge.HandleIncomingAsync(ReadyJson("h1"));
        Assert.True(bridge.IsClientReady);

        // A reload (renderer-crash recovery, dev reload) replaces the page — its buffered
        // notifications must WAIT for the new page's handshake, not drain into a dead document.
        bridge.ResetClientReady();
        bridge.SendNotification("APP", "TICK");

        Assert.False(bridge.IsClientReady);
        Assert.Null(bridge.TryBuildBatchJson());
        Assert.Equal(1, bridge.PendingNotificationCount);

        await bridge.HandleIncomingAsync(ReadyJson("h2"));
        Assert.NotNull(bridge.TryBuildBatchJson());
    }

    private sealed class ThrowingDispatcher : IMessageDispatcher
    {
        public Task<IpcResponse> DispatchAsync(IpcRequest request) =>
            throw new InvalidOperationException("secret detail");

        public Task<IpcResponse> SendAsync(string module, string type, string? scope = null, object? payload = null) =>
            throw new NotSupportedException();

        public Task<T?> SendAsync<T>(string module, string type, string? scope = null, object? payload = null) =>
            throw new NotSupportedException();
    }
}
