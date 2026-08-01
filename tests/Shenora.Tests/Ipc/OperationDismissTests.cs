using Shenora.Core;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// <see cref="IOperationRegistry.Dismiss"/> (§5A.3, D23 amendment) — the fix for the bug that named
/// the rule: an `Interrupted` offer used to have exactly one exit (`RequestResume`), and a `Paused`
/// entry had none at all. `Dismiss` gives both a sanctioned path to a TERMINAL status
/// (`Cancelled`) — declining an offer, distinct from `Cancel`'s "stop LIVE work" (permission-checked
/// against `Cancellable`), which is the conflation that produced this branch's only Critical.
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
    public void Dismiss_transitions_a_paused_entry_to_cancelled_and_publishes_a_snapshot()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Pause("dns");
        var eventsBeforeDismiss = events.Count;

        Assert.True(registry.Dismiss(operation.Id));

        // Unlike ClearFinished/RequestResume (which remove an entry with NO wire event), Dismiss
        // publishes an ordinary OPERATION_UPDATED snapshot — the entry becomes terminal, not gone.
        Assert.True(events.Count > eventsBeforeDismiss);
        var info = registry.GetAll().Single();
        Assert.Equal(OperationStatus.Cancelled, info.Status);
        Assert.NotNull(info.FinishedAt);
    }

    [Fact]
    public void Dismiss_transitions_an_interrupted_offer_to_cancelled()
    {
        var (registry, _) = Build();
        var id = registry.RegisterInterrupted("SCAN",
            new OperationOptions { Kind = "ANALYSIS", Resumable = true, ResumePayload = "session-7" });

        Assert.True(registry.Dismiss(id));

        Assert.Equal(OperationStatus.Cancelled, registry.GetAll().Single().Status);
    }

    /// <summary>
    /// A dismissed entry is now ORDINARY finished history (terminal, prunable) — the whole point of
    /// giving the waiting band an exit. Before this feature, an Interrupted/Paused entry could sit
    /// forever with no way to become eligible for MaxHistory/ClearFinished.
    /// </summary>
    [Fact]
    public void A_dismissed_entry_is_prunable_history_afterward()
    {
        var (registry, _) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Pause("dns");
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
    /// "Signals the CTS first when one exists, so a paused body parked on its token still unwinds"
    /// (§5A.3): a Paused entry still carries its own CancellationTokenSource (Pause doesn't dispose
    /// it — only a terminal Finish does), and Dismiss must signal it before the terminal transition,
    /// same order as Cancel/CancelTerminal.
    /// </summary>
    [Fact]
    public void Dismiss_signals_the_token_of_a_paused_entry_so_a_parked_body_unwinds()
    {
        var (registry, _) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Pause("dns");

        registry.Dismiss(operation.Id);

        Assert.True(operation.CancellationToken.IsCancellationRequested);
    }

    /// <summary>
    /// An Interrupted entry never has a CTS at all (RegisterInterrupted's own doc) — Dismiss must not
    /// throw reaching for one that was never allocated.
    /// </summary>
    [Fact]
    public void Dismiss_does_not_throw_for_an_interrupted_entry_with_no_token()
    {
        var (registry, _) = Build();
        var id = registry.RegisterInterrupted("SCAN",
            new OperationOptions { Kind = "ANALYSIS", Resumable = true, ResumePayload = "session-7" });

        var dismissed = registry.Dismiss(id);   // must not throw

        Assert.True(dismissed);
        Assert.Equal(OperationStatus.Cancelled, registry.GetAll().Single().Status);
    }
}
