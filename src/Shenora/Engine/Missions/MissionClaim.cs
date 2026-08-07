using Shenora;

namespace Shenora.Engine.Missions;

/// <summary>How a <see cref="MissionClaim"/> excludes other work on a conflicting key.</summary>
public enum ClaimMode
{
    /// <summary>Nothing else may hold a conflicting key while this is held.</summary>
    Exclusive = 0,

    /// <summary>
    /// Other <see cref="Shared"/> holders of a conflicting key may run concurrently; an
    /// <see cref="Exclusive"/> holder may not. The reader half of a reader/writer split.
    /// </summary>
    Shared = 1,
}

/// <summary>
/// One resource a <see cref="MissionDefinition"/> needs before it may run: a key inside a named
/// <see cref="IClaimScope"/>, held either exclusively or shared.
///
/// <para>
/// Claims are how this scheduler expresses "these two pieces of work must not overlap" WITHOUT the
/// caller taking a lock. That matters more than it first looks: the family's prior art took real
/// locks per resource, and both of the bugs that cost the most came from owning lock lifetime by
/// hand — a check-then-remove race while cleaning up a per-key semaphore (two callers got DIFFERENT
/// semaphores for the same key, so the key was not actually serialized), and a lock-ORDER rule
/// between two key spaces that every call site had to remember. A request declares its whole claim
/// SET up front and the scheduler admits it only when all of it is free, so there is no per-key lock
/// object to leak and no acquisition order to get wrong.
/// </para>
/// </summary>
/// <param name="Scope">Name of the <see cref="IClaimScope"/> that owns this key space.</param>
/// <param name="Key">The resource key, interpreted by that scope.</param>
/// <param name="Mode">Exclusive or shared.</param>
public readonly record struct MissionClaim(string Scope, string Key, ClaimMode Mode)
{
    /// <summary>An exclusive claim on <paramref name="key"/> within <paramref name="scope"/>.</summary>
    public static MissionClaim Exclusive(string scope, string key) => new(scope, key, ClaimMode.Exclusive);

    /// <summary>A shared claim on <paramref name="key"/> within <paramref name="scope"/>.</summary>
    public static MissionClaim Shared(string scope, string key) => new(scope, key, ClaimMode.Shared);
}
