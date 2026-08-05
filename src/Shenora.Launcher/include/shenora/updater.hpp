// The apply phase — the ONE part of staged updates that genuinely cannot be done in .NET, because it
// runs when the runtime may be absent and must replace files the app holds open.
//
// Everything else already shipped in portable C# (`Shenora.IO`): checking a release source, diffing,
// downloading, and SHA-256-verifying every staged file. This library deliberately re-implements none
// of it. In particular it does NOT re-hash anything: `ready.json` exists only when the C# side verified
// the whole stage, and the marker's documented meaning is that an applier can act without re-checking.
// Re-verifying here would either duplicate a rule that can drift or slow every start for nothing.
#pragma once

#include <filesystem>
#include <string>
#include <vector>

namespace shenora {

struct ApplyOptions {
    /// The install root — `{root}` in Sonora's topology (D50/§2). The launcher lives HERE.
    std::filesystem::path root;

    /// The directory the update overlays, relative to `root`. Sonora's separation puts the app in
    /// `app/` so the launcher is structurally OUTSIDE its own update target, which deletes a whole
    /// class of self-exclusion bug rather than guarding it (§2). An adopter using the flat layout
    /// passes "." and takes the guards back on.
    std::string app_subdir = "app";

    /// Wait this long for a lingering app instance to exit before overlaying.
    int close_timeout_ms = 10000;

    /// Where diagnostics go. A launcher has no console on Windows, so the default is a file next to
    /// the marker rather than stdout — the failure this reports happens on somebody else's machine.
    std::filesystem::path log_file;
};

struct ApplyResult {
    bool attempted = false;   // was a stage pending at all?
    bool applied = false;
    std::string version;
    std::vector<std::string> written;
    std::vector<std::string> removed;
    std::string failure;      // set only when applied == false && attempted == true
};

/// Apply a pending stage, or report that there was none.
///
/// The order mirrors `UpdateStage.ApplyAsync` exactly, because the two must agree about what a
/// half-applied tree looks like:
///   1. marker present?                → no: nothing staged, not a failure
///   2. read `staged/manifest.json`    → missing/empty/unreadable: REFUSE. Removals are
///                                       "installed minus release", so an unreadable release manifest
///                                       would delete every tracked path INCLUDING what was just
///                                       overlaid, turning a successful copy into a corrupt install.
///                                       This is the guard one donor had and the other did not (§4).
///   3. read the installed baseline    → absent is fine: overlay, remove nothing.
///   4. close lingering app processes  → §4, and skip our own pid.
///   5. overlay staged → app           → `manifest.json` excluded; it is written explicitly in 6.
///   6. write the new baseline
///   7. remove TRACKED paths only      → never a directory sweep: user data lives in the same tree.
///   8. clear the stage
ApplyResult apply_pending_update(const ApplyOptions& options);

}  // namespace shenora
