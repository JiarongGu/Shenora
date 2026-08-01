using Shenora.Core;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// <see cref="IOperationRegistry.Dismiss"/> (§5A.3, D23 amendment) — the fix for the bug that named
/// the rule: a checkpoint offer (reached via <see cref="IOperationRegistry.RegisterWaiting"/>) used
/// to have exactly one exit (`RequestResume`), and an entry reached via <see cref="IOperation.Wait"/>
/// had none at all. `Dismiss` gives both a sanctioned path to a TERMINAL status (`Cancelled`) —
/// declining an offer, distinct from `Cancel`'s "stop LIVE work" (permission-checked against
/// `Cancellable`), which is the conflation that produced this branch's only Critical.
/// </summary>
public class OperationDismissTests
{
    private static (OperationRegistry Registry, List<EventMessage> Events) Build()
    {
        var bus = new EventBus();
        var events = new List<EventMessage>();
        bus.SubscribeToAll(m => { lock (events) events.Add(m); return Task.CompletedTask; });
        return (new OperationRegistry(bus, new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero }), events);
    }

    [Fact]
    public void Dismiss_transitions_a_waiting_entry_to_cancelled_and_publishes_a_snapshot()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Wait("dns");
        var eventsBeforeDismiss = events.Count;

        Assert.True(registry.Dismiss(operation.Id));

        // Unlike ClearFinished (which removes entries with no OPERATION_UPDATED of their own), Dismiss
        // publishes an ordinary OPERATION_UPDATED snapshot — the entry becomes terminal, not gone.
        Assert.True(events.Count > eventsBeforeDismiss);
        var info = registry.GetAll().Single();
        Assert.Equal(OperationStatus.Cancelled, info.Status);
        Assert.NotNull(info.FinishedAt);
    }

    // A second Dismiss case used to live here, covering an entry registered from a crash checkpoint
    // (RegisterWaiting) — a shape that had no CancellationTokenSource at all. The 0.2.0 design pass
    // cut that half of the feature, so there is only ONE way to reach Waiting now and the case above
    // already covers it.

    /// <summary>
    /// A dismissed entry is now ORDINARY finished history (terminal, prunable) — the whole point of
    /// giving the waiting band an exit. Before this feature, a Waiting entry (reached either way)
    /// could sit forever with no way to become eligible for MaxHistory/ClearFinished.
    /// </summary>
    [Fact]
    public void A_dismissed_entry_is_prunable_history_afterward()
    {
        var (registry, _) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Wait("dns");
        registry.Dismiss(operation.Id);

        registry.ClearFinished();

        Assert.Empty(registry.GetAll());
    }

    /// <summary>
    /// The refusal that keeps this from re-earning the branch's only Critical (§5A.3): declining a
    /// pending offer and cancelling LIVE work are different acts. Dismiss REFUSES Running — that is
    /// Cancel's job, permission-checked against Cancellable. Routing around Cancel via Dismiss would
    /// be exactly the conflation this design closes.
    /// </summary>
    [Fact]
    public void Dismiss_refuses_a_running_operation_and_changes_nothing()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Cancellable = true });
        var eventsAfterStart = events.Count;

        var dismissed = registry.Dismiss(operation.Id);

        Assert.False(dismissed);
        Assert.Equal(OperationStatus.Running, registry.GetAll().Single().Status);
        Assert.Equal(eventsAfterStart, events.Count);
        Assert.False(operation.CancellationToken.IsCancellationRequested);   // not even signalled
    }

    [Fact]
    public void Dismiss_returns_false_for_an_unknown_id()
    {
        var (registry, events) = Build();

        Assert.False(registry.Dismiss("no-such-id"));
        Assert.Empty(events);
    }

    [Fact]
    public void Dismiss_returns_false_for_an_already_terminal_operation()
    {
        var (registry, _) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Complete();

        Assert.False(registry.Dismiss(operation.Id));
        Assert.Equal(OperationStatus.Completed, registry.GetAll().Single().Status);
    }

    /// <summary>
    /// "Signals the CTS first when one exists, so a waiting body parked on its token still unwinds"
    /// (§5A.3): an entry reached via <see cref="IOperation.Wait"/> still carries its own
    /// CancellationTokenSource (<c>Wait</c> doesn't dispose it — only a terminal Finish does), and
    /// Dismiss must signal it before the terminal transition, same order as Cancel/CancelTerminal.
    /// </summary>
    [Fact]
    public void Dismiss_signals_the_token_of_an_entry_reached_via_Wait_so_a_parked_body_unwinds()
    {
        var (registry, _) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Wait("dns");

        registry.Dismiss(operation.Id);

        Assert.True(operation.CancellationToken.IsCancellationRequested);
    }

    /// <summary>
    /// Dismiss must not throw on an ALREADY-TERMINAL entry whose CTS <c>Finish</c> has disposed and
    /// nulled — the one remaining way <c>Dismiss</c> can meet a null token now that the
    /// checkpoint-registered (never-had-a-CTS) shape is gone. It refuses honestly instead.
    /// </summary>
    [Fact]
    public void Dismiss_does_not_throw_for_an_entry_whose_token_is_already_disposed()
    {
        var (registry, _) = Build();
        var operation = registry.Start("SCAN", new OperationOptions { Kind = "ANALYSIS" });
        operation.Complete();   // Finish disposes and clears the CTS

        var dismissed = registry.Dismiss(operation.Id);   // must not throw

        Assert.False(dismissed);   // terminal, so refused — honestly, not by crashing
        Assert.Equal(OperationStatus.Completed, registry.GetAll().Single().Status);
    }

    /// <summary>
    /// Hardening (this batch's review): <c>Dismiss</c> used to return <c>true</c> unconditionally once
    /// its OWN permission check passed, regardless of what <c>Finish</c>'s re-validation (under a
    /// separately re-acquired lock) actually decided. The window: a concurrent <c>Resume()</c> on the
    /// SAME waiting entry, landing between <c>Dismiss</c>'s own lock release and <c>Finish</c>'s own
    /// lock acquisition, flips the entry back to <c>Running</c> before <c>Finish</c> ever runs — so
    /// <c>Finish</c> correctly refuses, but the OLD code still answered the client <c>true</c>,
    /// leaving a live, un-cancelled operation reported as successfully dismissed. Same shape as
    /// <see cref="OperationRegistryTests.Concurrent_Cancel_and_Complete_never_leak_an_exception_and_settle_on_one_terminal_state"/>
    /// (many real-thread pairs racing at once, released by one shared gate — thread-pool tasks alone
    /// do not reliably hit a window this narrow): whenever <c>Dismiss</c> answers <c>true</c>, the
    /// entry's FINAL status must actually be <see cref="OperationStatus.Cancelled"/> — never a
    /// dismissed-but-still-Running ghost.
    /// </summary>
    [Fact]
    public void Concurrent_Dismiss_and_Resume_never_reports_dismissed_true_without_actually_cancelling()
    {
        const int Pairs = 300;
        // MaxHistory must cover every operation this test can dismiss, or PruneHistory removes the
        // evidence before the post-condition check below can see it — unrelated to the race itself
        // (same caveat the sibling Cancel/Complete race test notes for the identical reason).
        var bus = new EventBus();
        var registry = new OperationRegistry(bus, new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero, MaxHistory = Pairs });
        var operations = Enumerable.Range(0, Pairs)
            .Select(_ =>
            {
                var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
                operation.Wait("dns");
                return operation;
            })
            .ToList();

        using var gate = new ManualResetEventSlim(false);
        var escaped = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var dismissedTrue = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        var threads = new List<Thread>(Pairs * 2);

        foreach (var operation in operations)
        {
            var dismissThread = new Thread(() =>
            {
                gate.Wait();
                try
                {
                    if (registry.Dismiss(operation.Id)) dismissedTrue[operation.Id] = true;
                }
                catch (Exception ex) { escaped.Add(ex); }
            });
            var resumeThread = new Thread(() =>
            {
                gate.Wait();
                try { operation.Resume(); }
                catch (Exception ex) { escaped.Add(ex); }
            });
            threads.Add(dismissThread);
            threads.Add(resumeThread);
            dismissThread.Start();
            resumeThread.Start();
        }

        gate.Set();   // release all Pairs*2 threads at once — maximize contention

        // Bounded PER-JOIN (same discipline as the sibling Cancel/Complete race test) — a single
        // shared deadline would let one thread's ordinary scheduling delay eat the budget left for
        // every later one.
        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)),
                "a Dismiss/Resume thread did not finish within the bound");
        }

        Assert.Empty(escaped);

        // THE actual assertion: every operation Dismiss reported as dismissed=true must have actually
        // ended Cancelled — never left Running (or anything else) by a race Dismiss didn't notice.
        foreach (var operation in operations)
        {
            if (!dismissedTrue.ContainsKey(operation.Id)) continue;   // Resume won the race — not this test's concern
            var status = registry.GetAll().Single(o => o.Id == operation.Id).Status;
            Assert.True(status == OperationStatus.Cancelled,
                $"operation {operation.Id} was reported dismissed=true but ended {status}, not Cancelled");
        }
    }
}
