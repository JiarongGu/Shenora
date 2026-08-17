using Microsoft.Extensions.Time.Testing;
using Shenora.Core.Events;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// The merged model (D66): a request IS the thing that is tracked, and the GRACE PERIOD is what makes
/// tracking every request affordable.
/// </summary>
public class IpcRequestTrackerTests
{
    private static (IpcRequestTracker Tracker, List<EventMessage> Events, FakeTimeProvider Clock) Build(
        TimeSpan? grace = null, int maxHistory = 50)
    {
        var bus = new EventBus();
        var events = new List<EventMessage>();
        bus.SubscribeToAll(m => { lock (events) events.Add(m); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        return (new IpcRequestTracker(bus, new IpcRequestTrackerOptions
        {
            GracePeriod = grace ?? TimeSpan.FromMilliseconds(50),
            ProgressInterval = TimeSpan.Zero,      // throttling has its own test
            MaxHistory = maxHistory,
            TimeProvider = clock,
        }), events, clock);
    }

    private static IpcRequest Request(string id = "r-1", string module = "DEPLOY", string type = "PUSH",
                                      string? scope = null) =>
        new() { Id = id, Module = module, Type = type, Scope = scope };

    private static IpcRequestStatus Payload(EventMessage message) =>
        Assert.IsType<IpcRequestStatus>(message.Payload);

    /// <summary>
    /// 🔴 THE HEADLINE. A request that finishes inside the grace period emits NOTHING AT ALL — no running
    /// snapshot, no completion, no history entry. The response was the answer, and nobody wanted a spinner
    /// for work that took five milliseconds.
    /// <para>
    /// This is what makes "track every request, declare nothing" affordable. Without it, defaulting the
    /// tracker on (D64) would put two events on the wire for every call in the app.
    /// </para>
    /// </summary>
    [Fact]
    public void A_request_that_finishes_inside_the_grace_period_emits_nothing()
    {
        var (tracker, events, clock) = Build(grace: TimeSpan.FromMilliseconds(50));

        using (var scope = tracker.Begin(Request()))
        {
            scope.Report(new IpcProgress(1, 10, "steps"));
            clock.Advance(TimeSpan.FromMilliseconds(5));      // finishes well inside the window
        }

        clock.Advance(TimeSpan.FromSeconds(1));               // and the grace timer can never fire late
        Assert.Empty(events);
        Assert.Empty(tracker.GetAll());                       // nothing retained either — there is nothing to show
    }

    /// <summary>
    /// The other half: a request that OUTLIVES the window is announced exactly once, and the snapshot
    /// carries the REQUEST's own id — D66's whole point. There is no second identity to correlate.
    /// </summary>
    [Fact]
    public void A_request_that_outlives_the_grace_period_is_announced_under_its_own_request_id()
    {
        var (tracker, events, clock) = Build();
        var request = Request(id: "req-42", module: "DEPLOY", type: "PUSH", scope: "prod");

        using var scope = tracker.Begin(request);
        Assert.Empty(events);                                  // still silent inside the window

        clock.Advance(TimeSpan.FromMilliseconds(50));

        var message = Assert.Single(events);
        Assert.Equal("SHENORA.REQUESTS", message.Module);
        Assert.Equal(IpcRequestEvents.Updated, message.Type);
        Assert.Equal("prod", message.Scope);
        var status = Payload(message);
        Assert.Equal("req-42", status.Id);                     // the REQUEST's id, not a minted guid
        Assert.Equal("DEPLOY", status.Module);
        Assert.Equal("PUSH", status.Type);                     // the action IS the kind
        Assert.Equal(IpcRequestState.Running, status.State);
    }

    /// <summary>
    /// Progress reported while still inside the window is KEPT, not dropped — so the first snapshot a page
    /// ever sees is current rather than empty. This is what lets a route report progress unconditionally
    /// without caring whether it will turn out to be slow.
    /// </summary>
    [Fact]
    public void Progress_reported_inside_the_window_is_carried_by_the_first_announcement()
    {
        var (tracker, events, clock) = Build();

        using var scope = tracker.Begin(Request());
        scope.Report(new IpcProgress(3, 10, "steps"), new IpcLabel(Text: "step 3/10"));
        Assert.Empty(events);

        clock.Advance(TimeSpan.FromMilliseconds(50));

        var status = Payload(Assert.Single(events));
        Assert.Equal(new IpcProgress(3, 10, "steps"), status.Progress);
        Assert.Equal("step 3/10", status.Detail!.Text);
    }

    /// <summary>Once announced, the terminal transition is published like any other — and only then.</summary>
    [Fact]
    public void An_announced_request_publishes_its_completion()
    {
        var (tracker, events, clock) = Build();

        var scope = tracker.Begin(Request());
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Single(events);

        scope.Dispose();

        Assert.Equal(2, events.Count);
        var status = Payload(events[^1]);
        Assert.Equal(IpcRequestState.Completed, status.State);
        Assert.NotNull(status.FinishedAt);
    }

    /// <summary>
    /// CANCEL targets the REQUEST id — <c>XMLHttpRequest.abort()</c>. The token is signalled first, so a
    /// body observing it unwinds rather than racing a finished-then-cancelled flip.
    /// </summary>
    [Fact]
    public void Cancel_by_request_id_signals_the_token_and_records_cancelled()
    {
        var (tracker, _, clock) = Build();
        var scope = tracker.Begin(Request(id: "req-7"));
        clock.Advance(TimeSpan.FromMilliseconds(50));

        Assert.True(tracker.Cancel("req-7"));

        Assert.True(scope.CancellationToken.IsCancellationRequested);
        Assert.Equal(IpcRequestState.Cancelled, tracker.GetAll().Single().State);
    }

    /// <summary>An unknown or already-finished id refuses honestly rather than claiming success.</summary>
    [Fact]
    public void Cancel_returns_false_for_an_unknown_request()
    {
        var (tracker, _, _) = Build();

        Assert.False(tracker.Cancel("no-such-request"));
    }

    /// <summary>
    /// 🔴 Cancel's permission check and its transition run under two SEPARATE lock acquisitions — the token
    /// must be signalled outside the lock, because its callbacks re-enter the tracker. So the answer has to
    /// come from the transition, never from re-reading the table afterwards: an un-announced finish REMOVES
    /// the entry, so "no entry" would read as "I cancelled it" for a request something else finished.
    /// <para>
    /// Driven deterministically rather than with racing threads: <c>Cancel</c> signals the token
    /// synchronously, so a callback registered on it runs inside that window by construction.
    /// </para>
    /// </summary>
    [Fact]
    public void Cancel_refuses_when_something_else_finished_the_request_first()
    {
        var (tracker, _, _) = Build();
        var scope = tracker.Begin(Request(id: "req-race"));

        // Lands in the gap between Cancel's check and its Finish, and finishes the request some OTHER way.
        scope.CancellationToken.Register(() => scope.Fail(new IpcError { Code = "DISK_FULL" }));

        Assert.False(tracker.Cancel("req-race"));

        // Still inside the grace period, so the failed request left no trace — which is exactly why the
        // missing entry cannot be read as proof that the cancel landed.
        Assert.Empty(tracker.GetAll());
    }

    /// <summary>
    /// A body that unwound on cancellation must not be recorded as COMPLETED just because its scope
    /// disposed — that would report success for work that stopped.
    /// </summary>
    [Fact]
    public void Disposing_after_cancellation_records_cancelled_not_completed()
    {
        var (tracker, _, clock) = Build();
        using var scope = tracker.Begin(Request(id: "req-9"));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        tracker.Cancel("req-9");

        scope.Dispose();

        Assert.Equal(IpcRequestState.Cancelled, tracker.GetAll().Single().State);
    }

    /// <summary>A failure carries a structured error, never free text.</summary>
    [Fact]
    public void Fail_carries_a_structured_error()
    {
        var (tracker, events, clock) = Build();
        using var scope = tracker.Begin(Request());
        clock.Advance(TimeSpan.FromMilliseconds(50));

        scope.Fail(new IpcError { Code = "DEPLOY_REJECTED", Parameters = new Dictionary<string, string> { ["env"] = "prod" } });

        var status = Payload(events[^1]);
        Assert.Equal(IpcRequestState.Failed, status.State);
        Assert.Equal("DEPLOY_REJECTED", status.Error!.Code);
        Assert.Equal("prod", status.Error.Parameters!["env"]);
    }

    /// <summary>In-flight sorts before finished, so a UI never renders history above live work.</summary>
    [Fact]
    public void GetAll_orders_in_flight_before_finished()
    {
        var (tracker, _, clock) = Build();

        var done = tracker.Begin(Request(id: "done"));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        done.Dispose();

        var live = tracker.Begin(Request(id: "live"));
        clock.Advance(TimeSpan.FromMilliseconds(50));

        Assert.Equal(["live", "done"], tracker.GetAll().Select(r => r.Id));
        live.Dispose();
    }

    /// <summary>
    /// Only ANNOUNCED requests can enter history at all — which is why a busy app running thousands of
    /// fast requests never accumulates any. Eviction of what does enter is announced, so a long-lived
    /// client store mirrors a bounded list instead of growing forever.
    /// </summary>
    [Fact]
    public void History_is_bounded_and_eviction_is_announced()
    {
        var (tracker, events, clock) = Build(maxHistory: 1);

        foreach (var id in new[] { "a", "b" })
        {
            var scope = tracker.Begin(Request(id: id));
            clock.Advance(TimeSpan.FromMilliseconds(50));
            scope.Dispose();
        }

        Assert.Single(tracker.GetAll());
        var removal = events.Last(m => m.Type == IpcRequestEvents.Removed);
        Assert.Null(removal.Scope);                            // global: a batch can span scopes
    }
}
