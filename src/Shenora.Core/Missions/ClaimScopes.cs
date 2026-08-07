using Shenora.Core;

namespace Shenora.Missions;

/// <summary>
/// A named key space plus its conflict rule — the seam that lets ONE scheduler serve resource kinds
/// whose notions of "these two keys overlap" differ.
///
/// <para>
/// This is the pivot of the whole design. A filesystem planner and a job queue were, in the family's
/// prior art, two unrelated components of ~500 lines each; they differ only in what makes two keys
/// conflict. Paths conflict when one CONTAINS the other; entity ids conflict only when equal. Put
/// that difference behind this interface and the rest — submission order, bounded parallelism,
/// dispatch, dedup, retry, cancellation — is written once.
/// </para>
/// </summary>
public interface IClaimScope
{
    /// <summary>Scope name, matched against <see cref="MissionClaim.Scope"/>. Case-sensitive.</summary>
    string Name { get; }

    /// <summary>
    /// Canonical form of a key. Called ONCE per claim at submit time, so conflict checks — which run
    /// on every dispatch pass — never pay for normalization.
    /// </summary>
    string Normalize(string key);

    /// <summary>
    /// Whether two ALREADY-NORMALIZED keys refer to overlapping resources. Must be symmetric and
    /// reflexive; the scheduler relies on both.
    /// </summary>
    bool Conflicts(string normalizedA, string normalizedB);
}

/// <summary>
/// Keys conflict only when EQUAL. The scope for entity ids, categories, queue names — anything
/// without hierarchy.
/// </summary>
public sealed class FlatClaimScope : IClaimScope
{
    private readonly StringComparison _comparison;

    /// <param name="name">Scope name.</param>
    /// <param name="ignoreCase">Compare keys case-insensitively (ordinal). Default false.</param>
    public FlatClaimScope(string name, bool ignoreCase = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        _comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public string Normalize(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _comparison == StringComparison.OrdinalIgnoreCase ? key.ToUpperInvariant() : key;
    }

    /// <inheritdoc/>
    public bool Conflicts(string normalizedA, string normalizedB) =>
        string.Equals(normalizedA, normalizedB, StringComparison.Ordinal);
}

/// <summary>
/// Keys conflict when equal OR when one CONTAINS the other — a hierarchical namespace separated by
/// a delimiter. Generalized from the family's path-overlap dispatcher, but nothing here is about
/// filesystems: tree nodes, registry keys and URL prefixes have the same rule. For platform paths
/// use <see cref="PathClaims"/>, which is this class pre-configured.
///
/// <para>
/// The containment test is taken at a SEPARATOR BOUNDARY, which is the part that is easy to get
/// wrong: a naive <c>StartsWith</c> makes <c>a/bc</c> a child of <c>a/b</c>, so two unrelated
/// resources serialize against each other forever and the bug looks like "the queue is slow".
/// </para>
/// </summary>
public sealed class NestedClaimScope : IClaimScope
{
    private readonly char _separator;
    private readonly bool _ignoreCase;

    /// <param name="name">Scope name.</param>
    /// <param name="separator">The hierarchy delimiter.</param>
    /// <param name="ignoreCase">Compare keys case-insensitively (ordinal).</param>
    public NestedClaimScope(string name, char separator, bool ignoreCase = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        _separator = separator;
        _ignoreCase = ignoreCase;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <summary>
    /// Collapses repeated separators, trims a trailing one, and upper-cases when case-insensitive —
    /// so <c>a//b/</c> and <c>a/b</c> are the same resource. Without this, two spellings of one key
    /// pass the conflict test and run concurrently, which is the failure this scope exists to stop.
    /// </summary>
    public string Normalize(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var span = key.AsSpan().Trim();
        var buffer = new System.Text.StringBuilder(span.Length);
        var lastWasSeparator = false;
        foreach (var ch in span)
        {
            var isSeparator = ch == _separator;
            if (isSeparator && lastWasSeparator) continue;
            buffer.Append(ch);
            lastWasSeparator = isSeparator;
        }
        // A trailing separator names the same resource as the bare key.
        if (buffer.Length > 1 && buffer[^1] == _separator) buffer.Length--;
        var result = buffer.ToString();
        return _ignoreCase ? result.ToUpperInvariant() : result;
    }

    /// <inheritdoc/>
    public bool Conflicts(string normalizedA, string normalizedB)
    {
        if (string.Equals(normalizedA, normalizedB, StringComparison.Ordinal)) return true;
        return Contains(normalizedA, normalizedB) || Contains(normalizedB, normalizedA);
    }

    /// <summary>True when <paramref name="parent"/> is a strict ancestor of <paramref name="child"/>.</summary>
    private bool Contains(string parent, string child)
    {
        if (parent.Length >= child.Length) return false;
        if (!child.StartsWith(parent, StringComparison.Ordinal)) return false;
        // The boundary check: "a/b" contains "a/b/c" but NOT "a/bc".
        return child[parent.Length] == _separator;
    }
}
