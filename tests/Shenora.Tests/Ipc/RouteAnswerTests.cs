using Shenora.Core.Ipc;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Ipc;

/// <summary>
/// A task is an <c>object</c>, so every way of mapping a route compiles one as its answer. The clipboard's async
/// route did (<c>return Done();</c>), and its WRITE answered with the serialized task; <c>MapRoute(r =&gt;
/// svc.SaveAsync())</c> answers the same way and never awaits the work. Every route path settles the answer:
/// <c>ModuleBase</c> (its own tests) and the two dispatcher maps here.
/// </summary>
public class RouteAnswerTests
{
    private static Task<IpcResponse> Dispatch(Action<MessageDispatcher> map, string type)
    {
        var dispatcher = new MessageDispatcher();
        map(dispatcher);
        return dispatcher.DispatchAsync(IpcRequests.Create("ROUTES", type), CancellationToken.None);
    }

    [Fact]
    public async Task A_mapped_route_answering_with_a_task_of_its_answer_answers_the_answer()
    {
        var response = await Dispatch(d => d.MapRoute("ROUTES", "ONE", _ => Task.FromResult<object?>("answer")), "ONE");

        Assert.True(response.Success);
        Assert.Equal("answer", response.Data);
    }

    [Fact]
    public async Task A_mapped_route_answering_with_a_task_it_never_awaited_is_an_error()
    {
        var response = await Dispatch(d => d.MapRoute("ROUTES", "ONE", _ => Task.Delay(1)), "ONE");

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.UnknownError, response.Error!.Code);
    }

    [Fact]
    public async Task An_async_route_in_a_table_is_settled_too()
    {
        var response = await Dispatch(d => d.MapModule("ROUTES", routes => routes
            .RouteAsync("DONE", async (_, _) => { await Task.Yield(); return Task.FromResult<object?>(null); })
            .RouteAsync("FORGOT", async (_, _) => { await Task.Yield(); return Task.Delay(1); })), "DONE");
        var forgot = await Dispatch(d => d.MapModule("ROUTES", routes => routes
            .RouteAsync("FORGOT", async (_, _) => { await Task.Yield(); return Task.Delay(1); })), "FORGOT");

        Assert.True(response.Success);
        Assert.Null(response.Data);
        Assert.False(forgot.Success);
    }
}
