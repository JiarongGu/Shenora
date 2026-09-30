// The Chromium shell's Linux helper: the executable every CEF subprocess runs (zygote, renderer, GPU, network,
// utility), laid out by the app's build as "<App>-helper" beside the app and libcef.so.
//
//   node devtools/dev.mjs cef-native --rid linux-x64      (on Linux) builds it with gcc against no headers
//
// The macOS helper's twin, and without .NET for a second reason here: Chromium's zygote FORKS the renderers, and
// forking a process that runs the .NET runtime is not safe. In order, as CEF's own helper does:
//   1. libcef.so, found beside this executable (/proc/self/exe);
//   2. cef_api_hash, which selects the Stable API version and must be CEF's first call;
//   3. cef_execute_process, whose exit code is this process's. Chromium sets up its own sandbox inside it, from the
//      zygote's namespaces or chrome-sandbox.
// CEF's own types are declared here rather than included: the two used are fixed by its C API and tiny.
#include <dlfcn.h>
#include <libgen.h>
#include <limits.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

#ifndef CEF_API_VERSION
#error "CEF_API_VERSION is not set; build through `node devtools/dev.mjs cef-native`."
#endif

typedef struct { int argc; char** argv; } cef_main_args_t;
typedef const char* (*api_hash_t)(int version, int entry);
typedef int (*execute_process_t)(const cef_main_args_t* args, void* application, void* windows_sandbox_info);

int main(int argc, char** argv) {
  char exe[PATH_MAX];
  ssize_t length = readlink("/proc/self/exe", exe, sizeof exe - 1);
  if (length <= 0) {
    fprintf(stderr, "shenora helper: /proc/self/exe cannot be read\n");
    return 1;
  }
  exe[length] = '\0';

  char library[PATH_MAX];
  if (snprintf(library, sizeof library, "%s/libcef.so", dirname(exe)) >= (int)sizeof library) return 1;
  void* cef = dlopen(library, RTLD_NOW | RTLD_LOCAL);
  if (!cef) {
    fprintf(stderr, "shenora helper: dlopen %s: %s\n", library, dlerror());
    return 1;
  }
  api_hash_t api_hash = (api_hash_t)dlsym(cef, "cef_api_hash");
  execute_process_t execute_process = (execute_process_t)dlsym(cef, "cef_execute_process");
  if (!api_hash || !execute_process) {
    fprintf(stderr, "shenora helper: %s is not a CEF build the kit's binding knows\n", library);
    return 1;
  }

  api_hash(CEF_API_VERSION, 0);
  cef_main_args_t args = { argc, argv };
  return execute_process(&args, NULL, NULL);
}
