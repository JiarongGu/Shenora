using Microsoft.Extensions.Time.Testing;
using Shenora;
using Shenora.Core.Events;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Ipc;

public class OperationRegistryTests
{
    private static (OperationRegistry Registry, List<EventMessage> Events) Build(
        OperationRegistryOptions? options = null)
    {
        var bus = new EventBus();
        var events = new List<EventMessage>();
        bus.SubscribeToAll(m => { lock (events) events.Add(m); return Task.CompletedTask; });
        // ProgressInterval = zero disables throttling; Task 3 covers the throttle itself.
        return (new OperationRegistry(bus, options ?? new OperationRegistryOptions
        {
            ProgressInterval = TimeSpan.Zero,
        }), events);
    }

    private static OperationInfo Payload(EventMessage message) => Assert.IsType<OperationInfo>(message.Payload);

    [Fact]
    public void Start_publishes_a_running_snapshot_under_the_operations_module()
    {
        var (registry, events) = Build();

        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "prod" });

        var message = Assert.Single(events);
        Assert.Equal("SHENORA.OPERATIONS", message.Module);
        Assert.Equal(OperationEvents.Updated, message.Type);
        Assert.Equal("prod", message.Scope);
        var info = Payload(message);
        Assert.Equal(operation.Id, info.Id);
        Assert.Equal("DEPLOY", info.Module);      // the OWNING module rides in the payload
        Assert.Equal("PUSH", info.Kind);
        Assert.Equal(OperationStatus.Running, info.Status);
        Assert.Null(info.Progress);               // null = indeterminate, not zero
    }

    [Fact]
    public void Report_updates_progress_and_detail()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        operation.Report(new OperationProgress(40, 100, "percent"), new OperationLabel(Text: "uploading", Key: "deploy.stage.upload"));

        var info = Payload(events[^1]);
        Assert.Equal(new OperationProgress(40, 100, "percent"), info.Progress);
        Assert.Equal("deploy.stage.upload", info.Detail!.Key);
        Assert.Equal("uploading", info.Detail.Text);
    }

    /// <summary>
    /// DELIBERATE REVERSAL (generic-library audit, before publish): this used to pin a silent 0–100
    /// CLAMP (<c>ClampProgress</c>) — a consumer reporting bytes or a raw item count against
    /// <c>OperationOptions.Progress</c>/<c>IOperation.Report</c> got permanently stamped 100% with no
    /// diagnostic. Percent is not the mechanism; it is one app's unit, carried in
    /// <see cref="OperationProgress"/> alongside an optional <c>Total</c> and an app-defined
    /// <c>Unit</c>. The kit now passes <see cref="OperationProgress"/> through UNCHANGED — a
    /// <c>Value</c> above its own <c>Total</c> (or any other "out of range" shape) is the app's own
    /// bug to see, not the kit's to silently hide.
    /// </summary>
    [Fact]
    public void Report_passes_progress_through_unchanged_even_when_Value_exceeds_Total()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        operation.Report(new OperationProgress(140, 100, "percent"));

        Assert.Equal(new OperationProgress(140, 100, "percent"), Payload(events[^1]).Progress);
    }

    [Fact]
    public void Complete_is_terminal_and_finishing_twice_is_a_no_op()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        operation.Complete();
        var afterComplete = events.Count;
        operation.Fail("TOO_LATE");                 // the "Complete at the end + Fail in the catch" pattern
        operation.Report(new OperationProgress(50));

        Assert.Equal(afterComplete, events.Count);  // nothing after the terminal transition
        var info = Payload(events[^1]);
        Assert.Equal(OperationStatus.Completed, info.Status);
        Assert.Null(info.Progress);                 // never reported — Complete must not invent a number
        Assert.NotNull(info.FinishedAt);
    }

    /// <summary>
    /// DELIBERATE REVERSAL (generic-library audit, before publish): <c>Complete</c> used to force
    /// <c>Progress = 100</c> unconditionally, which assumed every consumer measures in percent. When a
    /// known <c>Total</c> exists, completing means "all of it" — <c>Value</c> becomes <c>Total</c>,
    /// never a hardcoded 100.
    /// </summary>
    [Fact]
    public void Complete_sets_progress_value_to_total_when_a_total_is_known()
    {
        var (registry, _) = Build();
        var operation = registry.Start("SCAN", new OperationOptions { Kind = "FILES" });
        operation.Report(new OperationProgress(47, 1200, "files"));

        operation.Complete();

        Assert.Equal(new OperationProgress(1200, 1200, "files"), registry.GetAll().Single().Progress);
    }

    /// <summary>
    /// The other half of the same reversal: when the last report carried NO known total (an absolute
    /// count with nothing to divide by — bytes off a chunked stream, say), <c>Complete</c> must not
    /// invent one. Leaving the value untouched is the honest answer; fabricating a <c>Total</c> the
    /// app never gave the kit would be exactly the "hide the app's own data" failure the clamp used to
    /// commit.
    /// </summary>
    [Fact]
    public void Complete_leaves_progress_untouched_when_no_total_is_known()
    {
        var (registry, _) = Build();
        var operation = registry.Start("SCAN", new OperationOptions { Kind = "FILES" });
        operation.Report(new OperationProgress(4096, Unit: "bytes"));   // no Total — an absolute count

        operation.Complete();

        Assert.Equal(new OperationProgress(4096, Unit: "bytes"), registry.GetAll().Single().Progress);
    }

    [Fact]
    public void Fail_carries_a_structured_error_never_free_text()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        operation.Fail("DEPLOY_REJECTED", new Dictionary<string, string> { ["env"] = "prod" });

        var info = Payload(events[^1]);
        Assert.Equal(OperationStatus.Failed, info.Status);
        Assert.Equal("DEPLOY_REJECTED", info.Error!.Code);
        Assert.Equal("prod", info.Error.Parameters!["env"]);
    }

    [Fact]
    public void Cancel_cancels_the_operations_own_token()
    {
        var (registry, _) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Cancellable = true });

        Assert.True(registry.Cancel(operation.Id));

        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.Equal(OperationStatus.Cancelled, registry.GetAll().Single().Status);
    }

    /// <summary>
    /// Carried finding (routed from the Task 2 review, fixed in Task 5): <c>Start</c> allocates a CTS
    /// for every operation regardless of <c>Cancellable</c> — that flag instead gates whether
    /// <c>Cancel()</c> is allowed to signal it. Cancelling a non-cancellable operation used to flip
    /// the status to Cancelled while the body kept running to completion underneath it — the UI
    /// showed "cancelled" for work that was still going, and the body's own later <c>Complete()</c>
    /// silently no-op'd because the entry was already terminal. <c>Cancellable</c> is documented as
    /// "exposes a WORKING cancel", so the honest contract is: refuse and change nothing.
    /// </summary>
    [Fact]
    public void Cancel_returns_false_and_changes_nothing_for_a_non_cancellable_operation()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" }); // Cancellable defaults to false
        var eventsAfterStart = events.Count;

        var cancelled = registry.Cancel(operation.Id);

        Assert.False(cancelled);
        Assert.False(operation.CancellationToken.IsCancellationRequested);
        Assert.Equal(OperationStatus.Running, registry.GetAll().Single().Status);
        Assert.Equal(eventsAfterStart, events.Count); // no spurious Cancelled snapshot published
    }

    [Fact]
    public void GetAll_filters_by_module_and_scope_and_lists_running_first()
    {
        var (registry, _) = Build();
        var running = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "prod" });
        var done = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "prod" });
        registry.Start("SCAN", new OperationOptions { Kind = "FILES", Scope = "dev" });
        done.Complete();

        var deployProd = registry.GetAll(module: "DEPLOY", scope: "prod");

        Assert.Equal(2, deployProd.Count);
        Assert.Equal(running.Id, deployProd[0].Id);       // running before finished
        Assert.Equal(done.Id, deployProd[1].Id);
        Assert.Single(registry.GetAll(module: "SCAN"));
    }

    /// <summary>
    /// FINDING 4 (Important, whole-branch review): <c>GetAll</c> used to filter scope by STRICT
    /// equality, so an UNSCOPED operation was excluded from a scoped <c>LIST</c> — but both event
    /// buses (<c>Shenora.Core.Events.EventBus</c>, the TS <c>ShenoraEventBus</c>) apply the family rule that a
    /// scope-less (global) event still reaches scoped subscribers. A scoped operations store therefore
    /// never SAW an unscoped operation in its snapshot but DID fold its deltas, so its contents
    /// silently depended on whether it mounted before or after the work started. <c>GetAll</c> must
    /// follow the SAME rule the bus already established, not diverge from it. Asserts the resulting
    /// SET (not just that a filter argument reached the host): a "dev"-scoped entry must still be
    /// excluded, so this is not just "return everything".
    /// </summary>
    [Fact]
    public void GetAll_scope_filter_follows_the_bus_rule_an_unscoped_operation_matches_any_requested_scope()
    {
        var (registry, _) = Build();
        var scoped = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "prod" });
        var global = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });          // no scope at all
        var otherScope = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "dev" });

        var prod = registry.GetAll(module: "DEPLOY", scope: "prod");

        Assert.Equal(2, prod.Count);
        Assert.Contains(prod, o => o.Id == scoped.Id);
        Assert.Contains(prod, o => o.Id == global.Id);
        Assert.DoesNotContain(prod, o => o.Id == otherScope.Id);
    }

    /// <summary>
    /// TWO bands since D66: in-flight (`Running`, oldest first) → Terminal. The middle band was
    /// `Waiting`, and it went with the rest of the waiting model — a request is in flight or done.
    /// <para>
    /// Still worth a test after the simplification, and this is why: the ordering is what a UI renders
    /// straight into a list, so "finished work sorts above live work" is a defect a user sees
    /// immediately and a type checker never will.
    /// </para>
    /// </summary>
    [Fact]
    public void GetAll_orders_in_flight_before_terminal()
    {
        var (registry, _) = Build();
        var done = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        done.Complete();
        var firstRunning = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        var secondRunning = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        var all = registry.GetAll();

        // Oldest-first WITHIN the in-flight band, and the finished one last however recently it ended.
        Assert.Equal([firstRunning.Id, secondRunning.Id, done.Id], all.Select(o => o.Id));
    }

    /// <summary>
    /// Terminal sorts NEWEST-first (coordinator ruling) — a history/log view surfaces the most
    /// recently finished work first. A `FakeTimeProvider`, advanced explicitly between the two
    /// finishes, makes the ordering deterministic rather than racing real wall-clock resolution.
    /// </summary>
    [Fact]
    public void GetAll_orders_terminal_entries_newest_finished_first()
    {
        var bus = new EventBus();
        var clock = new FakeTimeProvider();
        var registry = new OperationRegistry(bus,
            new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero, TimeProvider = clock });
        var first = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        first.Complete();
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        second.Fail("X");

        var all = registry.GetAll();

        Assert.Equal([second.Id, first.Id], all.Select(o => o.Id));
    }

    /// <summary>
    /// IMPORTANT 3 (this batch's review): sorting terminal entries on `FinishedAt` ALONE has no
    /// deterministic tiebreak for same-tick finishes. `TimeProvider.System` has ~15.6 ms granularity
    /// on Windows, so two operations finishing within the same tick — routine under real load — tie,
    /// and LINQ's stable sort then falls back to the PRE-SORT (dictionary enumeration) order, which
    /// reshuffles after any removal or insert unrelated to these two entries: a history panel would
    /// reorder on churn nobody touched. `Sequence` (a strictly monotonic counter, never reused) is the
    /// deterministic tiebreak; newest SEQUENCE first matches "newest first" without depending on clock
    /// resolution at all. No clock advance between the two finishes here, on purpose — this is the
    /// exact same-tick collision a real clock produces.
    /// </summary>
    [Fact]
    public void GetAll_breaks_a_terminal_tie_on_finishedAt_by_sequence_not_enumeration_order()
    {
        var bus = new EventBus();
        var clock = new FakeTimeProvider();   // frozen — both finish at the SAME instant, on purpose
        var registry = new OperationRegistry(bus,
            new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero, TimeProvider = clock });
        var first = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        var second = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        first.Complete();     // same FinishedAt as second — clock never advanced between the two
        second.Fail("X");

        var all = registry.GetAll();

        Assert.Equal([second.Id, first.Id], all.Select(o => o.Id));   // newest SEQUENCE first, tie or not
    }

    // --- Wait/Resume (§5A.3, D23 amendment) ----------------------------------------------------

    [Fact]
    public void ClearFinished_removes_history_and_keeps_running_work()
    {
        var (registry, _) = Build();
        var running = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" }).Complete();

        registry.ClearFinished();

        Assert.Equal(running.Id, registry.GetAll().Single().Id);
    }

    /// <summary>
    /// FINDING 1 (Critical, generic-library audit): <c>ClearFinished</c> used to take NO filter at
    /// all while its own read counterpart (<c>GetAll</c>) is properly filtered by module/scope — so
    /// "clear completed" in one scoped window (a secondary window, a scoped container) wiped every
    /// OTHER scope's finished history too. Mirrors <c>GetAll</c> exactly, including the same
    /// unscoped-entry-matches-any-requested-scope rule.
    /// </summary>
    [Fact]
    public void ClearFinished_with_a_scope_filter_only_clears_that_scopes_finished_history()
    {
        var (registry, _) = Build();
        var prodDone = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "prod" });
        prodDone.Complete();
        var devDone = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "dev" });
        devDone.Complete();

        registry.ClearFinished(scope: "prod");

        var remaining = registry.GetAll();
        Assert.DoesNotContain(remaining, o => o.Id == prodDone.Id);
        Assert.Contains(remaining, o => o.Id == devDone.Id);
    }

    /// <summary>Same shape as the scope test above, filtering by owning MODULE instead.</summary>
    [Fact]
    public void ClearFinished_with_a_module_filter_only_clears_that_modules_finished_history()
    {
        var (registry, _) = Build();
        var deployDone = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        deployDone.Complete();
        var scanDone = registry.Start("SCAN", new OperationOptions { Kind = "FILES" });
        scanDone.Complete();

        registry.ClearFinished(module: "DEPLOY");

        var remaining = registry.GetAll();
        Assert.DoesNotContain(remaining, o => o.Id == deployDone.Id);
        Assert.Contains(remaining, o => o.Id == scanDone.Id);
    }

    /// <summary>
    /// The exact multi-window bug the finding describes: clearing scope A's finished history must
    /// leave scope B's completely untouched, running work aside.
    /// </summary>
    [Fact]
    public void ClearFinished_scoped_to_one_window_does_not_wipe_another_windows_finished_history()
    {
        var (registry, _) = Build();
        var windowA = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "window-a" });
        windowA.Complete();
        var windowB = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "window-b" });
        windowB.Complete();

        registry.ClearFinished(scope: "window-a");

        Assert.Single(registry.GetAll(), o => o.Id == windowB.Id);
        Assert.Equal(OperationStatus.Completed, registry.GetAll().Single(o => o.Id == windowB.Id).Status);
    }

    /// <summary>
    /// FINDING 4 (Important, generic-library audit): the host bounds finished history
    /// (<see cref="OperationRegistryOptions.MaxHistory"/>) but the CLIENT — the side actually
    /// rendering — never heard about it: <see cref="OperationEvents.Updated"/> only ever adds or
    /// updates an id, so an evicted id used to just vanish from the host with no wire event at all.
    /// <see cref="OperationEvents.Removed"/> closes that: eviction now publishes the evicted id(s).
    /// </summary>
    [Fact]
    public void MaxHistory_eviction_emits_OPERATION_REMOVED_naming_the_evicted_id()
    {
        var bus = new EventBus();
        var events = new List<EventMessage>();
        bus.SubscribeToAll(m => { lock (events) events.Add(m); return Task.CompletedTask; });
        var registry = new OperationRegistry(bus, new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero, MaxHistory = 1 });
        var evicted = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        evicted.Complete();
        events.Clear();

        registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" }).Complete();   // pushes MaxHistory=1 over the cap

        var removed = events.Single(e => e.Type == OperationEvents.Removed);
        var ids = IpcJson.SerializeToElement(removed.Payload).GetProperty("operationIds")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal([evicted.Id], ids);
    }

    /// <summary>Mirrors the eviction test above, for the explicit <see cref="IOperationRegistry.ClearFinished"/> route.</summary>
    [Fact]
    public void ClearFinished_emits_OPERATION_REMOVED_naming_every_id_it_actually_removed()
    {
        var (registry, events) = Build();
        var kept = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });   // still running — not removed
        var removedA = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        removedA.Complete();
        var removedB = registry.Start("SCAN", new OperationOptions { Kind = "X" });
        removedB.Complete();
        events.Clear();

        registry.ClearFinished();

        var message = events.Single(e => e.Type == OperationEvents.Removed);
        var ids = IpcJson.SerializeToElement(message.Payload).GetProperty("operationIds")
            .EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        Assert.Equal(new HashSet<string> { removedA.Id, removedB.Id }, ids);
        Assert.DoesNotContain(kept.Id, ids);
    }

    /// <summary>ClearFinished with nothing to remove must not publish an empty/spurious OPERATION_REMOVED.</summary>
    [Fact]
    public void ClearFinished_with_no_matching_history_does_not_emit_OPERATION_REMOVED()
    {
        var (registry, events) = Build();
        registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });   // still running
        events.Clear();

        registry.ClearFinished();

        Assert.DoesNotContain(events, e => e.Type == OperationEvents.Removed);
    }

    [Fact]
    public void Construction_rejects_a_negative_MaxHistory_naming_the_option()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OperationRegistry(new EventBus(), new OperationRegistryOptions { MaxHistory = -1 }));

        Assert.Contains(nameof(OperationRegistryOptions.MaxHistory), ex.Message);
    }

    [Fact]
    public void Construction_rejects_a_negative_ProgressInterval_naming_the_option()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OperationRegistry(new EventBus(),
                new OperationRegistryOptions { ProgressInterval = TimeSpan.FromMilliseconds(-1) }));

        Assert.Contains(nameof(OperationRegistryOptions.ProgressInterval), ex.Message);
    }

    /// <summary>
    /// The everyday race this primitive exists for: a user clicks cancel on a long-running task at
    /// the exact moment it finishes on its own. `Cancel` reads the CTS under the lock but calls
    /// `.Cancel()` OUTSIDE it (deliberately — see the comment on `OperationRegistry.Cancel`), so a
    /// concurrent `Complete`/`Fail`/`Cancel` can dispose that same CTS first, between the lock
    /// release and the `.Cancel()` call — a window of roughly one machine instruction.
    /// <para>
    /// A single pair on thread-pool tasks does NOT reliably hit a window that narrow (tried first;
    /// 300 sequential pool-task iterations never reproduced the fault). What DOES reproduce it: real
    /// OS threads (guaranteed immediate, true parallelism — no thread-pool queuing), MANY pairs
    /// racing at once released by one shared gate, so system-wide scheduler contention (far more
    /// runnable threads than cores) makes a preemption land inside the window for at least one pair.
    /// </para>
    /// </summary>
    [Fact]
    public void Concurrent_Cancel_and_Complete_never_leak_an_exception_and_settle_on_one_terminal_state()
    {
        const int Pairs = 500;
        // MaxHistory must cover every operation this test finishes, or pruning removes the
        // evidence before the post-condition check below can see it — unrelated to the race
        // this test targets.
        var (registry, _) = Build(new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero, MaxHistory = Pairs });
        var operations = Enumerable.Range(0, Pairs)
            .Select(_ => registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Cancellable = true }))
            .ToList();

        using var gate = new ManualResetEventSlim(false);
        var escaped = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var threads = new List<Thread>(Pairs * 2);

        foreach (var operation in operations)
        {
            var cancelThread = new Thread(() =>
            {
                gate.Wait();
                try { registry.Cancel(operation.Id); }
                catch (Exception ex) { escaped.Add(ex); }
            });
            var completeThread = new Thread(() =>
            {
                gate.Wait();
                try { operation.Complete(); }
                catch (Exception ex) { escaped.Add(ex); }
            });
            threads.Add(cancelThread);
            threads.Add(completeThread);
            cancelThread.Start();
            completeThread.Start();
        }

        gate.Set(); // release all Pairs*2 threads at once — maximize contention

        // Bounded PER-JOIN (ALSO IN THIS BATCH, whole-branch review), not one 5s budget shared across
        // all 1000 joins: a single shared deadline means an EARLIER thread's ordinary scheduling delay
        // on a loaded machine eats into the time budget left for every later one, so this used to fail
        // for contention rather than for the race it exists to catch. Each Join() still returns the
        // instant its thread finishes — this only changes what happens when one is genuinely stuck.
        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)),
                "a Cancel/Complete thread did not finish within the bound");
        }

        // The actual assertion for the Critical finding: no ObjectDisposedException (or anything
        // else) escaped a Cancel()/Complete() call on either thread.
        Assert.Empty(escaped);

        foreach (var operation in operations)
        {
            var status = registry.GetAll().Single(o => o.Id == operation.Id).Status;
            Assert.True(status is OperationStatus.Cancelled or OperationStatus.Completed);
        }
    }
}
