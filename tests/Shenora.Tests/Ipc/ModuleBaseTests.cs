using Shenora.Tests.TestSupport;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Ipc;

public class ModuleBaseTests
{
    private sealed class EchoFacade() : ModuleBase
    {
        public override string ModuleName => "ECHO";

        protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken) => request.Type switch
        {
            "PING" => Task.FromResult<object?>("pong"),
            "NONE" => Task.FromResult<object?>(null),
            "FAIL" => throw new ShenoraException("ECHO_FAILED", "reason", "test"),
            "BOOM" => throw new InvalidOperationException("secret detail"),
            _ => throw new ShenoraException(IpcErrorCodes.NoHandler),
        };
    }

    // An async route, where `return Done();` makes the task itself the answer, and one that forgets an await.
    private sealed class AsyncFacade() : ModuleBase
    {
        public override string ModuleName => "ASYNC";

        protected override async Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return request.Type == "DONE" ? Done() : Task.Delay(1, cancellationToken);
        }
    }

    [Fact]
    public async Task An_async_routes_Done_answers_nothing()
    {
        var response = await new AsyncFacade().HandleMessageAsync(IpcRequests.Create("ASYNC", "DONE"));

        Assert.True(response.Success);
        Assert.Null(response.Data);
    }

    [Fact]
    public async Task A_route_answering_with_a_task_it_never_awaited_is_an_error_not_the_tasks_fields()
    {
        var response = await new AsyncFacade().HandleMessageAsync(IpcRequests.Create("ASYNC", "FORGOT"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.UnknownError, response.Error!.Code);
    }

    private static IpcRequest Request(string type) => IpcRequests.Create("ECHO", type);

    [Fact]
    public async Task Wraps_route_result_in_a_success_response()
    {
        var response = await new EchoFacade().HandleMessageAsync(Request("PING"));

        Assert.True(response.Success);
        Assert.Equal("pong", response.Data);
    }

    [Fact]
    public async Task Null_route_result_is_a_success_without_data()
    {
        var response = await new EchoFacade().HandleMessageAsync(Request("NONE"));

        Assert.True(response.Success);
        Assert.Null(response.Data);
    }

    [Fact]
    public async Task Operation_exceptions_become_structured_errors()
    {
        var request = Request("FAIL");
        var response = await new EchoFacade().HandleMessageAsync(request);

        Assert.False(response.Success);
        Assert.Equal(request.Id, response.Id);
        Assert.Equal("ECHO_FAILED", response.Error!.Code);
        Assert.Equal("test", response.Error.Parameters!["reason"]);
    }

    [Fact]
    public async Task Unknown_exceptions_become_unknown_error_without_leaking_details()
    {
        var response = await new EchoFacade().HandleMessageAsync(Request("BOOM"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.UnknownError, response.Error!.Code);
        Assert.Equal(nameof(InvalidOperationException), response.Error.Parameters!["exceptionType"]);
        Assert.DoesNotContain("secret detail", IpcJson.Serialize(response));
    }

    [Fact]
    public async Task MapModule_routes_a_facade_by_its_module_name()
    {
        var dispatcher = new MessageDispatcher().MapModule(new EchoFacade());

        var handled = await dispatcher.DispatchAsync(new IpcRequest { Module = "echo", Type = "PING" });
        var other = await dispatcher.DispatchAsync(new IpcRequest { Module = "OTHER", Type = "PING" });

        Assert.True(handled.Success);
        Assert.Equal("pong", handled.Data);
        Assert.Equal(IpcErrorCodes.NoHandler, other.Error!.Code);
    }
}
