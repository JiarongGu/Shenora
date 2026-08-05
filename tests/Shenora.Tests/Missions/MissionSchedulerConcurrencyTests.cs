using Shenora.Core;

namespace Shenora.Tests.Missions;

/// <summary>
/// The load-bearing tests for <see cref="MissionScheduler"/>: they must prove BOTH halves of the
/// contract in the same run — that disjoint work runs in PARALLEL and that conflicting work is
/// SERIALIZED.
///
/// <para>
/// Asserting only that results are correct is worthless here: a fully sequential implementation
/// passes every correctness assertion while destroying the entire point of the component. So these
/// record peak concurrency, globally and per key, and assert on both. The technique is lifted from
/// the sibling that learned it — its in-memory filesystem tracked `MaxConcurrentSamePath` (must stay
/// 1) alongside a global peak (must exceed 1).
/// </para>
///
/// <para>
/// Every test passes an EXPLICIT lane capacity. Defaulting to <c>ProcessorCount</c> makes a
/// parallelism assertion pass or fail depending on the machine, which is how a regression hides on
/// whichever box happens to have two cores.
/// </para>
/// </summary>
public class MissionSchedulerConcurrencyTests
{
    /// <summary>Records concurrent execution, globally and per key, so both invariants are observable.</summary>
    private sealed class ConcurrencyProbe
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _activeByKey = [];
        private int _active;

        public int MaxConcurrentTotal { get; private set; }
        public Dictionary<string, int> MaxConcurrentByKey { get; } = [];
        public List<string> CompletionOrder { get; } = [];

        public async Task RunAsync(string key, TimeSpan duration, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _active++;
                MaxConcurrentTotal = Math.Max(MaxConcurrentTotal, _active);
                _activeByKey.TryGetValue(key, out var perKey);
                _activeByKey[key] = perKey + 1;
                MaxConcurrentByKey.TryGetValue(key, out var peak);
                MaxConcurrentByKey[key] = Math.Max(peak, perKey + 1);
            }
            try { await Task.Delay(duration, ct); }
            finally
            {
                lock (_gate)
                {
                    _active--;
                    _activeByKey[key]--;
                    CompletionOrder.Add(key);
                }
            }
        }
    }

    private static MissionScheduler NewScheduler(int capacity, IMissionPolicy? policy = null) =>
        new(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = capacity,
            Scopes = [new FlatClaimScope("entity"), new NestedClaimScope("tree", '/')],
            Policy = policy,
        });

    private static MissionDefinition Work(Func<MissionExecution, CancellationToken, Task> run, params MissionClaim[] claims) =>
        new() { Run = run, Claims = claims };

    [Fact]
    public async Task Disjoint_work_runs_in_parallel()
    {
        var probe = new ConcurrencyProbe();
        await using var scheduler = NewScheduler(capacity: 4);

        var submissions = Enumerable.Range(0, 4).Select(i =>
            scheduler.SubmitAsync(Work(
                (_, _) => probe.RunAsync($"item{i}", TimeSpan.FromMilliseconds(120)),
                MissionClaim.Exclusive("entity", $"item{i}"))));

        var results = await Task.WhenAll(submissions);

        Assert.All(results, r => Assert.Equal(MissionOutcome.Completed, r.Outcome));
        // The whole reason this component exists. A serial implementation scores 1 here.
        Assert.True(probe.MaxConcurrentTotal > 1,
            $"disjoint work never overlapped (peak {probe.MaxConcurrentTotal}) — the scheduler is running serially");
    }

    [Fact]
    public async Task Work_on_the_same_key_is_serialized()
    {
        var probe = new ConcurrencyProbe();
        await using var scheduler = NewScheduler(capacity: 4);

        var submissions = Enumerable.Range(0, 4).Select(_ =>
            scheduler.SubmitAsync(Work(
                (_, _) => probe.RunAsync("shared", TimeSpan.FromMilliseconds(40)),
                MissionClaim.Exclusive("entity", "shared"))));

        await Task.WhenAll(submissions);

        Assert.Equal(1, probe.MaxConcurrentByKey["shared"]);
    }

    [Fact]
    public async Task Parallel_and_serialized_hold_in_the_SAME_run()
    {
        // The mixed workload is the real proof: capacity alone could produce either result above.
        var probe = new ConcurrencyProbe();
        await using var scheduler = NewScheduler(capacity: 4);

        var tasks = new List<Task<MissionResult>>();
        for (var i = 0; i < 3; i++)
            tasks.Add(scheduler.SubmitAsync(Work(
                (_, _) => probe.RunAsync("contended", TimeSpan.FromMilliseconds(60)),
                MissionClaim.Exclusive("entity", "contended"))));
        for (var i = 0; i < 3; i++)
        {
            var id = i;
            tasks.Add(scheduler.SubmitAsync(Work(
                (_, _) => probe.RunAsync($"free{id}", TimeSpan.FromMilliseconds(60)),
                MissionClaim.Exclusive("entity", $"free{id}"))));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(1, probe.MaxConcurrentByKey["contended"]);
        Assert.True(probe.MaxConcurrentTotal > 1, "disjoint work should still overlap alongside a contended key");
    }

    [Fact]
    public async Task An_ancestor_conflicts_with_its_descendant()
    {
        // The hierarchical rule: deleting a directory must not run while a file inside it is written.
        var probe = new ConcurrencyProbe();
        await using var scheduler = NewScheduler(capacity: 4);

        var parent = scheduler.SubmitAsync(Work(
            (_, _) => probe.RunAsync("nested", TimeSpan.FromMilliseconds(60)),
            MissionClaim.Exclusive("tree", "root/a")));
        var child = scheduler.SubmitAsync(Work(
            (_, _) => probe.RunAsync("nested", TimeSpan.FromMilliseconds(60)),
            MissionClaim.Exclusive("tree", "root/a/b/c")));

        await Task.WhenAll(parent, child);

        Assert.Equal(1, probe.MaxConcurrentByKey["nested"]);
    }

    [Fact]
    public async Task A_sibling_key_sharing_a_prefix_does_NOT_conflict()
    {
        // The boundary bug: a naive StartsWith makes "root/ab" a child of "root/a", and two unrelated
        // resources serialize forever while looking merely slow.
        var probe = new ConcurrencyProbe();
        await using var scheduler = NewScheduler(capacity: 4);

        var a = scheduler.SubmitAsync(Work(
            (_, _) => probe.RunAsync("x", TimeSpan.FromMilliseconds(120)),
            MissionClaim.Exclusive("tree", "root/a")));
        var ab = scheduler.SubmitAsync(Work(
            (_, _) => probe.RunAsync("y", TimeSpan.FromMilliseconds(120)),
            MissionClaim.Exclusive("tree", "root/ab")));

        await Task.WhenAll(a, ab);

        Assert.True(probe.MaxConcurrentTotal > 1, "'root/a' and 'root/ab' are unrelated and must not serialize");
    }

    [Fact]
    public async Task Shared_claims_run_together_but_exclude_a_writer()
    {
        var probe = new ConcurrencyProbe();
        await using var scheduler = NewScheduler(capacity: 4);

        var readers = Enumerable.Range(0, 3).Select(_ =>
            scheduler.SubmitAsync(Work(
                (_, _) => probe.RunAsync("reads", TimeSpan.FromMilliseconds(80)),
                MissionClaim.Shared("entity", "doc")))).ToList();
        await Task.WhenAll(readers);

        Assert.True(probe.MaxConcurrentByKey["reads"] > 1, "shared claims must not exclude each other");

        var writerProbe = new ConcurrencyProbe();
        var mixed = new List<Task<MissionResult>>
        {
            scheduler.SubmitAsync(Work(
                (_, _) => writerProbe.RunAsync("mixed", TimeSpan.FromMilliseconds(60)),
                MissionClaim.Shared("entity", "doc"))),
            scheduler.SubmitAsync(Work(
                (_, _) => writerProbe.RunAsync("mixed", TimeSpan.FromMilliseconds(60)),
                MissionClaim.Exclusive("entity", "doc"))),
        };
        await Task.WhenAll(mixed);

        Assert.Equal(1, writerProbe.MaxConcurrentByKey["mixed"]);
    }

    [Fact]
    public async Task The_default_lane_bounds_total_concurrency()
    {
        var probe = new ConcurrencyProbe();
        await using var scheduler = NewScheduler(capacity: 2);

        var tasks = Enumerable.Range(0, 8).Select(i =>
            scheduler.SubmitAsync(Work(
                (_, _) => probe.RunAsync($"k{i}", TimeSpan.FromMilliseconds(40)),
                MissionClaim.Exclusive("entity", $"k{i}")))).ToList();

        await Task.WhenAll(tasks);

        Assert.True(probe.MaxConcurrentTotal <= 2, $"capacity 2 exceeded (peak {probe.MaxConcurrentTotal})");
        Assert.True(probe.MaxConcurrentTotal > 1, "capacity 2 should still overlap two items");
    }

    [Fact]
    public async Task A_named_lane_with_capacity_one_serializes_across_unrelated_keys()
    {
        // The GPU-gate shape: unrelated work that shares one scarce device.
        var probe = new ConcurrencyProbe();
        await using var scheduler = NewScheduler(capacity: 4);
        scheduler.Lane("gpu").Capacity = 1;

        var tasks = Enumerable.Range(0, 4).Select(i =>
            scheduler.SubmitAsync(new MissionDefinition
            {
                Run = (_, _) => probe.RunAsync("gpu", TimeSpan.FromMilliseconds(40)),
                Claims = [MissionClaim.Exclusive("entity", $"k{i}")],
                Lanes = [new MissionLane("gpu")],
            })).ToList();

        await Task.WhenAll(tasks);

        Assert.Equal(1, probe.MaxConcurrentByKey["gpu"]);
    }

    [Fact]
    public async Task Weighted_permits_bound_a_budget_lane()
    {
        // A lane as a BUDGET: capacity 4, items costing 2, so at most two run at once.
        var probe = new ConcurrencyProbe();
        await using var scheduler = NewScheduler(capacity: 8);
        scheduler.Lane("memory").Capacity = 4;

        var tasks = Enumerable.Range(0, 6).Select(i =>
            scheduler.SubmitAsync(new MissionDefinition
            {
                Run = (_, _) => probe.RunAsync("mem", TimeSpan.FromMilliseconds(50)),
                Claims = [MissionClaim.Exclusive("entity", $"k{i}")],
                Lanes = [new MissionLane("memory", Permits: 2)],
            })).ToList();

        await Task.WhenAll(tasks);

        Assert.True(probe.MaxConcurrentByKey["mem"] <= 2,
            $"cost-2 items in a capacity-4 lane must cap at 2 (saw {probe.MaxConcurrentByKey["mem"]})");
    }

    [Fact]
    public async Task Holding_a_lane_defers_new_work_without_cancelling_what_runs()
    {
        await using var scheduler = NewScheduler(capacity: 4);
        var lane = scheduler.Lane("held");
        lane.Hold();

        var started = false;
        var submitted = scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => { started = true; return Task.CompletedTask; },
            Lanes = [new MissionLane("held")],
        });

        await Task.Delay(80);
        Assert.False(started, "a held lane must not admit work");
        Assert.Equal(1, scheduler.PendingCount);

        lane.Release();
        var result = await submitted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MissionOutcome.Completed, result.Outcome);
    }
}
