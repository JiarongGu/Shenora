using Microsoft.Extensions.DependencyInjection;
using Shenora.Ipc;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Ipc;

public class ScopedContainerRouterTests
{
    private sealed class ScopedFacade(string scopeId) : BaseFacade
    {
        public override string ModuleName => "SCOPED";

        protected override Task<object?> RouteMessageAsync(IpcRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<object?>($"handled-by-{scopeId}");
    }

    private sealed class TrackedDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private static IpcRequest Request(string module, string? scope = null) =>
        IpcRequests.Create(module, scope: scope);

    private static ScopedContainerRouter CreateRouter(
        Action<string, IServiceCollection>? configureScope = null,
        Action<string, IServiceProvider>? onScopeCreated = null)
    {
        var router = new ScopedContainerRouter(new ScopedContainerRouterOptions
        {
            ConfigureScope = configureScope ?? ((scopeId, services) =>
            {
                services.AddSingleton(new ScopedFacade(scopeId));
                services.AddSingleton<TrackedDisposable>();
            }),
            OnScopeCreated = onScopeCreated,
        });
        router.MapModule<ScopedFacade>("SCOPED");
        return router;
    }

    [Fact]
    public async Task Scoped_requests_route_to_their_own_scope_container()
    {
        using var router = CreateRouter();
        var dispatcher = new MessageDispatcher().UseScopedRouter(router);

        var first = await dispatcher.DispatchAsync(Request("SCOPED", "s1"));
        var second = await dispatcher.DispatchAsync(Request("SCOPED", "s2"));

        Assert.Equal("handled-by-s1", first.Data);
        Assert.Equal("handled-by-s2", second.Data);
    }

    [Fact]
    public async Task Non_scoped_modules_fall_through()
    {
        using var router = CreateRouter();
        var dispatcher = new MessageDispatcher()
            .UseScopedRouter(router)
            .MapRoute("GLOBAL", "ANY", _ => "global");

        var response = await dispatcher.DispatchAsync(Request("GLOBAL", "s1"));

        Assert.Equal("global", response.Data);
        Assert.Empty(router.ActiveScopes); // no container was created for a pass-through
    }

    [Fact]
    public async Task Scoped_module_without_a_scope_answers_scope_required()
    {
        using var router = CreateRouter();
        var dispatcher = new MessageDispatcher().UseScopedRouter(router);

        var response = await dispatcher.DispatchAsync(Request("SCOPED"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.ScopeRequired, response.Error!.Code);
        Assert.Equal("SCOPED", response.Error.Parameters!["module"]);
    }

    [Fact]
    public void A_null_ConfigureScope_is_rejected_at_construction()
    {
        // `required` forces the caller to WRITE the initializer, not to write a non-null value — so an
        // explicit null (or a field that happened to be null) surfaced as an NRE from inside scope
        // creation, which the pipeline's error mapping then reported to the client as UNKNOWN_ERROR: a
        // composition bug wearing a runtime failure's clothes (P5.5 H3).
        Assert.Throws<ArgumentNullException>(() => new ScopedContainerRouter(new ScopedContainerRouterOptions
        {
            ConfigureScope = null!,
        }));
    }

    [Fact]
    public async Task Unresolvable_facade_falls_through()
    {
        // The module is declared scoped, but the scope's container doesn't register the facade.
        using var router = new ScopedContainerRouter(new ScopedContainerRouterOptions
        {
            ConfigureScope = (_, _) => { },
        });
        router.MapModule<ScopedFacade>("SCOPED");
        var dispatcher = new MessageDispatcher()
            .UseScopedRouter(router)
            .MapRoute("SCOPED", "ANY", _ => "global-fallback");

        var response = await dispatcher.DispatchAsync(Request("SCOPED", "s1"));

        Assert.Equal("global-fallback", response.Data);
    }

    [Fact]
    public async Task Scope_containers_are_cached_per_id()
    {
        using var router = CreateRouter();
        var dispatcher = new MessageDispatcher().UseScopedRouter(router);

        await dispatcher.DispatchAsync(Request("SCOPED", "s1"));
        await dispatcher.DispatchAsync(Request("SCOPED", "s1"));

        Assert.Same(router.GetScopeServices("s1"), router.GetScopeServices("s1"));
        Assert.Equal(["s1"], router.ActiveScopes);
    }

    [Fact]
    public async Task Scope_creation_is_single_flight_under_concurrency()
    {
        var creations = 0;
        using var router = CreateRouter(configureScope: (scopeId, services) =>
        {
            Interlocked.Increment(ref creations);
            services.AddSingleton(new ScopedFacade(scopeId));
        });

        await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => router.GetScopeServices("s1"))));

        Assert.Equal(1, creations);
    }

    [Fact]
    public void Failed_creation_is_not_cached()
    {
        var attempts = 0;
        using var router = CreateRouter(configureScope: (scopeId, services) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new OperationException("SCOPE_NOT_FOUND", "scope", scopeId);
            services.AddSingleton(new ScopedFacade(scopeId));
        });

        Assert.Throws<OperationException>(() => router.GetScopeServices("s1"));
        Assert.NotNull(router.GetScopeServices("s1")); // retry succeeds — no poisoned Lazy
    }

    [Fact]
    public async Task Scope_validation_errors_reach_the_wire_structured()
    {
        // ConfigureScope throwing OperationException flows through UseErrorHandler unleaked.
        using var router = CreateRouter(configureScope: (scopeId, _) =>
            throw new OperationException("SCOPE_NOT_FOUND", "scope", scopeId, "no such profile on disk"));
        var dispatcher = new MessageDispatcher()
            .UseErrorHandler()
            .UseScopedRouter(router);

        var response = await dispatcher.DispatchAsync(Request("SCOPED", "missing"));

        Assert.False(response.Success);
        Assert.Equal("SCOPE_NOT_FOUND", response.Error!.Code);
        Assert.Equal("missing", response.Error.Parameters!["scope"]);
    }

    [Fact]
    public void OnScopeCreated_runs_once_and_a_throw_disposes_the_half_built_scope()
    {
        var created = new List<string>();
        TrackedDisposable? tracked = null;
        var fail = true;
        using var router = CreateRouter(onScopeCreated: (scopeId, services) =>
        {
            created.Add(scopeId);
            tracked = services.GetRequiredService<TrackedDisposable>();
            if (fail) throw new InvalidOperationException("init failed");
        });

        Assert.Throws<InvalidOperationException>(() => router.GetScopeServices("s1"));
        Assert.True(tracked!.Disposed); // the half-built provider was not leaked

        fail = false;
        router.GetScopeServices("s1"); // creation retried
        Assert.Equal(["s1", "s1"], created);
    }

    [Fact]
    public void Invalidate_disposes_and_the_next_request_rebuilds()
    {
        using var router = CreateRouter();
        var tracked = router.GetScopeServices("s1").GetRequiredService<TrackedDisposable>();

        router.InvalidateScope("s1");

        Assert.True(tracked.Disposed);
        Assert.Empty(router.ActiveScopes);
        Assert.False(router.GetScopeServices("s1").GetRequiredService<TrackedDisposable>().Disposed);
    }

    [Fact]
    public async Task Invalidate_racing_an_in_flight_creation_still_disposes_the_built_provider()
    {
        // Regression: skipping not-yet-created Lazies let a provider that finished building
        // moments later leak untracked (two live containers over single-writer resources).
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        TrackedDisposable? tracked = null;
        using var router = CreateRouter(configureScope: (scopeId, services) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            services.AddSingleton<TrackedDisposable>();
        }, onScopeCreated: (_, services) => tracked = services.GetRequiredService<TrackedDisposable>());

        var creation = Task.Run(() => router.GetScopeServices("s1"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        var invalidation = Task.Run(() => router.InvalidateScope("s1")); // blocks on the in-flight build
        release.Set();
        await Task.WhenAll(creation, invalidation);

        Assert.NotNull(tracked);
        Assert.True(tracked!.Disposed); // the just-built provider was NOT leaked
        Assert.Empty(router.ActiveScopes);
    }

    [Fact]
    public void Dispose_disposes_every_scope_container()
    {
        var router = CreateRouter();
        var first = router.GetScopeServices("s1").GetRequiredService<TrackedDisposable>();
        var second = router.GetScopeServices("s2").GetRequiredService<TrackedDisposable>();

        router.Dispose();

        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.Throws<ObjectDisposedException>(() => router.GetScopeServices("s1"));
    }
}
