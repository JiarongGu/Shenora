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
/// <see cref="IClaimScope"/>, held either exclusively or shared. A mission declares its whole claim SET
/// up front and is admitted only when all of it is free, so there is no per-key lock object to leak and
/// no acquisition order to get wrong.
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
