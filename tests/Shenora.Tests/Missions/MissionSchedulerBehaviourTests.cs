using Shenora;
using Shenora.Engine.Missions;
using Shenora.Engine;

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
    public async Task A_recovered_record_is_REMOVED_so_it_cannot_be_recovered_again_next_boot()
    {
        // The resubmit mints a NEW mission id, so the recovered record's own id is orphaned: the new
        // mission's ForgetAsync removes the NEW id and never this one. Left in place, LoadAsync returns
        // it again on every subsequent boot and the work re-runs forever.
        // This asserts the QUEUED id specifically — the neighbouring test only ever checked the RUNNING
        // one, which is removed on a different path, and that is why the loop went unnoticed.
        var store = new RecordingStore();
        store.Pending.Add(new MissionRecord("w-queued", "scan", null, MissionState.Queued, DateTimeOffset.UtcNow));

        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions { QueueStore = store });

        var requeued = await scheduler.RecoverAsync(_ => new MissionDefinition { Run = (_, _) => Task.CompletedTask });

        Assert.Equal(1, requeued);
        Assert.Contains("w-queued", store.Removed);
    }

    [Fact]
    public async Task One_unrecoverable_record_does_not_abandon_the_rest_of_the_pass()
    {
        // SubmitAsync is not async, so an unusable definition throws SYNCHRONOUSLY at the call site.
        // Unguarded, one bad row aborts recovery for every later record AND leaves the ones already
        // resubmitted still in the store — so the next boot repeats the whole thing.
        var store = new RecordingStore();
        store.Pending.Add(new MissionRecord("w-bad", "broken", null, MissionState.Queued, DateTimeOffset.UtcNow));
        store.Pending.Add(new MissionRecord("w-good", "scan", null, MissionState.Queued, DateTimeOffset.UtcNow));

        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions { QueueStore = store });

        // An UNREGISTERED CLAIM SCOPE is the synchronous throw — an unknown LANE is not, because lanes
        // are created on first use by design.
        var requeued = await scheduler.RecoverAsync(record => record.MissionId == "w-bad"
            ? new MissionDefinition
            {
                Run = (_, _) => Task.CompletedTask,
                Claims = [new MissionClaim("no-such-scope", "k", ClaimMode.Exclusive)],
            }
            : new MissionDefinition { Run = (_, _) => Task.CompletedTask });

        Assert.Equal(1, requeued);                       // the good one still ran
        Assert.Contains("w-good", store.Removed);
        Assert.Contains("w-bad", store.Removed);         // and the poison row is dropped, not retried forever
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

    [Fact]
    public async Task A_FAILED_mission_reports_the_attempts_actually_made()
    {
        // RunWithRetryAsync returns a count only on SUCCESS; the throw path used to leave the
        // pre-call value in the result — 0, claiming a body that ran (and retried) never ran at all.
        await using var scheduler = NewScheduler();

        var permanent = await scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => throw new InvalidOperationException("bug"),
            Retry = new RetryPolicy { Attempts = 3, Delay = TimeSpan.FromMilliseconds(5) },
        });
        Assert.Equal(MissionOutcome.Failed, permanent.Outcome);
        Assert.Equal(1, permanent.Attempts);

        var exhausted = await scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => throw new IOException("still locked"),
            Retry = new RetryPolicy { Attempts = 3, Delay = TimeSpan.FromMilliseconds(5) },
        });
        Assert.Equal(MissionOutcome.Failed, exhausted.Outcome);
        Assert.Equal(3, exhausted.Attempts);

        var failedCommit = await scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => Task.CompletedTask,
            Commit = (_, _) => throw new IOException("target locked"),
            Retry = new RetryPolicy { Attempts = 2, Delay = TimeSpan.FromMilliseconds(5) },
        });
        Assert.Equal(MissionOutcome.Failed, failedCommit.Outcome);
        Assert.Equal(2, failedCommit.Attempts);   // commit attempts — the same meaning the success path reports
    }

    [Fact]
    public async Task Cancelling_while_queued_completes_WITHOUT_any_other_dispatch_event()
    {
        // The check in DispatchLocked runs on submit, completion and lane change. If none of those
        // ever comes, only the token's own wake answers the caller — pinned by never letting the
        // running mission finish until the cancelled one has already been answered. (The neighbouring
        // cancel test releases the runner immediately after cancelling, which masks exactly this.)
        await using var scheduler = NewScheduler(new MissionSchedulerOptions { GlobalLaneCapacity = 1 });

        var blocker = new TaskCompletionSource();
        var busy = scheduler.SubmitAsync(new MissionDefinition { Run = async (_, _) => await blocker.Task });

        using var cts = new CancellationTokenSource();
        var queued = scheduler.SubmitAsync(new MissionDefinition { Run = (_, _) => Task.CompletedTask }, cts.Token);

        while (scheduler.PendingCount == 0) await Task.Delay(5);
        await cts.CancelAsync();

        var result = await queued.WaitAsync(TimeSpan.FromSeconds(5));   // hangs forever without the wake
        Assert.Equal(MissionOutcome.Cancelled, result.Outcome);

        blocker.SetResult();
        await busy;
    }

    [Fact]
    public async Task A_durable_missions_store_writes_arrive_in_order_even_when_the_first_is_slow()
    {
        // The Queued append used to be fire-and-forget while Running/Remove were awaited: a store
        // slower than the mission body could receive Queued LAST, leaving a phantom record that the
        // next boot's recovery re-executes.
        var store = new OrderRecordingStore();
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions { QueueStore = store });

        var result = await scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => Task.CompletedTask,
            Durable = true,
        });

        Assert.Equal(MissionOutcome.Completed, result.Outcome);
        Assert.Equal(["append:Queued", "append:Running", "remove"], store.Events);
    }

    [Fact]
    public async Task A_cancelled_pending_durable_mission_leaves_no_record_to_recover()
    {
        // The caller cancelled it; resurrecting it at the next boot would run work the user said no to.
        var store = new RecordingStore();
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            GlobalLaneCapacity = 1,
            QueueStore = store,
        });

        var blocker = new TaskCompletionSource();
        var busy = scheduler.SubmitAsync(new MissionDefinition { Run = async (_, _) => await blocker.Task });

        using var cts = new CancellationTokenSource();
        var queued = scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => Task.CompletedTask,
            Durable = true,
        }, cts.Token);

        while (scheduler.PendingCount == 0) await Task.Delay(5);
        await cts.CancelAsync();
        var result = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MissionOutcome.Cancelled, result.Outcome);

        // The removal chains behind the Queued append off the completion path; poll briefly.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (store.Removed) { if (store.Removed.Contains(result.MissionId)) break; }
            await Task.Delay(10);
        }
        lock (store.Removed) Assert.Contains(result.MissionId, store.Removed);

        blocker.SetResult();
        await busy;
    }

    [Fact]
    public async Task A_durable_mission_submitted_with_an_ALREADY_cancelled_token_leaves_no_record()
    {
        // The phase review's repro for the publication race: dispatch runs INSIDE SubmitAsync's lock,
        // so the cancel branch's forget could run before the Queued append had even been assigned —
        // its await resolved against a placeholder default, the remove was a no-op, and the append
        // landed afterwards with nothing left to remove it: a phantom record recovery re-executes.
        // The forget waits on the entry's own GATE now, so the store always sees append → remove.
        var store = new RecordingStore();
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions { QueueStore = store });

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (_, _) => Task.CompletedTask,
            Durable = true,
        }, cts.Token);

        Assert.Equal(MissionOutcome.Cancelled, result.Outcome);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (store.Removed) { if (store.Removed.Contains(result.MissionId)) break; }
            await Task.Delay(10);
        }
        lock (store.Saved) Assert.Contains(store.Saved, r => r.MissionId == result.MissionId);
        lock (store.Removed) Assert.Contains(result.MissionId, store.Removed);
    }

    [Fact]
    public async Task Dispose_releases_the_keys_of_missions_still_pending()
    {
        // The dispose paths complete pending entries directly, bypassing DispatchLocked's _byKey
        // cleanup — IsActive(key) answered true forever for work that no longer existed.
        var scheduler = NewScheduler(new MissionSchedulerOptions { GlobalLaneCapacity = 1 });

        var blocker = new TaskCompletionSource();
        var busy = scheduler.SubmitAsync(new MissionDefinition { Run = async (_, _) => await blocker.Task });
        var key = new MissionKey("import:9");
        var queued = scheduler.SubmitAsync(new MissionDefinition { Run = (_, _) => Task.CompletedTask, Key = key });
        while (scheduler.PendingCount == 0) await Task.Delay(5);
        Assert.True(scheduler.IsActive(key));

        scheduler.Dispose();   // the synchronous path: cancels pending, does not await running

        Assert.False(scheduler.IsActive(key));
        Assert.Equal(MissionOutcome.Cancelled, (await queued).Outcome);
        blocker.SetResult();
        await busy;
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

    /// <summary>A store whose QUEUED append is slow — the shape that exposed out-of-order writes.</summary>
    private sealed class OrderRecordingStore : IMissionQueueStore
    {
        public List<string> Events { get; } = [];

        public async Task AppendAsync(MissionRecord record, CancellationToken cancellationToken)
        {
            if (record.State == MissionState.Queued) await Task.Delay(50, cancellationToken);
            lock (Events) Events.Add($"append:{record.State}");
        }

        public Task RemoveAsync(string missionId, CancellationToken cancellationToken)
        {
            lock (Events) Events.Add("remove");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MissionRecord>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MissionRecord>>([]);
    }

    /// <summary>
    /// 🔴 The identity problem: MissionId is the SCHEDULER's and is per-process, so without the caller's
    /// own key nothing an app receives — an observer callback, a Snapshot row, a durable record — could be
    /// mapped back to the item it submitted. A queue UI could list work and not say what any of it was.
    /// </summary>
    [Fact]
    public async Task The_callers_own_KEY_survives_into_the_execution_and_the_durable_record()
    {
        var store = new RecordingStore();
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions { QueueStore = store });
        var key = new MissionKey("import:42");

        MissionExecution seen = default;
        await scheduler.SubmitAsync(new MissionDefinition
        {
            Run = (execution, _) => { seen = execution; return Task.CompletedTask; },
            Key = key,
            Kind = "import",
            Durable = true,
        });

        Assert.Equal(key, seen.Key);                                   // the body can recognise its own work
        Assert.Contains(store.Saved, r => r.Key == key);               // and so can a store, across a restart
    }
}
