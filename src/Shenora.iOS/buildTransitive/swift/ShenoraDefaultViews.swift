// THE KIT'S DEFAULT LIVE ACTIVITY — used when an app sets `ShenoraLiveActivity=true` and supplies no
// views of its own. This is D69: the app describes the activity in C# (`LiveActivityAppearance`) and this
// GENERIC widget reads that config at runtime. No code generation, no `.xcodeproj`, and — the point —
// **no Swift in the adopting app**.
//
// 🔴 IT IS A DEFAULT, NOT A CEILING. An app that wants its own design system points
// `ShenoraLiveActivityViews` at its own file and this one is not compiled at all. That is D13 holding:
// the kit ships no design system, so what it ships here has to be plain enough to be replaced without
// regret and complete enough to be worth not replacing.
//
// ⚠ THE VIEWS ARE DELIBERATELY LOUD, and that is earned rather than styled. An earlier hand-written
// sample put a single glyph in the compact region; on a real iPhone the verdict was "just making a longer
// bar and shows nothing". A single glyph on a black pill IS nothing, visually — and an empty Island is
// indistinguishable from a widget the system failed to match, which is the far more alarming failure.
// **Make the default unmistakable, so that "blank" only ever means broken.**

import ActivityKit
import SwiftUI
import WidgetKit

@main
struct ShenoraDefaultWidgets: WidgetBundle {
    var body: some Widget {
        ShenoraDefaultActivity()
    }
}

struct ShenoraDefaultActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: ShenoraActivityAttributes.self) { context in
            // ── LOCK SCREEN / BANNER ──────────────────────────────────────────────────────────────────
            // An app-described region wins; anything it left unset keeps the kit's arrangement, so
            // restyling the pill does not mean restating the card.
            // 🔴 NO `.padding()` HERE. When the app describes the region it owns its insets completely
            // (`Stack.Padding`) — a margin the kit added and the app could not remove would be the most
            // annoying possible default, and "why is there a gap I did not ask for" is unanswerable from
            // the app's side. The built-in arrangement below keeps its own padding, because there the kit
            // IS the author.
            if let region = context.attributes.layout?.lockScreen {
                ShenoraElementView(element: region, state: context.state,
                                   tint: context.attributes.appearance.color)
            } else {
            // ⚠ THE LOCK-SCREEN CARD'S METRICS ARE THE HELPER'S REAL DELIVERABLE — an adopter should get
            // these right without thinking about them. `.top` alignment (not the default `.center`) so
            // the icon sits on the title's line rather than floating beside the middle of a two-line
            // block; a FIXED icon frame so swapping the symbol cannot re-flow the text; and a 14/4 gap
            // pair that keeps the bar visually attached to the text it describes.
            HStack(alignment: .top, spacing: 14) {
                Image(systemName: context.attributes.appearance.symbol)
                    .font(.title2)
                    .frame(width: 28, height: 28)
                    .foregroundStyle(context.attributes.appearance.color)
                VStack(alignment: .leading, spacing: 4) {
                    Text(context.state.title ?? context.attributes.name)
                        .font(.headline)
                        .lineLimit(1)
                    if let subtitle = context.state.subtitle {
                        Text(subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                    ShenoraProgress(state: context.state, tint: context.attributes.appearance.color)
                        .padding(.top, 2)
                }
            }
            .padding(16)
            }
        } dynamicIsland: { context in
            DynamicIsland {
                // ── THE EXPANDED CARD, PER REGION ─────────────────────────────────────────────────────
                // 🔴 EACH SLOT FALLS BACK INDEPENDENTLY, exactly like the compact pair below. An app can
                // fill the flanking row and keep the kit's strip, or the reverse. The earlier shape —
                // ONE `expanded` element that took the whole card — could only ever be given `.bottom`,
                // so describing the card LEFT THE CUTOUT ROW EMPTY and the result read as a dead band
                // across the top (owner, 2026-08-09: "make this more utilize the top section").
                //
                // 🔴 THE BRANCH IS INSIDE EACH REGION, NOT AROUND THEM, AND THAT IS A COMPILER FACT
                // RATHER THAN A PREFERENCE. `@DynamicIslandExpandedContentBuilder` has no `buildEither`,
                // so wrapping the three regions in an `if/else` fails with *"closure containing control
                // flow statement cannot be used with result builder"* plus a misleading *"generic
                // parameter 'Expanded' could not be inferred"*. The regions are structural; only their
                // CONTENT may vary — which is also what forces the per-region fallback to be the design.
                //
                // 🔴 THE EXPANDED SLOTS WENT UNREAD ENTIRELY UNTIL 2026-08-09. The presentation record had
                // declared one, documented it, serialized it, mirrored it in Swift and decoded it — and no
                // view ever consulted it. D63's defect class, committed in the same release that
                // introduced the feature: an app describing the card got the kit's default and would have
                // read that as "my layout was ignored", with nothing logged on either side.
                // `Every_layout_region_is_consulted_by_the_default_views` now fails if a region is added
                // to the record and left unread here.
                //
                // 🔴 THREE REGIONS, BECAUSE THE SENSOR HOUSING IS PHYSICALLY IN THE WAY. `.leading` and
                // `.trailing` are the row that FLANKS the cutout; `.bottom` is the full-width strip under
                // it. Collapsing everything into `.bottom` was tried and rejected on a device: it aligns
                // perfectly and leaves the row beside the cutout empty, so the card gains a dead band at
                // the top and looks more cramped, not less.
                //
                // ⚠ Each region carries its OWN insets, so content cannot align ACROSS them. The fix is
                // not to fight that — it is to put things in the region they belong to and let the system
                // own the gutter: a symbol and a value are edge content, prose and a bar are not.
                DynamicIslandExpandedRegion(.leading) {
                    if let region = ShenoraPanelSplit.leading(context.attributes.layout?.expanded) {
                        ShenoraElementView(element: region, state: context.state,
                                           tint: context.attributes.appearance.color)
                    } else {
                        // `.frame(width:)` so the icon occupies the SAME space whatever symbol an app picks
                        // — otherwise `waveform` and `circle.fill` push the layout to different widths and
                        // an adopter's card jumps when they change the symbol.
                        Image(systemName: context.attributes.appearance.symbol)
                            .font(.title2)
                            .frame(width: 32, alignment: .leading)
                            .foregroundStyle(context.attributes.appearance.color)
                    }
                }
                DynamicIslandExpandedRegion(.trailing) {
                    if let region = ShenoraPanelSplit.trailing(context.attributes.layout?.expanded) {
                        ShenoraElementView(element: region, state: context.state,
                                           tint: context.attributes.appearance.color)
                    // ⚠ Only when determinate: "0%" for unknown-length work is a lie that looks like a
                    // stalled job, which is precisely the distinction `progress == nil` exists to make.
                    } else if let percent = context.state.percentText {
                        Text(percent)
                            .font(.title3)
                            .monospacedDigit()          // so the width does not jitter as digits change
                            .frame(minWidth: 52, alignment: .trailing)
                    }
                }
                DynamicIslandExpandedRegion(.bottom) {
                    if let region = ShenoraPanelSplit.below(context.attributes.layout?.expanded) {
                        ShenoraElementView(element: region, state: context.state,
                                           tint: context.attributes.appearance.color)
                    } else {
                        VStack(alignment: .leading, spacing: 6) {
                            Text(context.state.title ?? context.attributes.name)
                                .font(.headline).lineLimit(1)
                            if let subtitle = context.state.subtitle {
                                Text(subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                            }
                            ShenoraProgress(state: context.state, tint: context.attributes.appearance.color)
                        }
                        // Breathing room under the bar. Without it the bar sits ON the card's bottom edge,
                        // which reads as clipped rather than as full-bleed.
                        .padding(.top, 2)
                        .padding(.bottom, 6)
                    }
                }
            } compactLeading: {
                if let region = context.attributes.layout?.compactLeading {
                    ShenoraElementView(element: region, state: context.state,
                                       tint: context.attributes.appearance.color)
                } else {
                    Image(systemName: context.attributes.appearance.symbol)
                        .foregroundStyle(context.attributes.appearance.color)
                }
            } compactTrailing: {
                if let region = context.attributes.layout?.compactTrailing {
                    ShenoraElementView(element: region, state: context.state,
                                       tint: context.attributes.appearance.color)
                } else if let percent = context.state.percentText {
                    Text(percent).monospacedDigit()
                } else {
                    ProgressView().progressViewStyle(.circular)
                }
            } minimal: {
                if let region = context.attributes.layout?.minimal {
                    ShenoraElementView(element: region, state: context.state,
                                       tint: context.attributes.appearance.color)
                } else {
                    Image(systemName: context.attributes.appearance.symbol)
                        .foregroundStyle(context.attributes.appearance.color)
                }
            }
        }
    }
}

// ⚠ `ShenoraProgress` and `ShenoraActivityState.percentText` live in ShenoraLayout.swift, not here,
// because the interpreter needs them too — and the static library linked into the APP compiles the layout
// file WITHOUT this one, so anything shared has to sit there.
