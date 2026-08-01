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

    [Fact]
    public async Task A_message_arriving_after_dispose_is_answered_not_thrown()
    {
        // The bridge hands every dispatch a lifetime token (P6.4). Reading `_lifetime.Token` at
        // dispatch time would throw ObjectDisposedException once Dispose has run — and a message
        // arriving during teardown is the NORMAL case, not a corner one, because teardown is exactly
        // when the page is going away. Capturing the token once at construction avoids it: a
        // CancellationToken is a struct that stays readable after its source is disposed.
        var dispatcher = new MessageDispatcher();
        dispatcher.MapRoute("APP", "PING", _ => "pong");
        var bridge = CreateBridge(new WebViewIpcBridgeOptions { Dispatcher = dispatcher });

        bridge.Dispose();

        var response = await bridge.HandleIncomingAsync(IpcJson.Serialize(
            new IpcRequest { Id = "r1", Module = "APP", Type = "PING" }));

        // It answers — with the cancellation the disposed lifetime implies, never with a crash.
        using var doc = JsonDocument.Parse(response!);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(IpcErrorCodes.OperationCancelled,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

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
            .UseModule(WebViewIpcBridge.HandshakeModule, (_, _) =>
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

    // ── The ready gate's re-arm paths (P5.5 H3) ───────────────────────────────────────────────────
    // The gate used to close on EVERY NavigationStarting while the client sends READY only once per
    // real page load. So a navigation that never replaced the document — one an app tap or a policy
    // CANCELLED, one that failed before committing — closed the gate FOREVER on a page that was still
    // very much alive: notifications buffered to the 10 000 cap and then silently dropped the oldest,
    // for the process lifetime. It now closes on ContentLoading (a new document really is loading) and
    // on ProcessFailed (the renderer that handshook is dead).

    [Fact]
    public async Task A_new_document_closes_the_gate_so_the_next_page_rehandshakes()
    {
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions { Dispatcher = new MessageDispatcher() });
        await bridge.HandleIncomingAsync(ReadyJson());
        Assert.True(bridge.IsClientReady);

        bridge.ResetClientReady("a new document is loading"); // what OnContentLoading calls

        Assert.False(bridge.IsClientReady);
        bridge.SendNotification("APP", "TICK");
        Assert.Null(bridge.TryBuildBatchJson());          // buffered, not drained into a dead document
        Assert.Equal(1, bridge.PendingNotificationCount); // and the queue is INTACT

        await bridge.HandleIncomingAsync(ReadyJson("h2")); // the new page's handshake
        Assert.NotNull(bridge.TryBuildBatchJson());        // the buffered event survives the reload
    }

    [Fact]
    public async Task Closing_the_gate_twice_is_harmless_and_re_arming_always_works()
    {
        // The gate must be re-armable an unbounded number of times — reload, crash-reload, dev hot
        // reload — because the ONE path that used to close it had no counterpart.
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions { Dispatcher = new MessageDispatcher() });

        for (var i = 0; i < 3; i++)
        {
            await bridge.HandleIncomingAsync(ReadyJson($"h{i}"));
            Assert.True(bridge.IsClientReady);
            bridge.ResetClientReady();
            bridge.ResetClientReady(); // idempotent — ContentLoading and ProcessFailed can both fire
            Assert.False(bridge.IsClientReady);
        }

        await bridge.HandleIncomingAsync(ReadyJson("final"));
        bridge.SendNotification("APP", "TICK");
        Assert.NotNull(bridge.TryBuildBatchJson());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_zero_notification_cap_is_rejected_at_construction(int cap)
    {
        // MaxQueuedNotifications = 0 made Enqueue dequeue the item it had just enqueued, so EVERY
        // notification for the life of the process vanished with no error and no log line (P5.5 H3).
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => CreateBridge(new WebViewIpcBridgeOptions
        {
            Dispatcher = new MessageDispatcher(),
            MaxQueuedNotifications = cap,
        }));
        Assert.Contains("silently discard", error.Message, StringComparison.Ordinal);
        // ALSO IN THIS BATCH (whole-branch review): this bound used to surface only from
        // NotificationPump's own constructor, naming NotificationPumpOptions.MaxQueued — a type the
        // adopter setting WebViewIpcBridgeOptions.MaxQueuedNotifications never touched. The bridge
        // must validate (and name) its OWN option, the same way it already does for the upper bound
        // below.
        Assert.Contains(nameof(WebViewIpcBridgeOptions.MaxQueuedNotifications), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sub_millisecond_notification_interval_is_rejected_at_construction()
    {
        // It used to truncate to 0 and throw out of Attach() — an opaque WinForms Timer exception at a
        // call site that has nothing to do with the option.
        var zero = Assert.Throws<ArgumentOutOfRangeException>(() => CreateBridge(new WebViewIpcBridgeOptions
        {
            Dispatcher = new MessageDispatcher(),
            NotificationInterval = TimeSpan.Zero,
        }));
        // Same naming defect as the cap above: this used to blame NotificationPumpOptions.FlushInterval.
        Assert.Contains(nameof(WebViewIpcBridgeOptions.NotificationInterval), zero.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentOutOfRangeException>(() => CreateBridge(new WebViewIpcBridgeOptions
        {
            Dispatcher = new MessageDispatcher(),
            NotificationInterval = TimeSpan.MaxValue, // beyond the timer's int32 millisecond limit
        }));
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

    // ── The outgoing serialize guard (P5.5 H1) ────────────────────────────────────────────────────
    // Payloads are APP-supplied objects, so serialization can throw on data the framework only sees
    // at flush time. The queue is drained BEFORE the serialize, so an unguarded throw both crashed
    // the UI thread (this runs on a 50 ms WinForms timer) and lost the whole batch.

    [Fact]
    public async Task An_unserializable_notification_is_dropped_without_taking_its_batch_down()
    {
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions { Dispatcher = new MessageDispatcher() });
        await bridge.HandleIncomingAsync(ReadyJson());

        bridge.SendNotification("APP", "GOOD_BEFORE", payload: new { n = 1 });
        bridge.SendNotification("APP", "CYCLIC", payload: Cyclic());
        bridge.SendNotification("APP", "THROWING", payload: new ThrowingGetterPayload());
        bridge.SendNotification("APP", "GOOD_AFTER", payload: new { n = 2 });

        var json = bridge.TryBuildBatchJson();   // must NOT throw

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var items = doc.RootElement.GetProperty("payload");
        Assert.Equal(2, items.GetArrayLength()); // the two offenders dropped, the good ones survive
        Assert.Equal("GOOD_BEFORE", items[0].GetProperty("type").GetString());
        Assert.Equal("GOOD_AFTER", items[1].GetProperty("type").GetString());
        Assert.Equal(0, bridge.PendingNotificationCount);
    }

    [Fact]
    public async Task A_batch_of_only_unserializable_notifications_yields_no_batch_rather_than_throwing()
    {
        using var bridge = CreateBridge(new WebViewIpcBridgeOptions { Dispatcher = new MessageDispatcher() });
        await bridge.HandleIncomingAsync(ReadyJson());

        bridge.SendNotification("APP", "CYCLIC", payload: Cyclic());

        Assert.Null(bridge.TryBuildBatchJson());
        Assert.Equal(0, bridge.PendingNotificationCount);
    }

    /// <summary>A parent/child cycle — the shape a real app hits with ORM entities.</summary>
    private static object Cyclic()
    {
        var parent = new Node { Name = "parent" };
        var child = new Node { Name = "child", Parent = parent };
        parent.Child = child;
        return parent;
    }

    private sealed class Node
    {
        public string Name { get; set; } = "";
        public Node? Parent { get; set; }
        public Node? Child { get; set; }
    }

    private sealed class ThrowingGetterPayload
    {
        public string Boom => throw new InvalidOperationException("secret detail from a getter");
    }

    private sealed class ThrowingDispatcher : IMessageDispatcher
    {
        public Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("secret detail");

        public Task<IpcResponse> SendAsync(string module, string type, string? scope = null, object? payload = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<T?> SendAsync<T>(string module, string type, string? scope = null, object? payload = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        // Part of the interface since P5.5 H6 so late mapping needs no downcast. This double exists only
        // to prove the bridge never leaks exception text, so composing on it is not a scenario.
        public IMessageDispatcher Use(MessageMiddleware middleware) => throw new NotSupportedException();
    }
}
