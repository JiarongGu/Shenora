using Shenora.Core;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

public class OperationResumeTests
{
    private static (OperationRegistry Registry, List<EventMessage> Events) Build()
    {
        var bus = new EventBus();
        var events = new List<EventMessage>();
        bus.SubscribeToAll(m => { lock (events) events.Add(m); return Task.CompletedTask; });
        return (new OperationRegistry(bus, new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero }), events);
    }

    private static OperationOptions Checkpoint(string payload) =>
        new() { Kind = "ANALYSIS", Resumable = true, ResumePayload = payload, Scope = "p1" };

    [Fact]
    public void RegisterInterrupted_announces_a_resumable_entry_from_the_apps_checkpoint()
    {
        var (registry, _) = Build();

        var id = registry.RegisterInterrupted("SCAN", Checkpoint("session-7"));

        var info = registry.GetAll().Single();
        Assert.Equal(id, info.Id);
        Assert.Equal(OperationStatus.Interrupted, info.Status);
        Assert.Equal("session-7", info.ResumePayload);
    }

    [Fact]
    public void Re_announcing_the_same_checkpoint_does_not_stack_entries()
    {
        var (registry, _) = Build();

        var first = registry.RegisterInterrupted("SCAN", Checkpoint("session-7"));
        var second = registry.RegisterInterrupted("SCAN", Checkpoint("session-7"));

        Assert.Equal(first, second);
        Assert.Single(registry.GetAll());
    }

    [Fact]
    public void RequestResume_emits_for_the_owning_module_and_drops_the_offer()
    {
        var (registry, events) = Build();
        var id = registry.RegisterInterrupted("SCAN", Checkpoint("session-7"));
        events.Clear();

        Assert.True(registry.RequestResume(id));

        var message = events.Single(e => e.Type == OperationEvents.ResumeRequested);
        var payload = IpcJson.SerializeToElement(message.Payload);
        Assert.Equal("SCAN", payload.GetProperty("module").GetString());
        Assert.Equal("session-7", payload.GetProperty("resumePayload").GetString());
        Assert.Empty(registry.GetAll());   // the resumed op registers a FRESH operation when it restarts
    }

    [Fact]
    public void An_interrupted_entry_is_not_prunable_history()
    {
        var bus = new EventBus();
        var registry = new OperationRegistry(bus, new OperationRegistryOptions { MaxHistory = 1 });
        var id = registry.RegisterInterrupted("SCAN", Checkpoint("session-7"));
        for (var i = 0; i < 5; i++) registry.Start("SCAN", new OperationOptions { Kind = "X" }).Complete();

        Assert.Contains(registry.GetAll(), o => o.Id == id);   // a pending resume OFFER, not history
    }

    /// <summary>
    /// ALSO IN THIS BATCH (whole-branch review): a regression guard distinct from
    /// <see cref="An_interrupted_entry_is_not_prunable_history"/> above — that one covers the
    /// AUTOMATIC <see cref="OperationRegistryOptions.MaxHistory"/> eviction path, this one covers the
    /// explicit <see cref="IOperationRegistry.ClearFinished"/> call a client-triggered
    /// <c>CLEAR_FINISHED</c> route drives. It is correct TODAY only structurally —
    /// <c>ClearFinished</c> walks <c>_finishedOrder</c>, and only <c>Finish</c> (never
    /// <c>RegisterInterrupted</c>) ever writes to it — so nothing previously proved it, and a future
    /// rewrite that instead filtered <c>_entries</c> by terminal STATUS would silently destroy a
    /// pending offer with this suite still green.
    /// </summary>
    [Fact]
    public void ClearFinished_does_not_evict_a_pending_interrupted_resume_offer()
    {
        var (registry, _) = Build();
        var offerId = registry.RegisterInterrupted("SCAN", Checkpoint("session-7"));
        registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" }).Complete(); // ordinary finished history

        registry.ClearFinished();

        Assert.Contains(registry.GetAll(), o => o.Id == offerId);
    }

    /// <summary>
    /// A silently-accepted unusable entry would be worse than a loud rejection: without a resume
    /// payload nobody — kit or app — could ever act on the offer.
    /// </summary>
    [Fact]
    public void RegisterInterrupted_rejects_non_resumable_options_naming_Resumable()
    {
        var (registry, _) = Build();

        var ex = Assert.Throws<ArgumentException>(() =>
            registry.RegisterInterrupted("SCAN", Checkpoint("session-7") with { Resumable = false }));

        Assert.Contains(nameof(OperationOptions.Resumable), ex.Message);
    }

    [Fact]
    public void RegisterInterrupted_rejects_a_missing_resume_payload_naming_ResumePayload()
    {
        var (registry, _) = Build();

        var ex = Assert.Throws<ArgumentException>(() =>
            registry.RegisterInterrupted("SCAN", Checkpoint("session-7") with { ResumePayload = null }));

        Assert.Contains(nameof(OperationOptions.ResumePayload), ex.Message);
    }

    [Fact]
    public void RequestResume_returns_false_for_an_unknown_id()
    {
        var (registry, events) = Build();

        Assert.False(registry.RequestResume("no-such-id"));
        Assert.Empty(events);
    }

    /// <summary>
    /// The BOTH-conditions contract: a Running operation's id is known to the registry but is not an
    /// interrupted offer, so RequestResume must refuse it exactly like an unknown id — never resume
    /// (or remove) work that is still actively running.
    /// </summary>
    [Fact]
    public void RequestResume_returns_false_for_an_operation_that_is_not_interrupted()
    {
        var (registry, events) = Build();
        var running = registry.Start("SCAN", new OperationOptions { Kind = "X" });
        events.Clear();

        Assert.False(registry.RequestResume(running.Id));
        Assert.Empty(events);
        Assert.Equal(OperationStatus.Running, registry.GetAll().Single().Status);
    }

    // --- The Paused/Interrupted asymmetry (§5A.4, D23 amendment) -------------------------------

    /// <summary>
    /// §5A.4's rule: RequestResume now accepts a Paused entry too, but the app calls IOperation.Resume
    /// on its own handle once it has ACTUALLY resumed — the client ASKING is not the state changing.
    /// So unlike the Interrupted case below, the entry must stay exactly as it was (still Paused,
    /// still present) after a successful RequestResume.
    /// </summary>
    [Fact]
    public void RequestResume_on_a_paused_entry_leaves_it_in_place_for_the_app_to_flip_via_Resume()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Pause("dns");
        events.Clear();

        Assert.True(registry.RequestResume(operation.Id));

        var message = events.Single(e => e.Type == OperationEvents.ResumeRequested);
        var payload = IpcJson.SerializeToElement(message.Payload);
        Assert.Equal("paused", payload.GetProperty("status").GetString());

        // Left IN PLACE, still Paused — the defining half of the asymmetry.
        var info = registry.GetAll().Single(o => o.Id == operation.Id);
        Assert.Equal(OperationStatus.Paused, info.Status);

        // The app's own handle can still flip it — proves the entry is genuinely untouched, not a
        // look-alike replacement.
        operation.Resume();
        Assert.Equal(OperationStatus.Running, registry.GetAll().Single(o => o.Id == operation.Id).Status);
    }

    /// <summary>
    /// The other half of the asymmetry: an Interrupted entry is still REMOVED by RequestResume (there
    /// is no live handle to flip — the body died with the process), which is exactly why the payload
    /// now carries `status`: a handler cannot look this entry up afterward to tell the two cases apart.
    /// </summary>
    [Fact]
    public void RequestResume_on_an_interrupted_entry_still_removes_it_and_the_payload_names_the_status()
    {
        var (registry, events) = Build();
        var id = registry.RegisterInterrupted("SCAN", Checkpoint("session-7"));
        events.Clear();

        Assert.True(registry.RequestResume(id));

        var message = events.Single(e => e.Type == OperationEvents.ResumeRequested);
        var payload = IpcJson.SerializeToElement(message.Payload);
        Assert.Equal("interrupted", payload.GetProperty("status").GetString());
        Assert.Empty(registry.GetAll());   // gone — the resumed op registers a FRESH one when it restarts
    }
}
