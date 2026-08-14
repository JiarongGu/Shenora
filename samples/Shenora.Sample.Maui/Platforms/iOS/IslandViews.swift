// THE ONLY SWIFT AN ADOPTING APP WRITES. Four view bodies, and nothing else — no lifecycle, no
// Info.plist, no .xcodeproj, no codesigning. The kit compiles this together with its own
// ShenoraLiveActivity.swift into a widget extension, embeds it and signs it; the app opts in with one
// MSBuild property:
//
//   <ShenoraLiveActivityViews>Platforms/iOS/IslandViews.swift</ShenoraLiveActivityViews>
//
// The types below come from the kit's file, compiled into the same module: `ShenoraActivityAttributes`
// and its `ShenoraActivityState` (title / subtitle / progress, mirroring Shenora.Core.LiveActivityState).
//
// This is where a real app's design system lives, which is exactly why the kit does not ship it (D13).
//
// 🔴 THESE VIEWS ARE DELIBERATELY LOUD, and that is a lesson rather than a style choice. The first
// version put `Text("S")` in compactLeading and a percentage in compactTrailing — correct code, and on a
// real device (iPhone 17 Pro, 2026-08-07) the owner's verdict was "just making a longer bar and shows
// nothing". A single glyph on a black pill IS nothing, visually. The Dynamic Island gives a compact region
// roughly one SF Symbol wide, so anything subtler than a filled, tinted symbol reads as empty — and an
// empty Island is indistinguishable from a widget the system failed to match, which is the far more
// alarming failure. **Make the sample unmistakable, so that "blank" only ever means broken.**

import ActivityKit
import SwiftUI
import WidgetKit

@main
struct ShenoraSampleWidgets: WidgetBundle {
    var body: some Widget {
        ShenoraSampleActivity()
    }
}

/// The sample's accent. An app brings its own; the kit ships none (D13).
private let shenoraTint = Color(red: 0.42, green: 0.62, blue: 0.98)

struct ShenoraSampleActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: ShenoraActivityAttributes.self) { context in
            // The lock-screen / banner presentation. More room here than anywhere else, so it carries
            // the full story: what is happening, what it is happening to, and how far along it is.
            HStack(spacing: 12) {
                Image(systemName: "waveform.circle.fill")
                    .font(.system(size: 34))
                    .foregroundStyle(shenoraTint)

                VStack(alignment: .leading, spacing: 4) {
                    Text(context.state.title ?? "Working")
                        .font(.headline)
                    if let subtitle = context.state.subtitle {
                        Text(subtitle)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    // nil progress means INDETERMINATE, so a spinner rather than an empty bar — an empty
                    // bar says "0% done", which is a different claim.
                    if let progress = context.state.progress {
                        ProgressView(value: progress).tint(shenoraTint)
                    } else {
                        ProgressView().progressViewStyle(.linear).tint(shenoraTint)
                    }
                }
            }
            .padding()
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    Image(systemName: "waveform.circle.fill")
                        .font(.title2)
                        .foregroundStyle(shenoraTint)
                }
                DynamicIslandExpandedRegion(.trailing) {
                    Text(percentText(context.state.progress))
                        .font(.title3.monospacedDigit().bold())
                        .foregroundStyle(shenoraTint)
                }
                DynamicIslandExpandedRegion(.center) {
                    Text(context.state.title ?? "Working")
                        .font(.subheadline.bold())
                        .lineLimit(1)
                }
                DynamicIslandExpandedRegion(.bottom) {
                    if let progress = context.state.progress {
                        ProgressView(value: progress).tint(shenoraTint)
                    } else {
                        ProgressView().progressViewStyle(.linear).tint(shenoraTint)
                    }
                }
            } compactLeading: {
                // A FILLED, TINTED symbol. A bare glyph here is invisible against the pill.
                Image(systemName: "waveform.circle.fill")
                    .foregroundStyle(shenoraTint)
            } compactTrailing: {
                // A ring rather than text: the compact trailing region is about one symbol wide, and
                // "100%" in it is clipped to something unreadable.
                if let progress = context.state.progress {
                    ProgressView(value: progress)
                        .progressViewStyle(.circular)
                        .tint(shenoraTint)
                } else {
                    ProgressView().progressViewStyle(.circular).tint(shenoraTint)
                }
            } minimal: {
                Image(systemName: "waveform.circle.fill")
                    .foregroundStyle(shenoraTint)
            }
            .keylineTint(shenoraTint)
        }
    }

    /// Expanded-trailing has room for a percentage; the compact regions do not (see `compactTrailing`).
    private func percentText(_ progress: Double?) -> String {
        guard let progress else { return "—" }
        return "\(Int(progress * 100))%"
    }
}
