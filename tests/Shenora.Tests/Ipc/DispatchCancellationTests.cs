using Shenora.Tests.TestSupport;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// P6.4 closed the gap that the dispatch surface carried NO cancellation at all: a handler could not
/// observe a token it was never given, so work still awaiting when the page navigated away or the
/// host shut down had no way to learn that nobody was listening. The token is the CALLER's lifetime,
/// which is why these tests are about the transport's shutdown and never about a client "cancel"
/// message — that stays an app-level route carrying an operation id.
/// </summary>
public class DispatchCancellationTests
{
    private static IpcRequest Request(string module, string type) => IpcRequests.Create(module, type);

    [Fact]
    public async Task A_route_receives_the_token_the_caller_passed()
    {
        var dispatcher = new MessageDispatcher();
        CancellationToken seen = default;
        dispatcher.MapModule("APP", routes => routes.RouteAsync("WORK", (_, ct) =>
        {
            seen = ct;
            return Task.FromResult<object?>("done");
        }));

        using var cts = new CancellationTokenSource();
        var response = await dispatcher.DispatchAsync(Request("APP", "WORK"), cts.Token);

        Assert.True(response.Success);
        // Not just "a token" — the caller's. A default token would satisfy a weaker assertion and
        // prove nothing, since that is exactly what the surface used to give every handler.
        Assert.Equal(cts.Token, seen);
    }

    [Fact]
    public async Task A_facade_receives_the_token_through_ModuleBase()
    {
        var facade = new TokenCapturingFacade();
        var dispatcher = new MessageDispatcher();
        dispatcher.MapModule(facade);

        using var cts = new CancellationTokenSource();
        await dispatcher.DispatchAsync(Request("CAPTURE", "ANY"), cts.Token);

        Assert.Equal(cts.Token, facade.Seen);
    }

    [Fact]
    public async Task Cancelling_mid_flight_answers_OPERATION_CANCELLED_and_never_throws()
    {
        // The whole contract in one test: the boundary still never throws, and a cancel is a NORMAL
        // outcome with its own code rather than UNKNOWN_ERROR — a UI must be able to stay silent for
        // it, and could not tell the two apart otherwise.
        var dispatcher = new MessageDispatcher();
        var entered = new TaskCompletionSource();
        dispatcher.MapModule("APP", routes => routes.RouteAsync("SLOW", async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }));

        using var cts = new CancellationTokenSource();
        var dispatching = dispatcher.DispatchAsync(Request("APP", "SLOW"), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        // WaitAsync, not a bare await. If the pipeline ever stops passing the caller's token down, the
        // handler awaits Timeout.Infinite on a token nobody can cancel and this test HANGS instead of
        // failing — and a hang is the worst failure mode here: the suite runs serially precisely
        // because parallelism once masked a 17-second one. Verified by sabotage: swallowing the token
        // in BuildPipeline hung the whole run until this bound was added, then failed in 5 s.
        var response = await dispatching.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.OperationCancelled, response.Error?.Code);
    }

    [Fact]
    public async Task An_already_cancelled_token_answers_the_same_code_without_running_the_handler()
    {
        // The pre-cancelled case is the one that would naturally have been written to throw out of
        // DispatchAsync — which would break the never-throws contract that every transport relies on,
        // and hand the client a second shape for one outcome.
        var ran = false;
        var dispatcher = new MessageDispatcher();
        dispatcher.MapModule("APP", routes => routes.RouteAsync("WORK", (_, _) =>
        {
            ran = true;
            return Task.FromResult<object?>("done");
        }));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var response = await dispatcher.DispatchAsync(Request("APP", "WORK"), cts.Token);

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.OperationCancelled, response.Error?.Code);
        Assert.False(ran);
    }

    [Fact]
    public async Task An_apps_own_cancellation_code_survives()
    {
        // Cancellation is mapped AFTER OperationException on purpose: an app that models "aborted" in
        // its own words keeps them, and only an unnamed OperationCanceledException becomes the
        // kit's code.
        //
        // The token here is LIVE, deliberately. Written first with an already-cancelled one, this
        // test failed with OPERATION_CANCELLED — correctly: the pre-cancelled short-circuit runs
        // before the handler, so the handler never got to throw its own code and the test was
        // measuring the short-circuit instead of the mapping order it claims to pin. That case has
        // its own test above.
        var dispatcher = new MessageDispatcher();
        dispatcher.MapModule("APP", routes => routes.RouteAsync("WORK", (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            throw new OperationException("IMPORT_ABORTED");
        }));

        var response = await dispatcher.DispatchAsync(Request("APP", "WORK"), CancellationToken.None);

        Assert.Equal("IMPORT_ABORTED", response.Error?.Code);
    }

    [Fact]
    public async Task Middleware_sees_the_token_too()
    {
        var dispatcher = new MessageDispatcher();
        CancellationToken seen = default;
        dispatcher.Use(async (_, next, ct) => { seen = ct; return await next(); });
        dispatcher.MapRoute("APP", "WORK", _ => "done");

        using var cts = new CancellationTokenSource();
        await dispatcher.DispatchAsync(Request("APP", "WORK"), cts.Token);

        Assert.Equal(cts.Token, seen);
    }

    [Fact]
    public async Task A_programmatic_send_carries_its_token_down_the_same_pipeline()
    {
        // SendAsync and DispatchAsync must not diverge — that is the point of routing both through
        // one pipeline, and a token that only the transport path carried would be a quiet split.
        var dispatcher = new MessageDispatcher();
        CancellationToken seen = default;
        dispatcher.MapModule("APP", routes => routes.RouteAsync("WORK", (_, ct) =>
        {
            seen = ct;
            return Task.FromResult<object?>("done");
        }));

        using var cts = new CancellationTokenSource();
        await dispatcher.SendAsync("APP", "WORK", cancellationToken: cts.Token);

        Assert.Equal(cts.Token, seen);
    }

    private sealed class TokenCapturingFacade : ModuleBase
    {
        public CancellationToken Seen { get; private set; }

        public override string ModuleName => "CAPTURE";

        protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
        {
            Seen = cancellationToken;
            return Task.FromResult<object?>(null);
        }
    }
}
