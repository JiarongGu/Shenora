using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shenora.Tests.TestSupport;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Ipc;

public class IpcCompositionTests
{
    private sealed class AlphaFacade : ModuleBase
    {
        public override string ModuleName => "ALPHA";

        protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken) =>
            Task.FromResult<object?>("alpha");
    }

    private sealed class BetaFacade : ModuleBase
    {
        public override string ModuleName => "BETA";

        protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken) =>
            Task.FromResult<object?>("beta");
    }

    private static IpcRequest Request(string module, string type = "ANY") =>
        IpcRequests.Create(module, type);

    /// <summary>
    /// A pass-through decorator — the shape that used to break composition silently (P5.5 H6). It must
    /// be writable with FOUR members: dispatch, two sends, and compose.
    /// </summary>
    private sealed class CountingDispatcher(IMessageDispatcher inner) : IMessageDispatcher
    {
        public int Dispatched { get; private set; }

        public Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken = default)
        {
            Dispatched++;
            return inner.DispatchAsync(request);
        }

        public Task<IpcResponse> SendAsync(string module, string type, string? scope = null, object? payload = null, CancellationToken cancellationToken = default) =>
            inner.SendAsync(module, type, scope, payload);

        public Task<T?> SendAsync<T>(string module, string type, string? scope = null, object? payload = null, CancellationToken cancellationToken = default) =>
            inner.SendAsync<T>(module, type, scope, payload);

        public IMessageDispatcher Use(MessageMiddleware middleware) => inner.Use(middleware);
    }

    [Fact]
    public async Task Late_mapping_works_through_the_interface_with_no_downcast()
    {
        // The reference composition had to write `if (dispatcher is MessageDispatcher concrete)` to map
        // its window-facing facades after the form existed — and that `if` had NO else, so any
        // composition that registered a different IMessageDispatcher silently lost three whole modules,
        // with the only symptom being a title bar that stopped working (P5.5 H6). Every mapping helper is
        // now an extension over the interface.
        using var provider = new ServiceCollection().UseMessageDispatcher().BuildServiceProvider();
        IMessageDispatcher dispatcher = provider.GetRequiredService<IMessageDispatcher>();

        dispatcher.MapModule(new AlphaFacade());
        dispatcher.MapRoute("LATE", "PING", _ => "pong");
        dispatcher.MapModule("BUILDER", routes => routes.Route("ECHO", _ => "echoed"));

        Assert.Equal("alpha", (await dispatcher.DispatchAsync(Request("ALPHA"))).Data);
        Assert.Equal("pong", (await dispatcher.DispatchAsync(Request("LATE", "PING"))).Data);
        Assert.Equal("echoed", (await dispatcher.DispatchAsync(Request("BUILDER", "ECHO"))).Data);
    }

    [Fact]
    public async Task A_decorated_dispatcher_can_still_be_composed()
    {
        // The failure mode the downcast created: wrapping the dispatcher — for metrics, tracing, an
        // app-side guard — made `is MessageDispatcher` false and every late-mapped module vanish.
        var decorated = new CountingDispatcher(new MessageDispatcher());
        // A logger is now REQUIRED here, because a decorator's own logger is unreachable: see the
        // refusal pinned below. Passing NullLogger is the deliberate-silence spelling.
        decorated.UseErrorHandler(NullLogger.Instance);
        decorated.MapModule(new AlphaFacade());

        var response = await decorated.DispatchAsync(Request("ALPHA"));

        Assert.Equal("alpha", response.Data);
        Assert.Equal(1, decorated.Dispatched); // the decorator really is in the path
    }

    [Fact]
    public void UseErrorHandler_REFUSES_a_decorator_it_cannot_log_through()
    {
        // Behind a decorator the dispatcher's own logger is unreachable, so this used to fall back to
        // NullLogger SILENTLY: every unhandled exception then mapped to UNKNOWN_ERROR for the client and
        // was logged nowhere — the one place the kit promises the detail stays host-side. It now refuses
        // at COMPOSITION, the same call `Registry` makes one method over: never a permissive wrong answer.
        var decorated = new CountingDispatcher(new MessageDispatcher());

        var ex = Assert.Throws<InvalidOperationException>(() => decorated.UseErrorHandler());

        Assert.Contains(nameof(CountingDispatcher), ex.Message);   // names the offending type
        Assert.Contains("NullLogger", ex.Message);                 // and the deliberate-silence escape
    }

    [Fact]
    public async Task UseMessageDispatcher_maps_every_registered_facade()
    {
        using var provider = new ServiceCollection()
            .AddIpcModule<AlphaFacade>()
            .AddIpcModule<BetaFacade>()
            .UseMessageDispatcher()
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
            .AddIpcModule<AlphaFacade>()
            .UseMessageDispatcher((_, dispatcher) => dispatcher.Use(async (_, next, _) =>
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
            .UseMessageDispatcher((_, dispatcher) =>
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
        using var provider = new ServiceCollection().UseMessageDispatcher().BuildServiceProvider();

        var response = await provider.GetRequiredService<IMessageDispatcher>()
            .DispatchAsync(Request("NOWHERE"));

        Assert.Equal(IpcErrorCodes.NoHandler, response.Error!.Code);
    }

    // ── Lazy facade resolution (P5.5 H2) ──────────────────────────────────────────────────────────
    // UseMessageDispatcher used to resolve facades INSIDE the IMessageDispatcher singleton factory.
    // Any facade whose graph reaches IMessageDispatcher — the documented cross-module SendAsync seam —
    // re-entered that factory: DI's cycle detection is call-site based and cannot see a factory
    // delegate re-entering the provider, and the singleton isn't cached yet, so it simply ran again.
    // Unbounded recursion, StackOverflowException, process death with no exception and no log. That is
    // NOT catchable, so a test cannot assert the old behaviour — reaching the assert IS the assert.

    [Fact]
    public async Task A_facade_that_injects_the_dispatcher_resolves_instead_of_killing_the_process()
    {
        using var provider = new ServiceCollection()
            .AddIpcModule<SelfDispatchingFacade>()
            .UseMessageDispatcher()
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
            .AddIpcModule<DupOneFacade>()
            .AddIpcModule<DupTwoFacade>()   // both claim "DUP"
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
        // UseMessageDispatcher maps LAZILY (it must — see the recursion test above), so the duplicate
        // cannot be caught until the first dispatch. And DispatchAsync's contract is that it NEVER
        // throws, so this arrives as a structured error response with the detail kept host-side. The
        // fix here is "diagnosable instead of silent", not "fails at startup" — worth being precise
        // about, because the eager path above genuinely does fail at composition.
        using var provider = new ServiceCollection()
            .AddIpcModule<DupOneFacade>()
            .AddIpcModule<DupTwoFacade>()
            .UseMessageDispatcher()
            .BuildServiceProvider();

        var response = await provider.GetRequiredService<IMessageDispatcher>()
            .DispatchAsync(Request("DUP", "PING"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.UnknownError, response.Error!.Code);
        // The composition detail names types and must not cross the wire.
        Assert.DoesNotContain(nameof(DupTwoFacade), IpcJson.Serialize(response));
    }

    [Fact]
    public async Task One_facade_factory_failing_once_does_not_poison_dispatch_forever()
    {
        // The default Lazy mode caches a thrown exception for the life of the process, so ONE
        // transient DI failure at first dispatch used to turn every later request to every
        // DI-registered module into UNKNOWN_ERROR until restart. The map builds PublicationOnly now:
        // the next dispatch retries and heals.
        var attempts = 0;
        using var provider = new ServiceCollection()
            .AddSingleton<IIpcModule>(_ => ++attempts == 1
                ? throw new InvalidOperationException("transient resolution failure")
                : new DupOneFacade())
            .UseMessageDispatcher()
            .BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IMessageDispatcher>();

        var poisoned = await dispatcher.DispatchAsync(Request("DUP", "PING"));
        Assert.False(poisoned.Success);
        Assert.Equal(IpcErrorCodes.UnknownError, poisoned.Error!.Code);

        var healed = await dispatcher.DispatchAsync(Request("DUP", "PING"));
        Assert.True(healed.Success);
    }

    /// <summary>A facade that injects the dispatcher — ordinary, and previously fatal.</summary>
    private sealed class SelfDispatchingFacade(IMessageDispatcher dispatcher) : ModuleBase
    {
        public override string ModuleName => "SELF";

        protected override async Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken) => request.Type switch
        {
            "PING" => "pong",
            // Cross-module send through the injected dispatcher (the documented use).
            "ROUNDTRIP" => await dispatcher.SendAsync<string>("SELF", "PING"),
            _ => throw UnknownType(request),
        };
    }

    // Two facades claiming one module, with no dependencies — so the guard is what fails, not DI.
    private sealed class DupOneFacade : ModuleBase
    {
        public override string ModuleName => "DUP";
        protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken) => Task.FromResult<object?>("one");
    }

    private sealed class DupTwoFacade : ModuleBase
    {
        public override string ModuleName => "DUP";
        protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken) => Task.FromResult<object?>("two");
    }
}
