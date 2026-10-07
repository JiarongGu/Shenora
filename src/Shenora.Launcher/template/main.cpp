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
//
// A STARTUP SCREEN is opt-in: `shenora_launcher_startup_screen(<your target> IMAGE ...)` in your CMake compiles one
// in, and this file shows it from the first moment, through an update apply, until the app's first window closes it
// (the kit's shells do, through IStartupScreen). Without the call there is none and nothing below changes.
// ───────────────────────────────────────────────────────────────────────────────────────────────────
#include "shenora/platform.hpp"
#include "shenora/startup_screen.hpp"
#include "shenora/updater.hpp"

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <memory>
#include <optional>
#include <string>
#include <thread>
#include <vector>

namespace fs = std::filesystem;

namespace {

// ── EDIT THESE ─────────────────────────────────────────────────────────────────────────────────────
constexpr const char* kAppTitle = "MyApp";              // the title of a failure's message box
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
        shenora::show_error(kAppTitle, "Could not determine the launcher's own path.");
        return 2;
    }
    const fs::path root = self.parent_path();

    bool applyAndExit = false;
    std::optional<fs::path> dumpTo;
    double dumpPhase = 0.0;
    std::vector<std::string> forwarded;
    // UTF-8, as start_detached expects: on Windows `argv` is in the ANSI code page.
    const std::vector<std::string> arguments = shenora::utf8_arguments(argc, argv);
    for (std::size_t i = 0; i < arguments.size(); ++i) {
        const std::string& arg = arguments[i];
        // The conformance harness drives the launcher with these. --apply-and-exit: apply, report, and do NOT start
        // the app — it is what lets a Node harness test a PREBUILT binary end to end with no compiler and no GUI, the
        // model the design doc's §5 takes from the sibling. --startup-screen-dump: the screen's frame, no window.
        if (arg == "--apply-and-exit") applyAndExit = true;
        else if (arg == "--startup-screen-dump" && i + 2 < arguments.size()) {
            dumpTo = shenora::from_utf8(arguments[i + 1]);
            dumpPhase = std::atof(arguments[i + 2].c_str());
            i += 2;
        }
        else forwarded.push_back(arg);
    }

    shenora::ApplyOptions options;
    options.root = root;
    options.app_subdir = kAppSubdir;
    // ⚠ BESIDE the launcher, never inside `.update/`. The stage directory is deleted at the end of a
    // successful apply, and a log open inside it stops that delete on Windows — which leaves the marker
    // behind and re-applies the same update on every start. The conformance harness caught exactly that.
    options.log_file = root / "launcher.log";
    const auto log = [&](const std::string& line) { std::ofstream(options.log_file, std::ios::app) << line << '\n'; };

#if defined(SHENORA_STARTUP_SCREEN)
    if (dumpTo) {
        const auto* d = shenora::embedded_startup_screen();
        shenora::ScreenImage image;
        shenora::decode_png(d->png, d->png_size, image);
        const bool rounded = d->corners == shenora::ScreenCorners::Rounded;
        auto frame = shenora::compose_screen(*d, &image, d->width_dip, d->height_dip, 1.0, rounded);
        shenora::draw_progress(frame, *d, 1.0, rounded, dumpPhase);
        std::ofstream out(*dumpTo, std::ios::binary);
        const std::int32_t size[2] = {frame.width, frame.height};
        out.write(reinterpret_cast<const char*>(size), sizeof(size));
        out.write(reinterpret_cast<const char*>(frame.bgra.data()), static_cast<std::streamsize>(frame.bgra.size() * 4));
        return 0;
    }
    // First, before anything that can take time: the screen a person sees within milliseconds of the click.
    std::unique_ptr<shenora::StartupScreen> screen;
    if (!applyAndExit) {
        std::string error;
        screen = shenora::StartupScreen::show(*shenora::embedded_startup_screen(), error);
        if (!screen) log("startup screen not shown: " + error);
    }
    const int screenTimeout = shenora::embedded_startup_screen()->timeout_ms;
#else
    (void)dumpTo;      // parsed either way; read only by a launcher with a screen (-Werror's unused-but-set)
    (void)dumpPhase;
    std::unique_ptr<shenora::StartupScreen> screen;
    const int screenTimeout = 0;
#endif

    // The apply on a worker, so a screen keeps moving through a long one.
    std::atomic<bool> applied{false};
    shenora::ApplyResult result;
    std::thread worker([&] {
        result = shenora::apply_pending_update(options);
        applied = true;
    });
    if (screen) screen->pump_until([&] { return applied.load(); }, -1);
    worker.join();
    if (result.attempted && !result.applied) {
        // Report and CONTINUE. A failed update must still start the app that is already installed —
        // refusing to launch turns "the update did not apply" into "the product is bricked", and the
        // stage is left in place so the next start can retry.
        log("update not applied: " + result.failure);
    }

    if (applyAndExit) {
        // A machine-readable line for the harness. Deliberately terse and stable.
        std::printf("applied=%d attempted=%d version=%s written=%zu removed=%zu\n",
                    result.applied ? 1 : 0, result.attempted ? 1 : 0,
                    result.version.c_str(), result.written.size(), result.removed.size());
        return result.attempted && !result.applied ? 1 : 0;
    }

    if (!shenora::dotnet_runtime_present(kRequiredDotnetMajor)) {
        screen.reset();
        shenora::show_error(kAppTitle, kRuntimeMissingMessage);
        return 3;
    }

    const fs::path app = root / kAppSubdir / kAppExecutable;
    // `--app-root` is the kit's own contract (`AppRootArgument` + `ShenoraPaths`), so the app never has
    // to guess where it was installed. to_utf8, never string(): on Windows that is the ANSI code page, which
    // threw for an install path outside it and mangled one inside it.
    std::vector<std::string> args{ "--app-root", shenora::to_utf8(root) };
    // The app closes the screen once its first window is on screen (IStartupScreen, in the kit's shells).
    const bool handedOver = screen && !screen->closed();
    if (handedOver) {
        args.push_back("--startup-screen");
        args.push_back(screen->window_id());
    }
    args.insert(args.end(), forwarded.begin(), forwarded.end());

    if (!screen) {
        if (!shenora::start_detached(app, args)) {
            shenora::show_error(kAppTitle, "Could not start " + shenora::to_utf8(app));
            return 4;
        }
        // Return at once. The launcher holds no single-instance lock (the retired update design's §4 is about the
        // OLD APP instance on a restart), so returning now is a choice, not a requirement.
        return 0;
    }

    shenora::StartedProcess process;
    if (!shenora::start_watched(app, args, process)) {
        screen.reset();
        shenora::show_error(kAppTitle, "Could not start " + shenora::to_utf8(app));
        return 4;
    }
    // Until the app closes it, the app exits (a second launch it handed over), or the timeout.
    screen->pump_until([&] { return shenora::has_exited(process); }, screenTimeout);
    log(handedOver && screen->closed() ? "startup screen closed by the app"
                                       : "startup screen ended: the app exited, or the timeout passed");
    shenora::release_process(process);
    return 0;
}
