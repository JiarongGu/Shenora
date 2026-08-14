// THE WIRE SHAPE — the Swift side of the element tree an app describes in C#. Types only: no SwiftUI, no
// ActivityKit, no Foundation.
//
// 🔴 THE ABSENCE OF IMPORTS IS THE FEATURE, not tidiness. These types used to live at the top of
// `ShenoraLayout.swift` beside the views, which meant the only way to exercise the decoder was to build an
// app and look at a phone — and BOTH defects this subsystem has ever had were pure DATA bugs on this leg
// (the stub `encode(to:)`; enums crossing as NUMBERS), each found by eye after a long hunt. Split out, the
// whole decoder compiles under a bare `swiftc` on any Mac with no SDK, no simulator and no device, which
// is what `node devtools/dev.mjs mac layout-check` does against the same golden payload the C# test
// asserts. Anything added here must keep that true: an `import` costs the only mechanical test this wire
// has.
//
// ⚠ MIRRORS Shenora's `Presentation` and its element records (namespace
// Shenora.Modules.Platform.Activities), discriminator for discriminator. The `kind` strings ARE the wire:
// they come from `[JsonDerivedType(..., "text")]` on the C# side, and renaming one without the other
// silently drops back to the default look.

// ⚠ PUBLIC, because `ShenoraActivityAttributes` is public and carries these. Swift refuses a public
// property whose type is internal, and the error names the property rather than the type — so it reads
// as a problem with the attributes when it is a problem here.
public struct ShenoraLayout: Codable, Hashable {
    public var lockScreen: ShenoraElement?
    public var expanded: ShenoraElement?
    public var compactLeading: ShenoraElement?
    public var compactTrailing: ShenoraElement?
    public var minimal: ShenoraElement?
}

/// Space around an element, per edge — MIRRORS Shenora's `Insets`.
///
/// ⚠ Edge ORDER matters and matches the C# record's, which in turn matches `SafeAreaInsets`. SwiftUI
/// talks leading/trailing (writing-direction aware) while the wire says left/right; the mapping is done
/// once, in `ShenoraLayout.swift`, rather than at every call site.
public struct ShenoraInsets: Codable, Hashable {
    public var top: Double = 0
    public var right: Double = 0
    public var bottom: Double = 0
    public var left: Double = 0
}

/// One node.
///
/// ⚠ Decoded MANUALLY rather than with an enum + associated values, because `Codable` synthesis cannot
/// read C#'s `{"kind": "...", ...}` shape — the discriminator sits beside the payload rather than
/// wrapping it. Hand-rolling the container is the smaller price than reshaping the C# wire.
public indirect enum ShenoraElement: Codable, Hashable {
    case text(value: String, role: String, tint: String?)
    case icon(symbol: String, tint: String?)
    case progress(tint: String?)
    case layout(axis: String, children: [ShenoraElement], spacing: Double?, insets: ShenoraInsets?, justify: String, align: String)
    case cutout
    case spacer
    /// An element kind this build does not know — a NEWER app against an older kit. Rendered as nothing,
    /// which loses one node instead of the whole surface.
    case unknown

    private enum Keys: String, CodingKey {
        case kind, value, role, tint, symbol, axis, children, spacing, insets
        case justify, align
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: Keys.self)
        switch try c.decodeIfPresent(String.self, forKey: .kind) ?? "" {
        case "text":
            self = .text(value: (try? c.decode(String.self, forKey: .value)) ?? "",
                         role: (try? c.decode(String.self, forKey: .role)) ?? "Body",
                         tint: try? c.decodeIfPresent(String.self, forKey: .tint) ?? nil)
        case "icon":
            self = .icon(symbol: (try? c.decode(String.self, forKey: .symbol)) ?? "circle.fill",
                         tint: try? c.decodeIfPresent(String.self, forKey: .tint) ?? nil)
        case "progress":
            self = .progress(tint: try? c.decodeIfPresent(String.self, forKey: .tint) ?? nil)
        case "layout":
            self = .layout(axis: (try? c.decode(String.self, forKey: .axis)) ?? "Vertical",
                          children: (try? c.decode([ShenoraElement].self, forKey: .children)) ?? [],
                          spacing: try? c.decodeIfPresent(Double.self, forKey: .spacing) ?? nil,
                          insets: try? c.decodeIfPresent(ShenoraInsets.self, forKey: .insets) ?? nil,
                          justify: (try? c.decode(String.self, forKey: .justify)) ?? "Start",
                          align: (try? c.decode(String.self, forKey: .align)) ?? "Leading")
        case "cutout":
            self = .cutout
        case "spacer":
            self = .spacer
        default:
            self = .unknown
        }
    }

    /// 🔴 **THE ENCODER IS LOAD-BEARING — IT IS THE APP→WIDGET LEG.** This started life as a stub that
    /// wrote `{"kind":"unknown"}` for every node, on the reasoning that "the kit only ever decodes this".
    /// That reasoning is wrong: ActivityKit ENCODES the whole `ActivityAttributes` to hand it across the
    /// process boundary to the widget extension, so a lossy encoder deletes the layout on the way in.
    ///
    /// ⚠ **And the symptom was a perfect lie.** The app-side diagnostic said `decoded=true ct=true` —
    /// true, and about the C#→shim leg, which was never the broken one. The widget then decoded
    /// `.unknown` and drew `EmptyView`, so the pill showed the kit's default leading icon beside an empty
    /// trailing region: exactly what "my layout was ignored" looks like. Cost a full simulator round-trip
    /// per wrong hypothesis (font scale, region restrictions, `Group`+`ForEach` shape). **Measured
    /// 2026-08-09. Symmetry with the decoder is not tidiness here — it is the feature**, and
    /// `mac layout-check` re-encodes the golden tree and decodes it again precisely so a second stub
    /// cannot reach a device.
    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: Keys.self)
        switch self {
        case let .text(value, role, tint):
            try c.encode("text", forKey: .kind)
            try c.encode(value, forKey: .value)
            try c.encode(role, forKey: .role)
            try c.encodeIfPresent(tint, forKey: .tint)
        case let .icon(symbol, tint):
            try c.encode("icon", forKey: .kind)
            try c.encode(symbol, forKey: .symbol)
            try c.encodeIfPresent(tint, forKey: .tint)
        case let .progress(tint):
            try c.encode("progress", forKey: .kind)
            try c.encodeIfPresent(tint, forKey: .tint)
        case let .layout(axis, children, spacing, insets, justify, align):
            try c.encode("layout", forKey: .kind)
            try c.encode(axis, forKey: .axis)
            try c.encode(children, forKey: .children)
            try c.encodeIfPresent(spacing, forKey: .spacing)
            try c.encodeIfPresent(insets, forKey: .insets)
            try c.encode(justify, forKey: .justify)
            try c.encode(align, forKey: .align)
        case .cutout:
            try c.encode("cutout", forKey: .kind)
        case .spacer:
            try c.encode("spacer", forKey: .kind)
        case .unknown:
            try c.encode("unknown", forKey: .kind)
        }
    }
}
