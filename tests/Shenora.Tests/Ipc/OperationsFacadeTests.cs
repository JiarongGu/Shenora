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
    /// Carried finding (routed from the Task 2 review): every operation gets a CTS regardless of
    /// <c>Cancellable</c> — what that flag actually gates is whether <c>Cancel()</c> is allowed to
    /// signal it. Cancelling a non-cancellable operation used to flip the status to Cancelled while
    /// the body kept running to completion — the UI showed "cancelled" for work that was still
    /// going, and the body's own later Complete() no-op'd because the entry was already terminal.
    /// The honest CANCEL route answers <c>{ cancelled: false }</c> and leaves the operation exactly
    /// as it was.
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
    /// FINDING 1 (Critical, generic-library audit): the route used to read NO payload at all, even
    /// though <c>LIST</c> already reads both <c>module</c>/<c>scope</c> keys — so a scope-filtered
    /// client store's <c>CLEAR_FINISHED</c> silently cleared every OTHER scope's finished history
    /// too. The route now reads the SAME two payload keys <c>LIST</c> reads.
    /// </summary>
    [Fact]
    public async Task CLEAR_FINISHED_reads_the_scope_payload_and_leaves_other_scopes_finished_history_alone()
    {
        var (facade, registry) = Build();
        var prodDone = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "prod" });
        prodDone.Complete();
        var devDone = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH", Scope = "dev" });
        devDone.Complete();

        var response = await facade.HandleMessageAsync(
            IpcRequests.Create("OPERATIONS", "CLEAR_FINISHED", payload: new { scope = "prod" }));

        Assert.True(response.Success);
        var remaining = registry.GetAll();
        Assert.DoesNotContain(remaining, o => o.Id == prodDone.Id);
        Assert.Contains(remaining, o => o.Id == devDone.Id);
    }

    [Fact]
    public async Task RESUME_forwards_to_the_registry_and_answers_requested_true()
    {
        var (facade, registry) = Build();
        var id = registry.RegisterWaiting("SCAN",
            new OperationOptions { Kind = "ANALYSIS", ResumePayload = "session-7" });

        var response = await facade.HandleMessageAsync(
            IpcRequests.Create("OPERATIONS", "RESUME", payload: new { operationId = id }));

        Assert.True(response.Success);
        Assert.True(IpcJson.SerializeToElement(response.Data).GetProperty("requested").GetBoolean());
        Assert.Empty(registry.GetAll());   // the offer is gone; the resumed op registers a fresh one
    }

    [Fact]
    public async Task RESUME_answers_requested_false_for_an_unknown_operation_id()
    {
        var (facade, _) = Build();

        var response = await facade.HandleMessageAsync(
            IpcRequests.Create("OPERATIONS", "RESUME", payload: new { operationId = "whatever" }));

        Assert.True(response.Success);   // the REQUEST still succeeds — only the resume itself is honestly false
        Assert.False(IpcJson.SerializeToElement(response.Data).GetProperty("requested").GetBoolean());
    }

    /// <summary>DISMISS mirrors CANCEL's shape (§5A.3, D23 amendment): `{ operationId }` → `{ dismissed }`.</summary>
    [Fact]
    public async Task DISMISS_dismisses_a_waiting_operation_by_id()
    {
        var (facade, registry) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Wait("dns");

        var response = await facade.HandleMessageAsync(
            IpcRequests.Create("OPERATIONS", "DISMISS", payload: new { operationId = operation.Id }));

        Assert.True(response.Success);
        Assert.True(IpcJson.SerializeToElement(response.Data).GetProperty("dismissed").GetBoolean());
        Assert.Equal(OperationStatus.Cancelled, registry.GetAll().Single().Status);
    }

    /// <summary>
    /// The honest-refusal shape, same as CANCEL_answers_false_...: the REQUEST still succeeds; only
    /// the dismiss itself is honestly false, because Dismiss refuses Running on purpose (that is
    /// CANCEL's job, permission-checked against Cancellable — see IOperationRegistry.Dismiss's doc).
    /// </summary>
    [Fact]
    public async Task DISMISS_answers_false_and_leaves_a_running_operation_running()
    {
        var (facade, registry) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        var response = await facade.HandleMessageAsync(
            IpcRequests.Create("OPERATIONS", "DISMISS", payload: new { operationId = operation.Id }));

        Assert.True(response.Success);
        Assert.False(IpcJson.SerializeToElement(response.Data).GetProperty("dismissed").GetBoolean());
        Assert.Equal(OperationStatus.Running, registry.GetAll().Single().Status);
    }

    /// <summary>WAIT mirrors RESUME's shape (generic-library audit finding 3, renamed from PAUSE): `{ operationId }` → `{ requested }`.</summary>
    [Fact]
    public async Task WAIT_forwards_to_the_registry_and_answers_requested_true_leaving_the_operation_running()
    {
        var (facade, registry) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        var response = await facade.HandleMessageAsync(
            IpcRequests.Create("OPERATIONS", "WAIT", payload: new { operationId = operation.Id }));

        Assert.True(response.Success);
        Assert.True(IpcJson.SerializeToElement(response.Data).GetProperty("requested").GetBoolean());
        Assert.Equal(OperationStatus.Running, registry.GetAll().Single().Status);   // asking is not acting
    }

    [Fact]
    public async Task WAIT_answers_requested_false_for_an_operation_that_is_not_running()
    {
        var (facade, registry) = Build();
        var operation = registry.Start("DEPLOY", new OperationOptions { Kind = "PUSH" });
        operation.Wait("dns");

        var response = await facade.HandleMessageAsync(
            IpcRequests.Create("OPERATIONS", "WAIT", payload: new { operationId = operation.Id }));

        Assert.True(response.Success);
        Assert.False(IpcJson.SerializeToElement(response.Data).GetProperty("requested").GetBoolean());
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

    /// <summary>
    /// FINDING 2 (Important, whole-branch review): every <see cref="OperationRegistryOptions"/>
    /// property is <c>{ get; init; }</c>, so the OLD <c>AddShenoraOperations(Action&lt;
    /// OperationRegistryOptions&gt;? configure)</c> signature made <c>configure</c> a compile error
    /// (CS8852) for the one thing it existed for — <c>o => o.ModuleName = "MY_OPS"</c> cannot assign
    /// to an <c>init</c> property from outside an object initializer. The fix takes the options
    /// RECORD directly (matching every other options type in the kit — <c>WebViewIpcBridgeOptions</c>,
    /// <c>NotificationPumpOptions</c>), so <c>init</c> stays the kit's one immutability convention
    /// instead of this being the one mutable options record. Proves the rename reaches BOTH halves:
    /// the facade answers on it, and the registry publishes under it.
    /// </summary>
    [Fact]
    public async Task AddShenoraOperations_with_options_configures_a_renamed_module_end_to_end()
    {
        var services = new ServiceCollection();
        var bus = new EventBus();
        services.AddSingleton<IEventBus>(bus);
        services.AddShenoraOperations(new OperationRegistryOptions { ModuleName = "MY_OPS", ProgressInterval = TimeSpan.Zero });
        using var provider = services.BuildServiceProvider();

        var facade = provider.GetServices<IModuleFacade>().OfType<OperationsFacade>().Single();
        Assert.Equal("MY_OPS", facade.ModuleName);

        var events = new List<EventMessage>();
        bus.SubscribeToAll(m => { events.Add(m); return Task.CompletedTask; });
        provider.GetRequiredService<IOperationRegistry>().Start("DEPLOY", new OperationOptions { Kind = "PUSH" });

        Assert.Equal("MY_OPS", Assert.Single(events).Module);

        // The renamed module is reachable through the facade too, under its own name.
        var response = await facade.HandleMessageAsync(IpcRequests.Create("MY_OPS", "LIST"));
        Assert.True(response.Success);
    }
}
