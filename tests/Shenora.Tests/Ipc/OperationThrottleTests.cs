using System.Threading;
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

    /// <summary>
    /// <c>Task.Delay(TimeSpan, TimeProvider)</c>'s underlying promise completes its continuation
    /// ASYNCHRONOUSLY (<c>RunContinuationsAsynchronously</c>) even though
    /// <see cref="FakeTimeProvider.Advance(TimeSpan)"/> invokes the due timer callback
    /// synchronously — <c>Advance</c> only completes the promise, it does not run whatever is
    /// awaiting it (confirmed against the runtime's own tracked behavior, dotnet/runtime #85326).
    /// A bare assert immediately after <c>Advance</c> is therefore a genuine race that usually
    /// wins by luck; it was caught here the hard way, once an unrelated test added enough
    /// thread-pool pressure in the same run to make the queued continuation lose. Poll with a
    /// hard deadline instead of assuming synchronous completion — never an unbounded wait.
    /// </summary>
    private static void WaitForEventCount(List<EventMessage> events, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (events) { if (events.Count >= expected) return; }
            Thread.Sleep(5);
        }
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

        int beforeAdvance;
        lock (events) { beforeAdvance = events.Count; }
        clock.Advance(TimeSpan.FromMilliseconds(101));   // close the window; nothing else is reported
        WaitForEventCount(events, beforeAdvance + 1, TimeSpan.FromSeconds(5));   // bounded: see WaitForEventCount

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

    /// <summary>
    /// Review finding (Task 3 fix): TrailingScheduled must clear even when Task.Delay itself
    /// FAULTS, or it sticks at true forever and every later Report on that one operation is
    /// silently dropped — the exact "stuck-at-80%-bar" symptom this throttle exists to prevent.
    /// <see cref="TimeProvider"/> is public, consumer-settable surface, so a faulting custom
    /// CreateTimer is not purely academic.
    /// <para>
    /// The seam: <see cref="FaultOnceTimeProvider"/> makes exactly the FIRST CreateTimer call
    /// throw (synchronously, before Task.Delay ever returns an awaitable — so the whole faulting
    /// attempt resolves in-line with no real waiting) and delegates every later call to a real
    /// <see cref="FakeTimeProvider"/>, so the second scheduling attempt can genuinely complete once
    /// the test advances the clock. Bounded throughout: nothing here awaits real time.
    /// </para>
    /// </summary>
    [Fact]
    public void A_faulting_trailing_emit_still_resets_the_flag_so_a_later_report_is_not_muted_forever()
    {
        var bus = new EventBus();
        var events = new List<EventMessage>();
        bus.SubscribeToAll(m => { lock (events) events.Add(m); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        var provider = new FaultOnceTimeProvider(clock);
        var registry = new OperationRegistry(bus, new OperationRegistryOptions
        {
            ProgressInterval = TimeSpan.FromMilliseconds(100),
            TimeProvider = provider,
        });
        var operation = registry.Start("SCAN", new OperationOptions { Kind = "FILES" });
        events.Clear();   // drop the Start emission

        operation.Report(1);   // first-ever progress report: window trivially "elapsed" -> emits immediately
        operation.Report(2);   // throttled: schedules a trailing emit whose CreateTimer call FAULTS
        operation.Report(3);   // if the flag had stuck at true, this would be silently dropped forever

        int beforeAdvance;
        lock (events) { beforeAdvance = events.Count; }
        clock.Advance(TimeSpan.FromMilliseconds(101));   // fire the (successfully scheduled) trailing timer
        WaitForEventCount(events, beforeAdvance + 1, TimeSpan.FromSeconds(5));   // bounded: see WaitForEventCount

        Assert.Equal(2, events.Count);   // the immediate emit, plus exactly one trailing catch-up
        var last = Assert.IsType<OperationInfo>(events[^1].Payload);
        Assert.Equal(3, last.Progress);  // the operation is NOT permanently muted
    }

    /// <summary>
    /// Wraps a real <see cref="FakeTimeProvider"/> and makes only the FIRST <see cref="CreateTimer"/>
    /// call throw, delegating every later call to the fake clock untouched.
    /// </summary>
    private sealed class FaultOnceTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private int _timersCreated;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (Interlocked.Increment(ref _timersCreated) == 1)
                throw new InvalidOperationException("simulated timer-creation fault (test seam)");
            return inner.CreateTimer(callback, state, dueTime, period);
        }
    }
}
