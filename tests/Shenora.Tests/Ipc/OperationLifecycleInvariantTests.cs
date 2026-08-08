using Shenora;
using Shenora.Core.Events;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// Enforces §5A.1's rule STRUCTURALLY rather than by reviewer attention: <b>every non-terminal
/// <see cref="OperationStatus"/> must have a sanctioned transition reaching a terminal one.</b> This
/// is the whole point of the D23 amendment — the bug it closes was not a single wrong line, it was
/// three individually-correct guards (<c>Validate</c> hard-coding <c>Running</c>, <c>ClearFinished</c>
/// walking only <c>_finishedOrder</c>, <c>PruneHistory</c> skipping offers on purpose) composing into a
/// state (the former <c>Interrupted</c> status, now folded into <see cref="OperationStatus.Waiting"/>)
/// with no exit at all. An emergent trap like that is invisible in any single guard's diff, so this
/// test enumerates the LIVE enum via reflection — never a hardcoded status list — and fails BY NAME the
/// moment a future non-terminal status is added with no registered exit.
/// </summary>
public class OperationLifecycleInvariantTests
{
    /// <summary>
    /// For each non-terminal status: how to put a FRESH operation into it through the real registry
    /// (never by touching internal state directly), and a sanctioned exit that must land it on a
    /// terminal status. Both halves are exercised for real below — this is a live transition proven
    /// against the actual <see cref="OperationRegistry"/>, not a static claim about what "should" work.
    /// <para>
    /// A future non-terminal <see cref="OperationStatus"/> value with no entry here fails
    /// <see cref="Every_non_terminal_status_has_a_registered_exit_that_reaches_terminal"/> BY NAME
    /// (asserted against <c>Enum.GetValues</c>, not against this dictionary's own key set) — that
    /// assertion is what makes this non-vacuous: this table can be incomplete and the test still
    /// catches it.
    /// </para>
    /// <para>
    /// Two entries, not three — and one reach per status rather than two, which is the 0.2.0 design
    /// pass showing up here as SIMPLIFICATION. The former <c>Paused</c>/<c>Interrupted</c> pair had
    /// already collapsed into the single <see cref="OperationStatus.Waiting"/> value; the crash-
    /// checkpoint way of REACHING that value (<c>RegisterWaiting</c>) is now cut too, so this table's
    /// one-representative-reach-per-key limitation no longer hides anything: every entry reaches
    /// <see cref="OperationStatus.Waiting"/> through <see cref="IOperation.Wait"/>, and the companion
    /// test that used to cover the second reach is gone with the feature.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<OperationStatus, (
        Func<OperationRegistry, string> Reach,
        Func<OperationRegistry, string, bool> Exit)> NonTerminalExits =
        new Dictionary<OperationStatus, (Func<OperationRegistry, string>, Func<OperationRegistry, string, bool>)>
        {
            // Running's sanctioned exits are Complete/Fail/Cancel; Cancel(id) is exercised here
            // because it is the one that is ALSO permission-checked (Cancellable), so a regression
            // there would be caught by this same sweep.
            [OperationStatus.Running] = (
                registry => registry.Start("TEST", new OperationOptions { Kind = "X", Cancellable = true }).Id,
                (registry, id) => registry.Cancel(id)),

            // ONE entry since D66 cut the waiting band, and the test is still worth every line: it
            // enumerates OperationStatus REFLECTIVELY, so the day someone adds a non-terminal status
            // without a sanctioned exit, this fails by name rather than stranding every operation that
            // reaches it. A shrinking map does not weaken a guard that reads the enum itself.
        };

    private static OperationRegistry BuildRegistry() =>
        new(new EventBus(), new OperationRegistryOptions { ProgressInterval = TimeSpan.Zero });

    /// <summary>
    /// THE test. Enumerates <see cref="OperationStatus"/> via reflection (never a hardcoded list, so a
    /// future status is automatically included) and, for every value that is not one of the three
    /// terminal outcomes, requires a registered exit AND proves that exit actually lands on a terminal
    /// status through the real registry.
    /// </summary>
    [Fact]
    public void Every_non_terminal_status_has_a_registered_exit_that_reaches_terminal()
    {
        // Enum.GetValues<T>() PLUS OperationRegistry.IsTerminal — never a second hand-copied terminal
        // set (hardening, this batch's review): IsTerminal used to be duplicated here, so a status
        // classified terminal in the REGISTRY but missed in this file's own copy (or vice versa) could
        // silently be skipped by the sweep below. The registry's method is now `internal` (see its own
        // doc) precisely so this test can defer to it instead of re-declaring the classification.
        var nonTerminal = Enum.GetValues<OperationStatus>().Where(s => !OperationRegistry.IsTerminal(s)).ToArray();

        // Parser/self-check (the standing rule for every tripwire in this repo): a status set that
        // enumerated to nothing would make every assertion below vacuously pass.
        Assert.NotEmpty(nonTerminal);

        foreach (var status in nonTerminal)
        {
            Assert.True(NonTerminalExits.ContainsKey(status),
                $"OperationStatus.{status} has NO registered exit in this test (see NonTerminalExits). " +
                "A non-terminal status with no sanctioned path to Completed/Failed/Cancelled strands " +
                "every operation that reaches it — this is exactly the bug this test exists to catch.");

            var (reach, exit) = NonTerminalExits[status];
            var registry = BuildRegistry();
            var id = reach(registry);

            // Sanity-check the setup itself: if `reach` did not actually produce the status under
            // test, the exit check below would prove nothing about THIS status.
            Assert.Equal(status, registry.GetAll().Single(o => o.Id == id).Status);

            var exited = exit(registry, id);

            var final = registry.GetAll().SingleOrDefault(o => o.Id == id);
            Assert.True(exited,
                $"OperationStatus.{status}'s registered exit returned false — it refused to act, so " +
                "the operation is still stranded.");
            Assert.NotNull(final);   // a terminal entry is still visible immediately after finishing (before any prune)
            Assert.True(OperationRegistry.IsTerminal(final!.Status),
                $"OperationStatus.{status}'s registered exit did not reach a terminal state " +
                $"(ended at {final.Status} instead of Completed/Failed/Cancelled).");

            // Fold PRUNABILITY into the same sweep (hardening, this batch's review): the ORIGINAL bug
            // had two halves — no terminal exit AND never entering `_finishedOrder` — and the sweep
            // above only ever covered the first half. An exit that reaches a terminal status but
            // forgets to add the id to `_finishedOrder` would still strand the entry forever (never
            // evictable by `ClearFinished`/`MaxHistory`), just one step later than the original bug.
            registry.ClearFinished();
            Assert.Null(registry.GetAll().SingleOrDefault(o => o.Id == id));
        }
    }
}
