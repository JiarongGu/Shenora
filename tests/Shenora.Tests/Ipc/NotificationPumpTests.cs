using Shenora;
using Shenora.Core.Events;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Ipc;

public class NotificationPumpTests
{
    private static NotificationPump Pump(NotificationPumpOptions? options = null) => new(options ?? new());

    private static IpcNotification Note(string module = "APP", string type = "TICK", string? scope = null,
                                        string? key = null, object? payload = null) =>
        new() { Module = module, Type = type, Scope = scope, CoalesceKey = key, Payload = payload };

    [Fact]
    public void Nothing_is_delivered_before_the_client_is_ready()
    {
        using var pump = Pump();
        pump.Enqueue(Note());

        Assert.False(pump.TryDrainBatch(out _));
        Assert.Equal(1, pump.PendingCount);      // buffered, NOT dropped
    }

    [Fact]
    public void Opening_the_gate_delivers_everything_buffered_since_construction()
    {
        using var pump = Pump();
        pump.Enqueue(Note(type: "FIRST"));
        pump.Open();

        Assert.True(pump.TryDrainBatch(out var json));
        Assert.Contains("FIRST", json);
        Assert.Equal(0, pump.PendingCount);
    }

    [Fact]
    public void Closing_the_gate_buffers_again_instead_of_draining_into_a_dead_page()
    {
        using var pump = Pump();
        pump.Open();
        pump.Close();
        pump.Enqueue(Note());

        Assert.False(pump.TryDrainBatch(out _));
    }

    [Fact]
    public void The_queue_is_bounded_and_drops_the_OLDEST()
    {
        using var pump = Pump(new NotificationPumpOptions { MaxQueued = 2 });
        pump.Enqueue(Note(type: "ONE"));
        pump.Enqueue(Note(type: "TWO"));
        pump.Enqueue(Note(type: "THREE"));
        pump.Open();

        Assert.True(pump.TryDrainBatch(out var json));
        Assert.DoesNotContain("ONE", json);
        Assert.Contains("THREE", json);
    }

    [Fact]
    public void A_filter_decides_per_channel_what_is_delivered()
    {
        using var pump = Pump(new NotificationPumpOptions { Filter = n => n.Scope == "w1" });
        pump.Open();
        pump.Enqueue(Note(scope: "w1"));
        pump.Enqueue(Note(scope: "w2"));

        Assert.True(pump.TryDrainBatch(out var json));
        Assert.Contains("w1", json);
        Assert.DoesNotContain("w2", json);
    }

    [Fact]
    public void A_throwing_filter_fails_closed_dropping_the_notification_not_the_pump()
    {
        // The filter is app-supplied and guarded (AppCallback.RunOrDefault, fallback: false) like every
        // other app callback on a UI-thread-reachable path — a throwing predicate must resolve to a
        // policy decision (drop), never propagate out of Enqueue and crash whatever called it.
        using var pump = Pump(new NotificationPumpOptions { Filter = _ => throw new InvalidOperationException("boom") });
        pump.Open();

        pump.Enqueue(Note()); // must not throw despite the filter throwing

        Assert.Equal(0, pump.PendingCount);       // failed CLOSED — dropped, not queued
        Assert.False(pump.TryDrainBatch(out _));  // nothing pending to drain
    }

    [Fact]
    public void One_unserializable_payload_does_not_lose_the_rest_of_its_batch()
    {
        using var pump = Pump();
        pump.Open();
        pump.Enqueue(new IpcNotification { Module = "APP", Type = "BAD", Payload = new Throws() });
        pump.Enqueue(Note(type: "GOOD"));

        Assert.True(pump.TryDrainBatch(out var json));
        Assert.Contains("GOOD", json);
        Assert.DoesNotContain("BAD", json);
    }

    private sealed class Throws { public string Boom => throw new InvalidOperationException("nope"); }

    [Fact]
    public void Bus_events_arrive_as_notifications_and_stop_after_dispose()
    {
        var bus = new EventBus();
        var pump = Pump(new NotificationPumpOptions { EventBus = bus });
        pump.Open();
        bus.Emit("APP", "FROM_BUS");
        pump.Dispose();
        bus.Emit("APP", "AFTER_DISPOSE");

        Assert.Equal(1, pump.PendingCount);
    }

    /// <summary>
    /// 🔴 The batch has always coalesced ROUND TRIPS; this is what makes it coalesce PAYLOADS. A request
    /// reporting a hundred times inside one 50 ms window used to put a hundred snapshots in one message
    /// for the page to fold into the one number it renders.
    /// </summary>
    [Fact]
    public void A_later_notification_supersedes_an_earlier_one_carrying_the_same_key()
    {
        using var pump = Pump();
        pump.Open();
        pump.Enqueue(Note(type: "STATUS", key: "r-1", payload: new { step = "FIRST" }));
        pump.Enqueue(Note(type: "STATUS", key: "r-1", payload: new { step = "SECOND" }));
        pump.Enqueue(Note(type: "STATUS", key: "r-1", payload: new { step = "LAST" }));

        Assert.True(pump.TryDrainBatch(out var json));
        Assert.Contains("LAST", json);
        Assert.DoesNotContain("FIRST", json);
        Assert.DoesNotContain("SECOND", json);
    }

    /// <summary>
    /// The opt-in half, and the one that matters most: the pump cannot know whether an un-keyed payload is
    /// a snapshot or a DELTA, and coalescing deltas silently loses data. So an emitter that says nothing
    /// keeps every event it emitted.
    /// </summary>
    [Fact]
    public void Un_keyed_notifications_are_never_coalesced_even_when_otherwise_identical()
    {
        using var pump = Pump();
        pump.Open();
        pump.Enqueue(Note(type: "ADDED", payload: new { delta = "one" }));
        pump.Enqueue(Note(type: "ADDED", payload: new { delta = "two" }));

        Assert.True(pump.TryDrainBatch(out var json));
        Assert.Contains("one", json);
        Assert.Contains("two", json);
    }

    /// <summary>
    /// A key is scoped to its (module, type, scope), so two DIFFERENT events an app happens to key by the
    /// same entity id never eat each other — only successive snapshots of the same thing do.
    /// </summary>
    [Fact]
    public void A_key_shared_across_different_types_or_scopes_does_not_coalesce()
    {
        using var pump = Pump();
        pump.Open();
        pump.Enqueue(Note(type: "OPENED", key: "e-1", payload: new { mark = "opened" }));
        pump.Enqueue(Note(type: "CLOSED", key: "e-1", payload: new { mark = "closed" }));
        pump.Enqueue(Note(type: "OPENED", key: "e-1", scope: "w2", payload: new { mark = "scoped" }));

        Assert.True(pump.TryDrainBatch(out var json));
        Assert.Contains("opened", json);
        Assert.Contains("closed", json);
        Assert.Contains("scoped", json);
    }

    /// <summary>
    /// The survivor keeps the LATEST position, not the first one's — a superseding snapshot describes
    /// "now", so it must not be re-ordered ahead of un-keyed events that were queued between the two.
    /// </summary>
    [Fact]
    public void The_survivor_keeps_the_position_of_the_notification_that_superseded()
    {
        using var pump = Pump();
        pump.Open();
        pump.Enqueue(Note(type: "STATUS", key: "r-1", payload: new { mark = "stale" }));
        pump.Enqueue(Note(type: "BETWEEN"));
        pump.Enqueue(Note(type: "STATUS", key: "r-1", payload: new { mark = "fresh" }));

        Assert.True(pump.TryDrainBatch(out var json));
        Assert.DoesNotContain("stale", json);   // it really did coalesce, or the order below proves nothing
        Assert.True(json!.IndexOf("BETWEEN", StringComparison.Ordinal)
                    < json.IndexOf("fresh", StringComparison.Ordinal),
                    "the surviving snapshot must stay AFTER the event queued between the two, not take the older slot");
    }

    /// <summary>
    /// The key is a host-side buffering hint and must never reach the page: by the time a batch leaves,
    /// the coalescing has already happened and there is nothing left for a client to decide.
    /// </summary>
    [Fact]
    public void The_coalesce_key_never_crosses_the_wire()
    {
        using var pump = Pump();
        pump.Open();
        pump.Enqueue(Note(type: "STATUS", key: "a-very-distinctive-key"));

        Assert.True(pump.TryDrainBatch(out var json));
        Assert.DoesNotContain("a-very-distinctive-key", json);
        Assert.DoesNotContain("coalesce", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_options_are_rejected_at_construction_naming_the_option()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new NotificationPump(new NotificationPumpOptions { MaxQueued = 0 }));

        Assert.Contains(nameof(NotificationPumpOptions.MaxQueued), error.Message);
    }
}
