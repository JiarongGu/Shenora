using Shenora;
using Shenora.Tests.TestSupport;
using Shenora.Core.Events;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Ipc;

public class ModuleOperationTests
{
    private sealed class WorkFacade(IEventBus bus, IOperationRegistry registry, Func<IOperation, CancellationToken, Task> work)
        : ModuleBase(null, bus, registry)
    {
        public override string ModuleName => "WORK";

        protected override Task<object?> RouteMessageAsync(
            IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
            // Cancellable: true — Task 5's honest CANCEL contract refuses (and changes nothing) for an
            // operation that didn't opt in, and this facade's own cancel test needs the cancel to
            // actually take effect. The Cancellable: false path — a body that ends in
            // OperationCanceledException on its own, with nobody having asked for a CLIENT cancel — is
            // covered directly against the registry below
            // (Run_with_a_non_cancellable_body_that_throws_OperationCanceledException_still_ends_cancelled),
            // not through this facade, because it needs the DEFAULT Cancellable value this facade
            // deliberately overrides.
            => Task.FromResult<object?>(new { operationId = context.Run(new OperationOptions { Kind = "BUILD", Cancellable = true }, work) });
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
        var (facade, registry) = Build(async (op, ct) => { started.SetResult(); await release.Task; op.Report(new OperationProgress(90)); });

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

    /// <summary>
    /// FINDING 1 (Critical, whole-branch review): <c>Run</c>'s catch used to call
    /// <c>operation.Cancel()</c>, which delegated straight to the CLIENT-permission-checked
    /// <c>IOperationRegistry.Cancel(id)</c> — refused for a non-<c>Cancellable</c> operation and
    /// returned BEFORE <c>Finish</c>, so the entry was stranded <c>Running</c> forever: no terminal
    /// transition, no <c>OPERATION_UPDATED</c>, the CTS never disposed, and — proven below — never
    /// evictable by <c>ClearFinished</c> either, because it never entered <c>_finishedOrder</c>.
    /// Reachable on the DEFAULT option value (<see cref="OperationOptions.Cancellable"/> defaults to
    /// false), and <see cref="TaskCanceledException"/> derives from
    /// <see cref="OperationCanceledException"/> — an <c>HttpClient</c> timeout, a linked shutdown
    /// token, or a plain <c>ct.ThrowIfCancellationRequested()"</c> in the body all land here. Called
    /// directly against the registry (not through <see cref="WorkFacade"/>) because that facade
    /// hardcodes <c>Cancellable: true</c>.
    /// </summary>
    [Fact]
    public async Task Run_with_a_non_cancellable_body_that_throws_OperationCanceledException_still_ends_cancelled()
    {
        var bus = new EventBus();
        var registry = new OperationRegistry(bus, new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero });

        // Cancellable defaults to false — nobody asked the CLIENT-facing Cancel(id) for anything;
        // the body itself simply ended in cancellation (e.g. ct.ThrowIfCancellationRequested()).
        var id = registry.Run("WORK", new OperationOptions { Kind = "BUILD" },
            (op, ct) => throw new OperationCanceledException());

        var info = await WaitForTerminalAsync(registry, id);
        Assert.Equal(OperationStatus.Cancelled, info.Status);

        // The second half of the leak this finding describes: a stranded Running entry never enters
        // _finishedOrder, so ClearFinished can never evict it either. Prove the entry is now ordinary
        // finished history, not a permanent leak.
        registry.ClearFinished();
        Assert.Empty(registry.GetAll());
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

    private sealed class PublishOnlyFacade(IEventBus bus) : ModuleBase(null, bus)   // no registry at all
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

    private sealed class NoRegistryFacade(IEventBus bus, bool useRun) : ModuleBase(null, bus)   // no registry supplied
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
