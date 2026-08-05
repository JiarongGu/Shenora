#include "shenora/platform.hpp"

#ifndef _WIN32

#include <csignal>
#include <cstdlib>
#include <fstream>
#include <system_error>
#include <thread>

#include <unistd.h>
#include <sys/types.h>
#include <sys/wait.h>

namespace fs = std::filesystem;

namespace shenora {

fs::path executable_path() {
    std::error_code ec;
    // /proc/self/exe is Linux's answer and is exact — it survives a rename and a relative invocation,
    // neither of which argv[0] does.
    fs::path p = fs::read_symlink("/proc/self/exe", ec);
    return ec ? fs::path{} : p;
}

std::vector<int> processes_using(const fs::path& root) {
    std::vector<int> holders;
    const pid_t self = getpid();
    std::error_code ec;
    const std::string prefix = fs::absolute(root, ec).lexically_normal().string();
    if (ec) return holders;

    // Walk /proc rather than shelling out to lsof: no dependency, no PATH assumption, and it works in
    // the minimal container an update might run in. A process we cannot read is skipped, which keeps
    // the "empty means cannot tell" contract honest.
    for (const auto& entry : fs::directory_iterator("/proc", ec)) {
        if (ec) break;
        const std::string name = entry.path().filename().string();
        if (name.empty() || !std::all_of(name.begin(), name.end(), [](char c) { return c >= '0' && c <= '9'; }))
            continue;
        const int pid = std::atoi(name.c_str());
        if (pid == self) continue;

        std::error_code linkEc;
        const fs::path exe = fs::read_symlink(entry.path() / "exe", linkEc);
        if (linkEc) continue;
        const std::string image = exe.string();
        if (image.size() > prefix.size() && image.compare(0, prefix.size(), prefix) == 0) {
            holders.push_back(pid);
        }
    }
    return holders;
}

bool stop_process(int pid, int timeout_ms) {
    // SIGTERM first — the app gets to release its own locks. SIGKILL only after the wait, for the same
    // reason the Windows path posts WM_CLOSE before terminating.
    if (kill(pid, SIGTERM) != 0) return true;   // already gone, or not ours

    const int step = 50;
    for (int waited = 0; waited < timeout_ms; waited += step) {
        if (kill(pid, 0) != 0) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(step));
    }
    kill(pid, SIGKILL);
    for (int waited = 0; waited < 2000; waited += step) {
        if (kill(pid, 0) != 0) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(step));
    }
    return false;
}

bool start_detached(const fs::path& exe, const std::vector<std::string>& args) {
    const pid_t first = fork();
    if (first < 0) return false;
    if (first > 0) {
        // Reap the intermediate immediately. Double-fork so the app is re-parented to init and does
        // not become a zombie when this launcher exits moments later.
        int status = 0;
        waitpid(first, &status, 0);
        return true;
    }

    if (fork() != 0) _exit(0);   // intermediate child leaves at once

    setsid();
    std::vector<char*> argv;
    std::string exeStr = exe.string();
    argv.push_back(exeStr.data());
    std::vector<std::string> owned(args);
    for (auto& a : owned) argv.push_back(a.data());
    argv.push_back(nullptr);

    std::error_code ec;
    fs::current_path(exe.parent_path(), ec);
    execv(exeStr.c_str(), argv.data());
    _exit(127);   // only reached if execv failed
}

bool dotnet_runtime_present(int major) {
    // No registry here. The shared framework directory is the portable equivalent and is what the
    // installer lays down; checking for a versioned subdirectory is cheaper and more reliable than
    // parsing `dotnet --list-runtimes`, which needs `dotnet` on PATH in the first place.
    const char* roots[] = { "/usr/share/dotnet/shared/Microsoft.NETCore.App",
                            "/usr/lib/dotnet/shared/Microsoft.NETCore.App" };
    std::error_code ec;
    for (const char* root : roots) {
        for (const auto& entry : fs::directory_iterator(root, ec)) {
            if (ec) break;
            if (!entry.is_directory()) continue;
            if (std::atoi(entry.path().filename().string().c_str()) >= major) return true;
        }
    }
    if (const char* home = std::getenv("DOTNET_ROOT")) {
        for (const auto& entry : fs::directory_iterator(fs::path(home) / "shared" / "Microsoft.NETCore.App", ec)) {
            if (ec) break;
            if (entry.is_directory() && std::atoi(entry.path().filename().string().c_str()) >= major) return true;
        }
    }
    return false;
}

}  // namespace shenora

#endif  // !_WIN32
