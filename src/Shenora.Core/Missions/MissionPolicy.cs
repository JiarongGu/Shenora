namespace Shenora.Core;

/// <summary>
/// A lane and how many of its permits one mission needs.
///
/// <para>
/// The permit COUNT exists for long-term fit rather than for any current consumer: a lane is often a
/// budget (memory, VRAM, bandwidth) where items cost different amounts, not a slot count where every
/// item costs one. Adding this later would change the type of <see cref="MissionRequest.Lanes"/> and
/// break every caller, so it is here from the start — the cost of carrying it is one defaulted
/// parameter.
/// </para>
/// </summary>
/// <param name="Name">Lane name.</param>
/// <param name="Permits">Permits required. Must be at least 1; may exceed the lane's capacity only if capacity later grows.</param>
public readonly record struct MissionLane(string Name, int Permits = 1);

/// <summary>Read-only view of a queued item, passed to <see cref="IMissionPolicy"/>.</summary>
/// <param name="MissionId">Scheduler-assigned id.</param>
/// <param name="Kind">App-defined mission type from <see cref="MissionRequest.Kind"/>.</param>
/// <param name="Priority">From <see cref="MissionRequest.Priority"/>.</param>
/// <param name="QueuedUtc">When it was accepted.</param>
/// <param name="Sequence">Monotonic submission counter — the tie-break that makes ordering total and stable.</param>
public readonly record struct MissionView(
    string MissionId, string? Kind, int Priority, DateTimeOffset QueuedUtc, long Sequence);

/// <summary>What the scheduler is doing right now, passed to <see cref="IMissionPolicy"/>.</summary>
/// <param name="Pending">Accepted but not started.</param>
/// <param name="Running">Currently executing.</param>
public readonly record struct MissionSchedulerState(int Pending, int Running);

/// <summary>
/// The app's answer to <b>what</b> to pick up next and <b>when</b> to pick it up.
///
/// <para>
/// Scheduling order is a product decision, not a framework one — "user-initiated before background",
/// "smallest first", "nothing heavy before 9am", "pause while on battery" are all legitimate and
/// mutually exclusive, and a kit that hardcodes one of them gets forked by the second app that needs
/// another. So the kit ships the SAFETY rules (claim exclusion, lane capacity, no starvation) and
/// hands ordering and timing to the app.
/// </para>
///
/// <para>
/// <b>The safety boundary — the reason a custom policy cannot corrupt anything.</b> A policy is only
/// ever consulted about items that have ALREADY passed admission: their claims are free and their
/// lane permits are available. It chooses among legal moves. It cannot make conflicting work run
/// concurrently, cannot bypass a lane, and cannot reorder items that conflict with each other. The
/// worst a buggy policy can do is DELAY work — never corrupt it. That boundary is what makes this
/// safe to expose.
/// </para>
/// </summary>
public interface IMissionPolicy
{
    /// <summary>
    /// <b>When.</b> May this item start now? Returning false defers it and leaves it queued; it is
    /// re-asked on the next dispatch pass.
    ///
    /// <para>
    /// Dispatch runs on submit and on completion. A policy that defers on an EXTERNAL condition —
    /// a clock, system load, battery state — must therefore poke the scheduler when that condition
    /// changes, via <see cref="IMissionScheduler.Reevaluate"/>; otherwise deferred work waits for
    /// unrelated traffic to wake it. The kit deliberately owns no timer.
    /// </para>
    /// </summary>
    bool ShouldStart(in MissionView mission, in MissionSchedulerState state);

    /// <summary>
    /// <b>What.</b> Order two eligible items: negative if <paramref name="a"/> should start first.
    /// Must be a consistent total order — an inconsistent comparison makes dispatch order arbitrary.
    /// </summary>
    int Compare(in MissionView a, in MissionView b);
}

/// <summary>
/// The default policy: start anything eligible, highest <see cref="MissionRequest.Priority"/> first,
/// ties broken by submission order. With no priorities set this is plain FIFO, which is what every
/// implementation in the family did.
/// </summary>
public sealed class PriorityMissionPolicy : IMissionPolicy
{
    /// <summary>The shared instance — this policy holds no state.</summary>
    public static PriorityMissionPolicy Instance { get; } = new();

    /// <inheritdoc/>
    public bool ShouldStart(in MissionView mission, in MissionSchedulerState state) => true;

    /// <inheritdoc/>
    public int Compare(in MissionView a, in MissionView b)
    {
        var byPriority = b.Priority.CompareTo(a.Priority);   // higher priority first
        return byPriority != 0 ? byPriority : a.Sequence.CompareTo(b.Sequence);
    }
}
