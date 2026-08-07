using Shenora;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// <see cref="IOperationRegistry.RequestWait"/> (generic-library audit finding 3, renamed from
/// <c>RequestPause</c>) — an EXACT mirror of <see cref="IOperationRegistry.RequestResume"/>, but for
/// the other direction the design had left unbuilt. The kit shipped <c>RESUME</c>/<c>DISMISS</c> as
/// client-request routes and justified having no <c>WAIT</c> route on the grounds that "pausing is
/// the host's own knowledge" — true for a host that discovers its OWN blocker, but not for the
/// equally-common shape where a human clicks Pause on visible work (a download, a sync, a backup) and
/// the host must ask the owning module to actually stop. <c>RequestWait</c> is that ask: it changes
/// nothing itself — the owner's own <see cref="IOperation.Wait"/> is what flips the status — same
/// split that already exists between <see cref="IOperationRegistry.RequestResume"/> (asks) and
/// <see cref="IOperation.Resume"/> (acts).
/// </summary>
public class OperationWaitRequestTests
{
    private static (OperationRegistry Registry, List<EventMessage> Events) Build()
    {
        var bus = new EventBus();
        var events = new List<EventMessage>();
        bus.SubscribeToAll(m => { lock (events) events.Add(m); return Task.CompletedTask; });
        return (new OperationRegistry(bus, new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero }), events);
    }

    [Fact]
    public void RequestWait_emits_for_the_owning_module_and_leaves_the_operation_running()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "prod" });
        events.Clear();

        Assert.True(registry.RequestWait(operation.Id));

        var message = events.Single(e => e.Type == OperationEvents.WaitRequested);
        Assert.Equal("prod", message.Scope);
        var payload = IpcJson.SerializeToElement(message.Payload);
        Assert.Equal(operation.Id, payload.GetProperty("operationId").GetString());
        Assert.Equal("DEPLOY", payload.GetProperty("module").GetString());
        Assert.Equal("PUSH", payload.GetProperty("kind").GetString());
        Assert.Equal("prod", payload.GetProperty("scope").GetString());

        // Unlike RequestResume's no-live-handle case, asking never changes the state by itself — the
        // owner's OWN Wait(reason) is what flips it (same split as Resume vs RequestResume).
        Assert.Equal(OperationStatus.Running, registry.GetAll().Single().Status);
    }

    [Fact]
    public void RequestWait_returns_false_for_an_unknown_id()
    {
        var (registry, events) = Build();

        Assert.False(registry.RequestWait("no-such-id"));
        Assert.Empty(events);
    }

    /// <summary>Only Running qualifies — an already-waiting or terminal entry refuses.</summary>
    [Fact]
    public void RequestWait_returns_false_for_an_operation_that_is_not_running()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Wait("dns");
        events.Clear();

        Assert.False(registry.RequestWait(operation.Id));
        Assert.Empty(events);
        Assert.Equal(OperationStatus.Waiting, registry.GetAll().Single().Status);
    }

    [Fact]
    public void RequestWait_returns_false_for_an_already_terminal_operation()
    {
        var (registry, events) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Complete();
        events.Clear();

        Assert.False(registry.RequestWait(operation.Id));
        Assert.Empty(events);
    }
}
