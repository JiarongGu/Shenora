using Shenora.Core.Ipc;
using Shenora.Core.Events;
using Shenora.Modules.Requests;

namespace Shenora.Tests.Ipc;

/// <summary>
/// 🔴 <b>The page's control surface over in-flight requests — 14 of 42 lines covered until 2026-08-14.</b>
///
/// <para>
/// It is small, pure and reached by a PAGE, which is the combination that makes the gap worth closing: an
/// adopter's page sends these three routes by name, and the only thing standing between a typo in the
/// dispatch switch and a silently dead <c>CANCEL</c> button was the sample. `LIST` also carries a
/// contract that is easy to break and invisible when broken — its `module`/`scope` filter must match the
/// one the `Updated` events use, or a scoped store loads every scope once and never sheds the rest.
/// </para>
/// </summary>
public class IpcRequestsModuleTests
{
    private static (IpcRequestsModule Module, IpcRequestTracker Tracker) New()
    {
        var options = new IpcRequestTrackerOptions { GracePeriod = TimeSpan.Zero };
        var tracker = new IpcRequestTracker(new EventBus(), options);
        return (new IpcRequestsModule(tracker, options), tracker);
    }

    private static IpcRequest Request(string type, object? payload = null) => new()
    {
        Module = "SHENORA.REQUESTS",
        Type = type,
        Payload = payload is null ? null : IpcJson.SerializeToElement(payload),
    };

    /// <summary>A request to TRACK — `IpcRequest` is a class, not a record, so this is not a `with`.</summary>
    private static IpcRequest Tracked(string id, string module = "NOTES") => new()
    {
        Id = id,
        Module = module,
        Type = "WORK",
    };

    private static async Task<IpcResponse> AskAsync(IpcRequestsModule module, IpcRequest request) =>
        await module.HandleMessageAsync(request, CancellationToken.None);

    [Fact]
    public async Task LIST_returns_a_snapshot_of_what_is_in_flight()
    {
        var (module, tracker) = New();
        using var scope = tracker.Begin(Tracked("r1"));

        var response = await AskAsync(module, Request(IpcRequestsModule.ListType));

        Assert.True(response.Success);
        var list = Assert.IsAssignableFrom<IReadOnlyList<IpcRequestStatus>>(response.Data);
        Assert.Contains(list, s => s.Id == "r1" && s.State == IpcRequestState.Running);
    }

    /// <summary>
    /// 🔴 The snapshot must be filtered the SAME way the deltas are. A scoped store that loaded every
    /// scope once would never shed the rest — the failure is silent and only shows as a store that grows.
    /// </summary>
    [Fact]
    public async Task LIST_honours_the_module_filter_its_deltas_use()
    {
        var (module, tracker) = New();
        using var a = tracker.Begin(Tracked("notes-1", "NOTES"));
        using var b = tracker.Begin(Tracked("media-1", "MEDIA"));

        var response = await AskAsync(module, Request(IpcRequestsModule.ListType, new { module = "NOTES" }));

        var list = Assert.IsAssignableFrom<IReadOnlyList<IpcRequestStatus>>(response.Data);
        Assert.Contains(list, s => s.Id == "notes-1");
        Assert.DoesNotContain(list, s => s.Id == "media-1");
    }

    /// <summary>
    /// CANCEL carries the id the PAGE already has — the whole of D66 on the wire — and answers an honest
    /// bool rather than assuming success.
    /// </summary>
    [Fact]
    public async Task CANCEL_aborts_by_the_requests_own_id_and_reports_truthfully()
    {
        var (module, tracker) = New();
        using var scope = tracker.Begin(Tracked("r1"));

        var response = await AskAsync(module, Request(IpcRequestsModule.CancelType, new { requestId = "r1" }));

        Assert.True(response.Success);
        Assert.True(Cancelled(response));
        Assert.True(scope.CancellationToken.IsCancellationRequested);
    }

    /// <summary>An unknown id is refused, not claimed — the honest-bool half of the same route.</summary>
    [Fact]
    public async Task CANCEL_reports_false_for_an_id_it_does_not_know()
    {
        var (module, _) = New();

        var response = await AskAsync(module, Request(IpcRequestsModule.CancelType, new { requestId = "nope" }));

        Assert.True(response.Success);
        Assert.False(Cancelled(response));
    }

    /// <summary>A missing required key is a structured wire error, never a raw exception.</summary>
    [Fact]
    public async Task CANCEL_without_a_requestId_fails_STRUCTURALLY()
    {
        var (module, _) = New();

        var response = await AskAsync(module, Request(IpcRequestsModule.CancelType));

        Assert.False(response.Success);
        Assert.NotNull(response.Error);
        // The boundary's contract: a code the client can branch on, and no exception text.
        Assert.False(string.IsNullOrWhiteSpace(response.Error!.Code));
    }

    [Fact]
    public async Task CLEAR_FINISHED_drops_retained_history_and_leaves_the_running_alone()
    {
        var (module, tracker) = New();
        var finished = tracker.Begin(Tracked("done-1"));
        finished.Dispose();                                   // completes it into history
        using var running = tracker.Begin(Tracked("live-1"));

        var response = await AskAsync(module, Request(IpcRequestsModule.ClearFinishedType));

        Assert.True(response.Success);
        var after = tracker.GetAll();
        Assert.DoesNotContain(after, s => s.Id == "done-1");
        Assert.Contains(after, s => s.Id == "live-1");
    }

    /// <summary>
    /// An unrecognised type is the module's own structured refusal (<c>ModuleBase</c> owns the shape), not
    /// a fall-through that answers success with nothing.
    /// </summary>
    [Fact]
    public async Task An_unknown_type_is_refused_structurally()
    {
        var (module, _) = New();

        var response = await AskAsync(module, Request("RESUME"));   // a route D66 deliberately removed

        Assert.False(response.Success);
        Assert.NotNull(response.Error);
    }

    /// <summary>Route names are matched case-INsensitively — the switch upper-cases before comparing.</summary>
    [Fact]
    public async Task Route_names_are_matched_case_insensitively()
    {
        var (module, _) = New();

        var response = await AskAsync(module, Request("list"));

        Assert.True(response.Success);
    }

    private static bool Cancelled(IpcResponse response)
    {
        // The route answers an anonymous `{ cancelled = bool }`; read it the way the wire would.
        var json = IpcJson.SerializeToElement(response.Data!);
        return json.GetProperty("cancelled").GetBoolean();
    }
}
