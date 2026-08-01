using Microsoft.Extensions.DependencyInjection;
using Shenora.Core;
using Shenora.Ipc;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Ipc;

public class OperationsFacadeTests
{
    private static (OperationsFacade Facade, OperationRegistry Registry) Build()
    {
        var registry = new OperationRegistry(new EventBus(),
            new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero });
        return (new OperationsFacade(registry), registry);
    }

    [Fact]
    public async Task LIST_answers_the_client_stores_snapshot()
    {
        var (facade, registry) = Build();
        registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "prod" });
        registry.Start("SCAN", new OperationOptions { Kind = "FILES" });

        var response = await facade.HandleMessageAsync(
            IpcRequests.Create("OPERATIONS", "LIST", payload: new { module = "DEPLOY" }));

        Assert.True(response.Success);
        var operations = Assert.IsAssignableFrom<IReadOnlyList<OperationInfo>>(response.Data);
        Assert.Equal("DEPLOY", Assert.Single(operations).Module);
    }

    [Fact]
    public async Task CANCEL_cancels_by_operation_id()
    {
        var (facade, registry) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Cancellable = true });

        var response = await facade.HandleMessageAsync(
            IpcRequests.Create("OPERATIONS", "CANCEL", payload: new { operationId = operation.Id }));

        Assert.True(response.Success);
        Assert.True(operation.CancellationToken.IsCancellationRequested);
    }

    /// <summary>
    /// Carried finding (routed from the Task 2 review): a non-cancellable operation has no CTS, so
    /// cancelling it used to flip the status to Cancelled while the body kept running to completion —
    /// the UI showed "cancelled" for work that was still going, and the body's own later Complete()
    /// no-op'd because the entry was already terminal. The honest CANCEL route answers
    /// <c>{ cancelled: false }</c> and leaves the operation exactly as it was.
    /// </summary>
    [Fact]
    public async Task CANCEL_answers_false_and_leaves_a_non_cancellable_operation_running()
    {
        var (facade, registry) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" }); // Cancellable defaults to false

        var response = await facade.HandleMessageAsync(
            IpcRequests.Create("OPERATIONS", "CANCEL", payload: new { operationId = operation.Id }));

        Assert.True(response.Success); // the REQUEST still succeeds — only the cancel itself is honestly false
        Assert.False(IpcJson.SerializeToElement(response.Data).GetProperty("cancelled").GetBoolean());
        Assert.False(operation.CancellationToken.IsCancellationRequested);
        Assert.Equal(OperationStatus.Running, registry.GetAll().Single().Status);
    }

    [Fact]
    public async Task CLEAR_FINISHED_drops_history_and_keeps_running_work()
    {
        var (facade, registry) = Build();
        var running = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" }).Complete();

        var response = await facade.HandleMessageAsync(IpcRequests.Create("OPERATIONS", "CLEAR_FINISHED"));

        Assert.True(response.Success);
        Assert.Equal(running.Id, registry.GetAll().Single().Id);
    }

    /// <summary>
    /// RESUME is deliberately not wired yet — <c>IOperationRegistry.RegisterInterrupted</c>/
    /// <c>RequestResume</c> land in the next task, and a route with nothing behind it would mean
    /// inventing that registry member early. Until then it is exactly like any other unimplemented
    /// type. This test documents that as intentional, not a gap — the next task should replace it
    /// with real RESUME coverage rather than leaving it in place unexamined.
    /// </summary>
    [Fact]
    public async Task RESUME_is_not_wired_yet_and_gets_the_frameworks_NO_HANDLER_shape()
    {
        var (facade, _) = Build();

        var response = await facade.HandleMessageAsync(
            IpcRequests.Create("OPERATIONS", "RESUME", payload: new { operationId = "whatever" }));

        Assert.Equal(IpcErrorCodes.NoHandler, response.Error!.Code);
    }

    [Fact]
    public async Task An_unknown_type_gets_the_frameworks_NO_HANDLER_shape()
    {
        var (facade, _) = Build();

        var response = await facade.HandleMessageAsync(IpcRequests.Create("OPERATIONS", "NOPE"));

        Assert.Equal(IpcErrorCodes.NoHandler, response.Error!.Code);
    }

    [Fact]
    public void AddShenoraOperations_registers_one_registry_and_maps_the_facade()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventBus, EventBus>();
        services.AddShenoraOperations();
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IOperationRegistry>(),
                    provider.GetRequiredService<IOperationRegistry>());          // singleton
        Assert.Contains(provider.GetServices<IModuleFacade>(), f => f is OperationsFacade);
    }
}
