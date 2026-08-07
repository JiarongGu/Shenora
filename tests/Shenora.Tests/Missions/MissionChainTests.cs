using Shenora.Core;
using Shenora.Missions;

namespace Shenora.Tests.Missions;

/// <summary>
/// Chained missions: steps in order, sharing one context, as ONE queue entry
/// (D29).
///
/// <para>
/// The properties worth pinning are the ones the design ARGUED for, not the ones that are obvious
/// from the code: that a chain is indistinguishable from any other mission to the scheduler, that the
/// claim union is the STRONGER mode, that a step's retry does not re-run earlier steps, and that a
/// failing step stops the chain.
/// </para>
/// </summary>
public class MissionChainTests
{
    private static MissionScheduler NewScheduler(params IClaimScope[] scopes) =>
        new(new MissionSchedulerOptions { GlobalLaneCapacity = 4, Scopes = scopes });

    [Fact]
    public async Task Steps_run_in_order_and_share_one_context()
    {
        await using var scheduler = NewScheduler();
        var order = new List<string>();

        var chain = MissionChain.Sequence("IMPORT",
            new MissionStep("stage", (_, context, _) =>
            {
                order.Add("stage");
                context.Set("temp", "/tmp/staged");
                return Task.CompletedTask;
            }),
            new MissionStep("commit", (_, context, _) =>
            {
                order.Add("commit");
                // The whole point of a chain: step 2 uses what step 1 produced.
                Assert.Equal("/tmp/staged", context.Get<string>("temp"));
                return Task.CompletedTask;
            }));

        var result = await scheduler.SubmitAsync(chain);

        Assert.Equal(MissionOutcome.Completed, result.Outcome);
        Assert.Equal(["stage", "commit"], order);
    }

    [Fact]
    public async Task A_chain_is_an_ordinary_mission_to_the_scheduler()
    {
        // The design's central claim: no new scheduling concept. If this ever needs a special case in
        // the scheduler, the "one entry, not N" decision has quietly been reversed.
        await using var scheduler = NewScheduler(new FlatClaimScope("entity"));

        var chain = MissionChain.Sequence("CHAIN",
            new MissionStep("only", (_, _, _) => Task.CompletedTask,
                Claims: [MissionClaim.Exclusive("entity", "e1")]));

        Assert.Single(chain.Claims);
        Assert.Equal(MissionOutcome.Completed, (await scheduler.SubmitAsync(chain)).Outcome);
    }

    [Theory]
    [InlineData(false)]   // read then write
    [InlineData(true)]    // write then read — the order that catches a naive "last wins"
    public async Task The_claim_union_takes_the_stronger_mode_whichever_order_the_steps_are_in(bool writeFirst)
    {
        // A chain that READS a key in one step and WRITES it in another must hold it exclusively —
        // otherwise the write runs while another mission holds a shared claim it thinks is safe.
        //
        // BOTH orders are here because the first version of this test only had read-then-write, and a
        // deliberate "last wins" sabotage PASSED it: the exclusive claim happened to be last. A test
        // that cannot fail the thing it names is worth nothing (phase-workflow.md).
        await using var scheduler = NewScheduler(new FlatClaimScope("entity"));

        var read = new MissionStep("read", (_, _, _) => Task.CompletedTask,
            Claims: [MissionClaim.Shared("entity", "e1")]);
        var write = new MissionStep("write", (_, _, _) => Task.CompletedTask,
            Claims: [MissionClaim.Exclusive("entity", "e1")]);

        var chain = writeFirst
            ? MissionChain.Sequence("WRITE_THEN_READ", write, read)
            : MissionChain.Sequence("READ_THEN_WRITE", read, write);

        var claim = Assert.Single(chain.Claims);
        Assert.Equal(ClaimMode.Exclusive, claim.Mode);
        Assert.Equal(MissionOutcome.Completed, (await scheduler.SubmitAsync(chain)).Outcome);
    }

    [Fact]
    public async Task Duplicate_claims_across_steps_collapse_to_one()
    {
        await using var scheduler = NewScheduler(new FlatClaimScope("entity"));

        var chain = MissionChain.Sequence("TWICE",
            new MissionStep("a", (_, _, _) => Task.CompletedTask, Claims: [MissionClaim.Exclusive("entity", "e1")]),
            new MissionStep("b", (_, _, _) => Task.CompletedTask, Claims: [MissionClaim.Exclusive("entity", "e1")]));

        // Naming one claim twice would have the chain wait on itself if claims were counted.
        Assert.Single(chain.Claims);
        Assert.Equal(MissionOutcome.Completed, (await scheduler.SubmitAsync(chain)).Outcome);
    }

    [Fact]
    public async Task A_step_retry_repeats_only_that_step()
    {
        // The reason RunStepAsync copies the retry rule instead of delegating to the scheduler's: a
        // mission-level retry would re-run step 1 when step 2 failed.
        await using var scheduler = NewScheduler();
        var first = 0;
        var second = 0;

        var chain = MissionChain.Sequence("FLAKY",
            new MissionStep("first", (_, _, _) => { first++; return Task.CompletedTask; }),
            new MissionStep("second", (_, _, _) =>
            {
                second++;
                return second < 3 ? throw new IOException("locked") : Task.CompletedTask;
            }, Retry: new RetryPolicy { Attempts = 3, Delay = TimeSpan.FromMilliseconds(5) }));

        var result = await scheduler.SubmitAsync(chain);

        Assert.Equal(MissionOutcome.Completed, result.Outcome);
        Assert.Equal(1, first);    // NOT re-run
        Assert.Equal(3, second);
    }

    [Fact]
    public async Task A_failing_step_stops_the_chain_and_reports_through_the_mission()
    {
        await using var scheduler = NewScheduler();
        var reached = false;

        var chain = MissionChain.Sequence("BREAKS",
            new MissionStep("boom", (_, _, _) => throw new InvalidOperationException("step failed")),
            new MissionStep("never", (_, _, _) => { reached = true; return Task.CompletedTask; }));

        var result = await scheduler.SubmitAsync(chain);

        Assert.Equal(MissionOutcome.Failed, result.Outcome);
        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.False(reached, "a later step must not run after an earlier one failed");
    }

    [Fact]
    public async Task Cancelling_the_mission_cancels_the_chain_between_steps()
    {
        await using var scheduler = NewScheduler();
        using var cts = new CancellationTokenSource();
        var second = false;

        var chain = MissionChain.Sequence("CANCELS",
            new MissionStep("first", async (_, _, ct) =>
            {
                await cts.CancelAsync();
                await Task.Yield();
            }),
            new MissionStep("second", (_, _, _) => { second = true; return Task.CompletedTask; }));

        var result = await scheduler.SubmitAsync(chain, cts.Token);

        Assert.Equal(MissionOutcome.Cancelled, result.Outcome);
        Assert.False(second, "cancelling any step cancels the chain — one mission, one token");
    }

    [Fact]
    public async Task The_context_reports_which_step_is_running()
    {
        await using var scheduler = NewScheduler();
        var seen = new List<(int Index, string Name, int Count)>();

        var chain = MissionChain.Sequence("LABELS",
            new MissionStep("one", (_, c, _) => { seen.Add((c.StepIndex, c.StepName, c.StepCount)); return Task.CompletedTask; }),
            new MissionStep("two", (_, c, _) => { seen.Add((c.StepIndex, c.StepName, c.StepCount)); return Task.CompletedTask; }));

        await scheduler.SubmitAsync(chain);

        Assert.Equal([(0, "one", 2), (1, "two", 2)], seen);
    }

    [Fact]
    public void An_empty_chain_is_a_caller_bug()
    {
        Assert.Throws<ArgumentException>(() => MissionChain.Sequence("EMPTY"));
    }
}
