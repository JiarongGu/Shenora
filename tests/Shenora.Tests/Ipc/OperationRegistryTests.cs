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

    [Fact]
    public void ClearFinished_removes_history_and_keeps_running_work()
    {
        var (registry, _) = Build();
        var running = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" }).Complete();

        registry.ClearFinished();

        Assert.Equal(running.Id, registry.GetAll().Single().Id);
    }
}
