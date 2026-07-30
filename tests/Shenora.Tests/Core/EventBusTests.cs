using Microsoft.Extensions.DependencyInjection;
using Shenora.Core;

namespace Shenora.Tests.Core;

public class EventBusTests
{
    private static Func<EventMessage, Task> Collect(List<EventMessage> sink) =>
        message =>
        {
            lock (sink) sink.Add(message);
            return Task.CompletedTask;
        };

    [Theory]
    [InlineData("", "TICK")]
    [InlineData("APP", "")]
    public async Task The_convenience_overload_rejects_an_empty_module_or_type(string module, string type)
    {
        // The envelope overload is guarded by `required` plus the subscribe-side checks, but this one
        // accepted an empty module or type and built a message that could never match any subscription —
        // a silently undeliverable event, which is exactly what this bus exists to prevent (P5.5 H6).
        var bus = new EventBus();

        await Assert.ThrowsAsync<ArgumentException>(() => bus.EmitAsync(module, type));
    }

    [Fact]
    public async Task Exact_subscription_receives_only_its_event()
    {
        var bus = new EventBus();
        var received = new List<EventMessage>();
        bus.Subscribe("APP", "UPDATED", Collect(received));

        await bus.EmitAsync("APP", "UPDATED", payload: 42);
        await bus.EmitAsync("APP", "OTHER");
        await bus.EmitAsync("OTHER", "UPDATED");

        var message = Assert.Single(received);
        Assert.Equal(42, message.Payload);
    }

    [Fact]
    public async Task Module_subscription_receives_all_of_the_modules_types()
    {
        var bus = new EventBus();
        var received = new List<EventMessage>();
        bus.SubscribeToModule("APP", Collect(received));

        await bus.EmitAsync("APP", "A");
        await bus.EmitAsync("APP", "B");
        await bus.EmitAsync("OTHER", "A");

        Assert.Equal(["A", "B"], received.Select(m => m.Type));
    }

    [Fact]
    public async Task SubscribeToAll_receives_everything()
    {
        var bus = new EventBus();
        var received = new List<EventMessage>();
        bus.SubscribeToAll(Collect(received));

        await bus.EmitAsync("APP", "A");
        await bus.EmitAsync("OTHER", "B", scope: "s1");

        Assert.Equal(2, received.Count);
    }

    [Fact]
    public async Task Scoped_subscription_receives_its_scope_and_global_events_only()
    {
        var bus = new EventBus();
        var received = new List<EventMessage>();
        bus.Subscribe("APP", "UPDATED", "s1", Collect(received));

        await bus.EmitAsync("APP", "UPDATED", scope: "s1"); // own scope
        await bus.EmitAsync("APP", "UPDATED");              // global broadcast reaches everyone
        await bus.EmitAsync("APP", "UPDATED", scope: "s2"); // someone else's scope

        Assert.Equal(["s1", null], received.Select(m => m.Scope));
    }

    [Fact]
    public async Task Unscoped_subscription_receives_every_scope()
    {
        var bus = new EventBus();
        var received = new List<EventMessage>();
        bus.Subscribe("APP", "UPDATED", Collect(received));

        await bus.EmitAsync("APP", "UPDATED", scope: "s1");
        await bus.EmitAsync("APP", "UPDATED", scope: "s2");
        await bus.EmitAsync("APP", "UPDATED");

        Assert.Equal(3, received.Count);
    }

    [Fact]
    public async Task Unsubscribe_stops_delivery()
    {
        var bus = new EventBus();
        var received = new List<EventMessage>();
        var id = bus.Subscribe("APP", "UPDATED", Collect(received));

        await bus.EmitAsync("APP", "UPDATED");
        bus.Unsubscribe(id);
        await bus.EmitAsync("APP", "UPDATED");

        Assert.Single(received);
        Assert.Equal(0, bus.GetHandlerCount());
    }

    [Fact]
    public async Task Failing_handler_is_isolated_from_the_others()
    {
        var bus = new EventBus();
        var received = new List<EventMessage>();
        bus.Subscribe("APP", "UPDATED", _ => throw new InvalidOperationException("boom"));
        bus.Subscribe("APP", "UPDATED", Collect(received));

        await bus.EmitAsync("APP", "UPDATED"); // must not throw

        Assert.Single(received);
    }

    [Fact]
    public async Task Convenience_overload_fills_message_defaults()
    {
        var bus = new EventBus();
        var received = new List<EventMessage>();
        bus.SubscribeToAll(Collect(received));

        await bus.EmitAsync("APP", "UPDATED", payload: "data", scope: "s1");

        var message = Assert.Single(received);
        Assert.Equal("APP", message.Module);
        Assert.Equal("UPDATED", message.Type);
        Assert.Equal("data", message.Payload);
        Assert.Equal("s1", message.Scope);
        Assert.False(string.IsNullOrWhiteSpace(message.Id));
        Assert.True(DateTimeOffset.UtcNow - message.Timestamp < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Repeat_emits_keep_matching_via_the_cache()
    {
        var bus = new EventBus();
        var received = new List<EventMessage>();
        bus.Subscribe("APP", "UPDATED", Collect(received));

        // Second identical emit takes the memoized-match path.
        await bus.EmitAsync("APP", "UPDATED");
        await bus.EmitAsync("APP", "UPDATED");

        Assert.Equal(2, received.Count);
    }

    [Fact]
    public async Task Match_cache_keys_cannot_collide_across_dotted_names()
    {
        // ("APP","TASK", scope "s1") and ("APP","TASK.s1", no scope) must be DIFFERENT cache
        // entries — a '.'-joined key would let whichever fires first poison the other's result.
        var bus = new EventBus();
        var received = new List<EventMessage>();
        bus.Subscribe("APP", "TASK", "s1", Collect(received));

        await bus.EmitAsync("APP", "TASK.s1");              // different type — must NOT match
        await bus.EmitAsync("APP", "TASK", scope: "s1");    // must match despite the cached miss above

        var message = Assert.Single(received);
        Assert.Equal("TASK", message.Type);
    }

    [Fact]
    public void Application_builder_registers_the_event_bus()
    {
        using var app = global::Shenora.Core.ShenoraApplication.CreateBuilder([]).Build();

        Assert.IsType<EventBus>(app.Services.GetRequiredService<IEventBus>());
    }

    [Fact]
    public void App_registration_wins_over_the_builder_default()
    {
        var builder = global::Shenora.Core.ShenoraApplication.CreateBuilder([]);
        var custom = new EventBus();
        builder.Services.AddSingleton<IEventBus>(custom);
        using var app = builder.Build();

        Assert.Same(custom, app.Services.GetRequiredService<IEventBus>());
    }
}
