using Shenora.Missions;

namespace Shenora.Core;

/// <summary>
/// Filesystem paths as scheduler claims, plus the containment test every app that maps input to a
/// path needs.
///
/// <para>
/// This is the whole of what makes a <see cref="MissionScheduler"/> into the family's file-operation
/// planner: register <see cref="Scope"/>, attach a claim per path an operation touches, and
/// overlapping work serializes while disjoint work runs in parallel. The ~550-line planners two of
/// the sibling apps each maintain are this plus their archive code.
/// </para>
/// </summary>
public static class PathClaims
{
    /// <summary>The <see cref="MissionClaim.Scope"/> name these helpers produce.</summary>
    public const string ScopeName = "path";

    /// <summary>
    /// The claim scope to register in <see cref="MissionSchedulerOptions.Scopes"/>. Hierarchical, so
    /// <c>C:\a</c> conflicts with <c>C:\a\b</c> — which is the point: deleting a directory must not
    /// run while something writes a file inside it. Case-insensitive on Windows only.
    /// </summary>
    public static NestedClaimScope Scope { get; } =
        new(ScopeName, Path.DirectorySeparatorChar, ignoreCase: OperatingSystem.IsWindows());

    /// <summary>An exclusive claim on <paramref name="path"/> — for anything that MUTATES it.</summary>
    public static MissionClaim Exclusive(string path) => MissionClaim.Exclusive(ScopeName, Canonical(path));

    /// <summary>
    /// A shared claim on <paramref name="path"/> — for readers. Several readers of one path run
    /// together; a writer waits for them. None of the family's planners could express this, so they
    /// serialized reads behind writes they did not conflict with.
    /// </summary>
    public static MissionClaim Shared(string path) => MissionClaim.Shared(ScopeName, Canonical(path));

    /// <summary>
    /// Absolute, separator-normalized form — resolving <c>..</c>, <c>.</c> and mixed separators so
    /// two spellings of one location compare equal.
    ///
    /// <para>
    /// Doing this BEFORE the claim is what makes the exclusion sound: <c>data\mods\..\mods\x</c> and
    /// <c>data/mods/x</c> are the same directory, and a scheduler that treated them as different
    /// keys would happily run two mutations on it at once.
    /// </para>
    /// </summary>
    public static string Canonical(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            full = full.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return full;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="root"/> itself or sits underneath it,
    /// compared after full normalization.
    ///
    /// <para>
    /// The guard for anything that turns caller-supplied input into a path — a resource request, an
    /// import target, a cleanup sweep. Two traps this closes, and a naive check misses both: a
    /// <c>..</c> segment escaping the root (which <see cref="Canonical"/> resolves first), and a
    /// prefix match without a separator boundary, where <c>C:\data-old</c> passes as being inside
    /// <c>C:\data</c>.
    /// </para>
    /// </summary>
    /// <param name="root">The directory the candidate must not escape.</param>
    /// <param name="candidate">The path to test.</param>
    public static bool IsContained(string root, string candidate)
    {
        var normalizedRoot = TrimSeparator(Canonical(root));
        var normalizedCandidate = TrimSeparator(Canonical(candidate));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(normalizedRoot, normalizedCandidate, comparison)) return true;
        if (!normalizedCandidate.StartsWith(normalizedRoot, comparison)) return false;
        return normalizedCandidate[normalizedRoot.Length] == Path.DirectorySeparatorChar;
    }

    private static string TrimSeparator(string path) =>
        path.Length > 1 ? path.TrimEnd(Path.DirectorySeparatorChar) : path;
}
