using Shenora.Ipc;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Ipc;

/// <summary>
/// Claim / ask / release. Release closed the last recorded capability gap in the dispatcher: the
/// pipeline only ever grew, so turning a dynamically composed module off meant restarting the app.
/// The risky part is not "does it stop answering" — it is that removing one entry must not disturb
/// the ORDER of everything else, because the relative order of the error handler, logging, app
/// middleware and the scoped router is load-bearing (design §5).
/// </summary>
public class ModuleLifecycleTests
{
    private static IpcRequest Request(string module, string type = "PING") => IpcRequests.Create(module, type);

    private sealed class Facade(string module, string answer) : BaseFacade
    {
        public override string ModuleName => module;
        protected override Task<object?> RouteMessageAsync(IpcRequest request, CancellationToken cancellationToken)
            => Task.FromResult<object?>(answer);
    }

    [Fact]
    public async Task A_released_module_stops_answering_and_frees_its_name()
    {
        var dispatcher = new MessageDispatcher();
        dispatcher.MapModule(new Facade("PLUGIN", "first"));
        Assert.Equal("first", (await dispatcher.DispatchAsync(Request("PLUGIN"))).Data);

        Assert.True(dispatcher.TryReleaseModule("PLUGIN"));

        var afterRelease = await dispatcher.DispatchAsync(Request("PLUGIN"));
        Assert.Equal(IpcErrorCodes.NoHandler, afterRelease.Error?.Code);
        Assert.False(dispatcher.IsModuleMapped("PLUGIN"));
        Assert.DoesNotContain("PLUGIN", ((IModuleRegistry)dispatcher).MappedModules);

        // The name is genuinely free — a DIFFERENT facade can take it, which is the point of release
        // (a plug-in disabled and replaced, a tenant module rebuilt).
        Assert.True(dispatcher.TryMapModule(new Facade("PLUGIN", "second")));
        Assert.Equal("second", (await dispatcher.DispatchAsync(Request("PLUGIN"))).Data);
    }

    [Fact]
    public async Task Releasing_one_module_leaves_the_middleware_ORDER_of_everything_else_intact()
    {
        // The whole risk of implementing release. Removing an entry from the pipeline must be surgical:
        // if it perturbed the order, the error handler could end up after the module it is meant to
        // wrap, or the scoped router after the global facades it is meant to precede — and neither
        // failure looks like an ordering bug from the outside.
        var order = new List<string>();
        var dispatcher = new MessageDispatcher();
        dispatcher.Use(async (_, next, _) => { order.Add("outer"); return await next(); });
        dispatcher.MapModule(new Facade("DOOMED", "x"));
        dispatcher.Use(async (_, next, _) => { order.Add("inner"); return await next(); });
        dispatcher.MapModule(new Facade("KEPT", "y"));

        Assert.True(dispatcher.TryReleaseModule("DOOMED"));

        order.Clear();
        var response = await dispatcher.DispatchAsync(Request("KEPT"));

        Assert.Equal("y", response.Data);
        Assert.Equal(["outer", "inner"], order);
    }

    [Fact]
    public void Releasing_something_that_was_never_mapped_is_false_not_an_error()
    {
        var dispatcher = new MessageDispatcher();
        Assert.False(dispatcher.TryReleaseModule("NOPE"));
    }

    [Fact]
    public void Release_is_case_insensitive_because_routing_is()
    {
        var dispatcher = new MessageDispatcher();
        dispatcher.MapModule(new Facade("Plugin", "x"));

        Assert.True(dispatcher.TryReleaseModule("PLUGIN"));
        Assert.False(dispatcher.IsModuleMapped("plugin"));
    }

    [Fact]
    public async Task A_request_already_inside_a_released_facade_still_completes()
    {
        // Release removes the ROUTE; it does not abort work in flight. A caller mid-request gets its
        // answer — anything else would make disabling a plug-in a way to fail live requests.
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var dispatcher = new MessageDispatcher();
        dispatcher.MapModule(new SlowFacade(entered, release));

        var inFlight = dispatcher.DispatchAsync(Request("SLOW"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(dispatcher.TryReleaseModule("SLOW"));
        release.SetResult();

        var response = await inFlight.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.Success);
        Assert.Equal("finished", response.Data);
    }

    [Fact]
    public void Claiming_is_atomic_under_concurrency()
    {
        // The plug-in case: two threads offering the same name. A check followed by a separate map
        // would let both win and the second would be dead code — the silent-shadowing defect
        // IModuleRegistry exists to prevent, reintroduced by a race instead of by a typo.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var dispatcher = new MessageDispatcher();
            var wins = 0;
            var start = new ManualResetEventSlim();

            var racers = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
            {
                start.Wait();
                if (dispatcher.TryMapModule(new Facade("RACE", $"r{i}"))) Interlocked.Increment(ref wins);
            })).ToArray();

            start.Set();
            Task.WaitAll(racers);

            Assert.Equal(1, wins);
            Assert.Single(((IModuleRegistry)dispatcher).MappedModules);
        }
    }

    [Fact]
    public void A_dispatcher_that_cannot_answer_refuses_rather_than_reporting_success()
    {
        // Same rule as TryMapModule: a permissive wrong answer is the dangerous one. Claiming a
        // release succeeded when nothing was removed would leave a plug-in live while the app
        // believed it was off.
        IMessageDispatcher opaque = new OpaqueDispatcher();

        Assert.Throws<NotSupportedException>(() => opaque.TryReleaseModule("ANY"));
    }

    private sealed class SlowFacade(TaskCompletionSource entered, TaskCompletionSource release) : BaseFacade
    {
        public override string ModuleName => "SLOW";
        protected override async Task<object?> RouteMessageAsync(IpcRequest request, CancellationToken cancellationToken)
        {
            entered.SetResult();
            await release.Task;
            return "finished";
        }
    }

    private sealed class OpaqueDispatcher : IMessageDispatcher
    {
        public IMessageDispatcher Use(MessageMiddleware middleware) => this;
        public Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(IpcResponse.CreateSuccess(request.Id, null));
        public Task<IpcResponse> SendAsync(string module, string type, string? scope = null, object? payload = null,
                                           CancellationToken cancellationToken = default) =>
            DispatchAsync(new IpcRequest { Module = module, Type = type }, cancellationToken);
        public Task<T?> SendAsync<T>(string module, string type, string? scope = null, object? payload = null,
                                     CancellationToken cancellationToken = default) =>
            Task.FromResult<T?>(default);
    }
}
