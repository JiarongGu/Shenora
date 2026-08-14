using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Shenora.Modules.Platform.Activities;

// ⚠ The element names are SHORT on purpose (see `Presentation`'s remarks), so a host that already has an
// `Icon` or a `ProgressBar` aliases them — which is exactly what this WinForms-referencing test project
// has to do. Worth noting rather than hiding: it is the first time the kit's own tree has had to pay the
// cost the API docs predict for an adopter, and one line per collision is the whole cost.
using Icon = Shenora.Modules.Platform.Activities.Icon;
using ProgressBar = Shenora.Modules.Platform.Activities.ProgressBar;

namespace Shenora.Tests.Api;

/// <summary>
/// 🔴 <b>THE GOLDEN PAYLOAD — the first test in this repo that exercises the Live Activity DATA PATH
/// rather than describing it.</b>
/// <para>
/// <c>LiveActivityMirrorTests</c> next door reads the Swift as TEXT and asserts the two sides LOOK like
/// they agree — same names, same discriminators, same coding keys. That is worth having and it is not the
/// same thing as agreeing: <b>both defects this subsystem has ever had were pure DATA bugs</b> (the stub
/// <c>encode(to:)</c>, and the layout enums crossing as NUMBERS), both survived every text assertion, and
/// both were found by eye on a phone after long hunts — because a phone reports "wrong picture" and not
/// which of five legs dropped the payload.
/// </para>
/// <para>
/// So: <b>two files, committed, and TWO halves that consume the same two files.</b> This half serializes
/// the presentations with the SHIPPED options (<c>ActivityWire.Json</c> — the one
/// <c>Shenora.iOS</c>'s <c>IosLiveActivities</c> uses, not a copy) and asserts the whole payload
/// byte for byte, plus a canonical description of the tree it came from.
/// <b><c>node devtools/dev.mjs mac layout-check</c> is the other half</b>: it feeds the same JSON to the
/// real Swift decoder under <c>swiftc</c> and requires it to reproduce the same description — and to
/// survive a re-encode, which is the leg ActivityKit uses and the one the stub broke.
/// </para>
/// <para>
/// ⚠ <b>CI runs windows + ubuntu and has no macOS runner</b>, so the Swift half gates only when a human
/// runs <c>dev.mjs mac layout-check</c>. This half gates on every push and costs nothing. Say which one
/// you ran when claiming this wire is covered.
/// </para>
/// <para>
/// ⚠ <b>Regenerating:</b> set <c>SHENORA_UPDATE_GOLDEN=1</c> and run the suite. Then READ THE DIFF — a
/// golden that is regenerated without being read is a test that agrees with whatever the code now does,
/// which is worse than none because it reports the wire as pinned.
/// </para>
/// </summary>
public class LiveActivityGoldenTests
{
    private static string RepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(LiveActivityGoldenTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly.");
    }

    /// <summary>
    /// The two goldens live under <c>tests/</c> and are read from the REPO rather than copied to the
    /// output directory, because the Mac half uploads these exact paths. One file, both halves — a copy
    /// is a thing that can be stale.
    /// </summary>
    internal static string GoldenPath(string name) =>
        Path.Combine(RepoRoot(), "tests", "Shenora.Tests", "Api", "Goldens", name);

    internal const string PayloadFile = "live-activity.json";
    internal const string TreeFile = "live-activity.tree.txt";

    /// <summary>
    /// The fixture. Three of the shipped <see cref="Components"/> factories — which is what an adopter
    /// actually sends — plus one hand-built presentation that exists to reach every element kind and every
    /// member of all four enums. <c>Every_element_kind_and_enum_member_appears_in_the_golden</c> is what
    /// keeps that second claim true as the vocabulary grows.
    /// </summary>
    private static readonly (string Case, Presentation Presentation)[] Cases =
    [
        ("progressCard", Components.ProgressCard("arrow.down.circle.fill")),
        ("statusCard", Components.StatusCard("arrow.triangle.2.circlepath")),
        ("counterCard", Components.CounterCard("timer")),
        ("everyElement", EveryElement()),
    ];

    /// <summary>
    /// One of everything, arranged so the five regions between them use all six element kinds and all
    /// fourteen enum members. Deliberately NOT a pretty design — it is a coverage fixture, and the three
    /// component cases above are what a realistic payload looks like.
    /// </summary>
    private static Presentation EveryElement() => new()
    {
        LockScreen = new Layout
        {
            Axis = Axis.Vertical,
            Spacing = 4,
            Insets = Insets.All(8),
            Justify = Justify.Start,
            Align = Align.Leading,
            Children =
            [
                new Text("{title}", TextRole.Headline),
                new Text("plain body", TextRole.Body) { Tint = "#FF8800" },
                new Layout
                {
                    Axis = Axis.Horizontal,
                    Justify = Justify.SpaceBetween,
                    Align = Align.Center,
                    Children = [new Icon("bolt.fill"), new Spacer(), new Text("{progress}", TextRole.Value)],
                },
                new ProgressBar { Tint = "#00AAFF" },
            ],
        },
        Expanded = new Layout
        {
            Axis = Axis.Horizontal,
            Spacing = 3,
            Insets = new Insets(Top: 1, Right: 2, Bottom: 3, Left: 4),
            Justify = Justify.Center,
            Align = Align.Trailing,
            Children =
            [
                new Icon("gauge") { Tint = "#123456" },
                new Cutout(),
                new Text("{subtitle}", TextRole.Caption),
            ],
        },
        CompactLeading = new Icon("circle.fill"),
        CompactTrailing = new Layout
        {
            Axis = Axis.Vertical,
            Justify = Justify.End,
            Align = Align.Fill,
            Children = [new ProgressBar()],
        },
        Minimal = new Spacer(),
    };

    // ── what the two files hold ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every case, serialized with the shipped options and reassembled into one object so both halves read
    /// ONE file.
    /// <para>
    /// ⚠ The reassembly re-parses each payload and writes it INDENTED, which changes whitespace and
    /// nothing else — JSON whitespace is invisible to the Swift decoder, and a readable golden is a golden
    /// somebody will actually read when it fails. Everything the wire depends on (key names, the
    /// discriminator, enums as member names, omitted nulls) survives the reformat untouched, because it is
    /// structure rather than layout.
    /// </para>
    /// <para>
    /// Keys are sorted, and so is the tree file's case order, because the Swift half decodes into a
    /// dictionary and Swift dictionaries have no order. Sorting on both sides is what makes the comparison
    /// deterministic rather than lucky.
    /// </para>
    /// </summary>
    private static string BuildPayload()
    {
        var root = new JsonObject();
        foreach (var (name, presentation) in Cases.OrderBy(c => c.Case, StringComparer.Ordinal))
        {
            var compact = JsonSerializer.Serialize(presentation, ActivityWire.Json);
            root[name] = JsonNode.Parse(compact);
        }
        // ⚠ Newlines normalised, because the indented writer uses `Environment.NewLine` — so this file
        // would be CRLF when generated on Windows and LF when generated anywhere else, and a committed
        // artifact whose bytes depend on who ran the test is a diff waiting to happen. Both readers
        // normalise before comparing, so this is determinism rather than correctness.
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return Lf(json) + "\n";
    }

    /// <summary>
    /// The same trees, described from the C# OBJECT GRAPH — never from the JSON. That is the point: the
    /// Swift half produces this text from its DECODE, so a payload that serializes fine and decodes to
    /// something else fails on the Mac while this file stays green, and the two texts name the node.
    /// </summary>
    private static string BuildTree()
    {
        var sb = new StringBuilder();
        foreach (var (name, presentation) in Cases.OrderBy(c => c.Case, StringComparer.Ordinal))
        {
            sb.Append(name).Append('\n');
            foreach (var (region, element) in Regions(presentation))
            {
                sb.Append("  ").Append(region).Append(':');
                if (element is null) { sb.Append(" -\n"); continue; }
                sb.Append('\n');
                Describe(element, 2, sb);
            }
        }
        return sb.ToString();
    }

    /// <summary>The five surfaces in declaration order — the order the Swift half also walks.</summary>
    private static (string Region, Element? Element)[] Regions(Presentation p) =>
    [
        ("lockScreen", p.LockScreen),
        ("expanded", p.Expanded),
        ("compactLeading", p.CompactLeading),
        ("compactTrailing", p.CompactTrailing),
        ("minimal", p.Minimal),
    ];

    /// <summary>
    /// One node per line, two spaces per level. Every field the wire carries is printed, including the ones
    /// that are absent — <c>tint=-</c> and <c>spacing=-</c> rather than nothing, so "the property stopped
    /// crossing" and "the property is null here" are different lines instead of the same silence.
    /// </summary>
    private static void Describe(Element element, int depth, StringBuilder sb)
    {
        var pad = new string(' ', depth * 2);
        switch (element)
        {
            case Text t:
                sb.Append(pad).Append("text value=").Append(Quoted(t.Value))
                  .Append(" role=").Append(t.Role)
                  .Append(" tint=").Append(t.Tint ?? "-").Append('\n');
                break;
            case Icon i:
                sb.Append(pad).Append("icon symbol=").Append(Quoted(i.Symbol))
                  .Append(" tint=").Append(i.Tint ?? "-").Append('\n');
                break;
            case ProgressBar b:
                sb.Append(pad).Append("progress tint=").Append(b.Tint ?? "-").Append('\n');
                break;
            case Cutout:
                sb.Append(pad).Append("cutout").Append('\n');
                break;
            case Spacer:
                sb.Append(pad).Append("spacer").Append('\n');
                break;
            case Layout l:
                sb.Append(pad).Append("layout axis=").Append(l.Axis)
                  .Append(" spacing=").Append(l.Spacing is { } s ? Number(s) : "-")
                  .Append(" insets=").Append(Number(l.Insets.Top)).Append(',').Append(Number(l.Insets.Right))
                  .Append(',').Append(Number(l.Insets.Bottom)).Append(',').Append(Number(l.Insets.Left))
                  .Append(" justify=").Append(l.Justify)
                  .Append(" align=").Append(l.Align).Append('\n');
                foreach (var child in l.Children) Describe(child, depth + 1, sb);
                break;
            default:
                // Unreachable while the set is closed — and if it ever is not, this must SAY so rather than
                // print nothing, because a silently missing line is the failure the whole file is about.
                throw new InvalidOperationException(
                    $"No description for element type {element.GetType().Name}. Add one here AND in "
                    + "devtools/swift/layout-golden-check.swift — the two texts have to stay comparable.");
        }
    }

    /// <summary>
    /// ⚠ Quoting is deliberately NAIVE, and the fixture is kept naive to match. Swift and C# would need
    /// identical escaping rules the moment a value contained a quote or a backslash, which is a second
    /// implementation of a thing nobody needs — so the fixture forbids them instead.
    /// </summary>
    private static string Quoted(string value)
    {
        Assert.False(value.Contains('"') || value.Contains('\\') || value.Contains('\n'),
            $"The golden fixture must not use quotes, backslashes or newlines in a value ({value}) — the "
            + "Swift half reproduces this text with its own formatter and matching escape rules across two "
            + "languages is a bug farm.");
        return $"\"{value}\"";
    }

    /// <summary>
    /// ⚠ Integral only, asserted rather than assumed. C# would write <c>10</c> where Swift writes
    /// <c>10.0</c>, so rather than teach two languages the same float format the fixture stays on whole
    /// numbers and this fails loudly if one stops being whole.
    /// </summary>
    private static string Number(double value)
    {
        Assert.True(value == Math.Floor(value) && Math.Abs(value) < 1e9,
            $"The golden fixture must use whole-number spacing and insets ({value}) — see this method.");
        return ((long)value).ToString(CultureInfo.InvariantCulture);
    }

    // ── the gates ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_serialized_payload_matches_the_committed_golden()
    {
        AssertGolden(PayloadFile, BuildPayload(),
            "The Live Activity wire payload changed. Every key here is read by name on the Swift side, so "
            + "a renamed property, a lost `kind` discriminator or an enum written as a NUMBER decodes to a "
            + "default and renders something plausible and wrong — with no error on either side.");
    }

    [Fact]
    public void The_described_tree_matches_the_committed_golden()
    {
        AssertGolden(TreeFile, BuildTree(),
            "The described element tree changed. This text is what `dev.mjs mac layout-check` requires the "
            + "SWIFT decoder to reproduce, so it is the contract between the two halves — not a snapshot "
            + "of this file's own output.");
    }

    /// <summary>
    /// 🔴 <b>THE FIXTURE'S OWN TRIPWIRE.</b> "One of every element" is a claim about the fixture, and a
    /// claim about a fixture rots the moment someone adds a seventh element kind or a fifth
    /// <see cref="Justify"/>. Without this the golden would keep passing while covering less, which is
    /// exactly how <c>wire-reference.mjs</c> failed open: the gate said "matches the source" — truthfully,
    /// of less.
    /// </summary>
    [Fact]
    public void Every_element_kind_and_enum_member_appears_in_the_golden()
    {
        var payload = BuildPayload();

        var kinds = typeof(Element)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(a => a.TypeDiscriminator?.ToString())
            .OfType<string>()
            .ToArray();
        Assert.NotEmpty(kinds);   // self-check: reflection that found nothing must not pass

        var missingKinds = kinds.Where(k => !payload.Contains($"\"kind\": \"{k}\"", StringComparison.Ordinal)).ToArray();
        Assert.True(missingKinds.Length == 0,
            $"The golden payload contains no element of kind(s): {string.Join(", ", missingKinds)}. Add one "
            + $"to {nameof(EveryElement)} and regenerate — an element kind the fixture never sends is a "
            + "kind neither half of this test has ever decoded.");

        var members = Enum.GetNames<Axis>()
            .Concat(Enum.GetNames<Justify>())
            .Concat(Enum.GetNames<Align>())
            .Concat(Enum.GetNames<TextRole>())
            .ToArray();
        Assert.NotEmpty(members);

        // Matched with the leading `": "` so a member name that merely appears inside a TEXT value cannot
        // satisfy it — the fixture's strings are app-authored content and would otherwise count.
        var missingMembers = members.Where(m => !payload.Contains($": \"{m}\"", StringComparison.Ordinal)).ToArray();
        Assert.True(missingMembers.Length == 0,
            $"The golden payload never uses enum member(s): {string.Join(", ", missingMembers)}. The Swift "
            + "interpreter compares against the member NAME, so a member the fixture never sends is one "
            + "nothing has ever proved crosses as a name rather than as a number.");
    }

    /// <summary>
    /// Compare, or rewrite when <c>SHENORA_UPDATE_GOLDEN=1</c>.
    /// <para>
    /// ⚠ Both sides are normalised to LF before comparing. <c>.gitattributes</c> says <c>* text=auto</c>,
    /// so these files are CRLF in a Windows working tree and LF on the Mac that reads them — a byte
    /// comparison would pass here and fail there, for a difference neither decoder can see.
    /// </para>
    /// </summary>
    private static void AssertGolden(string file, string actual, string why)
    {
        var path = GoldenPath(file);
        var update = Environment.GetEnvironmentVariable("SHENORA_UPDATE_GOLDEN") == "1";

        if (update || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Assert.Fail($"Wrote {file}. Re-run without SHENORA_UPDATE_GOLDEN and READ THE DIFF before "
                + "committing — a regenerated golden agrees with whatever the code now does.");
        }

        var expected = Lf(File.ReadAllText(path));
        if (Lf(actual) == expected) return;

        Assert.Fail($"{file} does not match.\n\n{why}\n\n"
            + $"  golden: {path}\n"
            + $"  first difference at line {FirstDifferentLine(expected, Lf(actual))}\n\n"
            + "Regenerate with SHENORA_UPDATE_GOLDEN=1 only after you have decided the new payload is "
            + "correct, and re-run `dev.mjs mac layout-check` — the Swift half reads these same files.");
    }

    private static string Lf(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>1-based, or 0 when they differ only in length. Enough to point at the node.</summary>
    private static int FirstDifferentLine(string expected, string actual)
    {
        var a = expected.Split('\n');
        var b = actual.Split('\n');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return i + 1;
        return a.Length == b.Length ? 0 : Math.Min(a.Length, b.Length) + 1;
    }
}
