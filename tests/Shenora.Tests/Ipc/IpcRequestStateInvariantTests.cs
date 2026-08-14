using System.Reflection;
using Microsoft.Extensions.Time.Testing;

using Shenora.Core.Events;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// 🔴 <b>The lifecycle sweep, REBUILT 2026-08-09 after an audit found it gone.</b>
///
/// <para>
/// D23 recorded an <c>OperationLifecycleInvariantTests</c> that enumerated the live status set by
/// reflection and required an exit per non-terminal value, so that <i>"a future status added with no exit
/// fails BY NAME"</i>. It did not survive D66's merge of operations into <see cref="IpcRequest"/>, and
/// **nothing replaced it** — the decision entry went on claiming it still ran for two versions. This is
/// the same guarantee against <see cref="IpcRequestState"/>.
/// </para>
///
/// <para>
/// ⚠ <b>Why it is worth rebuilding rather than trusting the four values to stay four.</b>
/// <see cref="IpcRequestTracker"/> encodes "in flight" as <c>State == Running</c> and "finished" as
/// <c>State != Running</c>, in six places. A second non-terminal state — a <c>Waiting</c>, which this kit
/// has had before and cut — would therefore be treated as FINISHED by every one of them: listed under
/// history by <c>GetAll</c>, deleted by <c>ClearFinished</c>, and refused by <c>Report</c> and
/// <c>Fail</c>. Nothing would throw. **The assumption is load-bearing and invisible, which is exactly the
/// kind this repo pins.**
/// </para>
/// </summary>
public class IpcRequestStateInvariantTests
{
    /// <summary>
    /// The classification the tracker's code actually relies on. Adding a value to the enum without
    /// adding it here fails <see cref="Every_state_is_classified"/> BY NAME — which is the whole point.
    /// </summary>
    private static readonly IpcRequestState[] NonTerminal = [IpcRequestState.Running];

    private static readonly IpcRequestState[] Terminal =
        [IpcRequestState.Completed, IpcRequestState.Failed, IpcRequestState.Cancelled];

    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(50);

    /// <remarks>
    /// ⚠ A FAKE clock, and the reason is the behaviour under test elsewhere: a request that finishes
    /// INSIDE the grace period is never announced and leaves no history entry at all — so a tracker
    /// driven on the real clock answers `GetAll()` with nothing and every assertion here becomes vacuous.
    /// Announce first (advance), then exit.
    /// </remarks>
    private static (IpcRequestTracker Tracker, FakeTimeProvider Clock) Build()
    {
        var clock = new FakeTimeProvider();
        return (new IpcRequestTracker(new EventBus(), new IpcRequestTrackerOptions
        {
            GracePeriod = Grace,
            ProgressInterval = TimeSpan.Zero,
            TimeProvider = clock,
        }), clock);
    }

    private static IpcRequest Request(string id) =>
        new() { Id = id, Module = "DEPLOY", Type = "PUSH" };

    /// <summary>
    /// Every value is either terminal or not. **A new state fails here first, and the message names it** —
    /// so whoever adds one is told to decide which it is before anything else can go wrong.
    /// </summary>
    [Fact]
    public void Every_state_is_classified()
    {
        var classified = NonTerminal.Concat(Terminal).ToHashSet();
        var unclassified = Enum.GetValues<IpcRequestState>().Where(s => !classified.Contains(s)).ToArray();

        Assert.True(unclassified.Length == 0,
            $"IpcRequestState gained {string.Join(", ", unclassified)} with no classification here. "
            + "Decide whether it is terminal, add it to the right list, and then make sure "
            + "IpcRequestTracker agrees — it encodes 'in flight' as `== Running` in six places, so a "
            + "second NON-TERMINAL state is silently treated as finished by all of them.");
    }

    /// <summary>
    /// 🔴 <b>The exit requirement, stated against the code rather than against a list.</b> A request left in
    /// a non-terminal state forever is a spinner that never stops, so every non-terminal state must have a
    /// public way out. Today there is one such state and three ways out of it; the assertion is that each
    /// one lands somewhere this file calls terminal.
    /// </summary>
    [Theory]
    [InlineData("dispose", IpcRequestState.Completed)]
    [InlineData("fail", IpcRequestState.Failed)]
    [InlineData("cancel", IpcRequestState.Cancelled)]
    public void Every_exit_from_Running_lands_on_a_terminal_state(string exit, IpcRequestState expected)
    {
        var (tracker, clock) = Build();
        var scope = tracker.Begin(Request(exit));
        clock.Advance(Grace);                                  // announced, so it survives its exit

        Assert.Equal(IpcRequestState.Running, Single(tracker, exit).State);

        switch (exit)
        {
            case "dispose": scope.Dispose(); break;
            case "fail": scope.Fail(new IpcError { Code = "NOPE" }); scope.Dispose(); break;
            case "cancel": tracker.Cancel(exit); scope.Dispose(); break;
        }

        var finished = Single(tracker, exit);
        Assert.Equal(expected, finished.State);
        Assert.Contains(finished.State, Terminal);
    }

    /// <summary>
    /// ⚠ <b>The tracker's own split must agree with the classification above</b>, or the two drift and this
    /// file becomes decoration. `GetAll` orders in-flight first and history after; a state this file calls
    /// terminal must never be reported as still running.
    /// </summary>
    [Fact]
    public void The_trackers_own_split_agrees_with_the_classification()
    {
        var (tracker, clock) = Build();
        var live = tracker.Begin(Request("live"));
        var done = tracker.Begin(Request("done"));
        clock.Advance(Grace);
        done.Dispose();

        var all = tracker.GetAll();

        Assert.Contains(NonTerminal, s => s == Lookup(all, "live").State);
        Assert.Contains(Terminal, s => s == Lookup(all, "done").State);

        live.Dispose();
    }

    /// <summary>
    /// The enum is a WIRE contract — it crosses as its camelCase name — so a renamed value breaks every
    /// page that switched on it. Pinned by name, not by ordinal, because the ordinal is not what ships.
    /// </summary>
    [Fact]
    public void The_wire_names_are_the_ones_the_client_switches_on()
    {
        Assert.Equal(
            ["Cancelled", "Completed", "Failed", "Running"],
            Enum.GetNames<IpcRequestState>().OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    private static IpcRequestStatus Single(IpcRequestTracker tracker, string id) => Lookup(tracker.GetAll(), id);

    private static IpcRequestStatus Lookup(IReadOnlyList<IpcRequestStatus> all, string id) =>
        all.Single(s => s.Id == id);
}
