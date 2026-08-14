using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Shenora;
using Shenora.Modules.Platform;
using Shenora.Modules.Platform.Activities;

namespace Shenora.Tests.Api;

/// <summary>
/// The tripwire on the C#⇄SWIFT state mirror. <c>Shenora.Modules.Platform.LiveActivityState</c> and the
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

    private static string LayoutSwiftPath() =>
        Path.Combine(RepoRoot(), "src", "Shenora.iOS", "buildTransitive", "swift", "ShenoraLayout.swift");

    /// <summary>
    /// The wire half — the `Codable` types, split out of <c>ShenoraLayout.swift</c> so they import nothing
    /// and can be compiled by a bare <c>swiftc</c> (see <see cref="LiveActivityGoldenTests"/>).
    /// </summary>
    private static string LayoutWireSwiftPath() =>
        Path.Combine(RepoRoot(), "src", "Shenora.iOS", "buildTransitive", "swift", "ShenoraLayoutWire.swift");

    /// <summary>
    /// Both halves as one text. The decoder's <c>case "text":</c> arms live in the WIRE file and the
    /// renderer's <c>case "Headline":</c> arms live beside the views, so a check that spans the two — "is
    /// every kind and every enum member known to the interpreter?" — has to read both or it silently
    /// answers about half the question.
    /// </summary>
    private static string InterpreterSwift() =>
        File.ReadAllText(LayoutWireSwiftPath()) + "\n" + File.ReadAllText(LayoutSwiftPath());

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

    // ── the LAYOUT half of the same wire ─────────────────────────────────────────────────────────────
    //
    // 🔴 The state mirror above existed and the layout wire still broke, because the two halves fail
    // differently. A missing STATE field shows a stale value; a mismatched layout DISCRIMINATOR or enum
    // member makes the interpreter fall back to its own default and render something plausible and wrong.
    // Measured 2026-08-09: enums went over as NUMBERS, so every Horizontal stack laid out vertically and
    // every role rendered as body text — with the payload parsing cleanly at both ends.

    /// <summary>
    /// ⚠ <b>THE SHIPPED OPTIONS, not a reproduction of them.</b> This was a hand-copy of
    /// <c>IosLiveActivities</c>'s own <c>JsonSerializerOptions</c> until 2026-08-10, which is a tripwire
    /// with the wire on both sides of it: dropping <c>CamelCase</c> from the shipped copy would have
    /// produced the one payload shape the Swift decoder cannot read, with this test still green.
    /// <para>
    /// ⚠ <c>ActivityWire.Json</c> deliberately registers NO enum converter — the enum TYPES carry their
    /// own, so a call site that forgets is not a bug that can happen. Adding one there stops this test
    /// testing anything.
    /// </para>
    /// </summary>
    private static JsonSerializerOptions WireJson => ActivityWire.Json;

    [Fact]
    public void Layout_enums_go_over_the_wire_as_NAMES_not_numbers()
    {
        var json = JsonSerializer.Serialize(
            new Presentation
            {
                CompactTrailing = new Layout
                {
                    Axis = Axis.Horizontal,
                    Children = [new Text("x", TextRole.Value)],
                },
            },
            WireJson);

        Assert.Contains("\"axis\":\"Horizontal\"", json);
        Assert.Contains("\"role\":\"Value\"", json);
    }

    [Fact]
    public void Every_element_kind_and_enum_member_is_handled_by_the_Swift_interpreter()
    {
        var swift = InterpreterSwift();

        // The discriminators are the wire. They come off the attributes rather than a hand-kept list, so a
        // NEW element type is caught the moment it is declared with no interpreter case.
        var kinds = typeof(Element)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(a => a.TypeDiscriminator?.ToString())
            .OfType<string>()
            .ToArray();
        Assert.NotEmpty(kinds);   // self-check: reflection that found nothing must not pass

        var unhandled = kinds.Where(k => !swift.Contains($"case \"{k}\":", StringComparison.Ordinal)).ToArray();
        Assert.True(unhandled.Length == 0,
            $"The Swift interpreter has no case for element kind(s): {string.Join(", ", unhandled)}. "
            + $"Add them to ShenoraElement's decoder in {LayoutWireSwiftPath()} — an unmatched kind decodes to "
            + ".unknown and renders as NOTHING, which reads as a layout the widget never received.");

        // Enum members are compared as literals on the Swift side, so a rename on either side is silent.
        var members = Enum.GetNames<Axis>().Concat(Enum.GetNames<TextRole>()).ToArray();
        var missing = members.Where(m => !swift.Contains($"\"{m}\"", StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0,
            $"The Swift interpreter never mentions enum member(s): {string.Join(", ", missing)}. "
            + "It compares against the member NAME, so one it does not know falls through to its default "
            + "arm and renders the wrong thing rather than failing.");
    }

    /// <summary>
    /// 🔴 <b>A REGION NOBODY READS IS D63's DEFECT, AND IT SHIPPED IN THE RELEASE THAT ADDED THE FEATURE.</b>
    /// <c>Presentation.Expanded</c> was declared, documented, serialized, mirrored in Swift and
    /// decoded — and no view ever consulted it, so an app describing the expanded card got the kit's
    /// default and would have read that as "my layout was ignored". Nothing threw, logged or failed:
    /// ABSENT is indistinguishable from working, which is the whole reason D63 asks for a test that
    /// asserts the seam was USED rather than that it exists.
    /// </summary>
    [Fact]
    public void Every_layout_region_is_consulted_by_the_default_views()
    {
        var views = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Shenora.iOS", "buildTransitive", "swift", "ShenoraDefaultViews.swift"));

        var regions = typeof(Presentation)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .ToArray();
        Assert.NotEmpty(regions);   // self-check: reflection that found nothing must not pass

        // The views reach a region exactly one way — `context.attributes.layout?.<name>`. Matching the
        // bare name would let a COMMENT mentioning the region satisfy the check, which is the failure
        // mode this test exists to catch.
        //
        // ⚠ THE TRAILING `\b` IS NOT TIDINESS — WITHOUT IT THIS TEST CANNOT FAIL. Written first as a
        // plain `Contains("layout?.expanded")`, it passed against a sabotage that renamed the property to
        // `layout?.expandedXX`, because the sabotaged string still CONTAINS the original. A substring
        // check for an identifier is vacuous by construction, and a tripwire that cannot fail is worth
        // less than none: it reports the guarantee as held. Caught 2026-08-09 by sabotaging it.
        var unread = regions
            .Where(r => !Regex.IsMatch(views, $@"layout\?\.{Regex.Escape(r)}\b"))
            .ToArray();

        Assert.True(unread.Length == 0,
            $"Presentation region(s) no view reads: {string.Join(", ", unread)}. "
            + "The kit's default views must consult every region the record declares — an app that sets "
            + "one and sees the built-in arrangement gets no error, no log line and no failing test, "
            + "which reads as 'the kit ignored my layout'.");
    }

    /// <summary>
    /// 🔴 <b>A DECLARED ENUM MEMBER THE RENDERER NEVER ACTS ON IS D63 AGAIN, AND IT SHIPPED TWICE.</b>
    /// <c>Align.Fill</c> was declared, documented as "stretched across the axis — what a
    /// progress bar in a column wants", and passed TWICE by the kit's own presets — while the interpreter
    /// mapped it to the same value as <c>Leading</c> and applied no frame. The Swift even carried a
    /// comment saying "Fill stretches via frame" beside code containing no frame.
    /// <para>
    /// ⚠ The existing region test could not catch it: it proves each layout REGION is consulted, and this
    /// is a member of an enum INSIDE a region that was being read. Reaching a value is not acting on it —
    /// so this asserts the member name appears in a branch, not merely in the file.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_layout_enum_member_is_acted_on_by_the_interpreter()
    {
        var swift = InterpreterSwift();

        // 🔴 AT MOST ONE MEMBER PER ENUM MAY BE UNBRANCHED — the one the `default:` arm serves. That is
        // the rule rather than "every member has a branch", because a default IS a real behaviour and
        // demanding an explicit case for it would only add noise. It is also exactly how `Fill` hid: it
        // shared `default` with `Leading`, so TWO members of one enum meant the same thing, which is the
        // signature of a value nobody implemented.
        Enum[][] enums =
        [
            [.. Enum.GetValues<Align>().Cast<Enum>()],
            [.. Enum.GetValues<Justify>().Cast<Enum>()],
            [.. Enum.GetValues<Axis>().Cast<Enum>()],
            [.. Enum.GetValues<TextRole>().Cast<Enum>()],
        ];
        Assert.NotEmpty(enums);

        var failures = new List<string>();
        foreach (var members in enums)
        {
            var name = members[0].GetType().Name;
            // The interpreter compares against the member NAME in a `case "X"` or an `== "X"` test. A
            // name appearing only in a COMMENT does not count — that is the shape `Fill` had.
            var unbranched = members
                .Select(m => m.ToString())
                .Where(m => !Regex.IsMatch(swift, $@"(?:case\s+""{Regex.Escape(m)}""|==\s*""{Regex.Escape(m)}"")"))
                .ToArray();

            if (unbranched.Length > 1)
                failures.Add($"{name}: {string.Join(", ", unbranched)} — {unbranched.Length} members share "
                    + "the default arm, so at most one of them is implemented");
        }

        Assert.True(failures.Count == 0,
            "Layout enum members the Swift interpreter cannot tell apart:\n  " + string.Join("\n  ", failures)
            + "\nA member an app can set and the renderer ignores is indistinguishable from one that works "
            + "— it crosses the wire, decodes, and changes nothing, with no error on either side.");
    }

    [Fact]
    public void Every_element_property_name_is_a_key_the_Swift_decoder_reads()
    {
        var swift = File.ReadAllText(LayoutWireSwiftPath());

        // ⚠ Scoped to the CodingKeys enum. Matching anywhere in the file would let a property name that
        // merely appears in a comment satisfy the check.
        var start = swift.IndexOf("private enum Keys: String, CodingKey", StringComparison.Ordinal);
        Assert.True(start >= 0, $"ShenoraElement's CodingKeys not found in {LayoutWireSwiftPath()}.");
        var end = swift.IndexOf('}', start);
        var keys = swift[start..end];

        var properties = typeof(Element).Assembly.GetTypes()
            .Where(t => t.IsSealed && typeof(Element).IsAssignableFrom(t))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(properties);

        var unread = properties.Where(p => !Regex.IsMatch(keys, $@"\b{Regex.Escape(p)}\b")).ToArray();
        Assert.True(unread.Length == 0,
            $"The Swift decoder declares no key for element propert(ies): {string.Join(", ", unread)}. "
            + "A key it does not declare is a value it cannot read, and the element renders with that "
            + "property at its default — no error on either side.");
    }

    /// <summary>
    /// 🔴 THE CUTOUT'S DOCUMENTED DEPTH LIMIT IS A CLAIM ABOUT THE SWIFT, SO THE SWIFT HAS TO HOLD IT.
    /// <para>
    /// <c>Cutout</c>'s XML docs promise the splitter looks at the expanded element and its DIRECT children
    /// and no deeper — a real constraint, because a cutout nested further is silently not found and the
    /// whole panel renders in the bottom strip. That promise lives in C# and the behaviour lives in Swift,
    /// which is exactly the pair that drifts: making <c>splitRow</c> recursive would be a perfectly good
    /// change and would leave the C# doc quietly lying.
    /// </para>
    /// <para>
    /// ⚠ So this asserts the SHAPE that makes the claim true — <c>splitRow</c> does not call itself. If you
    /// deepen the search, this fails and points at the sentence to rewrite. It is not a test of rendering;
    /// it is a test that one document and one function still agree.
    /// </para>
    /// </summary>
    [Fact]
    public void The_cutout_splitter_searches_one_level_deep_as_the_Cutout_docs_promise()
    {
        var swift = File.ReadAllText(LayoutSwiftPath());
        var start = swift.IndexOf("private static func splitRow(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"splitRow not found in {LayoutSwiftPath()} — the Cutout docs describe it.");

        // The function body: from its opening brace to the blank line that ends it. Scoped, so a mention of
        // `splitRow` in a comment elsewhere in the file cannot make this fail or pass by accident.
        var bodyEnd = swift.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(bodyEnd > start, "could not delimit splitRow's body.");
        var body = swift[start..bodyEnd];

        // One occurrence = the declaration itself. Two = it recurses.
        var mentions = Regex.Matches(body, @"\bsplitRow\s*\(").Count;
        Assert.True(mentions == 1,
            $"splitRow appears to recurse ({mentions} call sites in its own body). That is a fine change, "
            + "but Cutout's XML docs promise the search is one level deep — update that paragraph in "
            + "src/Shenora/Modules/Platform/Activities/Presentation.cs in the same commit.");
    }

    /// <summary>
    /// 🔴 <b>THE KIT MAKES NO FRESHNESS CLAIM, AND BOTH CALL SITES HAVE TO AGREE ON THAT.</b>
    /// <para>
    /// <c>staleDate</c> tells the system when to consider an activity's content out of date, so a widget
    /// can read <c>context.isStale</c>. It is NOT a repaint trigger — but it was used as one for a day,
    /// and only on <c>update</c>: <c>start</c> passed <c>nil</c> while <c>update</c> passed
    /// <c>+60 s</c>, so every adopter's activity became "stale" a minute after its last update, with no
    /// option, no documentation, and no view anywhere reading the flag. An activity whose freshness
    /// semantics change the moment its first update lands is not a design, it is two defaults.
    /// </para>
    /// <para>
    /// ⚠ This is a TEXT check on the shim, which is the weak kind — but the thing it guards is the
    /// re-introduction of a WORKAROUND, and that arrives as a plausible one-line edit at one of the two
    /// call sites. The failure it prevents is invisible in every other way: an activity marked stale
    /// renders identically here, and only an adopter reading the kit's Swift would ever find out.
    /// Removing the horizon deliberately is fine — delete this test in the same commit and say why.
    /// </para>
    /// </summary>
    [Fact]
    public void The_shim_sets_no_staleDate_on_either_call_site()
    {
        var swift = File.ReadAllText(SwiftPath());

        // Every line passing a `staleDate:`, comments excluded — the remarks above deliberately discuss
        // the rejected value, so a whole-file match would fail on its own explanation.
        //
        // ⚠ The whole LINE is reported rather than a captured argument. The first version captured
        // `([^),]+)` and told a reader the shim passed `Date(` — a diagnostic truncated mid-expression,
        // which this repo has already paid for once (`net::ERR` cut one character before the underscore).
        // The test still PASSED and FAILED in the right places; only its evidence was mangled, which is
        // the half nobody re-reads.
        var callSites = swift.Split('\n')
            .Select((line, i) => (Text: line.Trim(), Number: i + 1))
            .Where(l => !l.Text.StartsWith("//", StringComparison.Ordinal) && l.Text.Contains("staleDate:"))
            .ToArray();

        Assert.Equal(2, callSites.Length);   // self-check: start + update, and a scan finding neither must fail

        var withHorizon = callSites
            .Where(l => !Regex.IsMatch(l.Text, @"staleDate:\s*nil\b"))
            .Select(l => $"{Path.GetFileName(SwiftPath())}:{l.Number}  {l.Text}")
            .ToArray();

        Assert.True(withHorizon.Length == 0,
            $"The live-activity shim passes a staleDate:\n  {string.Join("\n  ", withHorizon)}\nThe kit makes "
            + "no claim about content freshness — a horizon here declares EVERY adopter's activity out of "
            + "date that long after its last update, which is wrong for a status activity that "
            + "legitimately does not change, and nothing in the kit reads `context.isStale` to act on it. "
            + "If it was re-added to force a repaint: that premise was measured and did not hold "
            + "(`dev.mjs mac island-watch`), and the render bug it was written for turned out to be the "
            + "encode(to:) stub and enums crossing as numbers.");
    }

    /// <summary>
    /// 🔴 THE ELEMENT SET IS CLOSED, AND THE COMPILER HAS TO BE THE ONE SAYING SO.
    /// <para>
    /// <c>Element</c> is documented as a closed set, and every gate in this file assumes it: the kind
    /// tripwire enumerates <c>[JsonDerivedType]</c>, and the Swift interpreter branches on a fixed list of
    /// <c>kind</c> strings. A seventh element declared in an ADOPTER'S assembly satisfies none of that — it
    /// compiled, then threw <c>NotSupportedException</c> out of <c>Start</c>, and could not have rendered
    /// even if it serialized. So the constructor is <c>private protected</c>, and this asserts it stays
    /// that way: a `public`/`protected` constructor reopens the set silently, since nothing else here
    /// would notice.
    /// </para>
    /// </summary>
    [Fact]
    public void The_element_hierarchy_cannot_be_extended_outside_this_assembly()
    {
        // ⚠ THE RECORD COPY CONSTRUCTOR IS EXCLUDED, and it is not a loophole. Every record gets a
        // `protected Element(Element)` from the compiler and it cannot be suppressed — but it takes an
        // existing instance, so it can only ever COPY one of the six, never let an outside assembly
        // construct a seventh. What blocks derivation is the parameterless constructor being
        // `private protected`: an external `record Mine : Element` has no accessible base to call.
        // ⚠ This exclusion was added after the first version of this test failed on that very constructor —
        // which is worth keeping, because a test whose criterion is wrong reads exactly like a real defect.
        var constructors = typeof(Element)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(c => c.GetParameters() is not [{ } p] || p.ParameterType != typeof(Element))
            .ToArray();
        Assert.NotEmpty(constructors);

        // Derivable from outside = public, or protected in a way that ignores the assembly boundary
        // (`IsFamily`/`IsFamilyOrAssembly`). `private protected` is `IsFamilyAndAssembly`, which is the one
        // that keeps derivation in here.
        var open = constructors.Where(c => c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly).ToArray();
        Assert.True(open.Length == 0,
            "Element has a constructor an external assembly can derive from, so the 'closed set' the "
            + "docs, the [JsonDerivedType] tripwire and the Swift interpreter all rely on is not enforced. "
            + "A seventh element would compile in an adopter's app and fail at runtime.");
    }
}
