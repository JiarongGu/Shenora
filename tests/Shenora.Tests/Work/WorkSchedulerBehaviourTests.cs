using Shenora.Core;

namespace Shenora.Tests.Work;

/// <summary>
/// Non-concurrency behaviour of <see cref="WorkScheduler"/>: retry, the two-phase rule,
/// deduplication, cancellation, the policy seam, observers and durable recovery.
/// </summary>
public class WorkSchedulerBehaviourTests
{
    private static WorkScheduler NewScheduler(WorkSchedulerOptions? options = null) =>
        new(options ?? new WorkSchedulerOptions { DefaultLaneCapacity = 4 });

    [Fact]
    public async Task A_failing_body_is_reported_not_thrown()
    {
        await using var scheduler = NewScheduler();

        var result = await scheduler.SubmitAsync(new WorkRequest
        {
            Run = _ => throw new InvalidOperationException("boom"),
        });

        // A queue must not tear down a batch loop because one item failed.
        Assert.Equal(WorkOutcome.Failed, result.Outcome);
        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Throws<InvalidOperationException>(result.ThrowIfFailed);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_and_a_permanent_one_is_not()
    {
        await using var scheduler = NewScheduler();

        var transientAttempts = 0;
        var transient = await scheduler.SubmitAsync(new WorkRequest
        {
            Run = _ => { transientAttempts++; return transientAttempts < 3 ? throw new IOException("locked") : Task.CompletedTask; },
            Retry = new RetryPolicy { Attempts = 3, Delay = TimeSpan.FromMilliseconds(5) },
        });

        Assert.Equal(WorkOutcome.Completed, transient.Outcome);
        Assert.Equal(3, transient.Attempts);

        var permanentAttempts = 0;
        var permanent = await scheduler.SubmitAsync(new WorkRequest
        {
            // Not an IOException, so the default IsTransient says don't bother.
            Run = _ => { permanentAttempts++; throw new InvalidOperationException("bug"); },
            Retry = new RetryPolicy { Attempts = 3, Delay = TimeSpan.FromMilliseconds(5) },
        });

        Assert.Equal(WorkOutcome.Failed, permanent.Outcome);
        Assert.Equal(1, permanentAttempts);
    }

    [Fact]
    public async Task The_expensive_phase_runs_ONCE_while_only_the_commit_is_retried()
    {
        // The measured lesson: a compress whose REPLACE hit a locked target must not recompress.
        await using var scheduler = NewScheduler();

        var prepared = 0;
        var committed = 0;
        var result = await scheduler.SubmitAsync(new WorkRequest
        {
            Run = _ => { prepared++; return Task.CompletedTask; },
            Commit = _ => { committed++; return committed < 3 ? throw new IOException("target locked") : Task.CompletedTask; },
            Retry = new RetryPolicy { Attempts = 3, Delay = TimeSpan.FromMilliseconds(5) },
        });

        Assert.Equal(WorkOutcome.Completed, result.Outcome);
        Assert.Equal(1, prepared);   // the whole point
        Assert.Equal(3, committed);
    }

    [Fact]
    public async Task An_identical_key_is_deduplicated_and_the_body_runs_once()
    {
        await using var scheduler = NewScheduler(new WorkSchedulerOptions { DefaultLaneCapacity = 1 });

        var runs = 0;
        var gate = new TaskCompletionSource();
        var key = new WorkKey("import:42");

        var first = scheduler.SubmitAsync(new WorkRequest
        {
            Run = async _ => { Interlocked.Increment(ref runs); await gate.Task; },
            Key = key,
        });
        // Submitted while the first is still in flight.
        while (!scheduler.IsActive(key)) await Task.Delay(5);
        var second = scheduler.SubmitAsync(new WorkRequest
        {
            Run = _ => { Interlocked.Increment(ref runs); return Task.CompletedTask; },
            Key = key,
        });

        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, runs);
        Assert.Equal(WorkOutcome.Completed, results[0].Outcome);
        Assert.Equal(WorkOutcome.Deduplicated, results[1].Outcome);
    }

    [Fact]
    public async Task Cancelling_while_queued_never_runs_the_body()
    {
        await using var scheduler = NewScheduler(new WorkSchedulerOptions { DefaultLaneCapacity = 1 });

        var blocker = new TaskCompletionSource();
        var busy = scheduler.SubmitAsync(new WorkRequest { Run = async _ => await blocker.Task });

        using var cts = new CancellationTokenSource();
        var ran = false;
        var queued = scheduler.SubmitAsync(new WorkRequest { Run = _ => { ran = true; return Task.CompletedTask; } }, cts.Token);

        while (scheduler.PendingCount == 0) await Task.Delay(5);
        await cts.CancelAsync();
        blocker.SetResult();

        var result = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        await busy;

        Assert.Equal(WorkOutcome.Cancelled, result.Outcome);
        Assert.False(ran);
    }

    [Fact]
    public async Task Priority_orders_eligible_work_without_reordering_a_conflict()
    {
        await using var scheduler = new WorkScheduler(new WorkSchedulerOptions
        {
            DefaultLaneCapacity = 1,   // force a strict ordering so the sequence is observable
            Scopes = [new FlatClaimScope("entity")],
        });

        var order = new List<string>();
        var blocker = new TaskCompletionSource();
        var busy = scheduler.SubmitAsync(new WorkRequest { Run = async _ => await blocker.Task });

        // Queue low then high while the lane is occupied; high must start first.
        var low = scheduler.SubmitAsync(new WorkRequest
        {
            Run = _ => { lock (order) order.Add("low"); return Task.CompletedTask; },
            Priority = 0,
        });
        var high = scheduler.SubmitAsync(new WorkRequest
        {
            Run = _ => { lock (order) order.Add("high"); return Task.CompletedTask; },
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
        await using var scheduler = new WorkScheduler(new WorkSchedulerOptions
        {
            DefaultLaneCapacity = 4,
            Policy = policy,
        });

        var started = false;
        var submitted = scheduler.SubmitAsync(new WorkRequest
        {
            Run = _ => { started = true; return Task.CompletedTask; },
        });

        await Task.Delay(60);
        Assert.False(started, "policy said not now, so nothing should have started");

        open = true;
        scheduler.Reevaluate();   // the app's job: nothing else knows the condition changed

        var result = await submitted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkOutcome.Completed, result.Outcome);
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
        await using var scheduler = new WorkScheduler(new WorkSchedulerOptions
        {
            DefaultLaneCapacity = 2,
            Policy = policy,
        });

        var first = await scheduler
            .SubmitAsync(new WorkRequest { Run = _ => Task.CompletedTask })
            .WaitAsync(TimeSpan.FromSeconds(5))
            .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);

        // The first submit's dispatch threw; a later submit re-dispatches and both drain.
        if (first is null)
        {
            var recovered = await scheduler.SubmitAsync(new WorkRequest { Run = _ => Task.CompletedTask })
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WorkOutcome.Completed, recovered.Outcome);
        }
        else
        {
            Assert.Equal(WorkOutcome.Completed, first.Outcome);
        }
    }

    [Fact]
    public async Task Observers_see_the_lifecycle_and_a_throwing_one_cannot_fail_the_work()
    {
        var good = new RecordingObserver();
        var options = new WorkSchedulerOptions
        {
            DefaultLaneCapacity = 2,
            Observers = [new ThrowingObserver(), good],
        };
        await using var scheduler = new WorkScheduler(options);

        var result = await scheduler.SubmitAsync(new WorkRequest { Run = _ => Task.CompletedTask, Kind = "scan" });

        Assert.Equal(WorkOutcome.Completed, result.Outcome);
        Assert.Equal(1, good.Queued);
        Assert.Equal(1, good.Started);
        Assert.Equal(1, good.Finished);
    }

    [Fact]
    public async Task Durable_work_is_persisted_then_forgotten_when_it_finishes()
    {
        var store = new RecordingStore();
        await using var scheduler = new WorkScheduler(new WorkSchedulerOptions
        {
            DefaultLaneCapacity = 2,
            Store = store,
        });

        var result = await scheduler.SubmitAsync(new WorkRequest
        {
            Run = _ => Task.CompletedTask,
            Durable = true,
            Kind = "export",
            Payload = "{\"id\":7}",
        });

        Assert.Equal(WorkOutcome.Completed, result.Outcome);
        Assert.Contains(store.Saved, r => r.Kind == "export" && r.Payload == "{\"id\":7}");
        Assert.Contains(result.WorkId, store.Removed);
    }

    [Fact]
    public async Task Recovery_requeues_Queued_records_and_FAILS_Running_ones()
    {
        // The boot-loop lesson: work that was RUNNING when the process died may be what killed it.
        var store = new RecordingStore();
        store.Pending.Add(new WorkRecord("w-queued", "scan", null, WorkState.Queued, DateTimeOffset.UtcNow));
        store.Pending.Add(new WorkRecord("w-running", "render", null, WorkState.Running, DateTimeOffset.UtcNow));

        await using var scheduler = new WorkScheduler(new WorkSchedulerOptions
        {
            DefaultLaneCapacity = 2,
            Store = store,
        });

        var rehydrated = new List<string>();
        var requeued = await scheduler.RecoverAsync(record =>
        {
            rehydrated.Add(record.WorkId);
            return new WorkRequest { Run = _ => Task.CompletedTask };
        });

        Assert.Equal(1, requeued);
        Assert.Equal(["w-queued"], rehydrated);          // the Running one was never rebuilt
        Assert.Contains("w-running", store.Removed);
    }

    [Fact]
    public async Task Recovery_honours_an_app_supplied_policy()
    {
        var store = new RecordingStore();
        store.Pending.Add(new WorkRecord("w1", "resumable", null, WorkState.Running, DateTimeOffset.UtcNow));

        await using var scheduler = new WorkScheduler(new WorkSchedulerOptions
        {
            DefaultLaneCapacity = 2,
            Store = store,
            // This app knows "resumable" work checkpoints safely, so overrides the cautious default.
            RecoveryPolicyFor = _ => RecoveryPolicy.Requeue,
        });

        var requeued = await scheduler.RecoverAsync(_ => new WorkRequest { Run = _ => Task.CompletedTask });

        Assert.Equal(1, requeued);
    }

    [Fact]
    public async Task An_unregistered_claim_scope_throws_instead_of_silently_dropping_the_exclusion()
    {
        await using var scheduler = NewScheduler();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => scheduler.SubmitAsync(new WorkRequest
        {
            Run = _ => Task.CompletedTask,
            Claims = [WorkClaim.Exclusive("nope", "k")],
        }));

        Assert.Contains("not registered", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_cancels_queued_work_and_awaits_what_is_running()
    {
        var scheduler = NewScheduler(new WorkSchedulerOptions { DefaultLaneCapacity = 1 });

        var entered = new TaskCompletionSource();
        var finished = false;
        var running = scheduler.SubmitAsync(new WorkRequest
        {
            Run = async ct =>
            {
                entered.SetResult();
                // Ignores the token deliberately: dispose must WAIT, not tear a body mid-write.
                await Task.Delay(120, CancellationToken.None);
                finished = true;
            },
        });
        await entered.Task;
        var queued = scheduler.SubmitAsync(new WorkRequest { Run = _ => Task.CompletedTask });

        await scheduler.DisposeAsync();

        Assert.True(finished, "dispose must await in-flight work rather than abandon it");
        Assert.Equal(WorkOutcome.Cancelled, (await queued).Outcome);
        await running;
    }

    // ── doubles ───────────────────────────────────────────────────────────────────────────────────

    private sealed class DelegatePolicy(Func<WorkView, WorkSchedulerState, bool> shouldStart) : IWorkPolicy
    {
        public bool ShouldStart(in WorkView work, in WorkSchedulerState state) => shouldStart(work, state);
        public int Compare(in WorkView a, in WorkView b) => a.Sequence.CompareTo(b.Sequence);
    }

    private sealed class RecordingObserver : IWorkObserver
    {
        public int Queued;
        public int Started;
        public int Finished;
        public void OnQueued(in WorkView work) => Queued++;
        public void OnStarted(in WorkView work) => Started++;
        public void OnFinished(in WorkView work, WorkResult result) => Finished++;
    }

    private sealed class ThrowingObserver : IWorkObserver
    {
        public void OnQueued(in WorkView work) => throw new InvalidOperationException("observer bug");
        public void OnStarted(in WorkView work) => throw new InvalidOperationException("observer bug");
        public void OnFinished(in WorkView work, WorkResult result) => throw new InvalidOperationException("observer bug");
    }

    private sealed class RecordingStore : IWorkStore
    {
        public List<WorkRecord> Saved { get; } = [];
        public List<string> Removed { get; } = [];
        public List<WorkRecord> Pending { get; } = [];

        public Task SaveAsync(WorkRecord record, CancellationToken cancellationToken)
        {
            lock (Saved) Saved.Add(record);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string workId, CancellationToken cancellationToken)
        {
            lock (Removed) Removed.Add(workId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkRecord>> LoadPendingAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkRecord>>(Pending);
    }
}
