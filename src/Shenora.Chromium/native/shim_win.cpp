// The Chromium shell's native half on Windows (D82).
//
// Chromium's sandbox exists on Windows only through CEF's launcher since CEF 150: CEF's bootstrap.exe,
// renamed to the app's name, creates the sandbox and loads THIS DLL under the same name, calling
// RunWinMain (or RunConsoleMain for bootstrapc.exe). Every CEF subprocess re-enters it the same way.
//
//   <app>.exe       CEF's bootstrap.exe, renamed
//   <app>.dll       this shim
//   <app>.App.dll   the .NET app (its own name, because <app>.dll is taken)
//
// A publish with ShenoraChromiumPublishFolders keeps those three (and chrome_elf.dll, which CEF's launcher imports) at
// the root and moves CEF's runtime into engine\ and the .NET app into lib\. This shim reads either shape: libcef.dll is
// delay-loaded, and loaded here from engine\ when it is there.
//
// A subprocess (an argument starting --type=) runs CEF's own code and nothing else, so no renderer ever
// starts .NET. The browser process starts .NET through hostfxr and hands the app the sandbox and the
// instance as RUNTIME PROPERTIES (AppContext.GetData), which, unlike environment variables, no child
// process inherits.
//
// An app that asked for a startup screen (ShenoraChromiumStartupScreen, D103) has one drawn from the first moments of the
// browser process, before CEF's runtime is loaded and .NET has started, until the app's first window closes it. Its
// description and picture are RCDATA resources of the launcher exe, stamped there by the app's build.
#include <windows.h>
#include <shellapi.h>

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <exception>
#include <memory>
#include <string>
#include <thread>
#include <vector>

#include "include/capi/cef_app_capi.h"
#include "include/cef_api_hash.h"
#include "include/cef_sandbox_win.h"

#include <hostfxr.h>
#include <nethost.h>

#include "shenora/startup_screen.hpp"

namespace {

bool console_host = false;

// ── The startup screen ──────────────────────────────────────────────────────────────────────────────

constexpr wchar_t kScreenFlag[] = L"--startup-screen";

// What the screen's own thread and the browser process share. The thread outlives a call that gave up waiting on it.
struct ScreenState {
  HANDLE shown = CreateEventW(nullptr, TRUE, FALSE, nullptr);   // set once the screen is up, or is known not to be
  std::atomic<HWND> window{nullptr};
  std::atomic<bool> closed{false};   // asked to go before it was up, by a failure's message
  ScreenState() = default;
  ScreenState(const ScreenState&) = delete;
  ScreenState& operator=(const ScreenState&) = delete;
  ~ScreenState() { if (shown) CloseHandle(shown); }
};

std::shared_ptr<ScreenState> screen_state;

bool ReadResource(const wchar_t* name, std::vector<unsigned char>& out) {
  const HRSRC info = FindResourceW(GetModuleHandleW(nullptr), name, RT_RCDATA);
  if (!info) return false;
  const HGLOBAL loaded = LoadResource(GetModuleHandleW(nullptr), info);
  const DWORD size = SizeofResource(GetModuleHandleW(nullptr), info);
  const void* data = loaded ? LockResource(loaded) : nullptr;
  if (!data || size == 0) return false;
  out.assign(static_cast<const unsigned char*>(data), static_cast<const unsigned char*>(data) + size);
  return true;
}

// The description the build stamped: one `key=value` per line. A key it does not know is skipped, a value it cannot read
// stays the default, and no picture is no screen.
bool ReadDescription(shenora::StartupScreenDescription& d, std::vector<unsigned char>& png) {
  std::vector<unsigned char> text;
  if (!ReadResource(L"SHENORA_STARTUP_SCREEN", text) || !ReadResource(L"SHENORA_STARTUP_IMAGE", png)) return false;
  const std::string all(text.begin(), text.end());
  size_t at = 0;
  while (at < all.size()) {
    size_t end = all.find('\n', at);
    if (end == std::string::npos) end = all.size();
    std::string line = all.substr(at, end - at);
    at = end + 1;
    while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) line.pop_back();
    const size_t eq = line.find('=');
    if (eq == std::string::npos) continue;
    const std::string key = line.substr(0, eq), value = line.substr(eq + 1);
    const auto number = [&] { return static_cast<int>(strtol(value.c_str(), nullptr, 10)); };
    const auto color = [&] { return static_cast<std::uint32_t>(strtoul(value.c_str(), nullptr, 16)); };
    if (key == "width") d.width_dip = number();
    else if (key == "height") d.height_dip = number();
    else if (key == "background") d.background = color();
    else if (key == "progress_bar") d.progress_bar = value == "1";
    else if (key == "progress_color") d.progress_color = color();
    else if (key == "corners") d.corners = value == "square" ? shenora::ScreenCorners::Square : shenora::ScreenCorners::Rounded;
    else if (key == "monitor") d.monitor = value == "primary" ? shenora::ScreenMonitor::Primary : shenora::ScreenMonitor::Cursor;
    else if (key == "timeout_ms") d.timeout_ms = number();
  }
  return d.width_dip > 0 && d.height_dip > 0 && d.timeout_ms > 0;
}

bool HasScreenArgument(const std::vector<std::wstring>& args) {
  for (const auto& arg : args)
    if (_wcsicmp(arg.c_str(), kScreenFlag) == 0 || _wcsnicmp(arg.c_str(), L"--startup-screen=", 17) == 0) return true;
  return false;
}

std::vector<std::wstring> Arguments() {
  int argc = 0;
  LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
  std::vector<std::wstring> args;
  for (int i = 1; argv && i < argc; ++i) args.push_back(argv[i]);
  LocalFree(argv);
  return args;
}

// Shown on a thread of its own, which pumps it until the app closes it or its timeout passes: the browser process goes on
// to load CEF and start .NET meanwhile. Nothing here is fatal, and a screen that cannot be shown is none.
void StartScreen() {
  const auto args = Arguments();
  if (HasScreenArgument(args)) return;   // the update launcher's screen, already up
  auto d = std::make_shared<shenora::StartupScreenDescription>();
  auto png = std::make_shared<std::vector<unsigned char>>();
  if (!ReadDescription(*d, *png)) return;
  d->png = png->data();
  d->png_size = png->size();
  d->set_process_dpi = false;   // the app's own awareness is its own; this thread's is set below
  auto state = std::make_shared<ScreenState>();
  if (!state->shown) return;
  screen_state = state;
  try {
    std::thread([d, png, state] {
      using SetContext = DPI_AWARENESS_CONTEXT(WINAPI*)(DPI_AWARENESS_CONTEXT);
      if (auto set = reinterpret_cast<SetContext>(
              reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"user32"), "SetThreadDpiAwarenessContext"))))
        set(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
      // An exception out of a thread ends the process, and a screen is never worth that: a box too big to allocate is no screen.
      try {
        std::string error;
        auto screen = state->closed ? nullptr : shenora::StartupScreen::show(*d, error);
        if (screen) state->window = reinterpret_cast<HWND>(static_cast<uintptr_t>(strtoull(screen->window_id().c_str(), nullptr, 10)));
        else if (!error.empty()) OutputDebugStringA(("Shenora.Chromium: no startup screen: " + error + "\n").c_str());
        SetEvent(state->shown);
        if (screen) screen->pump_until([] { return false; }, d->timeout_ms);
      } catch (...) {
        state->window = nullptr;
        OutputDebugStringA("Shenora.Chromium: the startup screen failed\n");
        SetEvent(state->shown);
      }
    }).detach();
  } catch (const std::exception&) {
    screen_state.reset();
  }
}

// The screen's window as the app is to be told, waiting briefly for it to come up; 0 for none.
unsigned long long ScreenWindow() {
  if (!screen_state) return 0;
  if (WaitForSingleObject(screen_state->shown, 3000) != WAIT_OBJECT_0) return 0;
  return static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(screen_state->window.load()));
}

// Before a message the person must read, which a screen up would hide.
void CloseScreen() {
  if (!screen_state) return;
  screen_state->closed = true;
  if (const HWND window = screen_state->window.load()) PostMessageW(window, WM_CLOSE, 0, 0);
}

bool IsSubprocess() {
  int argc = 0;
  LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
  bool found = false;
  for (int i = 1; argv && i < argc && !found; ++i) found = wcsncmp(argv[i], L"--type=", 7) == 0;
  LocalFree(argv);
  return found;
}

// A GUI app has no console to print to, so a failure to start would otherwise be a silent exit.
void Report(const wchar_t* text) {
  CloseScreen();
  OutputDebugStringW(text);
  if (console_host) fwprintf(stderr, L"%s\n", text);
  else MessageBoxW(nullptr, text, L"Shenora.Chromium", MB_ICONERROR | MB_OK);
}

int Fail(const wchar_t* what, int code) {
  wchar_t text[512];
  // A self-contained publish carries its own runtime, found beside the app: reaching here from one means its folder lost it.
  swprintf_s(text, L"The app could not start .NET: %s (0x%08X). Install the .NET runtime it targets.", what,
             static_cast<unsigned>(code));
  Report(text);
  return code == 0 ? 1 : code;
}

// The exe's folder, without a trailing separator.
std::wstring ExeFolder() {
  wchar_t exe[MAX_PATH];
  const DWORD length = GetModuleFileNameW(nullptr, exe, MAX_PATH);
  const std::wstring path(exe, length);
  return path.substr(0, path.rfind(L'\\'));
}

bool Exists(const std::wstring& path) { return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES; }

// CEF's runtime: engine\ beside the exe in a publish laid out in folders, else the exe's own folder. Loaded here, before
// any CEF call, in every process (each subprocess re-enters this DLL), so a missing one is a message rather than a crash
// at the first delay-loaded call. engine\ also goes on the DLL search, for what Chromium loads after.
bool LoadEngine() {
  const std::wstring folder = ExeFolder();
  const std::wstring engine = folder + L"\\engine";
  // engine\ when its libcef.dll is there, or when none is beside the exe either: a folders publish missing its engine
  // names engine\ in the message, and a flat app that owns a folder called engine\ still loads from beside the exe.
  const bool folders = Exists(engine + L"\\libcef.dll") || (Exists(engine) && !Exists(folder + L"\\libcef.dll"));
  if (folders) SetDllDirectoryW(engine.c_str());
  const std::wstring libcef = (folders ? engine : folder) + L"\\libcef.dll";
  if (LoadLibraryW(libcef.c_str())) return true;
  const DWORD error = GetLastError();
  if (!IsSubprocess()) {
    wchar_t text[800];
    swprintf_s(text, L"The app could not load its Chromium engine from %s (error %lu). Reinstall the app.", libcef.c_str(),
               static_cast<unsigned long>(error));
    Report(text);
  }
  return false;
}

int RunSubprocess(HINSTANCE instance, void* sandbox_info) {
  cef_api_hash(CEF_API_VERSION, 0);  // the FIRST call in every process selects the API version
  cef_main_args_t args = {instance};
  return cef_execute_process(&args, nullptr, sandbox_info);
}

void SetPointer(hostfxr_set_runtime_property_value_fn set, hostfxr_handle host, const wchar_t* name, const void* value) {
  wchar_t text[32];
  swprintf_s(text, L"%llX", static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(value)));
  set(host, name, text);
}

int RunBrowser(HINSTANCE instance, void* sandbox_info) {
  wchar_t exe[MAX_PATH];
  const DWORD length = GetModuleFileNameW(nullptr, exe, MAX_PATH);
  if (length == 0 || length >= MAX_PATH) return Fail(L"the executable's path is too long", static_cast<int>(GetLastError()));
  // The .NET app: lib\<app>.App.dll in a publish laid out in folders, else beside the exe.
  const std::wstring path(exe, length);
  const size_t slash = path.rfind(L'\\');
  const std::wstring name = path.substr(slash + 1, path.rfind(L'.') - slash - 1);
  const std::wstring folder = path.substr(0, slash);
  std::wstring app = folder + L"\\lib\\" + name + L".App.dll";
  if (!Exists(app)) app = folder + L"\\" + name + L".App.dll";

  get_hostfxr_parameters params{sizeof(params), app.c_str(), nullptr};
  wchar_t fxr_path[MAX_PATH];
  size_t size = MAX_PATH;
  int rc = get_hostfxr_path(fxr_path, &size, &params);
  if (rc != 0) return Fail(L"no .NET runtime was found for the app", rc);

  HMODULE fxr = LoadLibraryW(fxr_path);
  if (!fxr) return Fail(L"hostfxr would not load", static_cast<int>(GetLastError()));
  auto init = reinterpret_cast<hostfxr_initialize_for_dotnet_command_line_fn>(GetProcAddress(fxr, "hostfxr_initialize_for_dotnet_command_line"));
  auto set_property = reinterpret_cast<hostfxr_set_runtime_property_value_fn>(GetProcAddress(fxr, "hostfxr_set_runtime_property_value"));
  auto run = reinterpret_cast<hostfxr_run_app_fn>(GetProcAddress(fxr, "hostfxr_run_app"));
  auto close = reinterpret_cast<hostfxr_close_fn>(GetProcAddress(fxr, "hostfxr_close"));
  if (!init || !set_property || !run || !close) return Fail(L"hostfxr is missing an export", 0);

  // hostfxr's command line is `<app.dll> [args...]`: the app path first, never the host exe. The app is told of the
  // startup screen drawn here, as the update launcher tells it of its own, so it closes it at its first window.
  const std::vector<std::wstring> own = Arguments();
  std::wstring screen_id;
  if (const unsigned long long window = ScreenWindow()) screen_id = std::to_wstring(window);
  std::vector<const wchar_t*> args{app.c_str()};
  for (const auto& arg : own) args.push_back(arg.c_str());
  if (!screen_id.empty()) {
    args.push_back(kScreenFlag);
    args.push_back(screen_id.c_str());
  }

  hostfxr_handle host = nullptr;
  rc = init(static_cast<int>(args.size()), args.data(), nullptr, &host);
  if (rc < 0 || !host) return Fail(L"hostfxr_initialize_for_dotnet_command_line failed", rc);
  SetPointer(set_property, host, L"Shenora.Chromium.SandboxInfo", sandbox_info);
  SetPointer(set_property, host, L"Shenora.Chromium.Instance", instance);

  rc = run(host);
  close(host);
  return rc;
}

int Run(HINSTANCE instance, void* sandbox_info) {
  const bool subprocess = IsSubprocess();
  if (!subprocess) StartScreen();   // first, before anything that takes time
  if (!LoadEngine()) return 1;
  return subprocess ? RunSubprocess(instance, sandbox_info) : RunBrowser(instance, sandbox_info);
}

}  // namespace

extern "C" __declspec(dllexport) int RunWinMain(HINSTANCE instance, LPWSTR, int, void* sandbox_info, cef_version_info_t*) {
  return Run(instance, sandbox_info);
}

extern "C" __declspec(dllexport) int RunConsoleMain(int, char*[], void* sandbox_info, cef_version_info_t*) {
  console_host = true;
  return Run(GetModuleHandleW(nullptr), sandbox_info);
}
