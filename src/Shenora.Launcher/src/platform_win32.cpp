#include "shenora/platform.hpp"

#ifdef _WIN32

#include <windows.h>
#include <psapi.h>
#include <shellapi.h>
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
    std::wstring prefix = fs::absolute(root).lexically_normal().wstring();
    // Ending in a separator, so `…\app` does not also claim `…\app-old\…`: a bare prefix closed, then killed, a
    // process that only shared the start of the name.
    while (!prefix.empty() && (prefix.back() == L'\\' || prefix.back() == L'/')) prefix.pop_back();
    prefix.push_back(L'\\');

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

namespace {

bool create(const fs::path& exe, const std::vector<std::string>& args, PROCESS_INFORMATION& info) {
    std::wstring command = quote_arg(exe.wstring());
    for (const auto& arg : args) { command.push_back(L' '); command += quote_arg(widen(arg)); }

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    // The launcher holds the foreground (the person just started it); the app's first window may take it.
    AllowSetForegroundWindow(ASFW_ANY);
    // DETACHED_PROCESS: a child sharing the launcher's console would be orphaned onto a console that is going away.
    return CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, DETACHED_PROCESS, nullptr,
                          exe.parent_path().wstring().c_str(), &startup, &info) != FALSE;
}

}  // namespace

bool start_detached(const fs::path& exe, const std::vector<std::string>& args) {
    PROCESS_INFORMATION info{};
    if (!create(exe, args, info)) return false;
    CloseHandle(info.hThread);
    CloseHandle(info.hProcess);
    return true;
}

bool start_watched(const fs::path& exe, const std::vector<std::string>& args, StartedProcess& out) {
    PROCESS_INFORMATION info{};
    if (!create(exe, args, info)) return false;
    CloseHandle(info.hThread);
    out.pid = static_cast<int>(info.dwProcessId);
    out.handle = info.hProcess;
    return true;
}

bool has_exited(StartedProcess& process) {
    return !process.handle || WaitForSingleObject(static_cast<HANDLE>(process.handle), 0) == WAIT_OBJECT_0;
}

void release_process(StartedProcess& process) {
    if (process.handle) CloseHandle(static_cast<HANDLE>(process.handle));
    process.handle = nullptr;
}

void show_error(const std::string& title, const std::string& message) {
    MessageBoxW(nullptr, widen(message).c_str(), widen(title).c_str(), MB_OK | MB_ICONERROR);
}

namespace {

/// A version named under the installer's `sharedfx` key, in one registry view.
bool registered_runtime(REGSAM view, int major) {
    HKEY key{};
    const wchar_t* path = L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64\\sharedfx\\Microsoft.NETCore.App";
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, path, 0, KEY_READ | view, &key) != ERROR_SUCCESS) return false;
    bool found = false;
    for (DWORD index = 0;; ++index) {
        wchar_t name[256];
        DWORD nameLen = static_cast<DWORD>(std::size(name));
        if (RegEnumValueW(key, index, name, &nameLen, nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS) break;
        if (_wtoi(name) >= major) { found = true; break; }
    }
    RegCloseKey(key);
    return found;
}

std::wstring environment(const wchar_t* name) {
    const DWORD need = GetEnvironmentVariableW(name, nullptr, 0);
    if (need == 0) return {};
    std::wstring value(need, L'\0');
    value.resize(GetEnvironmentVariableW(name, value.data(), need));
    return value;
}

/// A `shared/Microsoft.NETCore.App/<version>` folder of at least `major` under `root`.
bool runtime_folder(const fs::path& root, int major) {
    std::error_code ec;
    for (const auto& entry : fs::directory_iterator(root / L"shared" / L"Microsoft.NETCore.App", ec)) {
        if (ec) break;
        if (entry.is_directory(ec) && _wtoi(entry.path().filename().c_str()) >= major) return true;
    }
    return false;
}

}  // namespace

bool dotnet_runtime_present(int major) {
    // The registry is the cheap, offline answer and it is what both donors use. `dotnet --list-runtimes`
    // would be more precise and costs a process launch on every single start, which is the wrong trade
    // for a check whose false-negative merely triggers a (safe, idempotent) install prompt.
    // ⚠ The 32-bit VIEW first: the runtime installer writes InstalledVersions there even on x64 (where hostfxr reads
    // it), and the 64-bit view alone answered "missing" on a machine with .NET 10 installed, so the app never started.
    if (registered_runtime(KEY_WOW64_32KEY, major) || registered_runtime(KEY_WOW64_64KEY, major)) return true;
    // Then the folders, for an install that left no registry trace: DOTNET_ROOT, then the default location.
    const std::wstring dotnetRoot = environment(L"DOTNET_ROOT");
    if (!dotnetRoot.empty() && runtime_folder(dotnetRoot, major)) return true;
    const std::wstring programFiles = environment(L"ProgramFiles");
    return !programFiles.empty() && runtime_folder(fs::path(programFiles) / L"dotnet", major);
}

std::vector<std::string> utf8_arguments(int, char**) {
    std::vector<std::string> out;
    int count = 0;
    LPWSTR* wide = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!wide) return out;
    for (int i = 1; i < count; ++i) {
        const int length = static_cast<int>(wcslen(wide[i]));
        const int need = WideCharToMultiByte(CP_UTF8, 0, wide[i], length, nullptr, 0, nullptr, nullptr);
        std::string arg(static_cast<std::size_t>(need), '\0');
        WideCharToMultiByte(CP_UTF8, 0, wide[i], length, arg.data(), need, nullptr, nullptr);
        out.push_back(std::move(arg));
    }
    LocalFree(wide);
    return out;
}

}  // namespace shenora

#endif  // _WIN32
