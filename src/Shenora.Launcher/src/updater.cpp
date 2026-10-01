#include "shenora/updater.hpp"

#include "shenora/manifest.hpp"
#include "shenora/platform.hpp"

#include <fstream>
#include <set>
#include <sstream>
#include <system_error>

namespace fs = std::filesystem;

namespace shenora {
namespace {

// The names are the C# side's, and they are a contract rather than a convention: `UpdateStage` writes
// them and this reads them. Lower-case, because both sides compare through `normalize_path`.
constexpr const char* kStageDir = ".update";
constexpr const char* kStagedDir = "staged";
constexpr const char* kMarker = "ready.json";
constexpr const char* kManifest = "manifest.json";

std::string read_all(const fs::path& p, bool& ok) {
    std::ifstream in(p, std::ios::binary);
    if (!in) { ok = false; return {}; }
    std::ostringstream buffer;
    buffer << in.rdbuf();
    ok = true;
    return buffer.str();
}

class Log {
public:
    explicit Log(const fs::path& file) {
        if (file.empty()) return;
        std::error_code ec;
        fs::create_directories(file.parent_path(), ec);
        out_.open(file, std::ios::app);
    }
    void operator()(const std::string& line) {
        if (out_) out_ << "[shenora-launcher] " << line << '\n';
    }
private:
    std::ofstream out_;
};

}  // namespace

namespace {

// The body, split out so the public entry below can hold ONE exception boundary around all of it.
ApplyResult apply_update_body(const ApplyOptions& options) {
    ApplyResult result;
    Log log(options.log_file);

    const fs::path stageRoot = options.root / kStageDir;
    const fs::path staged = stageRoot / kStagedDir;
    const fs::path marker = stageRoot / kMarker;
    const fs::path appRoot = options.app_subdir == "." ? options.root : options.root / options.app_subdir;

    // ── 1. Is anything staged? ────────────────────────────────────────────────────────────────────
    std::error_code ec;
    if (!fs::exists(marker, ec)) return result;   // the ordinary path, and not a failure
    result.attempted = true;

    // ── 2. The release manifest, which removals are computed from ─────────────────────────────────
    bool ok = false;
    const std::string releaseJson = read_all(staged / kManifest, ok);
    Manifest release;
    if (!ok || !parse_manifest(releaseJson, release) || release.files.empty()) {
        // REFUSE. See the header: an unreadable or empty release manifest would drive a removal pass
        // that deletes every tracked path, including the files just overlaid. The C# CommitAsync now
        // refuses to publish a marker without this file for the same reason — but a launcher meets
        // stages written by older app versions, so it checks anyway rather than trusting the marker.
        result.failure = "the staged manifest is missing or empty, so removals cannot be computed safely";
        log(result.failure);
        return result;
    }
    result.version = release.version;

    // ── 3. The installed baseline. Absent is normal on a first apply ──────────────────────────────
    Manifest installed;
    const fs::path baseline = appRoot / kManifest;
    if (fs::exists(baseline, ec)) {
        bool baselineOk = false;
        const std::string baselineJson = read_all(baseline, baselineOk);
        if (!baselineOk || !parse_manifest(baselineJson, installed)) {
            // Overlay, remove NOTHING. Guessing at removals without a trustworthy baseline is the
            // destructive direction, and the C# side takes the same branch for the same reason.
            installed = Manifest{};
            log("the installed baseline is unreadable — applying without removals");
        }
    }

    // ── 4. Close what is holding the tree open (§4) ───────────────────────────────────────────────
    for (int pid : processes_using(appRoot)) {
        log("waiting for pid " + std::to_string(pid) + " to exit");
        if (!stop_process(pid, options.close_timeout_ms)) {
            result.failure = "a process is still holding files in the app directory (pid "
                             + std::to_string(pid) + ")";
            log(result.failure);
            return result;
        }
    }

    // ── 5. Overlay, excluding the manifest (written explicitly in step 7) ─────────────────────────
    //
    // Every file it replaces is copied aside first and put back if any write fails, and every file it adds is
    // removed: a failure part-way left a tree of two versions, and the caller then STARTED it. Now a failed
    // overlay leaves the installed version whole, and the stage stays for the next start to retry.
    const fs::path rollback = stageRoot / "rollback";
    fs::remove_all(rollback, ec);   // a previous run's, cut short
    std::vector<fs::path> replaced;
    std::vector<fs::path> added;
    const auto restore = [&]() {
        std::error_code undoEc;
        for (auto it = replaced.rbegin(); it != replaced.rend(); ++it) {
            fs::copy_file(rollback / *it, appRoot / *it, fs::copy_options::overwrite_existing, undoEc);
            if (undoEc) log("could not restore '" + to_utf8(*it, true) + "': " + undoEc.message());
        }
        for (const fs::path& relative : added) fs::remove(appRoot / relative, undoEc);
        fs::remove_all(rollback, undoEc);
    };
    fs::create_directories(appRoot, ec);
    for (const auto& entry : fs::recursive_directory_iterator(staged, ec)) {
        if (ec) break;
        if (!entry.is_regular_file()) continue;
        const fs::path relative = fs::relative(entry.path(), staged, ec);
        if (ec) continue;
        if (normalize_path(to_utf8(relative, true)) == kManifest) continue;

        const fs::path target = appRoot / relative;
        const bool existed = fs::exists(target, ec);
        if (existed) {
            fs::create_directories((rollback / relative).parent_path(), ec);
            fs::copy_file(target, rollback / relative, fs::copy_options::overwrite_existing, ec);
            if (ec) {
                result.failure = "could not keep a copy of '" + to_utf8(relative, true) + "': " + ec.message();
                log(result.failure);
                restore();
                return result;
            }
            replaced.push_back(relative);
        }
        fs::create_directories(target.parent_path(), ec);
        fs::copy_file(entry.path(), target, fs::copy_options::overwrite_existing, ec);
        if (ec) {
            result.failure = "could not write '" + to_utf8(relative, true) + "': " + ec.message();
            log(result.failure);
            if (!existed) added.push_back(relative);   // a failed copy can leave a partial file behind
            restore();
            return result;
        }
        if (!existed) added.push_back(relative);
        result.written.push_back(to_utf8(relative, true));
    }

    // ── 6. Removals: TRACKED paths only, never a directory sweep ──────────────────────────────────
    //
    // §4 and D30. User data lives in the same tree, so "delete what is not in the release" would
    // destroy it. The set is exactly "in the old manifest, not in the new one".
    //
    // BEFORE the new baseline: an apply cut short here keeps its marker, and the next start recomputes the same
    // removals from the old baseline. Written first, the baseline already matched the release, so the retry removed
    // nothing and the dropped files stayed for good.
    {
        std::set<std::string> keep;
        for (const auto& f : release.files) keep.insert(normalize_path(f.path));
        for (const auto& f : installed.files) {
            const std::string key = normalize_path(f.path);
            if (keep.count(key) != 0) continue;
            if (key == kManifest) continue;
            // from_utf8, and to_utf8 above: manifest paths are UTF-8, and on Windows a narrow path is read in the
            // ANSI code page, so a removal looked for another name and a name that code page cannot hold threw.
            const fs::path victim = appRoot / from_utf8(f.path).make_preferred();
            if (!fs::exists(victim, ec)) continue;
            fs::remove(victim, ec);
            if (!ec) result.removed.push_back(f.path);
        }
    }

    // ── 7. The new baseline, written explicitly ───────────────────────────────────────────────────
    {
        std::ofstream out(baseline, std::ios::binary | std::ios::trunc);
        if (!out) {
            result.failure = "could not write the installed manifest";
            log(result.failure);
            return result;
        }
        out << releaseJson;
        result.written.push_back(kManifest);
    }

    // ── 8. Clear the stage. Last, so a crash before here re-applies rather than losing the update ─
    //
    // ⚠ THE MARKER GOES FIRST, and separately. `remove_all` on the whole stage root can fail for
    // ordinary reasons — a file still mapped, an antivirus scan, a log this very process has open —
    // and the marker is the ONLY thing that makes a stage "pending". Deleting the tree as one call and
    // swallowing the error meant a failed clear left the marker behind, so the launcher re-applied the
    // same update on EVERY subsequent start, overwriting the running install each boot.
    //
    // Found by the conformance harness on its first run, from a cause worth remembering: the log file
    // was inside `.update/`, so this process held open a handle in the directory it was deleting. That
    // is fixed at the call site too (the log lives beside the launcher now), but the ordering here is
    // what makes the failure survivable rather than a boot loop.
    std::error_code markerEc;
    fs::remove(marker, markerEc);
    if (markerEc) {
        result.failure = "the update applied but the staging marker could not be removed, so it would "
                         "re-apply on the next start: " + markerEc.message();
        log(result.failure);
        return result;   // applied stays false: the caller must surface this, it is not a clean run
    }
    fs::remove_all(stageRoot, ec);
    if (ec) log("the staged files could not be fully removed (harmless — the marker is gone): " + ec.message());

    result.applied = true;
    log("applied version " + result.version + ": " + std::to_string(result.written.size())
        + " written, " + std::to_string(result.removed.size()) + " removed");
    return result;
}

}  // namespace

ApplyResult apply_pending_update(const ApplyOptions& options) {
    try {
        return apply_update_body(options);
    } catch (const std::exception& e) {
        // Nearly every operation in the body goes through an ec overload, but a range-for over a
        // directory iterator ADVANCES with the throwing operator++ — the non-throwing increment(ec)
        // is not reachable from range-for — so a tree changing mid-walk can still throw. Nothing
        // above main() catches: an escape here is std::terminate before the app ever starts, the
        // bricked-launch outcome this launcher exists to avoid, instead of the "update not applied"
        // it promises.
        ApplyResult result;
        result.attempted = true;
        result.failure = std::string("the update stopped on an exception: ") + e.what();
        Log log(options.log_file);
        log(result.failure);
        return result;
    }
}

}  // namespace shenora
