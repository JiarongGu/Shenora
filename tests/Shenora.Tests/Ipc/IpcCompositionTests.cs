using Microsoft.Extensions.DependencyInjection;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

public class IpcCompositionTests
{
    private sealed class AlphaFacade : BaseFacade
    {
        public override string ModuleName => "ALPHA";

        protected override Task<object?> RouteMessageAsync(IpcRequest request) =>
            Task.FromResult<object?>("alpha");
    }

    private sealed class BetaFacade : BaseFacade
    {
        public override string ModuleName => "BETA";

        protected override Task<object?> RouteMessageAsync(IpcRequest request) =>
            Task.FromResult<object?>("beta");
    }

    private static IpcRequest Request(string module, string type = "ANY") =>
        new() { Module = module, Type = type };

    [Fact]
    public async Task AddMessageDispatcher_maps_every_registered_facade()
    {
        using var provider = new ServiceCollection()
            .AddModuleFacade<AlphaFacade>()
            .AddModuleFacade<BetaFacade>()
            .AddMessageDispatcher()
            .BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<IMessageDispatcher>();

        Assert.Equal("alpha", (await dispatcher.DispatchAsync(Request("ALPHA"))).Data);
        Assert.Equal("beta", (await dispatcher.DispatchAsync(Request("BETA"))).Data);
    }

    [Fact]
    public async Task Configure_middleware_runs_before_the_registered_facades()
    {
        var order = new List<string>();
        using var provider = new ServiceCollection()
            .AddModuleFacade<AlphaFacade>()
            .AddMessageDispatcher((_, dispatcher) => dispatcher.Use(async (_, next) =>
            {
                order.Add("app-middleware");
                return await next();
            }))
            .BuildServiceProvider();

        var response = await provider.GetRequiredService<IMessageDispatcher>()
            .DispatchAsync(Request("ALPHA"));

        Assert.Equal("alpha", response.Data);
        Assert.Equal(["app-middleware"], order);
    }

    [Fact]
    public async Task The_error_handler_is_outermost()
    {
        using var provider = new ServiceCollection()
            .AddMessageDispatcher((_, dispatcher) =>
                dispatcher.MapRoute("APP", "BOOM", _ => throw new InvalidOperationException("secret detail")))
            .BuildServiceProvider();

        var response = await provider.GetRequiredService<IMessageDispatcher>()
            .DispatchAsync(Request("APP", "BOOM"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.UnknownError, response.Error!.Code);
        Assert.DoesNotContain("secret detail", IpcJson.Serialize(response));
    }

    [Fact]
    public async Task Unhandled_requests_still_answer_no_handler()
    {
        using var provider = new ServiceCollection().AddMessageDispatcher().BuildServiceProvider();

        var response = await provider.GetRequiredService<IMessageDispatcher>()
            .DispatchAsync(Request("NOWHERE"));

        Assert.Equal(IpcErrorCodes.NoHandler, response.Error!.Code);
    }
}
