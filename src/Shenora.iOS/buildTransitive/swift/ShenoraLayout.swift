// THE LAYOUT INTERPRETER — turns the element tree an app describes in C# into SwiftUI, at runtime.
//
// 🔴 IT NEVER THROWS AND NEVER RETURNS NOTHING. A malformed layout, an unknown element kind, a colour
// that will not parse: every one of them falls back rather than failing. The reason is the failure mode
// this whole subsystem keeps producing — an empty Dynamic Island is indistinguishable from a widget the
// system could not match, so a layout bug that BLANKS the surface reads as "the kit is broken" and costs
// a device round-trip to attribute. A layout bug that draws the default look reads as "my layout was
// ignored", which points straight at the layout.
//
// ⚠ THE WIRE TYPES LIVE NEXT DOOR, in `ShenoraLayoutWire.swift`, and the split is load-bearing: with no
// SwiftUI import they compile under a bare `swiftc` on any Mac, which is the only reason this wire has a
// mechanical test at all (`node devtools/dev.mjs mac layout-check`). Rendering needs SwiftUI and cannot
// join them.

import SwiftUI

// ── rendering ────────────────────────────────────────────────────────────────────────────────────────

/// Substitute the live state into a layout's text. This is why a layout described ONCE at start can show
/// values that change: the binding is resolved at render, not at description.
func shenoraBind(_ template: String, _ state: ShenoraActivityState) -> String {
    var out = template
    out = out.replacingOccurrences(of: "{title}", with: state.title ?? "")
    out = out.replacingOccurrences(of: "{subtitle}", with: state.subtitle ?? "")
    // ⚠ Indeterminate renders as EMPTY, never "0%". A percentage for work of unknown length is a lie that
    // looks like a stalled job — the same distinction `progress == nil` carries on the C# side.
    out = out.replacingOccurrences(of: "{progress}", with: state.percentText ?? "")
    return out
}

@available(iOS 16.2, *)
struct ShenoraElementView: View {
    let element: ShenoraElement
    let state: ShenoraActivityState
    /// The layout-level colour, used by any element that did not name its own.
    let tint: Color

    // ⚠ NO per-region type scale, and that is a MEASUREMENT rather than an omission. A `compact` flag that
    // stepped every role down one size was written here on the theory that the pill clips oversized text —
    // A/B'd on the simulator 2026-08-09 with the same state and the same tree, `.title3` and `.body` came
    // out pixel-identical, because the Island already constrains what it hosts. The blank pill that
    // prompted the theory had a different cause entirely (`encode(to:)` above). Anything added back here
    // needs a screenshot, not an argument.

    var body: some View {
        switch element {
        case let .text(value, role, elementTint):
            // 🔴 NO EXPLICIT `.primary`, and this cost a device round-trip before the simulator loop
            // existed. Setting `.primary` PINS the colour against the widget's own colour scheme, and the
            // Dynamic Island is always dark — so a light-scheme render drew black text on a black pill.
            // The symptom is the worst kind: the pill reserves the space and draws nothing, which looks
            // exactly like a layout that was never read. Inheriting lets the system pick the on-dark
            // colour, which is what the built-in views were doing by saying nothing.
            styled(Text(shenoraBind(value, state)), role: role)
                .modifier(ShenoraTint(color: shenoraColor(elementTint),
                                      dimmed: elementTint == nil && role == "Caption"))
                .lineLimit(1)

        case let .icon(symbol, elementTint):
            Image(systemName: symbol).foregroundStyle(shenoraColor(elementTint) ?? tint)

        case let .progress(elementTint):
            ShenoraProgress(state: state, tint: shenoraColor(elementTint) ?? tint)

        case let .layout(axis, children, spacing, insets, justify, align):
            // ⚠ The padding is ALWAYS applied, and the optional here is defensive rather than meaningful:
            // `Insets` is a non-nullable struct on the C# side, so the wire always carries an
            // `insets` object — zeros when the app set none. An earlier comment claimed the modifier was
            // applied "only when the app asked"; nothing on the wire can express that. Zeros measure the
            // same as no modifier in every region tested (2026-08-09), so this stays simple rather than
            // growing a nullability the API does not have.
            // 🔴 JUSTIFY IS SPACERS, not a SwiftUI modifier — SwiftUI has no `justify-content`. `Start`
            // needs a trailing spacer to stop the stack centring inside whatever gave it room, `End`
            // needs a leading one, `Center` needs both, and `SpaceBetween` puts them BETWEEN the
            // children. Doing it here is the whole reason the C# side can speak flexbox at all.
            Group {
                if axis == "Horizontal" {
                    HStack(alignment: shenoraVAlign(align), spacing: spacing ?? 6) {
                        if justify == "Center" || justify == "End" { Spacer(minLength: 0) }
                        ShenoraChildren(children: children, state: state, tint: tint,
                                        spread: justify == "SpaceBetween",
                                        fill: align == "Fill", horizontal: true)
                        if justify == "Center" || justify == "Start" { Spacer(minLength: 0) }
                    }
                } else {
                    VStack(alignment: shenoraHAlign(align), spacing: spacing ?? 2) {
                        if justify == "Center" || justify == "End" { Spacer(minLength: 0) }
                        ShenoraChildren(children: children, state: state, tint: tint,
                                        spread: justify == "SpaceBetween",
                                        fill: align == "Fill", horizontal: false)
                        if justify == "Center" || justify == "Start" { Spacer(minLength: 0) }
                    }
                }
            }
            .padding(EdgeInsets(top: CGFloat(insets?.top ?? 0), leading: CGFloat(insets?.left ?? 0),
                                bottom: CGFloat(insets?.bottom ?? 0), trailing: CGFloat(insets?.right ?? 0)))

        // The sensor housing's placeholder. It draws NOTHING by design — in the expanded panel the kit has
        // already split the row around it, and everywhere else there is no housing, so a cutout is simply
        // the blank span the app asked for.
        case .cutout:
            // Outside the expanded panel there is no housing to avoid, so a cutout is flexible blank
            // space — the same thing a spacer is. Inside it, the split has already consumed it and this
            // branch is never reached.
            Spacer(minLength: 0)

        case .spacer:
            Spacer(minLength: 0)

        case .unknown:
            // One unknown node is dropped; the rest of the tree still draws.
            EmptyView()
        }
    }

    /// Role → the platform's own type scale. Semantic in, visual out — which is the whole reason the C#
    /// side names a ROLE rather than a font.
    private func styled(_ text: Text, role: String) -> Text {
        switch role {
        case "Headline": return text.font(.headline)
        case "Caption": return text.font(.caption)
        case "Value": return text.font(.title3).monospacedDigit()
        default: return text.font(.body)
        }
    }
}

/// Apply a colour only when there is one to apply.
///
/// 🔴 The three cases are genuinely different and collapsing them is the bug this exists to prevent:
/// an explicit tint wins; a caption with no tint dims to `.secondary` (which IS scheme-aware); and
/// ordinary text with no tint gets NO modifier at all, so it inherits whatever the surface it lands on
/// calls readable. That last one is the case a `?? .primary` silently breaks.
@available(iOS 16.2, *)
private struct ShenoraTint: ViewModifier {
    let color: Color?
    let dimmed: Bool

    func body(content: Content) -> some View {
        if let color {
            content.foregroundStyle(color)
        } else if dimmed {
            content.foregroundStyle(.secondary)
        } else {
            content
        }
    }
}

/// ⚠ A separate view rather than a `ForEach` inline: `ShenoraElement` is not `Identifiable` and using an
/// index as the id would re-use identity across a tree that changes shape, which SwiftUI renders as
/// stale rows rather than as an error.
@available(iOS 16.2, *)
private struct ShenoraChildren: View {
    let children: [ShenoraElement]
    let state: ShenoraActivityState
    let tint: Color
    /// `SpaceBetween`: a flexible gap after every child but the last.
    var spread: Bool = false
    /// `Align = Fill`: stretch each child ACROSS the axis — full width in a column, full height in a
    /// row. Without it `Fill` is a value the wire carries and nothing acts on.
    var fill: Bool = false
    /// Which way the parent runs, so "across" means the right direction.
    var horizontal: Bool = false

    var body: some View {
        ForEach(Array(children.enumerated()), id: \.offset) { index, child in
            ShenoraElementView(element: child, state: state, tint: tint)
            if spread && index < children.count - 1 { Spacer(minLength: 0) }
        }
    }
}

/// A bar when the length is known and a spinner when it is not.
///
/// 🔴 `progress == nil` means INDETERMINATE, and rendering it as an empty bar is the one thing this must
/// not do — a download with no content-length and a download that has just started look identical then,
/// which is exactly the distinction the C# side documents `null` to carry.
///
/// ⚠ Lives HERE rather than beside the default views, because the static library linked into the APP
/// compiles this file and not that one — anything both halves need has to sit on this side of the split.
@available(iOS 16.2, *)
struct ShenoraProgress: View {
    let state: ShenoraActivityState
    let tint: Color

    var body: some View {
        if let progress = state.progress {
            ProgressView(value: max(0, min(1, progress))).tint(tint)
        } else {
            ProgressView().progressViewStyle(.linear).tint(tint)
        }
    }
}

extension ShenoraActivityState {
    /// Whole-percent text, or nil when the work is indeterminate.
    var percentText: String? {
        guard let progress else { return nil }
        return "\(Int((max(0, min(1, progress)) * 100).rounded()))%"
    }
}

/// Splits an app-described expanded panel across the platform's three separate views.
///
/// 🔴 **THIS IS WHY AN APP CAN DESCRIBE THE ISLAND AS ONE PANEL.** ActivityKit hands the expanded
/// presentation to a widget as three closures — leading, trailing, bottom — and nothing drawn in one can
/// cross into another, so no layout can literally span the sensor housing. The kit splits it instead: in
/// the first HORIZONTAL layout that contains a `.cutout`, the children before it become the leading view
/// and the children after it become the trailing view; everything else becomes the strip below.
///
/// ⚠ A panel with no cutout is not an error — it all goes to the strip, which is the only slot that can
/// host arbitrary content.
@available(iOS 16.2, *)
enum ShenoraPanelSplit {
    static func leading(_ element: ShenoraElement?) -> ShenoraElement? { flank(element, before: true) }
    static func trailing(_ element: ShenoraElement?) -> ShenoraElement? { flank(element, before: false) }

    /// Everything that is not the split row. A vertical layout keeps its other children; anything else
    /// falls through whole.
    static func below(_ element: ShenoraElement?) -> ShenoraElement? {
        guard let element else { return nil }
        guard case let .layout(axis, children, spacing, insets, justify, align) = element else { return element }
        if hasCutout(element) { return nil }                     // the row IS the split — nothing left under it
        let rest = children.filter { !hasCutout($0) }
        if rest.count == children.count { return element }       // no cutout anywhere: the whole thing
        if rest.isEmpty { return nil }
        return .layout(axis: axis, children: rest, spacing: spacing, insets: insets,
                       justify: justify, align: align)
    }

    /// The children on one side of the cutout, wrapped back up in their own layout so spacing, justify and
    /// align survive the split.
    private static func flank(_ element: ShenoraElement?, before: Bool) -> ShenoraElement? {
        guard let element, let row = splitRow(element),
              case let .layout(axis, children, spacing, _, justify, align) = row,
              let at = children.firstIndex(where: { if case .cutout = $0 { return true } else { return false } })
        else { return nil }
        let side = before ? Array(children.prefix(at)) : Array(children.suffix(from: at + 1))
        if side.isEmpty { return nil }
        if side.count == 1 { return side[0] }
        return .layout(axis: axis, children: side, spacing: spacing, insets: nil,
                       justify: justify, align: align)
    }

    /// The horizontal layout carrying the cutout — the element itself, or the first child that is one.
    private static func splitRow(_ element: ShenoraElement) -> ShenoraElement? {
        if hasCutout(element) { return element }
        guard case let .layout(_, children, _, _, _, _) = element else { return nil }
        return children.first(where: { hasCutout($0) })
    }

    /// True when this element is a layout whose OWN children include a cutout.
    private static func hasCutout(_ element: ShenoraElement) -> Bool {
        guard case let .layout(_, children, _, _, _, _) = element else { return false }
        return children.contains(where: { if case .cutout = $0 { return true } else { return false } })
    }
}

/// Cross-axis alignment for a horizontal stack — flexbox's `align-items`.
func shenoraVAlign(_ name: String) -> VerticalAlignment {
    switch name {
    case "Center": return .center
    case "Trailing": return .bottom
    // ⚠ `Fill` is NOT expressible as a VerticalAlignment — it is a SIZE, applied to the children as a
    // frame in ShenoraChildren. This line read "Fill stretches via frame" while no frame existed, so
    // `Align = Fill` was a documented option the renderer ignored (D63, fixed 2026-08-10).
    default: return .top
    }
}

/// Cross-axis alignment for a vertical stack.
func shenoraHAlign(_ name: String) -> HorizontalAlignment {
    switch name {
    case "Center": return .center
    case "Trailing": return .trailing
    default: return .leading
    }
}

/// `#RRGGBB` → Color, or nil. Anything unparseable is nil rather than an error: a malformed colour must
/// never be the reason a surface fails to draw.
func shenoraColor(_ hex: String?) -> Color? {
    guard var value = hex, value.hasPrefix("#"), value.count == 7 else { return nil }
    value.removeFirst()
    guard let rgb = UInt32(value, radix: 16) else { return nil }
    return Color(red: Double((rgb >> 16) & 0xFF) / 255.0,
                 green: Double((rgb >> 8) & 0xFF) / 255.0,
                 blue: Double(rgb & 0xFF) / 255.0)
}
