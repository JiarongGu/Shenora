using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

public class BaseFacadeTests
{
    private sealed class EchoFacade() : BaseFacade
    {
        public override string ModuleName => "ECHO";

        protected override Task<object?> RouteMessageAsync(IpcRequest request) => request.Type switch
        {
            "PING" => Task.FromResult<object?>("pong"),
            "NONE" => Task.FromResult<object?>(null),
            "FAIL" => throw new OperationException("ECHO_FAILED", "reason", "test"),
            "BOOM" => throw new InvalidOperationException("secret detail"),
            _ => throw new OperationException(IpcErrorCodes.NoHandler),
        };
    }

    private static IpcRequest Request(string type) => new() { Module = "ECHO", Type = type };

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
