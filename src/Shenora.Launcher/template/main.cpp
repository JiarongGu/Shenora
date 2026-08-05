// ───────────────────────────────────────────────────────────────────────────────────────────────────
// COPY THIS FILE INTO YOUR APP. It is the TEMPLATE half of the launcher; everything it calls is the
// library half and needs no editing.
//
// The split is not a judgement call — the design doc's §0 measured it. Two sibling apps, no contact,
// both wrote `updater.cpp` + `dotnet_runtime.cpp` (generic, now this library) against a per-app
// `main.cpp` (this file, 76 and 142 lines). What is per-app is small and is all in one place below.
//
// What you still own after copying: the constants here, your icon and version resources, your code
// signature, and the wording of any failure UI. On Windows those are embedded in the binary, so they
// are a post-build step before signing; on Linux the same facts live in a `.desktop` file and never
// touch the binary. That asymmetry is a build step, not a source fork.
// ───────────────────────────────────────────────────────────────────────────────────────────────────
#include "shenora/platform.hpp"
#include "shenora/updater.hpp"

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace fs = std::filesystem;

namespace {

// ── EDIT THESE FOUR ────────────────────────────────────────────────────────────────────────────────
constexpr const char* kAppSubdir = "app";              // Sonora's topology (D50/§2) — keep unless you must
constexpr const char* kAppExecutable = "MyApp.exe";    // inside {root}/{kAppSubdir}/
constexpr int kRequiredDotnetMajor = 10;
constexpr const char* kRuntimeMissingMessage =
    "This application needs the .NET 10 desktop runtime.\n"
    "Install it from https://dotnet.microsoft.com/download and start the app again.";
// ───────────────────────────────────────────────────────────────────────────────────────────────────

}  // namespace

int main(int argc, char** argv) {
    // The launcher lives at {root}/ and the app at {root}/app/ — so the root is simply where we are.
    // Resolved from the RUNNING image, never from argv[0] or the working directory: a shortcut can set
    // any working directory it likes, and a launcher that guesses wrong updates the wrong tree.
    const fs::path self = shenora::executable_path();
    if (self.empty()) {
        std::fprintf(stderr, "could not determine the launcher's own path\n");
        return 2;
    }
    const fs::path root = self.parent_path();

    bool applyAndExit = false;
    std::vector<std::string> forwarded;
    for (int i = 1; i < argc; ++i) {
        // The conformance harness drives the launcher with this: apply, report, and do NOT start the
        // app. It is what lets a Node harness test a PREBUILT binary end to end with no compiler and
        // no GUI — the model the design doc's §5 takes from the sibling.
        if (std::strcmp(argv[i], "--apply-and-exit") == 0) applyAndExit = true;
        else forwarded.emplace_back(argv[i]);
    }

    shenora::ApplyOptions options;
    options.root = root;
    options.app_subdir = kAppSubdir;
    // ⚠ BESIDE the launcher, never inside `.update/`. The stage directory is deleted at the end of a
    // successful apply, and a log open inside it stops that delete on Windows — which leaves the marker
    // behind and re-applies the same update on every start. The conformance harness caught exactly that.
    options.log_file = root / "launcher.log";

    const shenora::ApplyResult result = shenora::apply_pending_update(options);
    if (result.attempted && !result.applied) {
        // Report and CONTINUE. A failed update must still start the app that is already installed —
        // refusing to launch turns "the update did not apply" into "the product is bricked", and the
        // stage is left in place so the next start can retry.
        std::fprintf(stderr, "update not applied: %s\n", result.failure.c_str());
    }

    if (applyAndExit) {
        // A machine-readable line for the harness. Deliberately terse and stable.
        std::printf("applied=%d attempted=%d version=%s written=%zu removed=%zu\n",
                    result.applied ? 1 : 0, result.attempted ? 1 : 0,
                    result.version.c_str(), result.written.size(), result.removed.size());
        return result.attempted && !result.applied ? 1 : 0;
    }

    if (!shenora::dotnet_runtime_present(kRequiredDotnetMajor)) {
        std::fprintf(stderr, "%s\n", kRuntimeMissingMessage);
        return 3;
    }

    const fs::path app = root / kAppSubdir / kAppExecutable;
    // `--app-root` is the kit's own contract (`AppRootArgument` + `ShenoraPaths`), so the app never has
    // to guess where it was installed.
    std::vector<std::string> args{ "--app-root", root.string() };
    args.insert(args.end(), forwarded.begin(), forwarded.end());

    if (!shenora::start_detached(app, args)) {
        std::fprintf(stderr, "could not start %s\n", app.string().c_str());
        return 4;
    }
    // Return IMMEDIATELY. §4: the launcher must be gone before the app's single-instance gate runs,
    // or the new instance bounces off this process still being alive.
    return 0;
}
