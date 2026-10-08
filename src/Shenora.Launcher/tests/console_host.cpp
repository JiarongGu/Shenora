// The conformance harness's terminal (Windows only): runs a command as a shell runs a GUI program, in a fresh console
// screen buffer, and writes what the command printed THERE to a file. A pipe cannot show this: a GUI-subsystem
// launcher's printf reached a pipe and was lost in a console, so the harness passed while a person saw nothing.
// Usage: shenora-console-host <out-file> <exe> [args...]
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <fstream>
#include <string>

namespace {

std::wstring quoted(const std::wstring& arg) {
    if (!arg.empty() && arg.find_first_of(L" \t\"") == std::wstring::npos) return arg;   // as typed: cmd /c reads quotes
    std::wstring out = L"\"";
    for (const wchar_t c : arg) {
        if (c == L'"') out += L'\\';
        out += c;
    }
    return out + L"\"";
}

std::string utf8(const std::wstring& text) {
    const int n = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<std::size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), out.data(), n, nullptr, nullptr);
    return out;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc < 3) return 2;
    // A failure goes in the output file, so the harness's message names it rather than a bare exit code.
    const auto fail = [&](const char* what) {
        std::ofstream(argv[1], std::ios::binary) << "console-host: " << what << " (" << GetLastError() << ")\n";
        return 3;
    };
    const auto open = [](const wchar_t* name, SECURITY_ATTRIBUTES* sa) {
        return CreateFileW(name, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, sa, OPEN_EXISTING, 0,
                           nullptr);
    };
    // The harness starts this hidden and with no stdio, which gives it a windowless console of its own. With none at all
    // it makes one and hides it. Run from a terminal it borrows that one, and puts its buffer back after.
    HANDLE previous = open(L"CONOUT$", nullptr);
    if (previous == INVALID_HANDLE_VALUE) {
        if (!AllocConsole()) return fail("no console, and AllocConsole failed");
        if (const HWND window = GetConsoleWindow()) ShowWindow(window, SW_HIDE);
        previous = open(L"CONOUT$", nullptr);
        if (previous == INVALID_HANDLE_VALUE) return fail("no CONOUT$ after AllocConsole");
    }
    // A buffer of its own, made active: CONOUT$ opens the ACTIVE buffer, so what the command prints lands here and
    // nowhere else, and nothing already on the terminal can pass for it.
    SECURITY_ATTRIBUTES inherit{sizeof(inherit), nullptr, TRUE};
    const HANDLE buffer = CreateConsoleScreenBuffer(GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                                    &inherit, CONSOLE_TEXTMODE_BUFFER, nullptr);
    const HANDLE input = open(L"CONIN$", &inherit);
    if (buffer == INVALID_HANDLE_VALUE || input == INVALID_HANDLE_VALUE) return fail("no screen buffer or CONIN$");
    if (!SetConsoleActiveScreenBuffer(buffer)) return fail("SetConsoleActiveScreenBuffer failed");
    // An interactive shell's own handles are the console's, whatever started this host (a pipe, NUL).
    SetStdHandle(STD_INPUT_HANDLE, input);
    SetStdHandle(STD_OUTPUT_HANDLE, buffer);
    SetStdHandle(STD_ERROR_HANDLE, buffer);

    std::wstring command = quoted(argv[2]);
    for (int i = 3; i < argc; ++i) command += L" " + quoted(argv[i]);
    // Handles inherited, none passed explicitly: what cmd gives a program it starts without a redirect.
    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    int code = 4;
    if (CreateProcessW(nullptr, command.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr, &si, &pi)) {
        WaitForSingleObject(pi.hProcess, 30000);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        code = 0;
    }

    CONSOLE_SCREEN_BUFFER_INFO info{};
    GetConsoleScreenBufferInfo(buffer, &info);
    std::wstring text;
    for (SHORT y = 0; y <= info.dwCursorPosition.Y; ++y) {
        std::wstring line(static_cast<std::size_t>(info.dwSize.X), L' ');
        DWORD read = 0;
        ReadConsoleOutputCharacterW(buffer, line.data(), static_cast<DWORD>(line.size()), COORD{0, y}, &read);
        line.resize(read);
        line.erase(line.find_last_not_of(L' ') + 1);
        text += line + L"\n";
    }
    SetConsoleActiveScreenBuffer(previous);
    CloseHandle(buffer);
    CloseHandle(input);
    CloseHandle(previous);
    std::ofstream(argv[1], std::ios::binary) << utf8(text);
    return code;
}
