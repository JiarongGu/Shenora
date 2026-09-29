// The Chromium shell's macOS helper: the executable every CEF subprocess runs (renderer, GPU, network, plugin,
// alerts), laid out by the app's build as "<App> Helper.app" and its four variants in Contents/Frameworks.
//
//   node devtools/dev.mjs cef-native --rid osx-x64      (on a Mac) builds it with clang against no headers
//
// It is the Windows shim's twin, without .NET: a subprocess runs CEF alone, so it never starts the runtime.
// In order, as CEF's own helper does:
//   1. the sandbox, from the framework's libcef_sandbox.dylib, BEFORE the framework is loaded;
//   2. the framework, found relative to this executable (Helper.app/Contents/MacOS → Contents/Frameworks);
//   3. cef_api_hash, which selects the Stable API version and must be CEF's first call;
//   4. cef_execute_process, whose exit code is this process's.
// CEF's own types are declared here rather than included: the two used are fixed by its C API and tiny.
#include <dlfcn.h>
#include <libgen.h>
#include <limits.h>
#include <mach-o/dyld.h>
#include <stdio.h>
#include <string.h>

#ifndef CEF_API_VERSION
#error "CEF_API_VERSION is not set; build through `node devtools/dev.mjs cef-native`."
#endif

typedef struct { int argc; char** argv; } cef_main_args_t;
typedef void* (*sandbox_initialize_t)(int argc, char** argv);
typedef const char* (*api_hash_t)(int version, int entry);
typedef int (*execute_process_t)(const cef_main_args_t* args, void* application, void* windows_sandbox_info);

static const char kFramework[] = "../../../Chromium Embedded Framework.framework";

/** `<this executable's folder>/<relative>` into `out`; 0 when it does not fit. */
static int beside_executable(const char* relative, char* out, size_t size) {
  char exe[PATH_MAX];
  uint32_t length = sizeof exe;
  if (_NSGetExecutablePath(exe, &length) != 0) return 0;
  const char* dir = dirname(exe);
  return dir && snprintf(out, size, "%s/%s", dir, relative) < (int)size;
}

static void* open_library(const char* relative) {
  char path[PATH_MAX];
  if (!beside_executable(relative, path, sizeof path)) return NULL;
  void* handle = dlopen(path, RTLD_LAZY | RTLD_LOCAL | RTLD_FIRST);
  if (!handle) fprintf(stderr, "shenora helper: dlopen %s: %s\n", path, dlerror());
  return handle;
}

int main(int argc, char** argv) {
  char relative[PATH_MAX];

  // The sandbox holds for the process's whole life, so its context is never destroyed.
  snprintf(relative, sizeof relative, "%s/Libraries/libcef_sandbox.dylib", kFramework);
  void* sandbox = open_library(relative);
  sandbox_initialize_t sandbox_initialize = sandbox ? (sandbox_initialize_t)dlsym(sandbox, "cef_sandbox_initialize") : NULL;
  if (!sandbox_initialize || !sandbox_initialize(argc, argv)) {
    fprintf(stderr, "shenora helper: the sandbox did not initialize\n");
    return 1;
  }

  snprintf(relative, sizeof relative, "%s/Chromium Embedded Framework", kFramework);
  void* cef = open_library(relative);
  api_hash_t api_hash = cef ? (api_hash_t)dlsym(cef, "cef_api_hash") : NULL;
  execute_process_t execute_process = cef ? (execute_process_t)dlsym(cef, "cef_execute_process") : NULL;
  if (!api_hash || !execute_process) {
    fprintf(stderr, "shenora helper: the CEF framework is not where the app's layout puts it\n");
    return 1;
  }

  api_hash(CEF_API_VERSION, 0);
  cef_main_args_t args = { argc, argv };
  return execute_process(&args, NULL, NULL);
}
