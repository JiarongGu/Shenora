using System.Diagnostics;
using Shenora;
using Shenora.Engine.Missions;

namespace Shenora.Tests.Missions;

/// <summary>
/// The global bound and its interaction with a named lane's capacity — filed by the first adopter on
/// 2026-08-05 as "<c>ILane.Capacity</c> can only NARROW a lane, never widen it, and the getter lies about
/// it".
///
/// <para>
/// ⚠ <b>These assert on PEAK CONCURRENCY, never on the property</b>, which is the whole lesson of the
/// report: a test that set a capacity and read it back passed in every one of the three measured
/// configurations, including the broken one. A capacity test that does not observe work running is not
/// testing capacity. <see cref="ILane.EffectiveCapacity"/> exists so an APP does not have to do this, and
/// it is asserted here alongside the observed peak rather than instead of it — a derived number is only
/// worth having if something proves it matches reality.
/// </para>
/// </summary>
public class MissionLaneCapacityTests
{
    private const string Lane = "gpu";

    /// <summary>Long enough that queued batches are unambiguous, short enough to keep the suite quick.</summary>
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(120);

    /// <summary>Records the highest number of missions running at the same instant.</summary>
    private sealed class PeakProbe
    {
        private readonly object _gate = new();
        private int _active;

        public int Peak { get; private set; }

        public async Task RunAsync(TimeSpan duration, CancellationToken ct = default)
        {
            lock (_gate) { _active++; Peak = Math.Max(Peak, _active); }
            try { await Task.Delay(duration, ct); }
            finally { lock (_gate) _active--; }
        }
    }

    private static MissionScheduler NewScheduler(int globalCapacity) =>
        new(new MissionSchedulerOptions { GlobalLaneCapacity = globalCapacity });

    /// <summary>Submit <paramref name="count"/> missions that all draw one permit from <see cref="Lane"/>.</summary>
    private static Task RunBatchAsync(MissionScheduler scheduler, PeakProbe probe, int count = 6) =>
        Task.WhenAll(Enumerable.Range(0, count).Select(_ => scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, ct) => probe.RunAsync(Slice, ct),
            Lanes = [new MissionLane(Lane)],
        })));

    [Fact]
    public async Task The_configured_bound_reaches_the_scheduler_as_its_global_lane()
    {
        // `GlobalLaneCapacity` (renamed from `DefaultLaneCapacity`, no alias kept) has to arrive as the
        // GLOBAL LANE's capacity, not merely round-trip on the options object — the rename touched the one
        // line in the constructor that reads it, and reading the wrong property there would silently fall
        // through to the auto default instead of failing.
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions { GlobalLaneCapacity = 2 });
        var probe = new PeakProbe();

        await RunBatchAsync(scheduler, probe);

        Assert.Equal(2, scheduler.GlobalLane.Capacity);
        Assert.Equal(2, probe.Peak);
    }

    [Fact]
    public async Task An_untouched_lane_runs_at_the_global_bound()
    {
        // Row A of the adopter's table. A new lane starts at the global bound, so it narrows nothing.
        await using var scheduler = NewScheduler(globalCapacity: 3);
        var probe = new PeakProbe();

        await RunBatchAsync(scheduler, probe);

        Assert.Equal(3, probe.Peak);
        Assert.Equal(3, scheduler.Lane(Lane).EffectiveCapacity);
    }

    [Fact]
    public async Task A_lane_narrower_than_the_global_bound_wins()
    {
        // Row C. The GPU-gate shape, and the direction that always worked.
        await using var scheduler = NewScheduler(globalCapacity: 3);
        scheduler.Lane(Lane).Capacity = 1;
        var probe = new PeakProbe();

        await RunBatchAsync(scheduler, probe);

        Assert.Equal(1, probe.Peak);
        Assert.Equal(1, scheduler.Lane(Lane).EffectiveCapacity);
    }

    [Fact]
    public async Task A_lane_wider_than_the_global_bound_is_still_bounded_and_SAYS_so()
    {
        // Row B — the reported defect. The behaviour is BY DESIGN (the global lane bounds total
        // concurrency, design §3), so this pins it rather than changing it. What was wrong is that it
        // was undetectable: Capacity answered 3 while the lane ran at 1.
        await using var scheduler = NewScheduler(globalCapacity: 1);
        var lane = scheduler.Lane(Lane);
        lane.Capacity = 3;
        var probe = new PeakProbe();

        await RunBatchAsync(scheduler, probe);

        Assert.Equal(1, probe.Peak);
        // The REQUESTED value is kept — not clamped — so a later widening of the global bound gives the
        // caller the width they asked for instead of having silently discarded it.
        Assert.Equal(3, lane.Capacity);
        // …and this is the member that no longer lies. It is the only thing here an app could have
        // ASKED; everything else about this configuration required timing the work.
        Assert.Equal(1, lane.EffectiveCapacity);
    }

    [Fact]
    public async Task Widening_the_global_lane_at_RUNTIME_restores_a_throttled_lane()
    {
        // The adopter's actual blocker: a governor that throttles under load and restores when idle
        // could throttle and never recover, because the bound was init-only and unreachable. This is
        // the capability that did not exist — and it is asserted by running work, twice, not by
        // reading a property back.
        await using var scheduler = NewScheduler(globalCapacity: 1);
        scheduler.Lane(Lane).Capacity = 3;

        var throttled = new PeakProbe();
        await RunBatchAsync(scheduler, throttled);
        Assert.Equal(1, throttled.Peak);

        scheduler.GlobalLane.Capacity = 3;
        Assert.Equal(3, scheduler.Lane(Lane).EffectiveCapacity);

        var restored = new PeakProbe();
        await RunBatchAsync(scheduler, restored);
        Assert.Equal(3, restored.Peak);
    }

    [Fact]
    public async Task Holding_the_global_lane_pauses_everything_without_cancelling_it()
    {
        // Exposing the bound as a LANE rather than as a setter is what makes this reachable: "pause the
        // scheduler" is Hold() on the lane every mission draws from. The machinery already did it; it
        // could not be asked for.
        await using var scheduler = NewScheduler(globalCapacity: 3);
        var probe = new PeakProbe();

        scheduler.GlobalLane.Hold();
        var batch = RunBatchAsync(scheduler, probe);

        // Nothing may start while the lane is held. A short wait is the assertion: if admission were
        // going to happen it would happen immediately, since dispatch is event-driven on submit.
        await Task.Delay(150);
        Assert.Equal(0, probe.Peak);
        Assert.Equal(6, scheduler.PendingCount);

        scheduler.GlobalLane.Release();
        await batch;
        Assert.Equal(3, probe.Peak);
    }

    [Fact]
    public async Task The_global_lane_is_addressable_by_name_and_is_the_SAME_lane()
    {
        // A decoy is the trap this closes: before it was addressable, a lane merely SHARING the global
        // name would have been a separate pool that accepted a capacity change and altered nothing —
        // the exact failure the report is about, reachable a second way.
        await using var scheduler = NewScheduler(globalCapacity: 2);

        Assert.Same(scheduler.GlobalLane, scheduler.Lane(MissionScheduler.GlobalLaneName));
        Assert.Equal(MissionScheduler.GlobalLaneName, scheduler.GlobalLane.Name);

        scheduler.Lane(MissionScheduler.GlobalLaneName).Capacity = 5;
        Assert.Equal(5, scheduler.GlobalLane.Capacity);
    }

    [Fact]
    public async Task A_mission_DECLARING_the_global_lane_weights_itself_against_the_SAME_pool()
    {
        // Every mission implicitly takes 1 from the global lane. Naming it explicitly ADDS to that —
        // `MissionDefinition.Lanes` is documented as permits drawn "on top of the scheduler's default
        // lane" — so `(global, 1)` costs 2 and is how a heavy mission counts double against the bound.
        //
        // ⚠ The assertion is chosen to DISCRIMINATE, which is the only reason it is worth having:
        // capacity 4 with each mission costing 2 gives a peak of 2, whereas the failure this guards
        // against — the name resolving to a separate pool that merely shares it — would give 4, since
        // the implicit permit and the declared one would come from different places. A capacity-2
        // scheduler could not tell those apart, and that is what this test asserted at first.
        await using var scheduler = NewScheduler(globalCapacity: 4);
        var probe = new PeakProbe();

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, ct) => probe.RunAsync(Slice, ct),
            Lanes = [new MissionLane(MissionScheduler.GlobalLaneName)],
        })));

        Assert.Equal(2, probe.Peak);
    }

    [Fact]
    public async Task Narrowing_the_global_lane_never_cancels_work_already_running()
    {
        // The same rule every lane already promises, now reachable on the bound itself: a governor
        // throttling mid-run means "run less from now on", never "kill what is going". Asserted by
        // completing the batch rather than by counting — a cancelled mission would fault the await.
        await using var scheduler = NewScheduler(globalCapacity: 3);
        var probe = new PeakProbe();

        var batch = RunBatchAsync(scheduler, probe);
        await Task.Delay(40);              // let the first tranche get in flight
        scheduler.GlobalLane.Capacity = 1;

        var stopwatch = Stopwatch.StartNew();
        await batch;                        // must COMPLETE, not fault
        stopwatch.Stop();

        // The first tranche keeps its permits; the remainder is serialised behind the new bound. Peak
        // therefore records what was already running, which is the point — it was not killed.
        Assert.Equal(3, probe.Peak);
        Assert.Equal(1, scheduler.GlobalLane.EffectiveCapacity);
    }
}
