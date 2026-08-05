// The thin platform seam. Everything else in this library is `std::filesystem` and therefore portable;
// these four things are not, and keeping them behind one header is what lets `updater.cpp` — the part
// carrying the earned guards — compile unchanged on Windows and Linux.
//
// D50's requirement is Linux AND Windows, with Linux for a future need. That is met by having the
// POSIX implementation exist and be built in CI from day one, rather than by leaving a TODO: a
// portability claim nothing compiles is the kind this repo does not make.
#pragma once

#include <filesystem>
#include <string>
#include <vector>

namespace shenora {

/// Absolute path of the RUNNING image.
///
/// ⚠ Resolved dynamically, never hard-coded to a name. §4 records why: with the primary sibling's
/// topology the launcher sits inside its own update target, and every self-exclusion guard it needs
/// depends on knowing which file it actually is. Sonora's topology (D50, and what the template uses)
/// makes those guards unreachable rather than merely correct — but this stays available, because an
/// adopter migrating from the other layout still needs it.
std::filesystem::path executable_path();

/// Process ids currently holding an executable open under `root`, EXCLUDING this process.
///
/// §4's "close-all before overlay, skipping the applier's own PID". Topology does not cover this one:
/// a hung instance of the app holds a lock the overlay needs, and the applier must not count itself.
/// Best effort — an empty list means "none found or cannot tell", never "definitely none", which is
/// the same contract `IFileLockInspector.WhoHolds` states on the C# side.
std::vector<int> processes_using(const std::filesystem::path& root);

/// Ask a process to exit, then wait up to `timeout_ms`. Returns true if it is gone.
bool stop_process(int pid, int timeout_ms);

/// Start `exe` with `args`, detached, and DO NOT wait.
///
/// ⚠ §4: the launcher must have exited before the app's single-instance gate runs, or the new
/// instance bounces off the old one. So this launches and returns; the caller returns from main
/// immediately after.
bool start_detached(const std::filesystem::path& exe, const std::vector<std::string>& args);

/// Is a .NET runtime of at least `major` present? False also means "cannot tell" — the caller's job is
/// then to install, which is safe to do redundantly.
bool dotnet_runtime_present(int major);

}  // namespace shenora
