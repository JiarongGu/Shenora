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

    // ── Lazy facade resolution (P5.5 H2) ──────────────────────────────────────────────────────────
    // AddMessageDispatcher used to resolve facades INSIDE the IMessageDispatcher singleton factory.
    // Any facade whose graph reaches IMessageDispatcher — the documented cross-module SendAsync seam —
    // re-entered that factory: DI's cycle detection is call-site based and cannot see a factory
    // delegate re-entering the provider, and the singleton isn't cached yet, so it simply ran again.
    // Unbounded recursion, StackOverflowException, process death with no exception and no log. That is
    // NOT catchable, so a test cannot assert the old behaviour — reaching the assert IS the assert.

    [Fact]
    public async Task A_facade_that_injects_the_dispatcher_resolves_instead_of_killing_the_process()
    {
        using var provider = new ServiceCollection()
            .AddModuleFacade<SelfDispatchingFacade>()
            .AddMessageDispatcher()
            .BuildServiceProvider();

        // Before the fix this line never returned — it overflowed the stack.
        var dispatcher = provider.GetRequiredService<IMessageDispatcher>();

        var response = await dispatcher.DispatchAsync(Request("SELF", "PING"));
        Assert.True(response.Success);

        // And the facade really can use the dispatcher it injected — the whole point of the seam.
        var viaFacade = await dispatcher.DispatchAsync(Request("SELF", "ROUNDTRIP"));
        Assert.True(viaFacade.Success);
    }

    [Fact]
    public void Two_facades_claiming_one_module_are_rejected_when_mapped_eagerly()
    {
        // Dispatch is first-match-wins, so the second facade's ENTIRE route table used to be
        // unreachable with nothing logged anywhere. On the eager path the composition now refuses
        // outright, naming both facades.
        using var provider = new ServiceCollection()
            .AddModuleFacade<DupOneFacade>()
            .AddModuleFacade<DupTwoFacade>()   // both claim "DUP"
            .BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(
            () => new MessageDispatcher().MapRegisteredModules(provider));

        Assert.Contains("DUP", error.Message);
        Assert.Contains(nameof(DupOneFacade), error.Message);
        Assert.Contains(nameof(DupTwoFacade), error.Message);
    }

    [Fact]
    public async Task A_duplicate_module_under_lazy_mapping_surfaces_as_a_logged_error_not_a_silent_shadow()
    {
        // AddMessageDispatcher maps LAZILY (it must — see the recursion test above), so the duplicate
        // cannot be caught until the first dispatch. And DispatchAsync's contract is that it NEVER
        // throws, so this arrives as a structured error response with the detail kept host-side. The
        // fix here is "diagnosable instead of silent", not "fails at startup" — worth being precise
        // about, because the eager path above genuinely does fail at composition.
        using var provider = new ServiceCollection()
            .AddModuleFacade<DupOneFacade>()
            .AddModuleFacade<DupTwoFacade>()
            .AddMessageDispatcher()
            .BuildServiceProvider();

        var response = await provider.GetRequiredService<IMessageDispatcher>()
            .DispatchAsync(Request("DUP", "PING"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.UnknownError, response.Error!.Code);
        // The composition detail names types and must not cross the wire.
        Assert.DoesNotContain(nameof(DupTwoFacade), IpcJson.Serialize(response));
    }

    /// <summary>A facade that injects the dispatcher — ordinary, and previously fatal.</summary>
    private sealed class SelfDispatchingFacade(IMessageDispatcher dispatcher) : BaseFacade
    {
        public override string ModuleName => "SELF";

        protected override async Task<object?> RouteMessageAsync(IpcRequest request) => request.Type switch
        {
            "PING" => "pong",
            // Cross-module send through the injected dispatcher (the documented use).
            "ROUNDTRIP" => await dispatcher.SendAsync<string>("SELF", "PING"),
            _ => throw UnknownType(request),
        };
    }

    // Two facades claiming one module, with no dependencies — so the guard is what fails, not DI.
    private sealed class DupOneFacade : BaseFacade
    {
        public override string ModuleName => "DUP";
        protected override Task<object?> RouteMessageAsync(IpcRequest request) => Task.FromResult<object?>("one");
    }

    private sealed class DupTwoFacade : BaseFacade
    {
        public override string ModuleName => "DUP";
        protected override Task<object?> RouteMessageAsync(IpcRequest request) => Task.FromResult<object?>("two");
    }
}
