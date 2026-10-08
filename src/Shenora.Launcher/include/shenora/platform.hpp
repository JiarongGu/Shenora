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

/// Start `exe` with `args`, detached, and DO NOT wait. The caller may return from main at once.
///
/// The launcher holds no single-instance lock: the guard in the retired update design's §4 was about the OLD APP
/// instance on a restart, so a launcher that stays alive while the app starts (a startup screen) is safe.
bool start_detached(const std::filesystem::path& exe, const std::vector<std::string>& args);

/// A started app the launcher keeps watching (a startup screen waits for it).
struct StartedProcess { int pid = 0; void* handle = nullptr; };

/// As start_detached, but the launcher can watch the app: `has_exited`, then `release_process`.
bool start_watched(const std::filesystem::path& exe, const std::vector<std::string>& args, StartedProcess& out);

/// Has the app exited (and, on POSIX, been reaped)?
bool has_exited(StartedProcess& process);

/// Let go of the app without waiting for it.
void release_process(StartedProcess& process);

/// A message a person must read: a message box on Windows (a GUI launcher has no console), stderr elsewhere.
void show_error(const std::string& title, const std::string& message);

/// Send stdout to the terminal the launcher was started from, where it has none: a GUI-subsystem program run from a
/// console prints nowhere otherwise. Redirected output (a pipe, a file) is left where it was sent. Nothing off Windows.
void stdout_to_parent_console();

/// Is a .NET runtime of at least `major` present? False also means "cannot tell" — the caller's job is
/// then to install, which is safe to do redundantly.
bool dotnet_runtime_present(int major);

/// The program's arguments after its own name, as UTF-8 — the encoding `start_detached` and the manifest assume.
///
/// ⚠ Not `argv` itself on Windows: there it is in the ANSI code page, so a forwarded argument outside that code
/// page reached the app mangled. Pass a path on as `to_utf8(path)` for the same reason, never `path.string()`.
std::vector<std::string> utf8_arguments(int argc, char** argv);

/// A path as UTF-8, in C++17 and C++20 alike: `u8string()` is a `std::u8string` from C++20 on, so a project that
/// copies the template into a C++20 build would not compile it.
inline std::string to_utf8(const std::filesystem::path& path, bool generic = false) {
#if defined(__cpp_char8_t)
    const auto text = generic ? path.generic_u8string() : path.u8string();
    return std::string(text.begin(), text.end());
#else
    return generic ? path.generic_u8string() : path.u8string();
#endif
}

/// A UTF-8 string as a path: `u8path` is deprecated from C++20 on, an error under warnings-as-errors.
inline std::filesystem::path from_utf8(const std::string& text) {
#if defined(__cpp_char8_t)
    return std::filesystem::path(std::u8string(text.begin(), text.end()));
#else
    return std::filesystem::u8path(text);
#endif
}

}  // namespace shenora
