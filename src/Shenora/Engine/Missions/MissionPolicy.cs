namespace Shenora.Engine.Missions;

/// <summary>
/// A lane and how many of its permits one mission needs — a weighted count, for a lane that is a budget
/// (memory, VRAM, bandwidth) rather than a slot count.
/// </summary>
/// <param name="Name">Lane name.</param>
/// <param name="Permits">Permits required. Must be at least 1; may exceed the lane's capacity only if capacity later grows.</param>
public readonly record struct MissionLane(string Name, int Permits = 1);

/// <summary>What the scheduler is doing right now, passed to <see cref="IMissionPolicy"/>.</summary>
/// <param name="Pending">Accepted but not started.</param>
/// <param name="Running">Currently executing.</param>
public readonly record struct MissionSchedulerState(int Pending, int Running);

/// <summary>
/// The app's answer to <b>what</b> to pick up next and <b>when</b> to pick it up. The kit keeps the
/// SAFETY rules (claim exclusion, lane capacity, no starvation): a policy is only ever consulted about
/// items that have ALREADY passed admission, so the worst a buggy one can do is DELAY work.
/// </summary>
public interface IMissionPolicy
{
    /// <summary>
    /// <b>When.</b> May this item start now? Returning false defers it and leaves it queued; it is
    /// re-asked on the next dispatch pass. ⚠ Deferring on an EXTERNAL condition — a clock, system load,
    /// battery state — needs <see cref="IMissionScheduler.Reevaluate"/> when that condition changes, or
    /// the item waits for unrelated traffic to wake it.
    /// </summary>
    bool ShouldStart(in MissionExecution mission, in MissionSchedulerState state);

    /// <summary>
    /// <b>What.</b> Order two eligible items: negative if <paramref name="a"/> should start first.
    /// Must be a consistent total order — an inconsistent comparison makes dispatch order arbitrary.
    /// </summary>
    int Compare(in MissionExecution a, in MissionExecution b);
}

/// <summary>
/// The default policy: start anything eligible, highest <see cref="MissionDefinition.Priority"/> first,
/// ties broken by submission order. With no priorities set this is plain FIFO.
/// </summary>
public sealed class PriorityMissionPolicy : IMissionPolicy
{
    /// <summary>The shared instance — this policy holds no state.</summary>
    public static PriorityMissionPolicy Instance { get; } = new();

    /// <inheritdoc/>
    public bool ShouldStart(in MissionExecution mission, in MissionSchedulerState state) => true;

    /// <inheritdoc/>
    public int Compare(in MissionExecution a, in MissionExecution b)
    {
        var byPriority = b.Priority.CompareTo(a.Priority);   // higher priority first
        return byPriority != 0 ? byPriority : a.Sequence.CompareTo(b.Sequence);
    }
}
