using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shenora.Core.Events;
using Shenora.Core.Ipc;
using Shenora.Modules.Requests;

namespace Shenora.Tests.Ipc;

/// <summary>
/// Tracking belongs to the DISPATCH PATH (D66 follow-up, 2026-08-08), and these are the tests that
/// would have failed before it moved.
/// <para>
/// 🔴 <b>Every one of them drives a REAL tracker through a REAL dispatcher and asserts the tracker was
/// USED</b> — the D63 shape. <see cref="IpcRequestTrackerTests"/> calls <c>Begin</c>/<c>Report</c>
/// directly, which is why it stayed green for a release in which nothing in a composed app ever called
/// either: the only <c>Begin</c> call site was inside <c>ModuleBase</c>, behind an
/// <see cref="IIpcRequestTracker"/> that every kit module declined to inject.
/// </para>
/// </summary>
public class IpcRequestDispatchTests
{
    private static (IpcRequestTracker Tracker, List<EventMessage> Events, FakeTimeProvider Clock) Tracking()
    {
        var bus = new EventBus();
        var events = new List<EventMessage>();
        bus.SubscribeToAll(m => { lock (events) events.Add(m); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        return (new IpcRequestTracker(bus, new IpcRequestTrackerOptions
        {
            GracePeriod = TimeSpan.FromMilliseconds(50),
            ProgressInterval = TimeSpan.Zero,
            TimeProvider = clock,
        }), events, clock);
    }

    private static IpcRequest Request(string id = "r-1", string module = "SLOW", string type = "WORK") =>
        new() { Id = id, Module = module, Type = type };

    /// <summary>Bounded, per the standing rule: an unbounded await on a cancellable body HANGS the suite.</summary>
    private static Task<T> Bounded<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(5));

    /// <summary>
    /// 🔴 THE HEADLINE. The route is an ad-hoc <c>MapRoute</c> lambda — not a <see cref="ModuleBase"/>, not
    /// a facade, nothing that could have injected a tracker even if its author wanted to — and it is
    /// tracked anyway. That is the whole point of the move: a module cannot forget to opt in, because
    /// there is nothing left to opt into.
    /// </summary>
    [Fact]
    public async Task A_request_is_tracked_by_the_dispatcher_with_the_module_wiring_nothing()
    {
        var (tracker, events, clock) = Tracking();
        var gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new MessageDispatcher(requests: tracker)
            .MapModule("SLOW", routes => routes.RouteAsync("WORK", async (_, _) => await gate.Task));

        var dispatch = dispatcher.DispatchAsync(Request(id: "req-1"));
        clock.Advance(TimeSpan.FromMilliseconds(50));            // outlives the grace period

        var announced = Assert.Single(events);
        var status = Assert.IsType<IpcRequestStatus>(announced.Payload);
        Assert.Equal("req-1", status.Id);                        // the REQUEST's own id, end to end
        Assert.Equal("SLOW", status.Module);
        Assert.Equal("WORK", status.Type);
        Assert.Equal(IpcRequestState.Running, status.State);

        gate.SetResult(null);
        Assert.True((await Bounded(dispatch)).Success);
        Assert.Equal(IpcRequestState.Completed, tracker.GetAll().Single().State);
    }

    /// <summary>
    /// 🔴 <b><see cref="IpcRequestState.Failed"/> was unreachable before this.</b> Tracking used to start
    /// in <see cref="ModuleBase"/>, whose <c>catch</c> returns an error RESPONSE — so the scope then
    /// disposed as <see cref="IpcRequestState.Completed"/> and the in-flight list reported success for a
    /// request the client was told had failed. The dispatcher sees the outcome, so it can record it.
    /// </summary>
    [Fact]
    public async Task A_route_that_throws_records_Failed_with_the_error_the_client_was_given()
    {
        var (tracker, _, clock) = Tracking();
        var gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new MessageDispatcher(requests: tracker)
            .MapModule("SLOW", routes => routes.RouteAsync("WORK", async (_, _) => await gate.Task));

        var dispatch = dispatcher.DispatchAsync(Request(id: "req-2"));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        gate.SetException(new ShenoraException("DEPLOY_REJECTED",
            new Dictionary<string, string> { ["env"] = "prod" }));

        var response = await Bounded(dispatch);
        Assert.False(response.Success);
        Assert.Equal("DEPLOY_REJECTED", response.Error!.Code);

        var status = tracker.GetAll().Single();
        Assert.Equal(IpcRequestState.Failed, status.State);
        // The SAME structured error, so the in-flight list and the response can never disagree about why.
        Assert.Equal("DEPLOY_REJECTED", status.Error!.Code);
        Assert.Equal("prod", status.Error.Parameters!["env"]);
    }

    /// <summary>
    /// The other failure shape, and it takes the other branch: a <see cref="ModuleBase"/> catches its own
    /// exception and RETURNS an error response, so nothing ever throws past the pipeline. Both paths have
    /// to record the failure or the coverage depends on how a module happened to be written.
    /// </summary>
    [Fact]
    public async Task A_module_that_returns_an_error_response_records_Failed_too()
    {
        var (tracker, _, clock) = Tracking();
        var gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new MessageDispatcher(requests: tracker).MapModule(new GatedModule(gate));

        var dispatch = dispatcher.DispatchAsync(Request(id: "req-3", module: "GATED"));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        gate.SetException(new ShenoraException("MODULE_SAID_NO"));

        Assert.False((await Bounded(dispatch)).Success);
        var status = tracker.GetAll().Single();
        Assert.Equal(IpcRequestState.Failed, status.State);
        Assert.Equal("MODULE_SAID_NO", status.Error!.Code);
    }

    /// <summary>
    /// <see cref="IModuleContext.Report"/> reaches the tracker although the module injected nothing — the
    /// ambient scope the dispatcher set. Reported INSIDE the grace period on purpose: the value is kept
    /// while silent, so the first snapshot a page ever sees is current rather than empty.
    /// </summary>
    [Fact]
    public async Task A_route_reports_progress_through_its_context_with_no_facade_wiring()
    {
        var (tracker, events, clock) = Tracking();
        var gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new GatedModule(gate, report: new IpcProgress(3, 10, "steps"));
        var dispatcher = new MessageDispatcher(requests: tracker).MapModule(module);

        var dispatch = dispatcher.DispatchAsync(Request(id: "req-4", module: "GATED"));
        clock.Advance(TimeSpan.FromMilliseconds(50));

        var status = Assert.IsType<IpcRequestStatus>(Assert.Single(events).Payload);
        Assert.Equal(new IpcProgress(3, 10, "steps"), status.Progress);

        gate.SetResult(null);
        await Bounded(dispatch);
    }

    /// <summary>
    /// 🔴 The id guard, and this is the ONE case that needs it. A route calls another module's
    /// <see cref="IIpcModule.HandleMessageAsync"/> DIRECTLY — a supported thing to do, and nothing begins
    /// a scope for it — while the caller's own scope is still ambient on that call. Without the id match,
    /// the inner module's <c>Report</c> lands on the OUTER request: progress from work the page never
    /// asked about, attributed to work it did.
    /// <para>
    /// ⚠ A module invoked from a test or from app startup, with no dispatch anywhere above it, is NOT this
    /// case — there is simply no ambient to pick up, so it needs no guard and proves nothing about one.
    /// The first version of this test did exactly that and passed with the guard removed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_module_invoked_directly_from_inside_a_route_does_not_report_against_the_outer_request()
    {
        var (tracker, events, clock) = Tracking();
        var gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nested = new GatedModule(Task.FromResult<object?>("ok"), report: new IpcProgress(99));

        var dispatcher = new MessageDispatcher(requests: tracker)
            .MapModule("OUTER", routes => routes.RouteAsync("WORK", async (_, _) =>
            {
                // Straight at the module, no dispatcher — so this request has no scope of its own.
                await nested.HandleMessageAsync(Request(id: "unrelated", module: "GATED"));
                return await gate.Task;
            }));

        var dispatch = dispatcher.DispatchAsync(Request(id: "outer-1", module: "OUTER"));
        clock.Advance(TimeSpan.FromMilliseconds(50));

        var status = Assert.IsType<IpcRequestStatus>(Assert.Single(events).Payload);
        Assert.Equal("outer-1", status.Id);
        Assert.Null(status.Progress);   // the nested module's 99 must NOT have been attributed here

        gate.SetResult(null);
        await Bounded(dispatch);
    }

    /// <summary>
    /// CANCEL by request id reaches the token the ROUTE observes — which only works because the dispatcher
    /// hands the scope's token down the pipeline rather than the caller's. Bounded, per the standing rule.
    /// </summary>
    [Fact]
    public async Task Cancel_by_request_id_cancels_the_token_the_route_is_running_under()
    {
        var (tracker, _, clock) = Tracking();
        var dispatcher = new MessageDispatcher(requests: tracker)
            .MapModule("SLOW", routes => routes.RouteAsync("WORK", async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            }));

        var dispatch = dispatcher.DispatchAsync(Request(id: "req-5"));
        clock.Advance(TimeSpan.FromMilliseconds(50));

        Assert.True(tracker.Cancel("req-5"));

        var response = await Bounded(dispatch);
        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.OperationCancelled, response.Error!.Code);
        // Cancelled, NOT overwritten as Failed by the OPERATION_CANCELLED response that follows it.
        Assert.Equal(IpcRequestState.Cancelled, tracker.GetAll().Single().State);
    }

    /// <summary>
    /// The fast path is still free: a request that answers inside the grace period leaves NO event and NO
    /// history, even though every request is now tracked. This is what makes tracking-everything
    /// affordable, so it is worth pinning at the DISPATCHER and not only at the tracker.
    /// </summary>
    [Fact]
    public async Task A_fast_request_costs_the_wire_nothing()
    {
        var (tracker, events, clock) = Tracking();
        var dispatcher = new MessageDispatcher(requests: tracker).MapRoute("APP", "PING", _ => "pong");

        Assert.True((await dispatcher.DispatchAsync(Request(module: "APP", type: "PING"))).Success);
        clock.Advance(TimeSpan.FromSeconds(1));                  // the grace timer can never fire late

        Assert.Empty(events);
        Assert.Empty(tracker.GetAll());
    }

    /// <summary>
    /// A nested dispatch (the documented cross-module <c>SendAsync</c> seam) tracks BOTH requests, each
    /// under its own id — the inner one must not report against the outer, and the outer must survive the
    /// inner returning.
    /// </summary>
    [Fact]
    public async Task A_nested_send_tracks_both_requests_independently()
    {
        var (tracker, events, clock) = Tracking();
        var inner = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IMessageDispatcher dispatcher = null!;   // captured by the outer route, invoked long after assignment
        dispatcher = new MessageDispatcher(requests: tracker)
            .MapModule("OUTER", routes => routes.RouteAsync("WORK", async (_, ct) =>
                (await dispatcher.SendAsync("INNER", "WORK", cancellationToken: ct)).Data))
            .MapModule("INNER", routes => routes.RouteAsync("WORK", async (_, _) => await inner.Task));

        var dispatch = dispatcher.DispatchAsync(Request(id: "outer-1", module: "OUTER"));
        clock.Advance(TimeSpan.FromMilliseconds(50));

        // Two announcements: the outer under its own id, the inner under the id SendAsync minted for it.
        var announced = events.Select(m => (IpcRequestStatus)m.Payload!).ToList();
        Assert.Equal(2, announced.Count);
        Assert.Contains(announced, s => s.Id == "outer-1" && s.Module == "OUTER");
        Assert.Contains(announced, s => s.Module == "INNER" && s.Id != "outer-1");

        inner.SetResult(null);
        Assert.True((await Bounded(dispatch)).Success);
        Assert.All(tracker.GetAll(), s => Assert.Equal(IpcRequestState.Completed, s.State));
    }

    /// <summary>
    /// 🔴 THE DEFAULT-WIRING TEST (D63): the tracker reaches the dispatcher through the KIT's own
    /// composition, not because this test handed it over. Sabotage it by dropping the tracker argument in
    /// <c>UseMessageDispatcher</c> and this is the test that fails.
    /// </summary>
    [Fact]
    public async Task The_standard_composition_tracks_requests_with_the_app_asking_for_nothing()
    {
        var clock = new FakeTimeProvider();
        var gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var services = new ServiceCollection();
        services.AddSingleton<IEventBus, EventBus>();
        services.AddShenoraRequests(new IpcRequestTrackerOptions
        {
            GracePeriod = TimeSpan.FromMilliseconds(50),
            TimeProvider = clock,
        });
        services.UseMessageDispatcher((_, dispatcher) =>
            dispatcher.MapModule("SLOW", routes => routes.RouteAsync("WORK", async (_, _) => await gate.Task)));

        using var provider = services.BuildServiceProvider();
        var dispatch = provider.GetRequiredService<IMessageDispatcher>().DispatchAsync(Request(id: "req-6"));
        clock.Advance(TimeSpan.FromMilliseconds(50));

        var tracked = provider.GetRequiredService<IIpcRequestTracker>().GetAll();
        Assert.Equal("req-6", Assert.Single(tracked).Id);

        gate.SetResult(null);
        await Bounded(dispatch);
    }

    /// <summary>
    /// 🔴 THE ACCEPTANCE TEST, through the composition a real app actually writes:
    /// <c>ShenoraApplication.CreateBuilder(…).Build()</c>, a module registered the ordinary way, and NOT
    /// ONE line about tracking anywhere. The test above proves <c>UseMessageDispatcher</c> passes the
    /// tracker along; this proves the builder puts one there in the first place, which is the half an
    /// adopter depends on and never writes.
    /// <para>
    /// The options are registered BEFORE <c>Build()</c> on purpose — <c>AddShenoraRequests</c> is
    /// <c>TryAdd</c> throughout, so an app that configures its own wins, which is also the only way to get
    /// a fake clock in here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_built_application_tracks_its_requests_with_no_tracking_wiring_at_all()
    {
        var clock = new FakeTimeProvider();
        var gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "Shenora.Tests.App",
            BaseDirectory = @"C:\MyApp",
            GetEnvironmentVariable = _ => null,
        });
        builder.Services.AddSingleton(new IpcRequestTrackerOptions
        {
            GracePeriod = TimeSpan.FromMilliseconds(50),
            TimeProvider = clock,
        });
        builder.Services.AddSingleton<IIpcModule>(new GatedModule(gate));

        using var app = builder.Build();
        var dispatch = app.Services.GetRequiredService<IMessageDispatcher>()
            .DispatchAsync(Request(id: "req-7", module: "GATED"));
        clock.Advance(TimeSpan.FromMilliseconds(50));

        var tracked = Assert.Single(app.Services.GetRequiredService<IIpcRequestTracker>().GetAll());
        Assert.Equal("req-7", tracked.Id);
        Assert.Equal(IpcRequestState.Running, tracked.State);

        gate.SetResult(null);
        await Bounded(dispatch);
    }

    /// <summary>
    /// 🔴 <see cref="IIpcRequestTracker"/> is a PUBLIC seam, so an app can supply one — and these calls sit
    /// on the boundary the whole kit promises never throws. A faulty tracker must cost its own bookkeeping
    /// and nothing else, or one bad implementation takes down every transport (on WinForms, as an
    /// unhandled UI-thread exception).
    /// </summary>
    [Theory]
    [InlineData(true)]    // Begin throws — dispatch must continue UNTRACKED, not fail the request
    [InlineData(false)]   // the scope's Fail/Dispose throw — on the success path and the failure path alike
    public async Task A_faulty_tracker_cannot_break_the_dispatch_boundary(bool throwOnBegin)
    {
        var dispatcher = new MessageDispatcher(requests: new ThrowingTracker(throwOnBegin))
            .MapModule("APP", routes => routes
                .RouteAsync("PING", (_, _) => Task.FromResult<object?>("pong"))
                .RouteAsync("BOOM", (_, _) => throw new ShenoraException("APP_SAID_NO")));

        var ok = await Bounded(dispatcher.DispatchAsync(Request(module: "APP", type: "PING")));
        Assert.True(ok.Success);
        Assert.Equal("pong", ok.Data);

        var failed = await Bounded(dispatcher.DispatchAsync(Request(module: "APP", type: "BOOM")));
        Assert.False(failed.Success);
        Assert.Equal("APP_SAID_NO", failed.Error!.Code);
    }

    /// <summary>
    /// The seam the two halves of coalescing meet at, and the one nothing else covers: the tracker sets
    /// <c>EventMessage.CoalesceKey</c>, the pump reads <c>IpcNotification.CoalesceKey</c>, and the BUS is
    /// in between. A key dropped in that hand-off would leave both halves individually correct and the
    /// feature entirely absent — with a batch that still looks perfectly well-formed.
    /// </summary>
    [Fact]
    public void A_request_snapshot_coalesces_end_to_end_from_the_tracker_through_the_bus_to_the_pump()
    {
        var bus = new EventBus();
        var clock = new FakeTimeProvider();
        using var tracker = new IpcRequestTracker(bus, new IpcRequestTrackerOptions
        {
            GracePeriod = TimeSpan.FromMilliseconds(50),
            ProgressInterval = TimeSpan.Zero,
            TimeProvider = clock,
        });
        using var pump = new NotificationPump(new NotificationPumpOptions { EventBus = bus });
        pump.Open();

        using (var scope = tracker.Begin(Request(id: "req-8")))
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));         // announced
            scope.Report(new IpcProgress(1, 3, "steps"));
            scope.Report(new IpcProgress(2, 3, "steps"));
            scope.Report(new IpcProgress(3, 3, "steps"));
        }

        // Announcement + three progress snapshots + the completion all landed in ONE window; the page
        // needs only the last, and folding the others would reach the same state anyway.
        Assert.True(pump.TryDrainBatch(out var json));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json!, "\"req-8\""));
        Assert.Contains("\"completed\"", json);
        Assert.DoesNotContain("\"running\"", json);
    }

    /// <summary>A tracker that throws wherever the dispatch boundary touches it.</summary>
    private sealed class ThrowingTracker(bool throwOnBegin) : IIpcRequestTracker
    {
        public IIpcRequestScope Begin(IpcRequest request, CancellationToken cancellationToken = default) =>
            throwOnBegin ? throw new InvalidOperationException("begin") : new ThrowingScope(request.Id);

        public bool Cancel(string requestId) => false;

        public void ClearFinished(string? module = null, string? scope = null) { }

        public IReadOnlyList<IpcRequestStatus> GetAll(string? module = null, string? scope = null) => [];

        private sealed class ThrowingScope(string id) : IIpcRequestScope
        {
            public string RequestId => id;

            public CancellationToken CancellationToken => CancellationToken.None;

            public void Report(IpcProgress? progress = null, IpcLabel? detail = null) =>
                throw new InvalidOperationException("report");

            public void Fail(IpcError error) => throw new InvalidOperationException("fail");

            public void Dispose() => throw new InvalidOperationException("dispose");
        }
    }

    /// <summary>A module whose one route waits on a gate the test owns, optionally reporting progress first.</summary>
    private sealed class GatedModule(Task<object?> gate, IpcProgress? report = null) : ModuleBase
    {
        public GatedModule(TaskCompletionSource<object?> gate, IpcProgress? report = null)
            : this(gate.Task, report) { }

        public override string ModuleName => "GATED";

        protected override async Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context,
                                                                 CancellationToken cancellationToken)
        {
            if (report is not null) context.Report(report);
            return await gate;
        }
    }
}
