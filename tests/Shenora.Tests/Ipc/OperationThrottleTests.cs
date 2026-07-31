using Microsoft.Extensions.Time.Testing;
using Shenora.Core;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// Progress emission is a FRAME RATE, and it has to be one: the notification batcher queues events
/// WITHOUT coalescing, so an unthrottled per-item Report loop ships hundreds of updates a second.
/// The trailing emit is the half that is easy to omit and impossible to notice — without it the last
/// progress value of a fast operation is simply lost, and a stuck-at-80% bar is the symptom.
/// </summary>
public class OperationThrottleTests
{
    private static (OperationRegistry Registry, List<EventMessage> Events, FakeTimeProvider Clock) Build()
    {
        var bus = new EventBus();
        var events = new List<EventMessage>();
        bus.SubscribeToAll(m => { lock (events) events.Add(m); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        var registry = new OperationRegistry(bus, new OperationRegistryOptions
        {
            ProgressInterval = TimeSpan.FromMilliseconds(100),
            TimeProvider = clock,
        });
        return (registry, events, clock);
    }

    [Fact]
    public void Rapid_progress_reports_collapse_to_one_emission_per_window()
    {
        var (registry, events, _) = Build();
        var operation = registry.Start("SCAN", new OperationOptions { Kind = "FILES" });
        events.Clear();   // drop the Start emission

        for (var i = 1; i <= 50; i++) operation.Report(i);

        Assert.Single(events);   // 50 reports, one frame
    }

    [Fact]
    public void The_last_progress_value_always_lands_via_the_trailing_emit()
    {
        var (registry, events, clock) = Build();
        var operation = registry.Start("SCAN", new OperationOptions { Kind = "FILES" });
        for (var i = 1; i <= 50; i++) operation.Report(i);

        clock.Advance(TimeSpan.FromMilliseconds(101));   // close the window; nothing else is reported

        var last = Assert.IsType<OperationInfo>(events[^1].Payload);
        Assert.Equal(50, last.Progress);
    }

    [Fact]
    public void Lifecycle_transitions_are_never_throttled()
    {
        var (registry, events, _) = Build();
        var operation = registry.Start("SCAN", new OperationOptions { Kind = "FILES" });
        operation.Report(10);
        events.Clear();

        operation.Complete();   // same window as the report above

        var info = Assert.IsType<OperationInfo>(Assert.Single(events).Payload);
        Assert.Equal(OperationStatus.Completed, info.Status);
    }

    [Fact]
    public void Finished_history_is_capped_and_running_work_is_never_pruned()
    {
        var bus = new EventBus();
        var registry = new OperationRegistry(bus, new OperationRegistryOptions { MaxHistory = 2 });
        var running = registry.Start("SCAN", new OperationOptions { Kind = "FILES" });
        for (var i = 0; i < 5; i++) registry.Start("SCAN", new OperationOptions { Kind = "FILES" }).Complete();

        var all = registry.GetAll();

        Assert.Equal(3, all.Count);                        // 1 running + 2 kept
        Assert.Contains(all, o => o.Id == running.Id);
    }
}
