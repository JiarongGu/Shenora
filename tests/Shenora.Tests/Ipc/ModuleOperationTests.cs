using Shenora.Core;
using Shenora.Ipc;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Ipc;

public class ModuleOperationTests
{
    private sealed class WorkFacade(IEventBus bus, IOperationRegistry registry, Func<IOperation, CancellationToken, Task> work)
        : BaseFacade(null, bus, registry)
    {
        public override string ModuleName => "WORK";

        protected override Task<object?> RouteMessageAsync(
            IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
            => Task.FromResult<object?>(new { operationId = context.Run(new OperationOptions { Kind = "BUILD" }, work) });
    }

    private static (WorkFacade Facade, OperationRegistry Registry) Build(Func<IOperation, CancellationToken, Task> work)
    {
        var bus = new EventBus();
        var registry = new OperationRegistry(bus, new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero });
        return (new WorkFacade(bus, registry, work), registry);
    }

    private static async Task<OperationInfo> WaitForTerminalAsync(OperationRegistry registry, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));   // BOUNDED — never bare
        while (!timeout.IsCancellationRequested)
        {
            var info = registry.GetAll().SingleOrDefault(o => o.Id == id);
            if (info is not null && info.Status != OperationStatus.Running) return info;
            await Task.Delay(10, timeout.Token);
        }
        throw new TimeoutException($"operation {id} never reached a terminal state");
    }

    [Fact]
    public async Task Run_returns_immediately_and_completes_in_the_background()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var (facade, registry) = Build(async (op, ct) => { started.SetResult(); await release.Task; op.Report(90); });

        var response = await facade.HandleMessageAsync(IpcRequests.Create("WORK", "BUILD"));
        var id = IpcJson.SerializeToElement(response.Data).GetProperty("operationId").GetString()!;

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationStatus.Running, registry.GetAll().Single(o => o.Id == id).Status);   // the route did NOT wait
        release.SetResult();
        Assert.Equal(OperationStatus.Completed, (await WaitForTerminalAsync(registry, id)).Status);
    }

    [Fact]
    public async Task A_running_operation_outlives_the_requests_cancellation_token()
    {
        // The trap this closes: capturing the REQUEST token kills long work the moment the page
        // navigates. The operation gets its OWN token; the request's is not linked.
        var release = new TaskCompletionSource();
        var (facade, registry) = Build(async (op, ct) => await release.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        using var requestLifetime = new CancellationTokenSource();

        var response = await facade.HandleMessageAsync(IpcRequests.Create("WORK", "BUILD"), requestLifetime.Token);
        var id = IpcJson.SerializeToElement(response.Data).GetProperty("operationId").GetString()!;
        await requestLifetime.CancelAsync();

        Assert.Equal(OperationStatus.Running, registry.GetAll().Single(o => o.Id == id).Status);
        release.SetResult();
        Assert.Equal(OperationStatus.Completed, (await WaitForTerminalAsync(registry, id)).Status);
    }

    [Fact]
    public async Task A_cancelled_body_finishes_as_cancelled_not_as_a_fault()
    {
        var (facade, registry) = Build(async (op, ct) => await Task.Delay(Timeout.Infinite, ct));
        var response = await facade.HandleMessageAsync(IpcRequests.Create("WORK", "BUILD"));
        var id = IpcJson.SerializeToElement(response.Data).GetProperty("operationId").GetString()!;

        Assert.True(registry.Cancel(id));

        Assert.Equal(OperationStatus.Cancelled, (await WaitForTerminalAsync(registry, id)).Status);
    }

    [Fact]
    public async Task An_expected_failure_keeps_the_apps_own_words()
    {
        var (facade, registry) = Build((op, ct) => throw new OperationException("BUILD_REJECTED", "step", "link"));
        var response = await facade.HandleMessageAsync(IpcRequests.Create("WORK", "BUILD"));
        var id = IpcJson.SerializeToElement(response.Data).GetProperty("operationId").GetString()!;

        var info = await WaitForTerminalAsync(registry, id);

        Assert.Equal(OperationStatus.Failed, info.Status);
        Assert.Equal("BUILD_REJECTED", info.Error!.Code);
        Assert.Equal("link", info.Error.Parameters!["step"]);
    }

    [Fact]
    public async Task Custom_events_work_with_no_operations_registered()
    {
        // The context is the MODULE's context, not an operations entry point: Publish is the primary,
        // always-available channel and must not acquire a dependency on the registry. A module that
        // only ever emits its own vocabulary is a first-class citizen.
        var bus = new EventBus();
        var seen = new List<EventMessage>();
        bus.SubscribeToAll(m => { seen.Add(m); return Task.CompletedTask; });
        var facade = new PublishOnlyFacade(bus);

        var response = await facade.HandleMessageAsync(IpcRequests.Create("WORK", "ANY"));

        Assert.True(response.Success);
        Assert.Equal("ITEM_IMPORTED", Assert.Single(seen).Type);
    }

    private sealed class PublishOnlyFacade(IEventBus bus) : BaseFacade(null, bus)   // no registry at all
    {
        public override string ModuleName => "WORK";

        protected override Task<object?> RouteMessageAsync(
            IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
        {
            context.Publish("ITEM_IMPORTED", new { item = "a.txt" });
            return Task.FromResult<object?>(null);
        }
    }

    [Fact]
    public async Task An_unexpected_failure_never_leaks_its_message()
    {
        var (facade, registry) = Build((op, ct) => throw new InvalidOperationException("connection string secret"));
        var response = await facade.HandleMessageAsync(IpcRequests.Create("WORK", "BUILD"));
        var id = IpcJson.SerializeToElement(response.Data).GetProperty("operationId").GetString()!;

        var info = await WaitForTerminalAsync(registry, id);

        Assert.Equal(IpcErrorCodes.UnknownError, info.Error!.Code);
        Assert.Equal(nameof(InvalidOperationException), info.Error.Parameters!["exceptionType"]);
        Assert.DoesNotContain("secret", IpcJson.Serialize(info));
    }

    private sealed class NoRegistryFacade(IEventBus bus, bool useRun) : BaseFacade(null, bus)   // no registry supplied
    {
        public override string ModuleName => "WORK";

        protected override Task<object?> RouteMessageAsync(
            IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
        {
            if (useRun)
                context.Run(new OperationOptions { Kind = "BUILD" }, (op, ct) => Task.CompletedTask);
            else
                context.Start(new OperationOptions { Kind = "BUILD" });
            return Task.FromResult<object?>(null);
        }
    }

    [Fact]
    public async Task Start_without_a_registry_fails_loudly_and_names_the_fix()
    {
        // Same shape as ModuleContextTests.Publish_without_a_bus_fails_loudly_and_names_the_fix: the
        // dispatch boundary still never throws (a facade-level composition mistake is not a wire
        // fault), so assert on the mapped error shape — the fix-naming text itself is host-log-only,
        // same as every other unexpected exception.
        var response = await new NoRegistryFacade(new EventBus(), useRun: false)
            .HandleMessageAsync(IpcRequests.Create("WORK", "BUILD"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.UnknownError, response.Error!.Code);
        Assert.Equal(nameof(InvalidOperationException), response.Error.Parameters!["exceptionType"]);
    }

    [Fact]
    public async Task Run_without_a_registry_fails_loudly_and_names_the_fix()
    {
        var response = await new NoRegistryFacade(new EventBus(), useRun: true)
            .HandleMessageAsync(IpcRequests.Create("WORK", "BUILD"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.UnknownError, response.Error!.Code);
        Assert.Equal(nameof(InvalidOperationException), response.Error.Parameters!["exceptionType"]);
    }
}
