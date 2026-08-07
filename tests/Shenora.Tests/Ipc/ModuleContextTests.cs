using Shenora;
using Shenora.Ipc;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Ipc;

public class ModuleContextTests
{
    private sealed class PublishingFacade(IEventBus? events) : BaseFacade(null, events)
    {
        public override string ModuleName => "REPORTS";

        protected override Task<object?> RouteMessageAsync(
            IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
        {
            context.Publish("STARTED", new { step = 1 }, scope: "s1");
            return Task.FromResult<object?>(new { module = context.Module });
        }
    }

    [Fact]
    public async Task Publish_emits_under_the_facades_own_module()
    {
        var bus = new EventBus();
        var seen = new List<EventMessage>();
        bus.SubscribeToAll(m => { seen.Add(m); return Task.CompletedTask; });

        await new PublishingFacade(bus).HandleMessageAsync(IpcRequests.Create("REPORTS", "RUN"));

        var message = Assert.Single(seen);
        Assert.Equal("REPORTS", message.Module);   // NOT a literal the route typed
        Assert.Equal("STARTED", message.Type);
        Assert.Equal("s1", message.Scope);
    }

    [Fact]
    public async Task Context_module_matches_the_facade_module_name()
    {
        var response = await new PublishingFacade(new EventBus())
            .HandleMessageAsync(IpcRequests.Create("REPORTS", "RUN"));

        Assert.True(response.Success);
        Assert.Equal("REPORTS", IpcJson.SerializeToElement(response.Data).GetProperty("module").GetString());
    }

    [Fact]
    public async Task Publish_without_a_bus_fails_loudly_and_names_the_fix()
    {
        // A silent no-op here is the failure class this repo keeps paying for. The response still
        // never throws (the dispatch boundary contract), so assert on the LOG-side error shape.
        var response = await new PublishingFacade(null).HandleMessageAsync(IpcRequests.Create("REPORTS", "RUN"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.UnknownError, response.Error!.Code);
        Assert.Equal(nameof(InvalidOperationException), response.Error.Parameters!["exceptionType"]);
    }
}
