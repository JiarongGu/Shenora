using Shenora.Engine.Missions;

namespace Shenora.Tests.Missions;

/// <summary>
/// What happens to a mission whose lifetime collapses while it is QUEUED — the window between
/// <c>Task.Run</c> queueing a body and that body's first line.
///
/// <para>
/// 🔴 The failure these guard against is a HANG, not an exception: anything thrown before
/// <c>RunEntryAsync</c>'s try block leaves the entry in <c>_running</c>, never completes the
/// <see cref="TaskCompletionSource{T}"/> behind <see cref="IMissionScheduler.SubmitAsync"/>, and
/// discards the fault unobserved. A caller awaiting that task waits forever, with nothing logged.
/// </para>
/// </summary>
public class MissionSchedulerTeardownTests
{
    private static MissionDefinition Blocking(Task gate) =>
        new() { Kind = "block", Run = async (_, _) => await gate };

    private static MissionDefinition Trivial() =>
        new() { Kind = "trivial", Run = (_, _) => Task.CompletedTask };

    /// <summary>
    /// The synchronous <see cref="IDisposable"/> path disposes the shutdown source without awaiting
    /// in-flight bodies, so a body that had not yet read the shutdown token must still be able to.
    /// </summary>
    [Fact]
    public async Task Disposing_the_scheduler_still_completes_every_submitted_mission()
    {
        var scheduler = new MissionScheduler(new MissionSchedulerOptions { GlobalLaneCapacity = 1 });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blocking = scheduler.SubmitAsync(Blocking(gate.Task));
        var queued = scheduler.SubmitAsync(Trivial());

        scheduler.Dispose();                 // cancels queued work and disposes the shutdown source
        gate.SetResult();

        // Neither may hang. The queued one is cancelled outright; the blocking one unwinds on its own.
        var result = await queued.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(MissionOutcome.Cancelled, result.Outcome);
        await blocking.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
