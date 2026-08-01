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
        new() { Kind = "ANALYSIS", ResumePayload = payload, Scope = "p1" };

    /// <summary>
    /// FINDING 2 (Important, generic-library audit): <c>Resumable</c> used to be a required-true gate
    /// on <see cref="IOperationRegistry.RegisterWaiting"/> even though it was consulted NOWHERE
    /// else — every entry it ever produced already had it forced <c>true</c> to get past that same
    /// check, making the flag vacuous. The non-empty <see cref="OperationOptions.ResumePayload"/> this
    /// method already requires expresses "this is resumable" on its own; the field is removed.
    /// </summary>
    [Fact]
    public void RegisterWaiting_succeeds_from_only_a_resume_payload_no_separate_flag_needed()
    {
        var (registry, _) = Build();

        var id = registry.RegisterWaiting("SCAN", new OperationOptions { Kind = "ANALYSIS", ResumePayload = "session-7" });

        Assert.Equal(OperationStatus.Waiting, registry.GetAll().Single(o => o.Id == id).Status);
    }

    [Fact]
    public void RegisterWaiting_announces_a_resumable_entry_from_the_apps_checkpoint()
    {
        var (registry, _) = Build();

        var id = registry.RegisterWaiting("SCAN", Checkpoint("session-7"));

        var info = registry.GetAll().Single();
        Assert.Equal(id, info.Id);
        Assert.Equal(OperationStatus.Waiting, info.Status);
        Assert.Equal("session-7", info.ResumePayload);
    }

    [Fact]
    public void Re_announcing_the_same_checkpoint_does_not_stack_entries()
    {
        var (registry, _) = Build();

        var first = registry.RegisterWaiting("SCAN", Checkpoint("session-7"));
        var second = registry.RegisterWaiting("SCAN", Checkpoint("session-7"));

        Assert.Equal(first, second);
        Assert.Single(registry.GetAll());
    }

    [Fact]
    public void RequestResume_emits_for_the_owning_module_and_drops_the_offer()
    {
        var (registry, events) = Build();
        var id = registry.RegisterWaiting("SCAN", Checkpoint("session-7"));
        events.Clear();

        Assert.True(registry.RequestResume(id));

        var message = events.Single(e => e.Type == OperationEvents.ResumeRequested);
        var payload = IpcJson.SerializeToElement(message.Payload);
        Assert.Equal("SCAN", payload.GetProperty("module").GetString());
        Assert.Equal("session-7", payload.GetProperty("resumePayload").GetString());
        Assert.Empty(registry.GetAll());   // the resumed op registers a FRESH operation when it restarts
    }

    [Fact]
    public void A_waiting_entry_registered_via_RegisterWaiting_is_not_prunable_history()
    {
        var bus = new EventBus();
        var registry = new OperationRegistry(bus, new OperationRegistryOptions { MaxHistory = 1 });
        var id = registry.RegisterWaiting("SCAN", Checkpoint("session-7"));
        for (var i = 0; i < 5; i++) registry.Start("SCAN", new OperationOptions { Kind = "X" }).Complete();

        Assert.Contains(registry.GetAll(), o => o.Id == id);   // a pending resume OFFER, not history
    }

    /// <summary>
    /// ALSO IN THIS BATCH (whole-branch review): a regression guard distinct from
    /// <see cref="A_waiting_entry_registered_via_RegisterWaiting_is_not_prunable_history"/> above —
    /// that one covers the AUTOMATIC <see cref="OperationRegistryOptions.MaxHistory"/> eviction path,
    /// this one covers the explicit <see cref="IOperationRegistry.ClearFinished"/> call a
    /// client-triggered <c>CLEAR_FINISHED</c> route drives. It is correct TODAY only structurally —
    /// <c>ClearFinished</c> walks <c>_finishedOrder</c>, and only <c>Finish</c> (never
    /// <c>RegisterWaiting</c>) ever writes to it — so nothing previously proved it, and a future
    /// rewrite that instead filtered <c>_entries</c> by terminal STATUS would silently destroy a
    /// pending offer with this suite still green.
    /// </summary>
    [Fact]
    public void ClearFinished_does_not_evict_a_pending_resume_offer()
    {
        var (registry, _) = Build();
        var offerId = registry.RegisterWaiting("SCAN", Checkpoint("session-7"));
        registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" }).Complete(); // ordinary finished history

        registry.ClearFinished();

        Assert.Contains(registry.GetAll(), o => o.Id == offerId);
    }

    [Fact]
    public void RegisterWaiting_rejects_a_missing_resume_payload_naming_ResumePayload()
    {
        var (registry, _) = Build();

        var ex = Assert.Throws<ArgumentException>(() =>
            registry.RegisterWaiting("SCAN", Checkpoint("session-7") with { ResumePayload = null }));

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
    /// The BOTH-conditions contract: a Running operation's id is known to the registry but is not
    /// Waiting, so RequestResume must refuse it exactly like an unknown id — never resume (or remove)
    /// work that is still actively running.
    /// </summary>
    [Fact]
    public void RequestResume_returns_false_for_a_running_operation()
    {
        var (registry, events) = Build();
        var running = registry.Start("SCAN", new OperationOptions { Kind = "X" });
        events.Clear();

        Assert.False(registry.RequestResume(running.Id));
        Assert.Empty(events);
        Assert.Equal(OperationStatus.Running, registry.GetAll().Single().Status);
    }

    // --- The live-handle / no-live-handle asymmetry (§5A.4, D23 amendment) ----------------------
    //
    // OperationStatus carries only ONE waiting value now — the former Paused/Interrupted pair
    // collapsed into it, because every transition in this registry already treated them as one band.
    // RequestResume's drop-vs-keep decision therefore keys on ResumePayload, not on a second status:
    // non-null means no live handle (RegisterWaiting's checkpoint, or one the app itself attached at
    // Start()), so the entry is removed; null means an ordinary IOperation.Wait() — left in place for
    // the app's own Resume() to flip.

    /// <summary>
    /// §5A.4's rule: RequestResume accepts an entry reached via <see cref="IOperation.Wait"/> (no
    /// <see cref="OperationOptions.ResumePayload"/>), but the app calls <see cref="IOperation.Resume"/>
    /// on its own handle once it has ACTUALLY resumed — the client ASKING is not the state changing.
    /// So unlike the checkpoint case below, the entry must stay exactly as it was (still Waiting,
    /// still present) after a successful RequestResume.
    /// </summary>
    [Fact]
    public void RequestResume_on_an_entry_reached_via_Wait_leaves_it_in_place_for_the_app_to_flip_via_Resume()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Wait("dns");
        events.Clear();

        Assert.True(registry.RequestResume(operation.Id));

        var message = events.Single(e => e.Type == OperationEvents.ResumeRequested);
        var payload = IpcJson.SerializeToElement(message.Payload);
        // status is kept on the payload so a handler can still branch, but it is always "waiting" now
        // — there is no second value left to distinguish; ResumePayload (null here) is the actual
        // signal a handler reads to tell the two cases apart.
        Assert.Equal("waiting", payload.GetProperty("status").GetString());

        // Left IN PLACE, still Waiting — the defining half of the asymmetry.
        var info = registry.GetAll().Single(o => o.Id == operation.Id);
        Assert.Equal(OperationStatus.Waiting, info.Status);

        // The app's own handle can still flip it — proves the entry is genuinely untouched, not a
        // look-alike replacement.
        operation.Resume();
        Assert.Equal(OperationStatus.Running, registry.GetAll().Single(o => o.Id == operation.Id).Status);
    }

    /// <summary>
    /// The other half of the asymmetry: an entry with a non-null <see cref="OperationOptions.ResumePayload"/>
    /// (registered via <see cref="IOperationRegistry.RegisterWaiting"/>'s checkpoint) is still REMOVED
    /// by RequestResume (there is no live handle to flip — the body died with the process). The payload
    /// still carries `status` (always "waiting" now) so a handler can branch on it, but the actual
    /// intrinsic difference — no live body — is what `resumePayload` being non-null already told it.
    /// </summary>
    [Fact]
    public void RequestResume_on_a_checkpoint_entry_still_removes_it_and_the_payload_names_the_status()
    {
        var (registry, events) = Build();
        var id = registry.RegisterWaiting("SCAN", Checkpoint("session-7"));
        events.Clear();

        Assert.True(registry.RequestResume(id));

        var message = events.Single(e => e.Type == OperationEvents.ResumeRequested);
        var payload = IpcJson.SerializeToElement(message.Payload);
        Assert.Equal("waiting", payload.GetProperty("status").GetString());
        Assert.Equal("session-7", payload.GetProperty("resumePayload").GetString());
        Assert.Empty(registry.GetAll());   // gone — the resumed op registers a FRESH one when it restarts
    }

    /// <summary>
    /// FINDING 4 (Important, generic-library audit): dropping a no-live-handle entry used to leave no
    /// wire trace at all — a client's mirror of it could only stay correct via a hand-written
    /// optimistic local prune, one of the two the audit calls out as no-longer-needed once removals
    /// are authoritative. <see cref="OperationEvents.Removed"/> now fires for this drop too.
    /// </summary>
    [Fact]
    public void RequestResume_on_a_checkpoint_entry_also_emits_OPERATION_REMOVED()
    {
        var (registry, events) = Build();
        var id = registry.RegisterWaiting("SCAN", Checkpoint("session-7"));
        events.Clear();

        Assert.True(registry.RequestResume(id));

        var removed = events.Single(e => e.Type == OperationEvents.Removed);
        var ids = IpcJson.SerializeToElement(removed.Payload).GetProperty("operationIds")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal([id], ids);
    }

    /// <summary>The Wait() case is left in place (§5A.4) — RequestResume there must NOT emit a removal.</summary>
    [Fact]
    public void RequestResume_on_an_entry_reached_via_Wait_does_not_emit_OPERATION_REMOVED()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Wait("dns");
        events.Clear();

        Assert.True(registry.RequestResume(operation.Id));

        Assert.DoesNotContain(events, e => e.Type == OperationEvents.Removed);
    }

    /// <summary>
    /// The hole this closes: <c>RequestResume</c> used to key its drop-vs-keep decision on
    /// <see cref="OperationOptions.ResumePayload"/> being non-null rather than on how the entry reached
    /// <see cref="OperationStatus.Waiting"/> — but that field is APP-controlled data, not a signal the
    /// kit owns. An app that attaches its own <see cref="OperationOptions.ResumePayload"/> at
    /// <see cref="IOperationRegistry.Start"/> time (not through <see cref="IOperationRegistry.RegisterWaiting"/>
    /// at all) and then calls <see cref="IOperation.Wait"/> has a genuinely LIVE handle — the body is
    /// parked, not dead — so it must be treated exactly like any other live-<c>Wait()</c> entry: LEFT IN
    /// PLACE, with the handle's own <see cref="IOperation.Resume"/> still able to flip it back to
    /// <see cref="OperationStatus.Running"/>. The registry now keys this on internal provenance
    /// (<c>Entry.Reconstructed</c>, set only by <see cref="IOperationRegistry.RegisterWaiting"/>) instead
    /// of the payload, so this combination is no longer ambiguous.
    /// </summary>
    [Fact]
    public void RequestResume_on_a_live_handle_leaves_it_in_place_even_when_its_own_ResumePayload_was_set_at_Start()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", ResumePayload = "checkpoint-attached-at-start" });
        operation.Wait("dns");
        events.Clear();

        Assert.True(registry.RequestResume(operation.Id));

        // Left IN PLACE, still Waiting — same shape as the ordinary live-Wait() case, not dropped.
        var info = registry.GetAll().Single(o => o.Id == operation.Id);
        Assert.Equal(OperationStatus.Waiting, info.Status);
        Assert.DoesNotContain(events, e => e.Type == OperationEvents.Removed);

        // The live handle still works — proves the entry is genuinely untouched, not a look-alike
        // replacement, and that its CancellationTokenSource was never disposed out from under it.
        operation.Resume();
        Assert.Equal(OperationStatus.Running, registry.GetAll().Single(o => o.Id == operation.Id).Status);
    }
}
