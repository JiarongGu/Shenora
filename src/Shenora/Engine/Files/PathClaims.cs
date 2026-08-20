using Shenora.Engine.Missions;

namespace Shenora.Engine.Files;

/// <summary>
/// Filesystem paths as scheduler claims, plus the containment test every app that maps input to a
/// path needs. Register <see cref="Scope"/> with a <see cref="MissionScheduler"/> and attach a claim
/// per path an operation touches: overlapping work serializes, disjoint work runs in parallel.
/// </summary>
public static class PathClaims
{
    /// <summary>The <see cref="MissionClaim.Scope"/> name these helpers produce.</summary>
    public const string ScopeName = "path";

    /// <summary>
    /// The claim scope to register in <see cref="MissionSchedulerOptions.Scopes"/>. Hierarchical, so
    /// <c>C:\a</c> conflicts with <c>C:\a\b</c> — deleting a directory must not run while something
    /// writes a file inside it. Case-insensitive on Windows only.
    /// </summary>
    public static NestedClaimScope Scope { get; } =
        new(ScopeName, Path.DirectorySeparatorChar, ignoreCase: PathComparison.IgnoresCase);

    /// <summary>An exclusive claim on <paramref name="path"/> — for anything that MUTATES it.</summary>
    public static MissionClaim Exclusive(string path) => MissionClaim.Exclusive(ScopeName, Canonical(path));

    /// <summary>
    /// A shared claim on <paramref name="path"/> — for readers. Several readers of one path run
    /// together; a writer waits for them.
    /// </summary>
    public static MissionClaim Shared(string path) => MissionClaim.Shared(ScopeName, Canonical(path));

    /// <summary>
    /// Absolute, separator-normalized form — resolving <c>..</c>, <c>.</c> and mixed separators so
    /// two spellings of one location compare equal, and therefore claim the same key.
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
    /// compared after full normalization — the guard for anything that turns caller-supplied input
    /// into a path.
    /// <para>
    /// ⚠ Closes two traps a naive check misses: a <c>..</c> segment escaping the root (which
    /// <see cref="Canonical"/> resolves first), and a prefix match without a separator boundary, where
    /// <c>C:\data-old</c> passes as being inside <c>C:\data</c>.
    /// </para>
    /// </summary>
    /// <param name="root">The directory the candidate must not escape.</param>
    /// <param name="candidate">The path to test.</param>
    public static bool IsContained(string root, string candidate)
    {
        var normalizedRoot = TrimSeparator(Canonical(root));
        var normalizedCandidate = TrimSeparator(Canonical(candidate));
        // Shared with the serving-side check so the two can never disagree about case — see PathComparison
        // for why a test cannot hold this.
        var comparison = PathComparison.ForPaths;

        if (string.Equals(normalizedRoot, normalizedCandidate, comparison)) return true;
        if (!normalizedCandidate.StartsWith(normalizedRoot, comparison)) return false;
        return normalizedCandidate[normalizedRoot.Length] == Path.DirectorySeparatorChar;
    }

    private static string TrimSeparator(string path) =>
        path.Length > 1 ? path.TrimEnd(Path.DirectorySeparatorChar) : path;
}
