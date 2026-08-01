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

    [Fact]
    public void ClearFinished_removes_history_and_keeps_running_work()
    {
        var (registry, _) = Build();
        var running = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" }).Complete();

        registry.ClearFinished();

        Assert.Equal(running.Id, registry.GetAll().Single().Id);
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
