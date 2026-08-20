using Shenora.Engine.Files;

namespace Shenora.Engine.Missions;

/// <summary>
/// A named key space plus its conflict rule — the seam that lets ONE scheduler serve resource kinds
/// whose notions of "these two keys overlap" differ: paths conflict when one CONTAINS the other,
/// entity ids only when equal.
/// </summary>
public interface IClaimScope
{
    /// <summary>Scope name, matched against <see cref="MissionClaim.Scope"/>. Case-sensitive.</summary>
    string Name { get; }

    /// <summary>Canonical form of a key. Called ONCE per claim at submit time, never on a dispatch pass.</summary>
    string Normalize(string key);

    /// <summary>
    /// Whether two ALREADY-NORMALIZED keys refer to overlapping resources. Must be symmetric and
    /// reflexive; the scheduler relies on both.
    /// </summary>
    bool Conflicts(string normalizedA, string normalizedB);
}

/// <summary>Keys conflict only when EQUAL — entity ids, categories, queue names, anything flat.</summary>
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
/// Keys conflict when equal OR when one CONTAINS the other, at a SEPARATOR BOUNDARY — a hierarchical
/// namespace such as tree nodes, registry keys or URL prefixes. For platform paths use
/// <see cref="PathClaims"/>, which is this class pre-configured.
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
    /// Collapses repeated separators, trims a trailing one, and upper-cases when case-insensitive — so
    /// <c>a//b/</c> and <c>a/b</c> are the same resource. ⚠ Two spellings that survive this pass the
    /// conflict test and run concurrently.
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
