#include "shenora/platform.hpp"

#ifdef _WIN32

#include <windows.h>
#include <psapi.h>
#include <tlhelp32.h>

#include <algorithm>

namespace fs = std::filesystem;

namespace shenora {
namespace {

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    int need = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring out(static_cast<std::size_t>(need), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), out.data(), need);
    return out;
}

/// Quote one argument for CreateProcessW's single command line, per the CRT's own parsing rules.
/// Hand-rolled because getting this wrong is how a path with a space silently becomes two arguments —
/// and an install root with a space is the common case, not the exotic one (`C:\Program Files\…`).
std::wstring quote_arg(const std::wstring& arg) {
    if (!arg.empty() && arg.find_first_of(L" \t\"") == std::wstring::npos) return arg;
    std::wstring out = L"\"";
    for (std::size_t i = 0; i < arg.size(); ++i) {
        std::size_t backslashes = 0;
        while (i < arg.size() && arg[i] == L'\\') { ++backslashes; ++i; }
        if (i == arg.size()) { out.append(backslashes * 2, L'\\'); break; }
        if (arg[i] == L'"') { out.append(backslashes * 2 + 1, L'\\'); }
        else { out.append(backslashes, L'\\'); }
        out.push_back(arg[i]);
    }
    out.push_back(L'"');
    return out;
}

}  // namespace

fs::path executable_path() {
    std::wstring buffer(MAX_PATH, L'\0');
    for (;;) {
        DWORD n = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (n == 0) return {};
        // ⚠ Not a length check: on truncation this returns the buffer SIZE, not the needed size, so
        // the only reliable signal is ERROR_INSUFFICIENT_BUFFER. A `n < size` test looks right and
        // silently truncates a long path.
        if (GetLastError() != ERROR_INSUFFICIENT_BUFFER) { buffer.resize(n); return fs::path(buffer); }
        buffer.resize(buffer.size() * 2);
    }
}

std::vector<int> processes_using(const fs::path& root) {
    std::vector<int> holders;
    const DWORD self = GetCurrentProcessId();
    const std::wstring prefix = fs::absolute(root).lexically_normal().wstring();

    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return holders;   // cannot tell — never "definitely none"

    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (Process32FirstW(snapshot, &entry)) {
        do {
            if (entry.th32ProcessID == self || entry.th32ProcessID == 0) continue;
            HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID);
            if (!process) continue;   // a process we cannot open is one we cannot stop either
            std::wstring image(MAX_PATH, L'\0');
            DWORD size = static_cast<DWORD>(image.size());
            if (QueryFullProcessImageNameW(process, 0, image.data(), &size)) {
                image.resize(size);
                // Case-insensitive prefix match: Windows paths are case-insensitive, and an app
                // launched via a differently-cased path is the same app.
                if (image.size() > prefix.size()
                    && _wcsnicmp(image.c_str(), prefix.c_str(), prefix.size()) == 0) {
                    holders.push_back(static_cast<int>(entry.th32ProcessID));
                }
            }
            CloseHandle(process);
        } while (Process32NextW(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return holders;
}

bool stop_process(int pid, int timeout_ms) {
    HANDLE process = OpenProcess(SYNCHRONIZE | PROCESS_TERMINATE, FALSE, static_cast<DWORD>(pid));
    if (!process) return true;   // already gone, or not ours to stop

    // Ask first: a WM_CLOSE to the app's windows lets it shut down cleanly and release its own locks.
    // Only then terminate — an app killed mid-write is exactly the corrupt install this whole
    // two-phase design exists to avoid.
    EnumWindows([](HWND window, LPARAM target) -> BOOL {
        DWORD owner = 0;
        GetWindowThreadProcessId(window, &owner);
        if (owner == static_cast<DWORD>(target)) PostMessageW(window, WM_CLOSE, 0, 0);
        return TRUE;
    }, static_cast<LPARAM>(pid));

    bool exited = WaitForSingleObject(process, static_cast<DWORD>(timeout_ms)) == WAIT_OBJECT_0;
    if (!exited) {
        TerminateProcess(process, 1);
        exited = WaitForSingleObject(process, 2000) == WAIT_OBJECT_0;
    }
    CloseHandle(process);
    return exited;
}

bool start_detached(const fs::path& exe, const std::vector<std::string>& args) {
    std::wstring command = quote_arg(exe.wstring());
    for (const auto& arg : args) { command.push_back(L' '); command += quote_arg(widen(arg)); }

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION info{};
    // DETACHED_PROCESS: the launcher is about to exit, and a child sharing its console would be
    // orphaned onto a console that is going away.
    const BOOL ok = CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE,
                                   DETACHED_PROCESS, nullptr,
                                   exe.parent_path().wstring().c_str(), &startup, &info);
    if (!ok) return false;
    CloseHandle(info.hThread);
    CloseHandle(info.hProcess);
    return true;
}

bool dotnet_runtime_present(int major) {
    // The registry is the cheap, offline answer and it is what both donors use. `dotnet --list-runtimes`
    // would be more precise and costs a process launch on every single start, which is the wrong trade
    // for a check whose false-negative merely triggers a (safe, idempotent) install prompt.
    HKEY key{};
    const wchar_t* path = L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64\\sharedfx\\Microsoft.NETCore.App";
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, path, 0, KEY_READ | KEY_WOW64_64KEY, &key) != ERROR_SUCCESS) {
        return false;
    }
    bool found = false;
    for (DWORD index = 0;; ++index) {
        wchar_t name[256];
        DWORD nameLen = static_cast<DWORD>(std::size(name));
        if (RegEnumValueW(key, index, name, &nameLen, nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS) break;
        int found_major = _wtoi(name);
        if (found_major >= major) { found = true; break; }
    }
    RegCloseKey(key);
    return found;
}

}  // namespace shenora

#endif  // _WIN32
