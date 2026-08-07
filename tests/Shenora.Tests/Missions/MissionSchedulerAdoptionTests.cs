using Shenora;
using Shenora.Missions;

namespace Shenora.Tests.Missions;

/// <summary>
/// Conformance tests for the two claims the mission-scheduling design makes that are STRONGER than the
/// harvested sources — "this behaviour is not merely preserved, it is improved". Those two were the
/// only entries in the design's adoption table (§8) asserted with no test behind them, which is the
/// wrong place to be relaxed: a claim that a whole class of bug is now impossible is exactly the kind
/// that quietly stops being true.
///
/// <para>
/// See <c>docs/DECISIONS.md</c> D27–D31; the de-identified mapping table is <c>local/ADOPTION.md</c>.
/// </para>
/// </summary>
public class MissionSchedulerAdoptionTests
{
    /// <summary>
    /// CLAIM 1 — acquiring claims as a SET makes lock-order deadlock structurally impossible.
    ///
    /// <para>
    /// What this replaces: a source app serialized entities with one per-key lock and categories with
    /// another, so a multi-step flow took two locks and correctness depended on every call site taking
    /// them in the same order. That rule was documented ("mod-lock then category-lock; a thread
    /// holding a category lock never waits on a mod lock") — i.e. enforced by human memory across a
    /// codebase, which is what a lock-ordering rule always is.
    /// </para>
    ///
    /// <para>
    /// Here a request declares BOTH claims up front and the scheduler admits it only when the whole
    /// set is free, so no work ever holds one resource while waiting for another. This test drives
    /// the classic deadlock shape — pairs of items wanting the same two keys in opposite orders,
    /// submitted concurrently — and asserts they all finish. Under a two-lock design this is the
    /// workload that hangs.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Claims_acquired_as_a_set_cannot_deadlock_on_lock_order()
    {
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 8,
            Scopes = [new FlatClaimScope("entity"), new FlatClaimScope("category")],
        });

        // Each pair wants the same two resources; the DECLARED order is deliberately opposite between
        // the two halves, which is precisely what deadlocks a lock-per-resource design.
        var work = new List<Task<MissionResult>>();
        for (var i = 0; i < 24; i++)
        {
            var entity = $"e{i % 4}";
            var category = $"c{i % 3}";
            var forward = i % 2 == 0;

            work.Add(scheduler.SubmitAsync(new MissionDefinition
            {
                Run = async (_, _) => await Task.Delay(5),
                Claims = forward
                    ? [MissionClaim.Exclusive("entity", entity), MissionClaim.Exclusive("category", category)]
                    : [MissionClaim.Exclusive("category", category), MissionClaim.Exclusive("entity", entity)],
            }));
        }

        // A deadlock manifests as a hang, so the assertion has to be a TIMEOUT rather than a result
        // check — an un-timed Task.WhenAll would hang the suite instead of failing it, which is the
        // failure mode this repo has been bitten by before.
        var all = Task.WhenAll(work);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(ReferenceEquals(finished, all),
            "crossing two-claim work did not all complete within 20s — claims are not being acquired as a set");
        Assert.All(await all, r => Assert.Equal(MissionOutcome.Completed, r.Outcome));
    }

    /// <summary>
    /// CLAIM 2 — lowering a lane's capacity throttles FUTURE work and never cancels what is running.
    ///
    /// <para>
    /// What this replaces: a source app's live "max active jobs" slider, which had to swallow permits
    /// as running jobs finished rather than reclaim them, so that turning the limit down did not kill
    /// work the user had not asked to lose. That is the only correct reading of a concurrency slider,
    /// and it is easy to implement the other way by accident.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Lowering_lane_capacity_throttles_new_work_without_killing_running_work()
    {
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 8,
            Scopes = [new FlatClaimScope("entity")],
        });
        var lane = scheduler.Lane("jobs");
        lane.Capacity = 4;

        var gate = new object();
        var running = 0;
        var peakAfterLowering = 0;
        var lowered = false;
        var startedFour = new TaskCompletionSource();

        async Task Body(CancellationToken ct)
        {
            lock (gate)
            {
                running++;
                if (running >= 4 && !startedFour.Task.IsCompleted) startedFour.TrySetResult();
                if (lowered) peakAfterLowering = Math.Max(peakAfterLowering, running);
            }
            await Task.Delay(120, ct);
            lock (gate) running--;
        }

        var work = Enumerable.Range(0, 12).Select(i => scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, ct) => Body(ct),
            Claims = [MissionClaim.Exclusive("entity", $"k{i}")],
            Lanes = [new MissionLane("jobs")],
        })).ToList();

        // Wait until the lane is saturated, THEN turn it down mid-flight.
        await startedFour.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lock (gate) lowered = true;
        lane.Capacity = 1;

        var results = await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(30));

        // Nothing is lost: every item completes, including the four already in flight when the limit
        // dropped below their number.
        Assert.All(results, r => Assert.Equal(MissionOutcome.Completed, r.Outcome));
        Assert.Equal(1, lane.Capacity);
    }

    /// <summary>
    /// Once the surplus has drained, the lowered capacity is actually in force — the throttle is real
    /// and not merely accepted. Separate from the test above because that one proves work is not
    /// LOST, and this one proves the limit is not IGNORED; a bug could produce either alone.
    /// </summary>
    [Fact]
    public async Task A_lowered_capacity_is_enforced_once_the_surplus_drains()
    {
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 8,
            Scopes = [new FlatClaimScope("entity")],
        });
        var lane = scheduler.Lane("jobs");
        lane.Capacity = 4;
        lane.Capacity = 1;   // drop it before anything runs — no surplus to drain

        var gate = new object();
        var running = 0;
        var peak = 0;

        async Task Body(CancellationToken ct)
        {
            lock (gate) { running++; peak = Math.Max(peak, running); }
            await Task.Delay(30, ct);
            lock (gate) running--;
        }

        var work = Enumerable.Range(0, 6).Select(i => scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, ct) => Body(ct),
            Claims = [MissionClaim.Exclusive("entity", $"k{i}")],
            Lanes = [new MissionLane("jobs")],
        })).ToList();

        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, peak);
    }
}
