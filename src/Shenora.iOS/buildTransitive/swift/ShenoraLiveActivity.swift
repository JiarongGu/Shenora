// THE KIT'S SWIFT HALF of a Live Activity. Shipped as SOURCE, not as a binary, and it has to be: the
// app's SwiftUI views must be compiled into the same module as the attributes type, because ActivityKit
// pairs a running activity with a widget by that type — and a Swift type's identity includes its module.
// (Measured: the same source compiled into two different module names silently never pairs, every API
// call still reports success, and nothing renders.)
//
// So the build step compiles THIS file plus the app's views together. See Shenora.iOS.targets.
//
// ── THE MIRROR ──────────────────────────────────────────────────────────────────────────────────────
// `ShenoraActivityState` below mirrors `Shenora.Core.LiveActivityState` field for field. A tripwire test
// (LiveActivityMirrorTests) reads both files and fails if they drift, because drift here fails SILENTLY:
// the JSON decodes to nothing, the activity does not appear, and no error is raised anywhere.
// Change one, change the other, in the same commit.

import ActivityKit
import Foundation

/// The static half — set once when the activity starts and never updated.
public struct ShenoraActivityAttributes: ActivityAttributes {
    public typealias ContentState = ShenoraActivityState

    /// A name for the job, for a view that wants to distinguish several. Not shown unless a view shows it.
    public var name: String

    public init(name: String) {
        self.name = name
    }
}

/// The dynamic half — MIRRORS Shenora.Core.LiveActivityState. Keep in step.
public struct ShenoraActivityState: Codable, Hashable {
    public var title: String?
    public var subtitle: String?
    /// 0…1, or nil for indeterminate work. A view should render a spinner for nil, not an empty bar.
    public var progress: Double?

    public init(title: String? = nil, subtitle: String? = nil, progress: Double? = nil) {
        self.title = title
        self.subtitle = subtitle
        self.progress = progress
    }
}

// ── THE LIFECYCLE, EXPORTED AS C ─────────────────────────────────────────────────────────────────────
// ActivityKit is Swift-only — its ObjC header is an empty include guard, verified against the SDK — so
// even start/update/end needs a shim. @_cdecl gives each one an unmangled symbol, which C# binds with
// [DllImport("__Internal")] because a static library's symbols land in the app binary itself.
//
// Everything crosses as UTF-8 JSON plus a returned handle string: one narrow, stable ABI rather than a
// growing set of typed entry points, which is the same choice the kit's IPC already makes.

private var shenoraLiveActivities: [String: Any] = [:]
private let shenoraLiveActivityGate = NSLock()

private func shenoraCString(_ s: String) -> UnsafeMutablePointer<CChar> {
    // strdup so ownership is unambiguous: Swift allocates, C# copies it out and calls
    // shenora_activity_free. Returning a pointer into a Swift String's storage would be a use-after-free
    // the moment that String is released.
    return strdup(s)!
}

@_cdecl("shenora_activity_free")
public func shenora_activity_free(_ p: UnsafeMutablePointer<CChar>?) {
    if let p { free(p) }
}

/// "" when activities can be started, otherwise a REASON — the OS being too old and the user having
/// switched them off are different answers an app may want to report differently.
@_cdecl("shenora_activity_unavailable")
public func shenora_activity_unavailable() -> UnsafeMutablePointer<CChar> {
    if #available(iOS 16.2, *) {
        return shenoraCString(ActivityAuthorizationInfo().areActivitiesEnabled
            ? "" : "Live Activities are switched off for this app in Settings.")
    }
    return shenoraCString("Live Activities need iOS 16.2 or newer.")
}

/// Start one. Returns the activity's id, or `!<reason>` — never a bare null, so a failure is diagnosable
/// from C# instead of arriving as a silent nothing.
@_cdecl("shenora_activity_start")
public func shenora_activity_start(_ namePtr: UnsafePointer<CChar>,
                                   _ statePtr: UnsafePointer<CChar>) -> UnsafeMutablePointer<CChar> {
    guard #available(iOS 16.2, *) else { return shenoraCString("!os-too-old") }

    let name = String(cString: namePtr)
    guard let stateData = String(cString: statePtr).data(using: .utf8) else {
        return shenoraCString("!bad-utf8")
    }

    do {
        let state = try JSONDecoder().decode(ShenoraActivityState.self, from: stateData)
        let activity = try Activity.request(
            attributes: ShenoraActivityAttributes(name: name),
            content: ActivityContent(state: state, staleDate: nil))
        shenoraLiveActivityGate.lock()
        shenoraLiveActivities[activity.id] = activity
        shenoraLiveActivityGate.unlock()
        return shenoraCString(activity.id)
    } catch {
        // The reason, not a bool. A rejected request and a malformed payload look identical from C#
        // otherwise, and the payload is what an app gets wrong while wiring this up.
        return shenoraCString("!\(error)")
    }
}

@_cdecl("shenora_activity_update")
public func shenora_activity_update(_ idPtr: UnsafePointer<CChar>,
                                    _ statePtr: UnsafePointer<CChar>) -> UnsafeMutablePointer<CChar> {
    guard #available(iOS 16.2, *) else { return shenoraCString("!os-too-old") }

    let id = String(cString: idPtr)
    shenoraLiveActivityGate.lock()
    let held = shenoraLiveActivities[id]
    shenoraLiveActivityGate.unlock()
    guard let activity = held as? Activity<ShenoraActivityAttributes> else {
        return shenoraCString("!unknown-handle")
    }
    guard let data = String(cString: statePtr).data(using: .utf8),
          let state = try? JSONDecoder().decode(ShenoraActivityState.self, from: data) else {
        return shenoraCString("!bad-state")
    }

    Task { await activity.update(ActivityContent(state: state, staleDate: nil)) }
    return shenoraCString("")
}

@_cdecl("shenora_activity_end")
public func shenora_activity_end(_ idPtr: UnsafePointer<CChar>) -> UnsafeMutablePointer<CChar> {
    guard #available(iOS 16.2, *) else { return shenoraCString("!os-too-old") }

    let id = String(cString: idPtr)
    shenoraLiveActivityGate.lock()
    let held = shenoraLiveActivities.removeValue(forKey: id)
    shenoraLiveActivityGate.unlock()
    // An already-ended or never-valid handle is IGNORED, per the contract — an app tearing down twice is
    // not an error worth reporting.
    guard let activity = held as? Activity<ShenoraActivityAttributes> else { return shenoraCString("") }

    Task { await activity.end(nil, dismissalPolicy: .immediate) }
    return shenoraCString("")
}
