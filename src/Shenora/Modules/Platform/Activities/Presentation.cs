using System.Text.Json.Serialization;

namespace Shenora.Modules.Platform.Activities;

/// <summary>
/// What the kit's generic widget should DRAW on each of a Live Activity's surfaces, described as a tree
/// of elements in C# and interpreted by SwiftUI at runtime — the same shape a React app describes its own
/// UI in, on the side of the boundary where the app already lives.
///
/// <para>
/// Every surface is optional; a null one uses the kit's built-in look. The element set is closed (D13) —
/// an app needing more writes SwiftUI views directly (<c>ShenoraLiveActivityViews</c>).
/// ⚠ The type names are short and collide easily; an app with its own <c>Text</c> aliases the <c>using</c>.
/// </para>
///
/// <code>
/// using Shenora.Modules.Platform.Activities;
///
/// var presentation = new Presentation
/// {
///     LockScreen = new Layout
///     {
///         Axis = Axis.Vertical,
///         Align = Align.Fill,
///         Insets = Insets.All(16),
///         Children = [new Icon("arrow.down.circle.fill"), new Text("{title}", TextRole.Headline)],
///     },
/// };
/// </code>
/// </summary>
public sealed record Presentation
{
    /// <summary>The lock-screen / banner card — the largest surface, and the only one with room for prose.</summary>
    public Element? LockScreen { get; init; }

    /// <summary>
    /// Dynamic Island, expanded (long-press) — the whole panel as ONE element, normally a horizontal
    /// <see cref="Layout"/> whose children include a <see cref="Cutout"/>.
    /// <para>
    /// 🔴 The kit splits it across the platform's separate leading/trailing views for you, so an app
    /// describes a panel rather than three regions. Anything WITHOUT a cutout renders in the full-width
    /// strip under the housing, which is the only slot that can host arbitrary content.
    /// </para>
    /// </summary>
    public Element? Expanded { get; init; }

    /// <summary>Dynamic Island, compact LEADING — left of the sensor housing. ⚠ About one symbol wide.</summary>
    public Element? CompactLeading { get; init; }

    /// <summary>Dynamic Island, compact TRAILING — right of the sensor housing. A short value, no prose.</summary>
    public Element? CompactTrailing { get; init; }

    /// <summary>
    /// The MINIMAL presentation — what shows when another activity shares the Island. ⚠ One glyph. Text
    /// here is silently clipped rather than shrunk, which reads as a broken widget.
    /// </summary>
    public Element? Minimal { get; init; }
}

/// <summary>
/// One node. Deliberately a closed set — see <see cref="Presentation"/> for why widening it is the wrong
/// move.
/// <para>
/// ⚠ The JSON discriminator is part of the WIRE: it is read by Swift, so renaming a derived type's
/// <c>"kind"</c> breaks rendering silently — the interpreter falls back rather than throwing, because a
/// malformed layout must not take the activity down with it.
/// </para>
/// <para>
/// 🔴 <b>CLOSED IS ENFORCED, NOT ASSERTED — the constructor is <c>private protected</c>.</b> Left plainly
/// derivable, an adopter could write <c>record MyElement : Element</c>, have it
/// COMPILE, and get a <see cref="NotSupportedException"/> out of
/// <see cref="ILiveActivities.Start"/> at runtime — polymorphic serialization refuses a runtime type with no
/// <c>[JsonDerivedType]</c>. And it could never have worked even if it serialized: the Swift interpreter
/// branches on a fixed set of <c>kind</c> strings, so a seventh one has nothing to render it. A capability
/// the type system offers and the wire cannot honour is a trap, and closing it turns a runtime crash in the
/// adopter's app into a compile error in ours.
/// </para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Text), "text")]
[JsonDerivedType(typeof(Icon), "icon")]
[JsonDerivedType(typeof(ProgressBar), "progress")]
[JsonDerivedType(typeof(Layout), "layout")]
[JsonDerivedType(typeof(Cutout), "cutout")]
[JsonDerivedType(typeof(Spacer), "spacer")]
public abstract record Element
{
    /// <summary>Only this assembly's six elements derive from this — see the type's remarks.</summary>
    private protected Element() { }
}

/// <summary>
/// A line of text.
/// <para>
/// <b>Bindings:</b> <c>{title}</c>, <c>{subtitle}</c> and <c>{progress}</c> are substituted from the live
/// <see cref="LiveActivityState"/> when the widget renders — so a presentation is described ONCE at start
/// and still shows changing values. <c>{progress}</c> renders as a whole percent; an indeterminate
/// progress renders as an empty string rather than "0%", which would be a lie that looks like a stalled
/// job.
/// </para>
/// </summary>
/// <param name="Value">Literal text, a binding, or both: <c>"step {progress} done"</c>.</param>
/// <param name="Role">
/// What the line IS, not how it looks. ⚠ Semantic on purpose — a <c>Style</c> property would be the first
/// brick of the design system this must not become.
/// </param>
public sealed record Text(string Value, TextRole Role = TextRole.Body) : Element
{
    /// <summary>A <c>#RRGGBB</c> override, or null for the presentation's own colour.</summary>
    public string? Tint { get; init; }
}

/// <summary>An SF Symbol. ⚠ Filled symbols read on the Island; outlines do not.</summary>
/// <param name="Symbol">An SF Symbol name, e.g. <c>arrow.down.circle.fill</c>.</param>
public sealed record Icon(string Symbol) : Element
{
    /// <summary>A <c>#RRGGBB</c> override, or null for the presentation's own colour.</summary>
    public string? Tint { get; init; }
}

/// <summary>
/// A progress bar bound to <see cref="LiveActivityState.Progress"/>.
/// <para>
/// An indeterminate progress renders as an indeterminate bar, never an empty one — the distinction the
/// state's <c>null</c> exists to carry.
/// </para>
/// </summary>
public sealed record ProgressBar : Element
{
    /// <summary>A <c>#RRGGBB</c> override, or null for the presentation's own colour.</summary>
    public string? Tint { get; init; }
}

/// <summary>Flexible space — pushes what follows to the far edge.</summary>
public sealed record Spacer : Element;

/// <summary>
/// A placeholder for the Dynamic Island's sensor housing — the hole an app lays out AROUND.
///
/// <para>
/// 🔴 <b>IT DRAWS NOTHING, AND THAT IS WHAT LETS AN APP DESCRIBE THE ISLAND AS ONE PANEL.</b> ActivityKit
/// hands the expanded presentation to a widget as SEPARATE views — leading, trailing, bottom — and nothing
/// drawn in one can cross into another, so no layout can literally span the housing. <b>The kit splits the
/// panel for you instead:</b> put a cutout among a <see cref="Layout"/>'s children and the children before
/// it render in the leading region, the ones after it in the trailing region, and everything else in the
/// strip below.
/// </para>
///
/// <para>
/// ⚠ <b>IT HAS TO BE NEAR THE TOP: the splitter looks at <see cref="Presentation.Expanded"/> itself and at
/// its DIRECT children, and no deeper.</b> A cutout buried two levels down is not found, and the failure is
/// quiet by design — the whole panel renders in the bottom strip and the cutout becomes blank space, rather
/// than the activity refusing to draw. That fallback is right (a malformed layout must never take the
/// activity down) but it looks like the split "did nothing", so the depth limit is stated here rather than
/// discovered. In practice this costs nothing: the split row IS the top-level arrangement of the expanded
/// surface, and nesting it deeper describes a panel the Island cannot render anyway.
/// </para>
///
/// <para>
/// ⚠ On every other surface — the lock-screen card, the compact pill — there is no housing to avoid, so a
/// cutout is simply flexible blank space, exactly like <see cref="Spacer"/>.
/// </para>
/// </summary>
public sealed record Cutout : Element;

/// <summary>
/// The container — a <c>div</c>, with flexbox's two axes. Children run along <see cref="Axis"/>,
/// <see cref="Justify"/> distributes them along it and <see cref="Align"/> lines them up across it.
/// </summary>
public sealed record Layout : Element
{
    /// <summary>Which way the children run.</summary>
    public required Axis Axis { get; init; }

    /// <summary>In order.</summary>
    public required IReadOnlyList<Element> Children { get; init; }

    /// <summary>Points between children; null takes the platform's own default for the axis.</summary>
    public double? Spacing { get; init; }

    /// <summary>
    /// Space around the layout.
    /// <para>
    /// 🔴 <b>When a surface supplies a layout, it owns its insets completely: the kit adds none.</b> The
    /// built-in arrangement keeps its own, but the moment an app describes a tree the kit stops
    /// second-guessing it — a margin it could not remove would be the most annoying possible default.
    /// </para>
    /// </summary>
    public Insets Insets { get; init; }

    /// <summary>
    /// How the children are distributed ALONG the axis — flexbox's <c>justify-content</c>.
    /// <para>
    /// ⚠ <see cref="Spacer"/> still works and is often clearer for one gap;
    /// <see cref="Justify.SpaceBetween"/> is what you want when the number of children varies, because
    /// the spacer count would have to vary with it.
    /// </para>
    /// </summary>
    public Justify Justify { get; init; } = Justify.Start;

    /// <summary>
    /// How the children line up ACROSS the axis — flexbox's <c>align-items</c>. <see cref="Align.Fill"/>
    /// stretches them, which is what a progress bar in a column wants.
    /// </summary>
    public Align Align { get; init; } = Align.Leading;
}

/// <summary>
/// Space around an element, per edge.
/// <para>
/// 🔴 <b>Insets and spacing are ARRANGEMENT, not styling, which is why they are here at all.</b> A layout
/// whose gaps cannot be set is an incomplete layout — the same category as its axis. What stays out is the
/// token vocabulary: named spacing scales, fonts, themes, per-role colours.
/// </para>
/// </summary>
public readonly record struct Insets(double Top, double Right, double Bottom, double Left)
{
    /// <summary>No inset — flush to the edge.</summary>
    public static Insets None => new(0, 0, 0, 0);

    /// <summary>The same on every edge, which is what most layouts want.</summary>
    public static Insets All(double value) => new(value, value, value, value);
}

/// <summary>Which way a <see cref="Layout"/> runs.</summary>
/// <remarks>
/// 🔴 <b>The string converter is part of the CONTRACT, not a serializer preference.</b> The widget
/// compares against the member NAME, so an enum written as a number decodes to nothing and the
/// interpreter falls back to its default — silently, on both sides. Putting it on the TYPE rather than on
/// one call site's options means the guarantee travels with the value wherever it is serialized.
/// (Measured: without it every role rendered as body text and every horizontal layout ran
/// vertically.) The generic converter, because iOS is AOT and the non-generic one resolves by reflection.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<Axis>))]
public enum Axis
{
    /// <summary>Left to right.</summary>
    Horizontal,

    /// <summary>Top to bottom.</summary>
    Vertical,
}

/// <summary>
/// How a <see cref="Layout"/> distributes its children along its axis — flexbox's
/// <c>justify-content</c>, with the names a React developer already uses.
/// <para>
/// ⚠ Four values, not six: no <c>SpaceEvenly</c>, no <c>SpaceAround</c>. On a surface this small the
/// difference is sub-pixel, and every extra value is one more thing the interpreter has to mean exactly
/// the same by on a platform that never defined it.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Justify>))]
public enum Justify
{
    /// <summary>Packed at the leading end. The default.</summary>
    Start,

    /// <summary>Packed in the middle.</summary>
    Center,

    /// <summary>Packed at the trailing end.</summary>
    End,

    /// <summary>First child leading, last child trailing, the space shared between them.</summary>
    SpaceBetween,
}

/// <summary>
/// How a <see cref="Layout"/> lines its children up across its axis — flexbox's <c>align-items</c>.
/// <para>
/// ⚠ Four values and no more. This is the alignment a row or a column needs, not a general box model.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Align>))]
public enum Align
{
    /// <summary>Against the leading edge. The default, because text reads from there.</summary>
    Leading,

    /// <summary>Centred across the axis.</summary>
    Center,

    /// <summary>Against the trailing edge — where a value belongs, so a column of numbers lines up.</summary>
    Trailing,

    /// <summary>
    /// Stretched across the axis. What a progress bar in a column wants.
    /// <para>
    /// ⚠ A member that shares the interpreter's default arm with <see cref="Leading"/> applies no frame,
    /// so an app can set it and nothing happens — declared and INERT. Pinned by a test asserting at most
    /// one member of an enum falls to the default.
    /// </para>
    /// </summary>
    Fill,
}

/// <summary>What a line of text is FOR. The widget maps each to the platform's own type scale.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TextRole>))]
public enum TextRole
{
    /// <summary>The primary line.</summary>
    Headline,

    /// <summary>Ordinary text.</summary>
    Body,

    /// <summary>Secondary, dimmed.</summary>
    Caption,

    /// <summary>Monospaced digits — for a value that changes, so it does not jitter as digits change width.</summary>
    Value,
}
