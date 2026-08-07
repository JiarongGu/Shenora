using Shenora;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// <see cref="IOperationRegistry.RequestResume"/> — the ASK half of resume, and the exact mirror of
/// <see cref="IOperationRegistry.RequestWait"/> (see <c>OperationWaitRequestTests</c>). It emits and
/// changes nothing; the owning module's own <see cref="IOperation.Resume"/> is what actually restarts
/// the work.
/// <para>
/// <b>This file used to be twice this size, and the deleted half is the point (0.2.0 design pass, D1).</b>
/// The registry once also accepted crash-checkpoint entries it had never started
/// (<c>RegisterWaiting</c> + <c>OperationOptions.ResumePayload</c>), and <c>RequestResume</c> REMOVED
/// those while KEEPING live ones — so every call had to answer "does this entry still have a body?".
/// Three answers were tried and each produced a defect: a second status (<c>Interrupted</c>, which
/// turned out to have no terminal exit at all — the stranded-state bug), then <c>ResumePayload</c>
/// (APP-controlled, so it dropped genuinely live operations), then an internal provenance flag. The
/// checkpoint half is now cut — crash recovery is the app's business, and a resumed run is a fresh
/// <see cref="IOperationRegistry.Start"/> — so the question no longer exists and neither do the tests
/// that pinned each attempt at answering it.
/// </para>
/// </summary>
public class OperationResumeTests
{
    private static (OperationRegistry Registry, List<EventMessage> Events) Build()
    {
        var bus = new EventBus();
        var events = new List<EventMessage>();
        bus.SubscribeToAll(m => { lock (events) events.Add(m); return Task.CompletedTask; });
        return (new OperationRegistry(bus, new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero }), events);
    }

    private static IOperation StartWaiting(OperationRegistry registry)
    {
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "p1" });
        operation.Wait("dns");
        return operation;
    }

    [Fact]
    public void RequestResume_emits_for_the_owning_module()
    {
        var (registry, events) = Build();
        var operation = StartWaiting(registry);
        events.Clear();

        Assert.True(registry.RequestResume(operation.Id));

        var message = events.Single(e => e.Type == OperationEvents.ResumeRequested);
        var payload = IpcJson.SerializeToElement(message.Payload);
        Assert.Equal(operation.Id, payload.GetProperty("operationId").GetString());
        Assert.Equal("DEPLOY", payload.GetProperty("module").GetString());
        Assert.Equal("PUSH", payload.GetProperty("kind").GetString());
        Assert.Equal("p1", payload.GetProperty("scope").GetString());
    }

    /// <summary>
    /// The payload is now the SAME four fields <see cref="OperationEvents.WaitRequested"/> carries.
    /// It used to also carry <c>resumePayload</c> and <c>status</c> — the former is gone with the
    /// checkpoint half, and the latter carried no information even before that (it was always
    /// <c>waiting</c>, kept only so a handler could branch between the two reaches that no longer
    /// exist). Pinned so the two ask-events cannot drift apart again.
    /// </summary>
    [Fact]
    public void RequestResume_and_RequestWait_emit_the_same_payload_shape()
    {
        var (registry, events) = Build();
        var waiting = StartWaiting(registry);
        var running = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "p1" });
        events.Clear();

        Assert.True(registry.RequestResume(waiting.Id));
        Assert.True(registry.RequestWait(running.Id));

        string[] Fields(string type) =>
            IpcJson.SerializeToElement(events.Single(e => e.Type == type).Payload)
                .EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "kind", "module", "operationId", "scope" }, Fields(OperationEvents.ResumeRequested));
        Assert.Equal(Fields(OperationEvents.WaitRequested), Fields(OperationEvents.ResumeRequested));
    }

    /// <summary>
    /// The defining property after D1: RequestResume NEVER removes or mutates the entry, whoever asks.
    /// The app's own handle is what flips it, which also proves the entry is genuinely untouched
    /// rather than a look-alike replacement (its CancellationTokenSource is still live).
    /// </summary>
    [Fact]
    public void RequestResume_leaves_the_entry_in_place_for_the_app_to_flip_via_Resume()
    {
        var (registry, _) = Build();
        var operation = StartWaiting(registry);

        Assert.True(registry.RequestResume(operation.Id));

        Assert.Equal(OperationStatus.Waiting, registry.GetAll().Single(o => o.Id == operation.Id).Status);

        operation.Resume();
        Assert.Equal(OperationStatus.Running, registry.GetAll().Single(o => o.Id == operation.Id).Status);
    }

    /// <summary>
    /// The client-side counterpart of the test above, and the reason the release's only Critical
    /// happened: an optimistic local prune that deleted a row the host deliberately kept made a
    /// still-waiting operation unreachable. Nothing may leave the registry here.
    /// </summary>
    [Fact]
    public void RequestResume_never_emits_OPERATION_REMOVED()
    {
        var (registry, events) = Build();
        var operation = StartWaiting(registry);
        events.Clear();

        Assert.True(registry.RequestResume(operation.Id));

        Assert.DoesNotContain(events, e => e.Type == OperationEvents.Removed);
        Assert.Contains(registry.GetAll(), o => o.Id == operation.Id);
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
    /// Waiting, so RequestResume must refuse it exactly like an unknown id — never "resume" work that
    /// is already running.
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

    [Fact]
    public void RequestResume_returns_false_for_a_terminal_operation()
    {
        var (registry, events) = Build();
        var operation = registry.Start("SCAN", new OperationOptions { Kind = "X" });
        operation.Complete();
        events.Clear();

        Assert.False(registry.RequestResume(operation.Id));
        Assert.Empty(events);
    }

    /// <summary>
    /// A waiting operation is NOT finished history: neither the automatic
    /// <see cref="OperationRegistryOptions.MaxHistory"/> eviction nor an explicit
    /// <see cref="IOperationRegistry.ClearFinished"/> may take it away, or the user loses the row they
    /// need in order to resume or dismiss it. Correct TODAY only structurally (both paths walk
    /// <c>_finishedOrder</c>, which only <c>Finish</c> ever writes), so a future rewrite that instead
    /// filtered <c>_entries</c> by status would silently destroy it with the suite still green.
    /// </summary>
    [Fact]
    public void A_waiting_operation_is_not_prunable_or_clearable_history()
    {
        var bus = new EventBus();
        var registry = new OperationRegistry(bus, new OperationRegistryOptions { MaxHistory = 1 });
        var waiting = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        waiting.Wait("dns");

        for (var i = 0; i < 5; i++) registry.Start("SCAN", new OperationOptions { Kind = "X" }).Complete();
        registry.ClearFinished();

        Assert.Contains(registry.GetAll(), o => o.Id == waiting.Id);
    }
}
