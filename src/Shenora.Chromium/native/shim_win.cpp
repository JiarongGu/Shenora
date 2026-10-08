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
#include <windows.h>
#include <shellapi.h>

#include <cstdio>
#include <string>
#include <vector>

#include "include/capi/cef_app_capi.h"
#include "include/cef_api_hash.h"
#include "include/cef_sandbox_win.h"

#include <hostfxr.h>
#include <nethost.h>

namespace {

bool console_host = false;

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
  OutputDebugStringW(text);
  if (console_host) fwprintf(stderr, L"%s\n", text);
  else MessageBoxW(nullptr, text, L"Shenora.Chromium", MB_ICONERROR | MB_OK);
}

int Fail(const wchar_t* what, int code) {
  wchar_t text[512];
  swprintf_s(text, L"The app could not start .NET: %s (0x%08X). Install the .NET runtime it targets, or publish it self-contained.",
             what, static_cast<unsigned>(code));
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

  // hostfxr's command line is `<app.dll> [args...]`: the app path first, never the host exe.
  int argc = 0;
  LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
  std::vector<const wchar_t*> args{app.c_str()};
  for (int i = 1; argv && i < argc; ++i) args.push_back(argv[i]);

  hostfxr_handle host = nullptr;
  rc = init(static_cast<int>(args.size()), args.data(), nullptr, &host);
  if (rc < 0 || !host) {
    LocalFree(argv);
    return Fail(L"hostfxr_initialize_for_dotnet_command_line failed", rc);
  }
  SetPointer(set_property, host, L"Shenora.Chromium.SandboxInfo", sandbox_info);
  SetPointer(set_property, host, L"Shenora.Chromium.Instance", instance);

  rc = run(host);
  close(host);
  LocalFree(argv);
  return rc;
}

int Run(HINSTANCE instance, void* sandbox_info) {
  if (!LoadEngine()) return 1;
  return IsSubprocess() ? RunSubprocess(instance, sandbox_info) : RunBrowser(instance, sandbox_info);
}

}  // namespace

extern "C" __declspec(dllexport) int RunWinMain(HINSTANCE instance, LPWSTR, int, void* sandbox_info, cef_version_info_t*) {
  return Run(instance, sandbox_info);
}

extern "C" __declspec(dllexport) int RunConsoleMain(int, char*[], void* sandbox_info, cef_version_info_t*) {
  console_host = true;
  return Run(GetModuleHandleW(nullptr), sandbox_info);
}
