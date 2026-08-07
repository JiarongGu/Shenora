using Shenora;
using Shenora.Engine.Missions;

namespace Shenora.Tests.Missions;

/// <summary>
/// Non-concurrency behaviour of <see cref="MissionScheduler"/>: retry, the two-phase rule,
/// deduplication, cancellation, the policy seam, observers and durable recovery.
/// </summary>
public class MissionSchedulerBehaviourTests
{
    private static MissionScheduler NewScheduler(MissionSchedulerOptions? options = null) =>
        new(options ?? new MissionSchedulerOptions { GlobalLaneCapacity = 4 });

    [Fact]
    public async Task A_failing_body_is_reported_not_thrown()
    {
        await using var scheduler = NewScheduler();

        var result = await scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => throw new InvalidOperationException("boom"),
        });

        // A queue must not tear down a batch loop because one item failed.
        Assert.Equal(MissionOutcome.Failed, result.Outcome);
        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Throws<InvalidOperationException>(result.ThrowIfFailed);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_and_a_permanent_one_is_not()
    {
        await using var scheduler = NewScheduler();

        var transientAttempts = 0;
        var transient = await scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => { transientAttempts++; return transientAttempts < 3 ? throw new IOException("locked") : Task.CompletedTask; },
            Retry = new RetryPolicy { Attempts = 3, Delay = TimeSpan.FromMilliseconds(5) },
        });

        Assert.Equal(MissionOutcome.Completed, transient.Outcome);
        Assert.Equal(3, transient.Attempts);

        var permanentAttempts = 0;
        var permanent = await scheduler.SubmitAsync(new MissionDefinition
        {
            // Not an IOException, so the default IsTransient says don't bother.
            Run = (_, _) => { permanentAttempts++; throw new InvalidOperationException("bug"); },
            Retry = new RetryPolicy { Attempts = 3, Delay = TimeSpan.FromMilliseconds(5) },
        });

        Assert.Equal(MissionOutcome.Failed, permanent.Outcome);
        Assert.Equal(1, permanentAttempts);
    }

    [Fact]
    public async Task The_expensive_phase_runs_ONCE_while_only_the_commit_is_retried()
    {
        // The measured lesson: a compress whose REPLACE hit a locked target must not recompress.
        await using var scheduler = NewScheduler();

        var prepared = 0;
        var committed = 0;
        var result = await scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => { prepared++; return Task.CompletedTask; },
            Commit = (_, _) => { committed++; return committed < 3 ? throw new IOException("target locked") : Task.CompletedTask; },
            Retry = new RetryPolicy { Attempts = 3, Delay = TimeSpan.FromMilliseconds(5) },
        });

        Assert.Equal(MissionOutcome.Completed, result.Outcome);
        Assert.Equal(1, prepared);   // the whole point
        Assert.Equal(3, committed);
    }

    [Fact]
    public async Task An_identical_key_is_deduplicated_and_the_body_runs_once()
    {
        await using var scheduler = NewScheduler(new MissionSchedulerOptions { GlobalLaneCapacity = 1 });

        var runs = 0;
        var gate = new TaskCompletionSource();
        var key = new MissionKey("import:42");

        var first = scheduler.SubmitAsync(new MissionDefinition
        {
            Run = async (_, _) => { Interlocked.Increment(ref runs); await gate.Task; },
            Key = key,
        });
        // Submitted while the first is still in flight.
        while (!scheduler.IsActive(key)) await Task.Delay(5);
        var second = scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => { Interlocked.Increment(ref runs); return Task.CompletedTask; },
            Key = key,
        });

        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, runs);
        Assert.Equal(MissionOutcome.Completed, results[0].Outcome);
        Assert.Equal(MissionOutcome.Deduplicated, results[1].Outcome);
    }

    [Fact]
    public async Task Cancelling_while_queued_never_runs_the_body()
    {
        await using var scheduler = NewScheduler(new MissionSchedulerOptions { GlobalLaneCapacity = 1 });

        var blocker = new TaskCompletionSource();
        var busy = scheduler.SubmitAsync(new MissionDefinition { Run = async (_, _) => await blocker.Task });

        using var cts = new CancellationTokenSource();
        var ran = false;
        var queued = scheduler.SubmitAsync(new MissionDefinition { Run = (_, _) => { ran = true; return Task.CompletedTask; } }, cts.Token);

        while (scheduler.PendingCount == 0) await Task.Delay(5);
        await cts.CancelAsync();
        blocker.SetResult();

        var result = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        await busy;

        Assert.Equal(MissionOutcome.Cancelled, result.Outcome);
        Assert.False(ran);
    }

    [Fact]
    public async Task Priority_orders_eligible_work_without_reordering_a_conflict()
    {
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 1,   // force a strict ordering so the sequence is observable
            Scopes = [new FlatClaimScope("entity")],
        });

        var order = new List<string>();
        var blocker = new TaskCompletionSource();
        var busy = scheduler.SubmitAsync(new MissionDefinition { Run = async (_, _) => await blocker.Task });

        // Queue low then high while the lane is occupied; high must start first.
        var low = scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => { lock (order) order.Add("low"); return Task.CompletedTask; },
            Priority = 0,
        });
        var high = scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => { lock (order) order.Add("high"); return Task.CompletedTask; },
            Priority = 10,
        });

        while (scheduler.PendingCount < 2) await Task.Delay(5);
        blocker.SetResult();
        await Task.WhenAll(busy, low, high);

        Assert.Equal(["high", "low"], order);
    }

    [Fact]
    public async Task A_policy_can_defer_work_and_Reevaluate_wakes_it()
    {
        var open = false;
        // ReSharper disable once AccessToModifiedClosure — deliberate: the gate flips mid-test.
        var policy = new DelegatePolicy((_, _) => open);
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 4,
            Policy = policy,
        });

        var started = false;
        var submitted = scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => { started = true; return Task.CompletedTask; },
        });

        await Task.Delay(60);
        Assert.False(started, "policy said not now, so nothing should have started");

        open = true;
        scheduler.Reevaluate();   // the app's job: nothing else knows the condition changed

        var result = await submitted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MissionOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task A_throwing_policy_defers_rather_than_wedging_the_scheduler()
    {
        var throwCount = 0;
        var policy = new DelegatePolicy((_, _) =>
        {
            // Throw the first time, behave afterwards: a broken policy must not be fatal.
            if (Interlocked.Increment(ref throwCount) == 1) throw new InvalidOperationException("policy bug");
            return true;
        });
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            Policy = policy,
        });

        var first = await scheduler
            .SubmitAsync(new MissionDefinition { Run = (_, _) => Task.CompletedTask })
            .WaitAsync(TimeSpan.FromSeconds(5))
            .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);

        // The first submit's dispatch threw; a later submit re-dispatches and both drain.
        if (first is null)
        {
            var recovered = await scheduler.SubmitAsync(new MissionDefinition { Run = (_, _) => Task.CompletedTask })
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(MissionOutcome.Completed, recovered.Outcome);
        }
        else
        {
            Assert.Equal(MissionOutcome.Completed, first.Outcome);
        }
    }

    [Fact]
    public async Task Observers_see_the_lifecycle_and_a_throwing_one_cannot_fail_the_work()
    {
        var good = new RecordingObserver();
        var options = new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            Observers = [new ThrowingObserver(), good],
        };
        await using var scheduler = new MissionScheduler(options);

        var result = await scheduler.SubmitAsync(new MissionDefinition { Run = (_, _) => Task.CompletedTask, Kind = "scan" });

        Assert.Equal(MissionOutcome.Completed, result.Outcome);
        Assert.Equal(1, good.Queued);
        Assert.Equal(1, good.Started);
        Assert.Equal(1, good.Finished);
    }

    [Fact]
    public async Task Durable_work_is_persisted_then_forgotten_when_it_finishes()
    {
        var store = new RecordingStore();
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            QueueStore = store,
        });

        var result = await scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => Task.CompletedTask,
            Durable = true,
            Kind = "export",
            Payload = "{\"id\":7}",
        });

        Assert.Equal(MissionOutcome.Completed, result.Outcome);
        Assert.Contains(store.Saved, r => r.Kind == "export" && r.Payload == "{\"id\":7}");
        Assert.Contains(result.MissionId, store.Removed);
    }

    [Fact]
    public async Task Recovery_requeues_Queued_records_and_FAILS_Running_ones()
    {
        // The boot-loop lesson: work that was RUNNING when the process died may be what killed it.
        var store = new RecordingStore();
        store.Pending.Add(new MissionRecord("w-queued", "scan", null, MissionState.Queued, DateTimeOffset.UtcNow));
        store.Pending.Add(new MissionRecord("w-running", "render", null, MissionState.Running, DateTimeOffset.UtcNow));

        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            QueueStore = store,
        });

        var rehydrated = new List<string>();
        var requeued = await scheduler.RecoverAsync(record =>
        {
            rehydrated.Add(record.MissionId);
            return new MissionDefinition { Run = (_, _) => Task.CompletedTask };
        });

        Assert.Equal(1, requeued);
        Assert.Equal(["w-queued"], rehydrated);          // the Running one was never rebuilt
        Assert.Contains("w-running", store.Removed);
    }

    [Fact]
    public async Task Recovery_honours_an_app_supplied_policy()
    {
        var store = new RecordingStore();
        store.Pending.Add(new MissionRecord("w1", "resumable", null, MissionState.Running, DateTimeOffset.UtcNow));

        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 2,
            QueueStore = store,
            // This app knows "resumable" work checkpoints safely, so overrides the cautious default.
            RecoveryPolicyFor = _ => RecoveryPolicy.Requeue,
        });

        var requeued = await scheduler.RecoverAsync(_ => new MissionDefinition { Run = (_, _) => Task.CompletedTask });

        Assert.Equal(1, requeued);
    }

    [Fact]
    public async Task An_unregistered_claim_scope_throws_instead_of_silently_dropping_the_exclusion()
    {
        await using var scheduler = NewScheduler();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => Task.CompletedTask,
            Claims = [MissionClaim.Exclusive("nope", "k")],
        }));

        Assert.Contains("not registered", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unseen_LANE_name_is_created_at_the_default_capacity_rather_than_throwing()
    {
        // The asymmetry with an unregistered SCOPE (above) is deliberate but easy to "fix" by
        // mistake, and the XML on SubmitAsync/MissionResult claimed both threw until 2026-08-02. It is
        // pinned here because the consequence is silent: a misspelled lane draws on a NEW lane at the
        // default capacity, so the exclusivity the app configured on the real lane is simply gone.
        await using var scheduler = NewScheduler(new MissionSchedulerOptions { GlobalLaneCapacity = 3 });
        scheduler.Lane("gpu").Capacity = 1;

        var result = await scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => Task.CompletedTask,
            Lanes = [new MissionLane("gpu-typo")],
        });

        Assert.Equal(MissionOutcome.Completed, result.Outcome);
        Assert.Equal(3, scheduler.Lane("gpu-typo").Capacity);   // the default, NOT the gate's 1
        Assert.Equal(1, scheduler.Lane("gpu").Capacity);
    }

    [Fact]
    public async Task Dispose_cancels_queued_work_and_awaits_what_is_running()
    {
        var scheduler = NewScheduler(new MissionSchedulerOptions { GlobalLaneCapacity = 1 });

        var entered = new TaskCompletionSource();
        var finished = false;
        var running = scheduler.SubmitAsync(new MissionDefinition
        {
            Run = async (_, ct) =>
            {
                entered.SetResult();
                // Ignores the token deliberately: dispose must WAIT, not tear a body mid-write.
                await Task.Delay(120, CancellationToken.None);
                finished = true;
            },
        });
        await entered.Task;
        var queued = scheduler.SubmitAsync(new MissionDefinition { Run = (_, _) => Task.CompletedTask });

        await scheduler.DisposeAsync();

        Assert.True(finished, "dispose must await in-flight work rather than abandon it");
        Assert.Equal(MissionOutcome.Cancelled, (await queued).Outcome);
        await running;
    }

    // ── doubles ───────────────────────────────────────────────────────────────────────────────────

    private sealed class DelegatePolicy(Func<MissionExecution, MissionSchedulerState, bool> shouldStart) : IMissionPolicy
    {
        public bool ShouldStart(in MissionExecution mission, in MissionSchedulerState state) => shouldStart(mission, state);
        public int Compare(in MissionExecution a, in MissionExecution b) => a.Sequence.CompareTo(b.Sequence);
    }

    private sealed class RecordingObserver : IMissionObserver
    {
        public int Queued;
        public int Started;
        public int Finished;
        public void OnQueued(in MissionExecution mission) => Queued++;
        public void OnStarted(in MissionExecution mission) => Started++;
        public void OnFinished(in MissionExecution mission, MissionResult result) => Finished++;
    }

    private sealed class ThrowingObserver : IMissionObserver
    {
        public void OnQueued(in MissionExecution mission) => throw new InvalidOperationException("observer bug");
        public void OnStarted(in MissionExecution mission) => throw new InvalidOperationException("observer bug");
        public void OnFinished(in MissionExecution mission, MissionResult result) => throw new InvalidOperationException("observer bug");
    }

    private sealed class RecordingStore : IMissionQueueStore
    {
        public List<MissionRecord> Saved { get; } = [];
        public List<string> Removed { get; } = [];
        public List<MissionRecord> Pending { get; } = [];

        public Task AppendAsync(MissionRecord record, CancellationToken cancellationToken)
        {
            lock (Saved) Saved.Add(record);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string missionId, CancellationToken cancellationToken)
        {
            lock (Removed) Removed.Add(missionId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MissionRecord>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MissionRecord>>(Pending);
    }
}
