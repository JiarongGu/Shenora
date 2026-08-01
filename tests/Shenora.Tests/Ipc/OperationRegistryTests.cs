using Microsoft.Extensions.Time.Testing;
using Shenora.Core;
using Shenora.Ipc;

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
        Assert.Equal("OPERATIONS", message.Module);
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

        operation.Report(40, new OperationLabel(Text: "uploading", Key: "deploy.stage.upload"));

        var info = Payload(events[^1]);
        Assert.Equal(40, info.Progress);
        Assert.Equal("deploy.stage.upload", info.Detail!.Key);
        Assert.Equal("uploading", info.Detail.Text);
    }

    [Fact]
    public void Progress_is_clamped_to_the_0_100_range()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        operation.Report(140);

        Assert.Equal(100, Payload(events[^1]).Progress);
    }

    [Fact]
    public void Complete_is_terminal_and_finishing_twice_is_a_no_op()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        operation.Complete();
        var afterComplete = events.Count;
        operation.Fail("TOO_LATE");                 // the "Complete at the end + Fail in the catch" pattern
        operation.Report(50);

        Assert.Equal(afterComplete, events.Count);  // nothing after the terminal transition
        var info = Payload(events[^1]);
        Assert.Equal(OperationStatus.Completed, info.Status);
        Assert.Equal(100, info.Progress);           // completion implies 100
        Assert.NotNull(info.FinishedAt);
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
    /// buses (<c>Shenora.Core.EventBus</c>, the TS <c>ShenoraEventBus</c>) apply the family rule that a
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
    /// The three-band sort (§5A.2, coordinator ruling on this batch): Active (`Running`) → Waiting
    /// (`Paused`/`Interrupted`) → Terminal — NOT "Running vs everything else". Before this fix a
    /// `Paused` entry fell into the "everything else" bucket right alongside completed history
    /// (`FinishedAt == null`, sorted by `Sequence` ascending with no band of its own), burying the
    /// exact row a user needs to find in order to resume or dismiss it — precisely the reason the
    /// Waiting band exists at all (§5A.2's table).
    /// </summary>
    [Fact]
    public void GetAll_orders_active_then_waiting_then_terminal()
    {
        var (registry, _) = Build();
        var done = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        done.Complete();
        var paused = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        paused.Pause("dns");
        var running = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        var all = registry.GetAll();

        Assert.Equal([running.Id, paused.Id, done.Id], all.Select(o => o.Id));
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

    // --- Pause/Resume (§5A.3, D23 amendment) ---------------------------------------------------

    /// <summary>
    /// FINDING 5 minor (generic-library audit): a pause whose cause is self-evident (the user clicked
    /// Pause) has nothing to branch a UI on — the surveyed app's four-value reason taxonomy does not
    /// generalize to every consumer, so a required parameter forced one on a caller who has none.
    /// </summary>
    [Fact]
    public void Pause_with_no_reason_succeeds_and_leaves_PauseReason_null()
    {
        var (registry, _) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        operation.Pause();

        var info = registry.GetAll().Single();
        Assert.Equal(OperationStatus.Paused, info.Status);
        Assert.Null(info.PauseReason);
    }

    [Fact]
    public void Pause_transitions_running_to_paused_and_publishes_immediately_with_the_reason()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        operation.Pause("dns", new OperationLabel(Text: "waiting on DNS"));

        var info = Payload(events[^1]);
        Assert.Equal(OperationStatus.Paused, info.Status);
        Assert.Equal("dns", info.PauseReason);
        Assert.Equal("waiting on DNS", info.Detail!.Text);
    }

    /// <summary>
    /// Validate rework (this batch, §5A.1): Pause requires Running specifically — it must refuse an
    /// ALREADY-paused entry (not just a terminal one), or a second Pause call would silently stomp the
    /// existing reason with no signal that the first pause was still in effect.
    /// </summary>
    [Fact]
    public void Pause_is_ignored_once_already_paused()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Pause("dns");
        var eventsAfterFirstPause = events.Count;

        operation.Pause("credentials");   // must NOT stomp the existing reason

        Assert.Equal(eventsAfterFirstPause, events.Count);   // no spurious second snapshot
        Assert.Equal("dns", registry.GetAll().Single().PauseReason);
    }

    [Fact]
    public void Resume_transitions_paused_to_running_and_clears_the_reason()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Pause("dns");

        operation.Resume();

        var info = Payload(events[^1]);
        Assert.Equal(OperationStatus.Running, info.Status);
        Assert.Null(info.PauseReason);
    }

    /// <summary>
    /// The other half of `PauseReason`'s lifetime (coordinator ruling, pinned so a future
    /// "simplification" that clears it on every terminal transition — a reasonable-LOOKING cleanup —
    /// fails loudly instead of silently discarding useful history): a terminal transition reached
    /// DIRECTLY from Paused (no intervening `Resume`) must retain the reason. "Failed while paused
    /// waiting on credentials" is exactly the kind of thing a finished-history reader wants.
    /// </summary>
    [Fact]
    public void A_terminal_transition_reached_directly_from_paused_retains_the_pause_reason()
    {
        var (registry, _) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Pause("credentials");

        operation.Fail("DEADLINE_EXCEEDED");

        Assert.Equal("credentials", registry.GetAll().Single().PauseReason);
    }

    /// <summary>
    /// The handle-level Resume() (Paused → Running) is distinct from the by-id RequestResume (which
    /// only ASKS, see OperationResumeTests) — it must refuse a plain Running operation rather than
    /// silently no-op-ing into an indistinguishable Running state.
    /// </summary>
    [Fact]
    public void Resume_is_ignored_for_an_operation_that_is_not_paused()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        var eventsAfterStart = events.Count;

        operation.Resume();

        Assert.Equal(eventsAfterStart, events.Count);
        Assert.Equal(OperationStatus.Running, registry.GetAll().Single().Status);
    }

    /// <summary>
    /// Validate rework (§5A.1): Report requires Running ONLY — a paused operation is not progressing,
    /// and letting progress tick while paused is how a UI ends up showing motion for stopped work.
    /// </summary>
    [Fact]
    public void Report_is_ignored_while_paused()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Pause("dns");
        var eventsAfterPause = events.Count;

        operation.Report(50);

        Assert.Equal(eventsAfterPause, events.Count);          // no spurious progress snapshot
        Assert.Null(registry.GetAll().Single().Progress);      // untouched
    }

    /// <summary>Validate rework (§5A.1): Complete/Fail accept Running OR Paused — a paused deploy can still fail on a deadline.</summary>
    [Fact]
    public void Complete_accepts_a_paused_operation()
    {
        var (registry, _) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Pause("dns");

        operation.Complete();

        Assert.Equal(OperationStatus.Completed, registry.GetAll().Single().Status);
    }

    [Fact]
    public void Fail_accepts_a_paused_operation()
    {
        var (registry, _) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Pause("dns");

        operation.Fail("DEADLINE_EXCEEDED");

        var info = registry.GetAll().Single();
        Assert.Equal(OperationStatus.Failed, info.Status);
        Assert.Equal("DEADLINE_EXCEEDED", info.Error!.Code);
    }

    /// <summary>Validate rework (§5A.1): the public by-id Cancel(id) accepts Running OR Paused, keeping its own Cancellable check.</summary>
    [Fact]
    public void Cancel_by_id_accepts_a_paused_operation()
    {
        var (registry, _) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Cancellable = true });
        operation.Pause("dns");

        Assert.True(registry.Cancel(operation.Id));

        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.Equal(OperationStatus.Cancelled, registry.GetAll().Single().Status);
    }

    /// <summary>
    /// The message-honesty fix (§5A.1, this batch): the OLD message unconditionally said "has already
    /// reached a terminal state (Interrupted)" for ANY status a transition refused — which is false for
    /// Interrupted (explicitly not terminal, see OperationStatus's own doc). A Report call against a
    /// Paused entry must get a message that does NOT claim "terminal", since Paused is not terminal
    /// either.
    /// </summary>
    [Fact]
    public void The_ignored_diagnostic_does_not_call_a_non_terminal_status_terminal()
    {
        string? logged = null;
        var bus = new EventBus();
        var registry = new OperationRegistry(bus, new OperationRegistryOptions
        {
            ProgressInterval = TimeSpan.Zero,
            Log = message => logged = message,
        });
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Pause("dns");

        operation.Report(50);   // Report only accepts Running — Paused must be refused

        Assert.NotNull(logged);
        Assert.DoesNotContain("terminal", logged, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Paused", logged, StringComparison.Ordinal);
    }

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

    /// <summary>
    /// §5A.2's table claims Paused is "never pruned" — same band as Interrupted. Verified rather than
    /// assumed (this batch): it should follow structurally from Pause() never touching
    /// <c>_finishedOrder</c>, but a test PINS it, the same way <c>OperationResumeTests</c> already pins
    /// it for Interrupted. Covers BOTH eviction paths: the automatic <c>MaxHistory</c> cap and the
    /// explicit <c>ClearFinished</c> call.
    /// </summary>
    [Fact]
    public void A_paused_entry_is_not_prunable_history_and_survives_ClearFinished()
    {
        var bus = new EventBus();
        var registry = new OperationRegistry(bus, new OperationRegistryOptions { MaxHistory = 1 });
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Pause("dns");
        for (var i = 0; i < 5; i++) registry.Start("SCAN", new OperationOptions { Kind = "X" }).Complete();

        Assert.Contains(registry.GetAll(), o => o.Id == operation.Id);   // survives MaxHistory eviction

        registry.ClearFinished();

        Assert.Contains(registry.GetAll(), o => o.Id == operation.Id);   // survives explicit ClearFinished too
        Assert.Equal(OperationStatus.Paused, registry.GetAll().Single(o => o.Id == operation.Id).Status);
    }

    // --- Find (generic-library audit finding 3, reinstated) ------------------------------------

    /// <summary>
    /// <c>Find</c> was dropped pre-0.2.0 as unearned surface ("no consumer resolves a handle from a
    /// bare id"). That ruling is now wrong on evidence: <c>RESUME</c>/<c>PAUSE</c> are both
    /// CLIENT-request routes whose handlers must translate the id they carry back into a handle to
    /// call <see cref="IOperation.Resume"/>/<see cref="IOperation.Pause"/> — every consumer of those
    /// two routes would otherwise keep its own id→handle map alongside the registry.
    /// </summary>
    [Fact]
    public void Find_returns_a_handle_that_can_act_on_the_live_entry()
    {
        var (registry, _) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        var found = registry.Find(operation.Id);

        Assert.NotNull(found);
        Assert.Equal(operation.Id, found!.Id);
        found.Report(50);
        Assert.Equal(50, registry.GetAll().Single().Progress);
    }

    [Fact]
    public void Find_returns_null_for_an_unknown_id()
    {
        var (registry, _) = Build();

        Assert.Null(registry.Find("no-such-id"));
    }

    /// <summary>
    /// "A returned handle validates state on every call, so a stale one is safe" — a handle resolved
    /// BEFORE the operation finished must still be a safe no-op afterward, not a dangling reference to
    /// guard against.
    /// </summary>
    [Fact]
    public void Find_returned_handle_is_safe_to_use_after_the_operation_has_finished()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        var found = registry.Find(operation.Id)!;
        operation.Complete();
        var eventsAfterComplete = events.Count;

        found.Report(50);      // must be ignored — Report only accepts Running
        found.Complete();      // already terminal — idempotent no-op

        Assert.Equal(eventsAfterComplete, events.Count);   // no spurious snapshot from the stale handle
        Assert.Equal(OperationStatus.Completed, registry.GetAll().Single().Status);
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
