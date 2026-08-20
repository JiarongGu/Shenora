using System.Text.Json.Serialization;

namespace Shenora.Modules.Platform.Activities;

/// <summary>
/// What the kit's generic widget should DRAW on each of a Live Activity's surfaces, as a tree of elements
/// interpreted by SwiftUI at runtime.
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
    /// 🔴 The kit splits it across the platform's separate leading/trailing views at the
    /// <see cref="Cutout"/>. Anything WITHOUT a cutout renders in the full-width strip under the housing.
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
/// One node. A closed set (D13), enforced rather than asserted — the constructor is
/// <c>private protected</c>, so only this assembly's six elements derive from it.
/// <para>
/// ⚠ The JSON discriminator is part of the WIRE: it is read by Swift, so renaming a derived type's
/// <c>"kind"</c> breaks rendering silently — the interpreter falls back rather than throwing, so that a
/// malformed layout does not take the activity down with it.
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
    private protected Element() { }
}

/// <summary>
/// A line of text.
/// <para>
/// <b>Bindings:</b> <c>{title}</c>, <c>{subtitle}</c> and <c>{progress}</c> are substituted from the live
/// <see cref="LiveActivityState"/> at every render, so a presentation described once at start still shows
/// changing values. ⚠ <c>{progress}</c> renders as a whole percent, and as an EMPTY string when progress
/// is null.
/// </para>
/// </summary>
/// <param name="Value">Literal text, a binding, or both: <c>"step {progress} done"</c>.</param>
/// <param name="Role">What the line IS, not how it looks. There is no style property (D13).</param>
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
/// A progress bar bound to <see cref="LiveActivityState.Progress"/>. A null progress renders as an
/// indeterminate bar, never an empty one.
/// </summary>
public sealed record ProgressBar : Element
{
    /// <summary>A <c>#RRGGBB</c> override, or null for the presentation's own colour.</summary>
    public string? Tint { get; init; }
}

/// <summary>Flexible space — pushes what follows to the far edge.</summary>
public sealed record Spacer : Element;

/// <summary>
/// A placeholder for the Dynamic Island's sensor housing — the hole an app lays out AROUND. It draws
/// nothing: it marks where the kit splits the panel, so a <see cref="Layout"/>'s children before the
/// cutout render in the Island's leading region, the ones after it in the trailing region, and everything
/// else in the strip below.
///
/// <para>
/// ⚠ <b>It must be <see cref="Presentation.Expanded"/> itself or one of its DIRECT children</b> — the
/// splitter looks no deeper. Nested further it is not found, and the failure is quiet: the whole panel
/// renders in the bottom strip and the cutout becomes blank space.
/// </para>
///
/// <para>
/// On every other surface there is no housing to avoid, so a cutout is flexible blank space, exactly like
/// <see cref="Spacer"/>.
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
    /// Space around the layout. 🔴 When a surface supplies a layout, it owns its insets completely: the
    /// kit adds none.
    /// </summary>
    public Insets Insets { get; init; }

    /// <summary>How the children are distributed ALONG the axis — flexbox's <c>justify-content</c>.</summary>
    public Justify Justify { get; init; } = Justify.Start;

    /// <summary>How the children line up ACROSS the axis — flexbox's <c>align-items</c>.</summary>
    public Align Align { get; init; } = Align.Leading;
}

/// <summary>Space around an element, per edge — in points.</summary>
public readonly record struct Insets(double Top, double Right, double Bottom, double Left)
{
    /// <summary>No inset — flush to the edge.</summary>
    public static Insets None => new(0, 0, 0, 0);

    /// <summary>The same on every edge.</summary>
    public static Insets All(double value) => new(value, value, value, value);
}

/// <summary>Which way a <see cref="Layout"/> runs.</summary>
/// <remarks>
/// 🔴 <b>The string converter is part of the CONTRACT, not a serializer preference.</b> The widget
/// compares against the member NAME, so an enum written as a number decodes to nothing and the
/// interpreter falls back to its default — silently, on both sides. It is on the TYPE so the guarantee
/// travels with the value wherever it is serialized, and generic because iOS is AOT.
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
/// <c>justify-content</c>. Four values: there is no <c>SpaceEvenly</c> and no <c>SpaceAround</c>.
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
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Align>))]
public enum Align
{
    /// <summary>Against the leading edge. The default.</summary>
    Leading,

    /// <summary>Centred across the axis.</summary>
    Center,

    /// <summary>Against the trailing edge — where a value belongs, so a column of numbers lines up.</summary>
    Trailing,

    /// <summary>Stretched across the axis. What a progress bar in a column wants.</summary>
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

    /// <summary>Monospaced digits, so a changing value does not jitter as digit widths change.</summary>
    Value,
}
