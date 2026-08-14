// THE SWIFT HALF OF THE LIVE ACTIVITY GOLDEN TEST.
//
// Compiled with `ShenoraLayoutWire.swift` — the SHIPPED decoder, not a copy — by
// `node devtools/dev.mjs mac layout-check`, which uploads this file as `main.swift` (swiftc only allows
// top-level code in a file with that name) together with the two goldens the C# suite asserts.
//
// 🔴 WHAT THIS PROVES THAT THE C# HALF CANNOT. `LiveActivityGoldenTests` proves the payload is what the
// kit means to send. This proves the payload is what the WIDGET reads:
//   1. the real decoder turns the committed JSON into the tree the C# side described from its own
//      objects — so a key that decodes to a default (the enums-as-NUMBERS defect) fails here by name;
//   2. that tree survives a re-ENCODE and a second decode — the leg ActivityKit itself uses to hand
//      attributes to the widget process, and the leg a stub `encode(to:)` silently deleted for a
//      fortnight while every app-side diagnostic printed `decoded=true`.
//
// ⚠ It compiles for the HOST (macOS), not for iOS. `JSONDecoder` is the same Foundation implementation on
// both, and the point of the wire file importing nothing is that there is no platform left to differ —
// but say "decoded on the Mac" rather than "decoded on a device" when reporting it.

import Foundation

// ── the same description the C# side writes ──────────────────────────────────────────────────────────
//
// ⚠ THIS FORMATTER IS ONE HALF OF A CONTRACT, and its twin is `LiveActivityGoldenTests.Describe`. Change
// either and both goldens have to be regenerated and re-read — which is the intended cost, because the
// two texts matching is the whole assertion.

/// Whole numbers as integers. The fixture is kept integral on the C# side precisely so neither language
/// needs a float format the other has to reproduce; anything else prints distinctively and mismatches.
func num(_ v: Double) -> String {
    v == v.rounded() && abs(v) < 1e9 ? String(Int(v)) : String(v)
}

func describe(_ element: ShenoraElement, _ depth: Int, _ out: inout String) {
    let pad = String(repeating: " ", count: depth * 2)
    switch element {
    case let .text(value, role, tint):
        out += "\(pad)text value=\"\(value)\" role=\(role) tint=\(tint ?? "-")\n"
    case let .icon(symbol, tint):
        out += "\(pad)icon symbol=\"\(symbol)\" tint=\(tint ?? "-")\n"
    case let .progress(tint):
        out += "\(pad)progress tint=\(tint ?? "-")\n"
    case .cutout:
        out += "\(pad)cutout\n"
    case .spacer:
        out += "\(pad)spacer\n"
    case let .layout(axis, children, spacing, insets, justify, align):
        // `insets=-` cannot come from the C# side — `Insets` is a non-nullable struct, so the wire always
        // carries the object. Printing it distinctly means "the key did not arrive" fails as itself rather
        // than as a zero.
        let box = insets.map { "\(num($0.top)),\(num($0.right)),\(num($0.bottom)),\(num($0.left))" } ?? "-"
        out += "\(pad)layout axis=\(axis) spacing=\(spacing.map(num) ?? "-") insets=\(box)"
        out += " justify=\(justify) align=\(align)\n"
        for child in children { describe(child, depth + 1, &out) }
    case .unknown:
        // The C# side cannot produce this — the element set is closed — so it is always a decode failure
        // here, and it must SAY so rather than print nothing.
        out += "\(pad)unknown\n"
    }
}

func describe(_ name: String, _ layout: ShenoraLayout) -> String {
    var out = "\(name)\n"
    let regions: [(String, ShenoraElement?)] = [
        ("lockScreen", layout.lockScreen),
        ("expanded", layout.expanded),
        ("compactLeading", layout.compactLeading),
        ("compactTrailing", layout.compactTrailing),
        ("minimal", layout.minimal),
    ]
    for (name, element) in regions {
        guard let element else { out += "  \(name): -\n"; continue }
        out += "  \(name):\n"
        describe(element, 2, &out)
    }
    return out
}

// ── the run ──────────────────────────────────────────────────────────────────────────────────────────

func fail(_ message: String) -> Never {
    print("FAIL: \(message)")
    exit(1)
}

let args = CommandLine.arguments
guard args.count == 3 else { fail("usage: layout-golden-check <payload.json> <tree.txt>") }

guard let payload = FileManager.default.contents(atPath: args[1]) else { fail("cannot read \(args[1])") }
guard let treeData = FileManager.default.contents(atPath: args[2]),
      let expectedRaw = String(data: treeData, encoding: .utf8) else { fail("cannot read \(args[2])") }
// `.gitattributes` says `* text=auto`, so these files are CRLF in the Windows tree that writes them and
// LF here. Normalising is not cosmetic: a byte comparison would fail for a difference no decoder can see.
let expected = expectedRaw.replacingOccurrences(of: "\r\n", with: "\n")

let cases: [String: ShenoraLayout]
do {
    cases = try JSONDecoder().decode([String: ShenoraLayout].self, from: payload)
} catch {
    fail("the golden payload did not decode into [String: ShenoraLayout] at all — \(error)")
}
guard !cases.isEmpty else { fail("the golden payload decoded to ZERO cases; a check over nothing passes") }

// Sorted, because a Swift dictionary has no order and the C# side sorts too. The comparison is
// deterministic by construction rather than by luck.
var actual = ""
for name in cases.keys.sorted() {
    actual += describe(name, cases[name]!)
}

if actual != expected {
    let a = expected.components(separatedBy: "\n")
    let b = actual.components(separatedBy: "\n")
    var line = 0
    while line < min(a.count, b.count) && a[line] == b[line] { line += 1 }
    print("FAIL: the Swift decoder did not reproduce the committed tree.")
    print("  first difference at line \(line + 1) of \(args[2])")
    print("  C#    : \(line < a.count ? a[line] : "<end of file>")")
    print("  Swift : \(line < b.count ? b[line] : "<end of file>")")
    print("")
    print("  A line that differs only in a value means the DECODER read something else — an enum that")
    print("  crossed as a number, a coding key that no longer matches, a property that stopped being")
    print("  written. Fix the side that is wrong, then regenerate BOTH goldens on Windows")
    print("  (SHENORA_UPDATE_GOLDEN=1) and re-run this.")
    exit(1)
}

// 🔴 THE RE-ENCODE. ActivityKit encodes the whole `ActivityAttributes` to hand them to the widget process,
// so an encoder that loses a node deletes the layout on the way IN — with every app-side diagnostic still
// reporting success, which is exactly how it shipped. Decoding what we just encoded is the cheapest thing
// that can ever catch it.
for name in cases.keys.sorted() {
    let round: ShenoraLayout
    do {
        round = try JSONDecoder().decode(ShenoraLayout.self, from: JSONEncoder().encode(cases[name]!))
    } catch {
        fail("\(name) did not survive an encode/decode round trip — \(error)")
    }
    let before = describe(name, cases[name]!)
    let after = describe(name, round)
    if before != after {
        print("FAIL: \(name) changed shape when re-encoded — ShenoraElement.encode(to:) is lossy.")
        print("  This is the app→widget leg. ActivityKit encodes the attributes to cross the process")
        print("  boundary, so whatever this drops is what the widget will NOT draw, silently.")
        exit(1)
    }
}

print("layout-check: OK — \(cases.count) presentation(s) decoded to the committed tree and survived a "
      + "re-encode.")
