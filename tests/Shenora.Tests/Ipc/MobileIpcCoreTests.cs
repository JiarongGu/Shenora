using Shenora;
using Shenora.Core.Events;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;
using Shenora.Mobile;

namespace Shenora.Tests.Ipc;

/// <summary>
/// What <c>MobileIpcBridge</c> does that is not the <c>HybridWebView</c>, through the core it hands the control to.
/// The bridge itself is MAUI source the test project cannot reference, so the event hook, the dispatcher timer and
/// the handler release stay a device's to prove; these pin the rest, which is most of its behaviour.
/// </summary>
public class MobileIpcCoreTests
{
    /// <summary>Runs posted work inline, as the UI thread would, or refuses it when told the window is gone.</summary>
    private sealed class FakeUi : UiDispatcherBase
    {
        public UiTargetState Target { get; set; } = UiTargetState.Ready;
        public override UiTargetState State => Target;
        public override bool IsOnUiThread => true;
        protected override bool TryPost(Action work, out Exception? failure) { failure = null; work(); return true; }
    }

    private sealed class Harness : IDisposable
    {
        public readonly FakeUi Ui = new();
        public readonly List<string> Sent = [];
        public readonly List<string> Lines = [];
        public readonly EventBus Bus = new();
        public Action? Tick;
        public int TimerStops;
        public Exception? SendFailure;
        public MobileIpcCore Core { get; }

        public Harness(MobileIpcBridgeOptions? options = null, IMessageDispatcher? dispatcher = null)
        {
            var dispatch = dispatcher ?? new MessageDispatcher();
            if (dispatcher is null) ((MessageDispatcher)dispatch).MapRoute("APP", "PING", _ => "pong");
            Core = new MobileIpcCore(
                options ?? new MobileIpcBridgeOptions { Dispatcher = dispatch, EventBus = Bus, Log = AppCallback.Logger(Lines.Add) },
                Ui,
                json => { if (SendFailure is { } failure) throw failure; Sent.Add(json); },
                (_, tick) => { Tick = tick; return () => TimerStops++; });
        }

        public void Dispose() => Core.Dispose();
    }

    private static string Json(string module, string type, string id = "r1") =>
        IpcJson.Serialize(new IpcRequest { Id = id, Module = module, Type = type });

    private static string Ready() => Json(IpcHostBridge.HandshakeModule, IpcHostBridge.HandshakeType, "h1");

    [Fact]
    public void A_bad_option_is_rejected_at_construction_naming_the_mobile_option()
    {
        // Named against the bridge's own option names, not the pump's: MaxQueued is not a name an adopter wrote.
        var max = Assert.Throws<ArgumentOutOfRangeException>(() => new Harness(
            new MobileIpcBridgeOptions { Dispatcher = new MessageDispatcher(), MaxQueuedNotifications = 0 }));
        Assert.Contains(nameof(MobileIpcBridgeOptions.MaxQueuedNotifications), max.Message);

        var interval = Assert.Throws<ArgumentOutOfRangeException>(() => new Harness(
            new MobileIpcBridgeOptions { Dispatcher = new MessageDispatcher(), NotificationInterval = TimeSpan.Zero }));
        Assert.Contains(nameof(MobileIpcBridgeOptions.NotificationInterval), interval.Message);
    }

    [Fact]
    public async Task A_request_is_answered_through_the_UI_dispatcher()
    {
        using var h = new Harness();

        await h.Core.HandleMessageAsync(Json("APP", "PING"));

        var response = Assert.Single(h.Sent);
        Assert.Contains("\"id\":\"r1\"", response);
        Assert.Contains("pong", response);
    }

    [Fact]
    public async Task The_handshake_opens_the_gate_and_a_tick_then_delivers_what_was_buffered_before_it()
    {
        using var h = new Harness();
        h.Core.Start();
        h.Core.SendNotification("APP", "EARLY", new { n = 1 }, scope: null);

        h.Tick!();
        Assert.Empty(h.Sent);                 // nobody has said ready, so it waits
        Assert.False(h.Core.IsClientReady);

        await h.Core.HandleMessageAsync(Ready());
        Assert.True(h.Core.IsClientReady);
        h.Sent.Clear();
        h.Tick!();

        var batch = Assert.Single(h.Sent);
        Assert.Contains("EARLY", batch);
        Assert.Equal(1, h.Core.NotificationReport.Delivered);
    }

    [Fact]
    public async Task The_event_bus_reaches_the_page_and_the_filter_applies_at_enqueue()
    {
        var bus = new EventBus();
        using var h = new Harness(new MobileIpcBridgeOptions
        {
            Dispatcher = new MessageDispatcher(),
            EventBus = bus,
            NotificationFilter = n => n.Type != "HIDDEN",
        });
        h.Core.Start();
        await h.Core.HandleMessageAsync(Ready());
        bus.Emit("APP", "HIDDEN");
        bus.Emit("APP", "SHOWN");
        h.Sent.Clear();

        h.Tick!();

        var batch = Assert.Single(h.Sent);
        Assert.Contains("SHOWN", batch);
        Assert.DoesNotContain("HIDDEN", batch);
        Assert.Equal(1, h.Core.NotificationReport.Filtered);
    }

    [Fact]
    public async Task An_empty_or_missing_message_is_ignored()
    {
        using var h = new Harness();

        await h.Core.HandleMessageAsync(null);
        await h.Core.HandleMessageAsync("");

        Assert.Empty(h.Sent);
        Assert.Empty(h.Lines);
    }

    [Fact]
    public async Task A_message_that_is_not_a_request_is_dropped_without_a_reply()
    {
        using var h = new Harness();

        await h.Core.HandleMessageAsync("not json");

        Assert.Empty(h.Sent);
    }

    [Fact]
    public async Task A_write_that_throws_is_logged_and_never_escapes_into_the_UI_thread()
    {
        // The handler's caller is an event with nobody to catch, and the flush's is a timer tick that would
        // repeat the exception every interval.
        using var h = new Harness { SendFailure = new InvalidOperationException("view is gone") };
        h.Core.Start();

        await h.Core.HandleMessageAsync(Json("APP", "PING"));
        await h.Core.HandleMessageAsync(Ready());
        h.Core.SendNotification("APP", "TICK", null, null);
        h.Tick!();

        Assert.Contains(h.Lines, line => line.Contains("SendRawMessage failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dispatcher_with_no_window_left_drops_the_reply_rather_than_throwing()
    {
        using var h = new Harness();
        h.Ui.Target = UiTargetState.Gone;

        await h.Core.HandleMessageAsync(Json("APP", "PING"));

        Assert.Empty(h.Sent);
    }

    [Fact]
    public void Start_runs_once_and_not_after_dispose()
    {
        using var h = new Harness();
        h.Core.Start();
        Assert.Throws<InvalidOperationException>(h.Core.Start);

        h.Core.Dispose();
        Assert.Throws<ObjectDisposedException>(h.Core.Start);
    }

    [Fact]
    public void Dispose_stops_the_timer_once_and_is_idempotent()
    {
        using var h = new Harness();
        h.Core.Start();

        h.Core.Dispose();
        h.Core.Dispose();

        Assert.Equal(1, h.TimerStops);
    }

    [Fact]
    public async Task Dispose_does_not_cancel_a_request_in_flight_and_its_reply_is_dropped_quietly()
    {
        // The mobile shape: the bridge dies with its PAGE on every activity recreation, and a save the page
        // started must finish — measured as a created, empty file when it was cancelled.
        var dispatcher = new ParkedDispatcher();
        using var h = new Harness(new MobileIpcBridgeOptions { Dispatcher = dispatcher });
        var inFlight = h.Core.HandleMessageAsync(Json("APP", "SLOW", "slow-1"));

        h.Core.Dispose();

        Assert.False(dispatcher.Seen.IsCancellationRequested);
        dispatcher.Gate.SetResult(IpcResponse.CreateSuccess("slow-1"));
        await inFlight;   // never throws
    }

    [Fact]
    public void A_notification_sent_after_dispose_queues_nothing()
    {
        using var h = new Harness();
        h.Core.Dispose();

        h.Core.SendNotification("APP", "LATE", null, null);

        Assert.Equal(0, h.Core.NotificationReport.Pending);
        Assert.Equal(0, h.Core.NotificationReport.Accepted);
    }
}
