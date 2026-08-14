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
// SwiftUI for `Color` alone — `ShenoraActivityAppearance.color` resolves the app's hex tint so both the
// kit's default views and an adopter's own views read one implementation instead of two.
import SwiftUI

/// The static half — set once when the activity starts and never updated.
public struct ShenoraActivityAttributes: ActivityAttributes {
    public typealias ContentState = ShenoraActivityState

    /// A name for the job, for a view that wants to distinguish several. Not shown unless a view shows it.
    public var name: String

    /// How the surface should LOOK — MIRRORS Shenora's `LiveActivityAppearance`, and read by the kit's
    /// default views at runtime (D69: config, not code generation).
    ///
    /// ⚠ It lives in ATTRIBUTES rather than state because ActivityKit fixes attributes for the activity's
    /// lifetime, which is what a look wants. Anything that changes belongs in `ShenoraActivityState`.
    public var appearance: ShenoraActivityAppearance

    /// WHAT to draw, as a tree the app described in C# — MIRRORS Shenora's `Presentation`.
    ///
    /// ⚠ Nil means "use the kit's built-in arrangement", and so does any region left unset inside it. An
    /// app restyling the compact pill should not have to restate the lock-screen card.
    public var layout: ShenoraLayout?

    public init(name: String,
                appearance: ShenoraActivityAppearance = ShenoraActivityAppearance(),
                layout: ShenoraLayout? = nil) {
        self.name = name
        self.appearance = appearance
        self.layout = layout
    }
}

/// The static look — MIRRORS Shenora's `LiveActivityAppearance`. Keep in step.
public struct ShenoraActivityAppearance: Codable, Hashable {
    /// An SF Symbol name. Filled symbols read on the Island; outlines do not.
    public var symbol: String
    /// `#RRGGBB`, or nil for the system accent.
    public var tint: String?

    public init(symbol: String = "circle.fill", tint: String? = nil) {
        self.symbol = symbol
        self.tint = tint
    }

    /// The tint as a SwiftUI Color, or `.accentColor`. Anything unparseable falls back rather than
    /// throwing — a malformed colour must not be the reason an activity fails to render.
    public var color: Color {
        guard var hex = tint, hex.hasPrefix("#"), hex.count == 7 else { return .accentColor }
        hex.removeFirst()
        guard let value = UInt32(hex, radix: 16) else { return .accentColor }
        return Color(
            red: Double((value >> 16) & 0xFF) / 255.0,
            green: Double((value >> 8) & 0xFF) / 255.0,
            blue: Double(value & 0xFF) / 255.0)
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

/// The latest APNs push token per activity, hex-encoded.
///
/// ⚠ ActivityKit delivers these ASYNCHRONOUSLY and more than once — `pushTokenUpdates` is a sequence, not a
/// value, and the system may reissue. So this is a cache the reader polls rather than something `start` can
/// return: a token fetched once and held forever would go stale silently, which for a push channel means a
/// server talking to an address nobody is listening on.
private var shenoraLiveActivityTokens: [String: String] = [:]

/// Store one token under the gate, from a SYNCHRONOUS function.
///
/// 🔴 **This exists to keep `NSLock` out of an `async` context**, which Swift warns about today and makes
/// an ERROR in the Swift 6 language mode: *"instance method 'unlock' is unavailable from asynchronous
/// contexts"*. The compiler is guarding against a lock held across a suspension point — which this code
/// never does, since the critical section is two statements with no `await` — but the rule is applied to
/// the CONTEXT, not to the section. Lifting it into a plain function satisfies both: the critical section
/// provably cannot suspend, and the caller's `Task` never touches the lock.
///
/// ⚠ Worth keeping rather than silencing: an adopter compiles this file in THEIR build, so a warning here
/// is noise in their output, and a Swift 6 error here would be a build failure they cannot fix.
private func shenoraStoreToken(_ activityId: String, _ hex: String) {
    shenoraLiveActivityGate.lock()
    shenoraLiveActivityTokens[activityId] = hex
    shenoraLiveActivityGate.unlock()
}

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
                                   _ statePtr: UnsafePointer<CChar>,
                                   _ appearancePtr: UnsafePointer<CChar>,
                                   _ layoutPtr: UnsafePointer<CChar>) -> UnsafeMutablePointer<CChar> {
    guard #available(iOS 16.2, *) else { return shenoraCString("!os-too-old") }

    let name = String(cString: namePtr)
    guard let stateData = String(cString: statePtr).data(using: .utf8) else {
        return shenoraCString("!bad-utf8")
    }

    // ⚠ A malformed appearance FALLS BACK to the defaults rather than refusing the activity. The look is
    // decoration; the activity is the job. Failing the start because a hex colour was wrong would trade a
    // cosmetic problem for a functional one.
    let appearance = (try? JSONDecoder().decode(
        ShenoraActivityAppearance.self,
        from: String(cString: appearancePtr).data(using: .utf8) ?? Data())) ?? ShenoraActivityAppearance()

    // ⚠ Same rule as the appearance: an unreadable layout falls back to the kit's built-in arrangement
    // rather than refusing the activity. A layout is a description of a surface; the surface is the job.
    let layoutJson = String(cString: layoutPtr)
    let layout = layoutJson.isEmpty ? nil : try? JSONDecoder().decode(
        ShenoraLayout.self, from: layoutJson.data(using: .utf8) ?? Data())

    // 🔴 SAY WHETHER THE LAYOUT ARRIVED AND DECODED. Without this the three failures — no layout sent, a
    // layout that would not decode, and a layout that decoded and drew nothing — are one symptom: an
    // empty region. This repo has paid twice for a component that could not report its own failure, and
    // this is the third place it would have.
    // ⚠ Reports the EMPTY case too, and that is the whole point of it. Gating this on "a layout arrived"
    // made the diagnostic silent in exactly the situation it was added to explain — "no line" then meant
    // both "nothing was sent" and "the shim is stale", which is the circularity that cost several runs.
    // ⚠ BUILT AS STATEMENTS, NOT ONE `+` CHAIN. Swift's type checker gives up on a long concatenation of
    // interpolations — *"unable to type-check this expression in reasonable time"* — and the error points
    // at the whole `print`, not at the piece that grew. Adding one more region is what tipped it over.
    var regions = "lock=\(layout?.lockScreen != nil)"
    regions += " expanded=\(layout?.expanded != nil)"
    // ⚠ Reports the SPLIT, not the raw element — the app describes ONE panel and the kit divides it, so
    // "the panel arrived" and "it divided into the regions I expected" are different questions, and only
    // the second one explains an empty flank.
    regions += " (l=\(ShenoraPanelSplit.leading(layout?.expanded) != nil)"
    regions += " r=\(ShenoraPanelSplit.trailing(layout?.expanded) != nil)"
    regions += " b=\(ShenoraPanelSplit.below(layout?.expanded) != nil))"
    regions += " cl=\(layout?.compactLeading != nil)"
    regions += " ct=\(layout?.compactTrailing != nil)"
    regions += " min=\(layout?.minimal != nil)"
    print("[SHENORA] [Shenora.iOS] layout: \(layoutJson.count) bytes, decoded=\(layout != nil) [\(regions)]")
    if !layoutJson.isEmpty && layout == nil {
        print("[SHENORA] [Shenora.iOS] layout JSON was: \(layoutJson.prefix(400))")
    }

    do {
        let state = try JSONDecoder().decode(ShenoraActivityState.self, from: stateData)

        // 🔴 `pushType: .token` OR NO TOKEN IS EVER ISSUED. Requested without it — which was this shim's
        // first shape — `pushTokenUpdates` simply never yields, so `ILiveActivities.PushToken` would answer
        // null forever and look like a broken seam rather than an unasked-for one. Measured 2026-08-09: no
        // token within 5 s on a real device, because nothing had asked for one.
        //
        // ⚠ Asking for a token needs the app's push entitlement, so it can be REFUSED — and an app that
        // never intends to push must still be able to start an activity. So it falls back, and says which
        // path it took: a silent fallback here would recreate the same "null forever" mystery one layer up.
        var activity: Activity<ShenoraActivityAttributes>
        let attributes = ShenoraActivityAttributes(name: name, appearance: appearance, layout: layout)
        // `staleDate: nil` — and it MUST match `shenora_activity_update`'s, which carries the reasoning.
        // These two disagreed for a day (nil here, +60 s there), so an activity's freshness semantics
        // changed the moment its first update landed: not a design, two people's defaults.
        let content = ActivityContent(state: state, staleDate: nil)
        do {
            activity = try Activity.request(attributes: attributes, content: content, pushType: .token)
        } catch {
            print("[SHENORA] [Shenora.iOS] push-capable activity refused (\(error)); starting without a "
                + "push token — PushToken will answer null, which is correct rather than broken.")
            activity = try Activity.request(attributes: attributes, content: content)
        }
        shenoraLiveActivityGate.lock()
        shenoraLiveActivities[activity.id] = activity
        shenoraLiveActivityGate.unlock()

        // Start listening for the push token NOW, because the system issues it after the activity exists
        // and there is no synchronous way to ask for it. The sequence also REISSUES, so this keeps taking
        // updates for the activity's lifetime rather than reading one and stopping.
        let activityId = activity.id
        Task {
            for await tokenData in activity.pushTokenUpdates {
                let hex = tokenData.map { String(format: "%02x", $0) }.joined()
                shenoraStoreToken(activityId, hex)
                print("[SHENORA] [Shenora.iOS] live activity push token: \(hex.count / 2) bytes")
            }
        }
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

    // 🔴 THE UPDATE IS ASYNC AND THIS FUNCTION IS NOT, so the work is detached and the caller is told ""
    // (accepted) before anything has happened. That is unavoidable across a C ABI — and it means an update
    // that does not land would report NOTHING to anyone, which is why the task below prints.
    //
    // ⚠ This comment used to end "Measured 2026-08-09: the widget rendered and then held its first value
    // while C# logged three accepted updates." That symptom is GONE — re-measured the same day on an
    // iPhone 17 Pro against the kit's own generic widget: three updates, each logged `update applied:
    // progress=… state=active`, and the Island visibly stepped 33% → 67% → 100%. The stale half was
    // describing the hand-written sample views, not this path; the DIAGNOSTIC it argued for is what
    // proved the difference, so it stays.
    //
    // So the task reports what it did, and `activityState` is the part that actually diagnoses: ActivityKit
    // silently IGNORES an update to an activity that is `.ended` or `.dismissed`, which is indistinguishable
    // from a broken view unless something says which one it was.
    Task {
        // 🔴 `staleDate: nil` — THE KIT MAKES NO FRESHNESS CLAIM, AND RE-ADDING ONE IS A DECISION, NOT A
        // FIX. This line carried `Date().addingTimeInterval(60)` between 2026-08-09 and 2026-08-10.
        //
        // Why it went (owner's call, 2026-08-10): `staleDate` is a claim about CONTENT FRESHNESS, not a
        // repaint trigger — it tells the system when to consider the content out of date so a widget can
        // read `context.isStale`. So a horizon here silently declared EVERY adopter's activity stale 60 s
        // after its last update, which is simply wrong for a status activity that legitimately does not
        // change ("Waiting for server"), it was undocumented and unconfigurable, and the kit's own generic
        // widget never reads `isStale` at all — a flag set and never handled (D63's class).
        //
        // ⚠ AND BE HONEST ABOUT WHAT THIS REVERTS TO: `nil` is the configuration the original symptom was
        // observed in (2026-08-09, iPhone 17 Pro — the compact pill held a previous value while every
        // update was accepted and applied). It goes back because that symptom has a better explanation:
        // the SAME DAY fixed two defects that each independently produce a wrong render — the
        // `ShenoraElement.encode(to:)` stub and the layout enums crossing as NUMBERS — and the pill has
        // since been measured repainting with `nil`, mechanically, by frame hash
        // (`dev.mjs mac island-watch`: 8 frames, 5 distinct). A workaround kept on a hypothesis is worse
        // than the platform default; if hardware ever contradicts this, it comes back WITH the evidence.
        //
        // ⚠ The original failure was invisible from here — `update applied: progress=1.00 state=active`
        // printed happily throughout, and the owner caught it by LOOKING. An update has three outcomes
        // (accepted, applied, REPAINTED) and this process can only observe the first two. That part of the
        // lesson stands whatever the cause turns out to be, which is why `island-watch` exists.
        await activity.update(ActivityContent(state: state, staleDate: nil))
        let shown = state.progress.map { String(format: "%.2f", $0) } ?? "nil"
        print("[SHENORA] [Shenora.iOS] activity update applied: progress=\(shown) state=\(activity.activityState)")
    }
    return shenoraCString("")
}

/// The latest push token for an activity, hex-encoded, or "" when none has been issued yet.
///
/// ⚠ "" rather than an error for the not-yet case: the token arriving late is the NORMAL path, so a caller
/// polling right after `start` must not be told something went wrong.
@_cdecl("shenora_activity_push_token")
public func shenora_activity_push_token(_ idPtr: UnsafePointer<CChar>) -> UnsafeMutablePointer<CChar> {
    let id = String(cString: idPtr)
    shenoraLiveActivityGate.lock()
    let token = shenoraLiveActivityTokens[id]
    shenoraLiveActivityGate.unlock()
    return shenoraCString(token ?? "")
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
