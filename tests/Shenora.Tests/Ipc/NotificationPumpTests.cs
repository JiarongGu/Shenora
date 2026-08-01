using Shenora.Core;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

public class NotificationPumpTests
{
    private static NotificationPump Pump(NotificationPumpOptions? options = null) => new(options ?? new());

    private static IpcNotification Note(string module = "APP", string type = "TICK", string? scope = null) =>
        new() { Module = module, Type = type, Scope = scope };

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

    [Fact]
    public void Invalid_options_are_rejected_at_construction_naming_the_option()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new NotificationPump(new NotificationPumpOptions { MaxQueued = 0 }));

        Assert.Contains(nameof(NotificationPumpOptions.MaxQueued), error.Message);
    }
}
