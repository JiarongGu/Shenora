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

import ActivityKit
import SwiftUI
import WidgetKit

@main
struct ShenoraSampleWidgets: WidgetBundle {
    var body: some Widget {
        ShenoraSampleActivity()
    }
}

struct ShenoraSampleActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: ShenoraActivityAttributes.self) { context in
            // The lock-screen / banner presentation.
            VStack(alignment: .leading, spacing: 4) {
                Text(context.state.title ?? "Working")
                    .font(.headline)
                if let subtitle = context.state.subtitle {
                    Text(subtitle).font(.caption)
                }
                // nil progress means INDETERMINATE, so a spinner rather than an empty bar — an empty bar
                // says "0% done", which is a different claim.
                if let progress = context.state.progress {
                    ProgressView(value: progress)
                } else {
                    ProgressView()
                }
            }
            .padding()
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    Text("S").font(.headline)
                }
                DynamicIslandExpandedRegion(.trailing) {
                    Text(percentText(context.state.progress))
                }
                DynamicIslandExpandedRegion(.center) {
                    Text(context.state.title ?? "Working").font(.caption)
                }
                DynamicIslandExpandedRegion(.bottom) {
                    if let progress = context.state.progress {
                        ProgressView(value: progress)
                    } else {
                        ProgressView()
                    }
                }
            } compactLeading: {
                Text("S")
            } compactTrailing: {
                Text(percentText(context.state.progress))
            } minimal: {
                Text("S")
            }
        }
    }

    /// Compact regions get a few characters at most, so a percentage is the only honest thing to put there.
    private func percentText(_ progress: Double?) -> String {
        guard let progress else { return "…" }
        return "\(Int(progress * 100))%"
    }
}
