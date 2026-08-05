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

ApplyResult apply_pending_update(const ApplyOptions& options) {
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

    // ── 5. Overlay, excluding the manifest (written explicitly in step 6) ─────────────────────────
    fs::create_directories(appRoot, ec);
    for (const auto& entry : fs::recursive_directory_iterator(staged, ec)) {
        if (ec) break;
        if (!entry.is_regular_file()) continue;
        const fs::path relative = fs::relative(entry.path(), staged, ec);
        if (ec) continue;
        if (normalize_path(relative.generic_string()) == kManifest) continue;

        const fs::path target = appRoot / relative;
        fs::create_directories(target.parent_path(), ec);
        fs::copy_file(entry.path(), target, fs::copy_options::overwrite_existing, ec);
        if (ec) {
            result.failure = "could not write '" + relative.generic_string() + "': " + ec.message();
            log(result.failure);
            return result;
        }
        result.written.push_back(relative.generic_string());
    }

    // ── 6. The new baseline, written explicitly ───────────────────────────────────────────────────
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

    // ── 7. Removals: TRACKED paths only, never a directory sweep ──────────────────────────────────
    //
    // §4 and D30. User data lives in the same tree, so "delete what is not in the release" would
    // destroy it. The set is exactly "in the old manifest, not in the new one".
    {
        std::set<std::string> keep;
        for (const auto& f : release.files) keep.insert(normalize_path(f.path));
        for (const auto& f : installed.files) {
            const std::string key = normalize_path(f.path);
            if (keep.count(key) != 0) continue;
            if (key == kManifest) continue;
            const fs::path victim = appRoot / fs::path(f.path).make_preferred();
            if (!fs::exists(victim, ec)) continue;
            fs::remove(victim, ec);
            if (!ec) result.removed.push_back(f.path);
        }
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

}  // namespace shenora
