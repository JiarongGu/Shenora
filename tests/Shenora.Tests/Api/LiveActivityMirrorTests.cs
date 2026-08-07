using System.Reflection;
using System.Text.RegularExpressions;
using Shenora;

namespace Shenora.Tests.Api;

/// <summary>
/// The tripwire on the C#⇄SWIFT state mirror. <c>Shenora.LiveActivityState</c> and the
/// <c>ShenoraActivityState</c> struct in <c>src/Shenora.iOS/buildTransitive/swift/ShenoraLiveActivity.swift</c> are
/// the same shape written twice, and they HAVE to be: ActivityKit decodes the Swift side from JSON the C#
/// side wrote.
/// <para>
/// <b>Drift here fails completely silently.</b> A field renamed on one side decodes to nil on the other, the
/// activity either does not appear or shows a stale value, and nothing anywhere raises an error — no
/// exception, no log line, no build warning. That is the same failure class the C#⇄TS IPC wire mirror is
/// guarded against, for the same reason, and this is the same kind of guard.
/// </para>
/// <para>
/// It reads the Swift as TEXT rather than parsing it, deliberately: a real Swift parser would be a large
/// dependency to catch a two-field mismatch, and the check that matters is "do the names and optionality
/// line up", which a regex answers honestly. The failure message names the offending field.
/// </para>
/// </summary>
public class LiveActivityMirrorTests
{
    private static string RepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(LiveActivityMirrorTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly.");
    }

    private static string SwiftPath() =>
        Path.Combine(RepoRoot(), "src", "Shenora.iOS", "buildTransitive", "swift", "ShenoraLiveActivity.swift");

    /// <summary>
    /// The <c>var name: Type</c> declarations inside the mirrored struct. Scoped to that struct so the
    /// attributes type's own <c>name</c> field — which is NOT part of the mirror — cannot satisfy the check.
    /// </summary>
    private static Dictionary<string, string> SwiftMirrorFields()
    {
        var source = File.ReadAllText(SwiftPath());
        var start = source.IndexOf("public struct ShenoraActivityState", StringComparison.Ordinal);
        Assert.True(start >= 0, $"ShenoraActivityState not found in {SwiftPath()} — was it renamed?");

        // From the struct header to its initialiser, which is where the stored properties end.
        var end = source.IndexOf("public init(", start, StringComparison.Ordinal);
        Assert.True(end > start, "ShenoraActivityState has no init(…) — the field scan needs that boundary.");
        var body = source[start..end];

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var match in Regex.Matches(body, @"var\s+(\w+)\s*:\s*([\w?]+)").Cast<Match>())
            fields[match.Groups[1].Value] = match.Groups[2].Value;
        return fields;
    }

    [Fact]
    public void The_Swift_mirror_has_exactly_the_same_fields_as_the_C_sharp_record()
    {
        var swift = SwiftMirrorFields();
        // camelCase, because that is what the serializer writes (JsonNamingPolicy.CamelCase) and therefore
        // what Swift's synthesised Codable keys must match.
        var expected = typeof(LiveActivityState)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(expected);   // self-check: a reflection call that found nothing must not pass

        var missingInSwift = expected.Except(swift.Keys).Order(StringComparer.Ordinal).ToArray();
        var extraInSwift = swift.Keys.Except(expected).Order(StringComparer.Ordinal).ToArray();

        Assert.True(missingInSwift.Length == 0,
            $"LiveActivityState has field(s) the Swift mirror does not: {string.Join(", ", missingInSwift)}. "
            + $"Add them to ShenoraActivityState in {SwiftPath()} — a field only C# knows about decodes to "
            + "nil on the Swift side and the activity silently shows nothing.");

        Assert.True(extraInSwift.Length == 0,
            $"The Swift mirror has field(s) LiveActivityState does not: {string.Join(", ", extraInSwift)}. "
            + "Either add them to the record or remove them from the Swift; a field only Swift knows about "
            + "is never populated.");
    }

    [Fact]
    public void Every_mirrored_field_is_OPTIONAL_on_both_sides()
    {
        // The serializer omits nulls, so every Swift property must tolerate a missing key — a
        // non-optional there makes the WHOLE decode fail, which takes the activity with it rather than
        // just that field. This is the subtler half of the mirror and the one a reader would not guess.
        var swift = SwiftMirrorFields();
        var notOptional = swift.Where(f => !f.Value.EndsWith('?')).Select(f => f.Key).Order().ToArray();

        Assert.True(notOptional.Length == 0,
            $"These Swift mirror fields are not optional: {string.Join(", ", notOptional)}. "
            + "C# omits nulls when serializing, so a non-optional Swift property fails the entire decode "
            + "and nothing renders at all.");

        var csharpRequired = typeof(LiveActivityState)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttributes()
                .Any(a => a.GetType().Name == "RequiredMemberAttribute"))
            .Select(p => p.Name)
            .ToArray();

        Assert.True(csharpRequired.Length == 0,
            $"LiveActivityState has required member(s): {string.Join(", ", csharpRequired)}. Keep every "
            + "field optional — the Swift side cannot express 'required' through a JSON payload that omits "
            + "nulls, so a required field here is a contract the mirror cannot honour.");
    }
}
