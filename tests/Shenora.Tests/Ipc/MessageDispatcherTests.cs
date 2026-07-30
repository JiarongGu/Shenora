using System.Text.Json;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

public class MessageDispatcherTests
{
    private sealed record Item(string Name, int Count);

    private static IpcRequest Request(string module, string type, string? scope = null, object? payload = null) =>
        new()
        {
            Module = module,
            Type = type,
            Scope = scope,
            Payload = payload is null ? null : IpcJson.SerializeToElement(payload),
        };

    [Fact]
    public async Task MapRoute_handles_and_wraps_success()
    {
        var dispatcher = new MessageDispatcher().MapRoute("APP", "PING", _ => "pong");

        var response = await dispatcher.DispatchAsync(Request("APP", "PING"));

        Assert.True(response.Success);
        Assert.Equal("pong", response.Data);
        Assert.Equal(IpcCategories.Ipc, response.Category);
    }

    [Fact]
    public async Task Response_echoes_the_request_id()
    {
        var dispatcher = new MessageDispatcher().MapRoute("APP", "PING", _ => null);
        var request = Request("APP", "PING");

        var response = await dispatcher.DispatchAsync(request);

        Assert.Equal(request.Id, response.Id);
    }

    [Fact]
    public async Task Unhandled_request_returns_structured_no_handler_error()
    {
        var dispatcher = new MessageDispatcher();

        var response = await dispatcher.DispatchAsync(Request("APP", "PING"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.NoHandler, response.Error!.Code);
        Assert.Equal("APP", response.Error.Parameters!["module"]);
        Assert.Equal("PING", response.Error.Parameters["type"]);
    }

    [Fact]
    public async Task Routing_is_case_insensitive()
    {
        var dispatcher = new MessageDispatcher().MapRoute("APP", "PING", _ => "pong");

        var response = await dispatcher.DispatchAsync(Request("app", "ping"));

        Assert.True(response.Success);
    }

    [Fact]
    public async Task Middlewares_run_in_registration_order()
    {
        var order = new List<string>();
        var dispatcher = new MessageDispatcher()
            .Use(async (_, next) => { order.Add("first"); return await next(); })
            .Use(async (_, next) => { order.Add("second"); return await next(); })
            .MapRoute("APP", "PING", _ => { order.Add("route"); return null; });

        await dispatcher.DispatchAsync(Request("APP", "PING"));

        Assert.Equal(["first", "second", "route"], order);
    }

    [Fact]
    public async Task Middleware_registered_after_a_dispatch_takes_effect()
    {
        var dispatcher = new MessageDispatcher();
        Assert.False((await dispatcher.DispatchAsync(Request("APP", "PING"))).Success);

        dispatcher.MapRoute("APP", "PING", _ => "pong"); // lazy pipeline rebuild

        Assert.True((await dispatcher.DispatchAsync(Request("APP", "PING"))).Success);
    }

    [Fact]
    public async Task UseModule_null_result_falls_through()
    {
        var dispatcher = new MessageDispatcher()
            .UseModule("APP", _ => Task.FromResult<IpcResponse?>(null))
            .MapRoute("APP", "PING", _ => "pong");

        var response = await dispatcher.DispatchAsync(Request("APP", "PING"));

        Assert.True(response.Success);
    }

    [Fact]
    public async Task Error_handler_maps_operation_exceptions_to_structured_errors()
    {
        var dispatcher = new MessageDispatcher()
            .UseErrorHandler()
            .MapRoute("APP", "FAIL", _ => throw new OperationException("APP_FAILED", "name", "x"));

        var response = await dispatcher.DispatchAsync(Request("APP", "FAIL"));

        Assert.False(response.Success);
        Assert.Equal("APP_FAILED", response.Error!.Code);
        Assert.Equal("x", response.Error.Parameters!["name"]);
    }

    [Fact]
    public async Task Error_handler_never_leaks_raw_exception_details()
    {
        var dispatcher = new MessageDispatcher()
            .UseErrorHandler()
            .MapRoute("APP", "BOOM", _ => throw new InvalidOperationException("secret detail"));

        var response = await dispatcher.DispatchAsync(Request("APP", "BOOM"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.UnknownError, response.Error!.Code);
        Assert.Equal(nameof(InvalidOperationException), response.Error.Parameters!["exceptionType"]);
        Assert.DoesNotContain("secret detail", IpcJson.Serialize(response));
    }

    [Fact]
    public async Task Dispatch_without_an_error_handler_still_never_throws()
    {
        var dispatcher = new MessageDispatcher()
            .MapRoute("APP", "FAIL", _ => throw new OperationException("APP_FAILED"))
            .MapRoute("APP", "BOOM", _ => throw new InvalidOperationException("secret detail"));

        var failed = await dispatcher.DispatchAsync(Request("APP", "FAIL"));
        var crashed = await dispatcher.DispatchAsync(Request("APP", "BOOM"));

        Assert.Equal("APP_FAILED", failed.Error!.Code);
        Assert.Equal(IpcErrorCodes.UnknownError, crashed.Error!.Code);
        Assert.DoesNotContain("secret detail", IpcJson.Serialize(crashed));
    }

    [Fact]
    public async Task SendAsync_builds_the_request_from_the_arguments()
    {
        IpcRequest? seen = null;
        var dispatcher = new MessageDispatcher()
            .MapRoute("APP", "PING", request => { seen = request; return null; });

        await dispatcher.SendAsync("APP", "PING", scope: "s1", payload: new { name = "x" });

        Assert.NotNull(seen);
        Assert.Equal("s1", seen!.Scope);
        Assert.Equal("x", seen.Payload!.Value.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(seen.Id));
    }

    [Fact]
    public async Task SendAsync_typed_converts_all_three_data_shapes()
    {
        var expected = new Item("y", 7);
        var dispatcher = new MessageDispatcher().MapModule("APP", routes => routes
            .Route("TYPED", _ => expected)                                   // already the target type
            .Route("ANON", _ => new { name = "y", count = 7 })               // live object → JSON round-trip
            .Route("ELEMENT", _ => IpcJson.SerializeToElement(expected)));   // JsonElement

        Assert.Equal(expected, await dispatcher.SendAsync<Item>("APP", "TYPED"));
        Assert.Equal(expected, await dispatcher.SendAsync<Item>("APP", "ANON"));
        Assert.Equal(expected, await dispatcher.SendAsync<Item>("APP", "ELEMENT"));
    }

    [Fact]
    public async Task SendAsync_typed_returns_default_for_null_data()
    {
        var dispatcher = new MessageDispatcher().MapRoute("APP", "NONE", _ => null);

        Assert.Null(await dispatcher.SendAsync<Item>("APP", "NONE"));
    }

    [Fact]
    public async Task SendAsync_typed_rethrows_the_structured_error()
    {
        var dispatcher = new MessageDispatcher()
            .UseErrorHandler()
            .MapRoute("APP", "FAIL", _ => throw new OperationException("APP_FAILED", "name", "x"));

        var ex = await Assert.ThrowsAsync<OperationException>(
            () => dispatcher.SendAsync<Item>("APP", "FAIL"));

        Assert.Equal("APP_FAILED", ex.Code);
        Assert.Equal("x", ex.Parameters!["name"]);
    }

    [Fact]
    public async Task MapModule_route_builder_maps_sync_and_async_routes()
    {
        var dispatcher = new MessageDispatcher().MapModule("APP", routes => routes
            .Route("SYNC", _ => "s")
            .RouteAsync("ASYNC", _ => Task.FromResult<object?>("a")));

        Assert.Equal("s", (await dispatcher.DispatchAsync(Request("APP", "SYNC"))).Data);
        Assert.Equal("a", (await dispatcher.DispatchAsync(Request("APP", "ASYNC"))).Data);
    }

    [Fact]
    public async Task UseLogging_passes_responses_through()
    {
        var dispatcher = new MessageDispatcher()
            .UseLogging()
            .MapRoute("APP", "PING", _ => "pong");

        var response = await dispatcher.DispatchAsync(Request("APP", "PING"));

        Assert.True(response.Success);
        Assert.Equal("pong", response.Data);
    }
}
