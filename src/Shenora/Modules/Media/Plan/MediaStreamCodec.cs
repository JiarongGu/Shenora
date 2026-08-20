namespace Shenora.Modules.Media;

/// <summary>
/// A codec, optionally narrowed to a PROFILE — the identity every capability question in this tier is keyed
/// by. 🔴 A bare name is not enough: <c>HEVC Main 10</c> is a different capability from the <c>hevc</c> a
/// device advertises, so a name alone can say "supported" about a stream that will not decode, and the file
/// then plays nothing with no error anywhere.
/// <para>
/// <b>THE MATCHING RULE:</b> a codec with NO profile matches ANY profile; a codec WITH one matches only that
/// profile. It is asymmetric — the POLICY (or device) side may be broad, the STREAM side is the concrete
/// thing asked about, so call <see cref="Matches"/> from the capability side and pass the stream's codec.
/// </para>
/// </summary>
/// <param name="Name">Lowercase codec name as a probe reports it — <c>h264</c>, <c>hevc</c>, <c>aac</c>.</param>
/// <param name="Profile">The profile when it is known (<c>Main 10</c>, <c>High</c>), else null meaning
/// "any".</param>
public readonly record struct MediaStreamCodec(string Name, string? Profile = null)
{
    /// <summary>A bare name, so <c>"h264"</c> still works wherever a codec is expected.</summary>
    public static implicit operator MediaStreamCodec(string name) => new(name);

    /// <summary>Does this capability cover <paramref name="stream"/>? Names must match; a null
    /// <see cref="Profile"/> here accepts any profile, a non-null one must match exactly.</summary>
    public bool Matches(MediaStreamCodec stream) =>
        string.Equals(Name, stream.Name, StringComparison.OrdinalIgnoreCase)
        && (Profile is null || string.Equals(Profile, stream.Profile, StringComparison.OrdinalIgnoreCase));

    /// <summary>Case-insensitive on both halves — generators disagree about casing.</summary>
    public bool Equals(MediaStreamCodec other) =>
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Profile, other.Profile, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        Name?.ToLowerInvariant(), Profile?.ToLowerInvariant());

    /// <inheritdoc />
    public override string ToString() => Profile is null ? Name : $"{Name}/{Profile}";
}

/// <summary>Matching over a set of capabilities, so the rule lives in ONE place.</summary>
public static class MediaStreamCodecExtensions
{
    /// <summary>
    /// Does any capability in <paramref name="capabilities"/> cover <paramref name="stream"/>? ⚠ Not
    /// <c>Contains</c>: a set lookup is exact equality, which would make a device advertising <c>hevc</c>
    /// fail to match a stream probed as <c>hevc/Main 10</c> — the opposite of the rule.
    /// </summary>
    public static bool Covers(this IReadOnlySet<MediaStreamCodec> capabilities, MediaStreamCodec stream)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        foreach (var capability in capabilities)
        {
            if (capability.Matches(stream)) return true;
        }
        return false;
    }
}
