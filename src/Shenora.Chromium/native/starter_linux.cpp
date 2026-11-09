// The Chromium shell's Linux starter: the executable a Linux app's startup screen needs in front of it (D103).
//
// On Windows the shim is native code CEF's launcher runs before .NET, so it can draw the screen from the first moment. On Linux the
// browser process IS the .NET apphost, so nothing native of the kit's runs first; with ShenoraChromiumStartupScreen the build lays this
// out as <App>, with the description and the picture stamped on the end of the file, and the app's own apphost beside it as <App>.App
// (in lib/ with a publish into folders). It:
//   1. reads its stamp from the end of /proc/self/exe, and with none, or with --startup-screen already on the command line (the update
//      launcher's screen is the one up), becomes the app: exec, nothing else;
//   2. shows the screen (the update launcher's own X11 window and pixels, shenora::StartupScreen: one implementation, three hosts);
//   3. starts the app, passing --startup-screen <window id> as the update launcher does, so the app closes it at its first window;
//   4. keeps the screen moving until the app closes it, the timeout passes, or the app ends, and then waits for the app, forwards the
//      signals it is sent, and ends with the app's own status: a service manager's main process, and a terminal's job, stay this one.
// A screen that cannot be shown (no display, no X11) is none and never a failed start.
//
//   node devtools/dev.mjs cef-native --rid linux-x64      (on Linux) builds it with a C++ compiler and the X11 headers
#include <fcntl.h>
#include <limits.h>
#include <signal.h>
#include <sys/prctl.h>
#include <sys/wait.h>
#include <unistd.h>

#include <cerrno>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

#include "screen_description.hpp"

namespace {

// The stamp: [description][PNG][description size: u32le][PNG size: u32le][magic], the magic being the last 16 bytes of the file.
constexpr char kMagic[16] = {'S', 'H', 'E', 'N', 'O', 'R', 'A', 'S', 'C', 'R', 'E', 'E', 'N', '0', '0', '1'};
constexpr char kFlag[] = "--startup-screen";

volatile sig_atomic_t g_child = 0;

void forward(int signal) {
  if (g_child > 0) kill(static_cast<pid_t>(g_child), signal);
}

std::string self_path() {
  char exe[PATH_MAX];
  const ssize_t length = readlink("/proc/self/exe", exe, sizeof exe - 1);
  return length > 0 ? std::string(exe, static_cast<size_t>(length)) : std::string();
}

// The app's apphost: <App>.App beside this executable, else in lib/ (a publish into folders).
std::string app_of(const std::string& self) {
  const size_t slash = self.find_last_of('/');
  const std::string dir = slash == std::string::npos ? "." : self.substr(0, slash);
  const std::string name = slash == std::string::npos ? self : self.substr(slash + 1);
  for (const std::string& candidate : {dir + "/" + name + ".App", dir + "/lib/" + name + ".App"})
    if (access(candidate.c_str(), X_OK) == 0) return candidate;
  return std::string();
}

bool read_stamp(const std::string& self, std::string& text, std::vector<unsigned char>& png) {
  const int fd = open(self.c_str(), O_RDONLY | O_CLOEXEC);
  if (fd < 0) return false;
  bool ok = false;
  constexpr size_t tail_size = 8 + sizeof kMagic;   // two sizes and the magic
  unsigned char tail[tail_size];
  const off_t size = lseek(fd, 0, SEEK_END);
  if (size >= static_cast<off_t>(tail_size) && pread(fd, tail, tail_size, size - static_cast<off_t>(tail_size)) == static_cast<ssize_t>(tail_size)
      && memcmp(tail + 8, kMagic, sizeof kMagic) == 0) {
    const auto u32 = [&](const unsigned char* p) {
      return static_cast<std::uint32_t>(p[0]) | static_cast<std::uint32_t>(p[1]) << 8 | static_cast<std::uint32_t>(p[2]) << 16 | static_cast<std::uint32_t>(p[3]) << 24;
    };
    const std::uint64_t text_size = u32(tail), png_size = u32(tail + 4);
    if (text_size + png_size + tail_size <= static_cast<std::uint64_t>(size)) {
      const off_t begin = size - static_cast<off_t>(tail_size + png_size + text_size);
      text.resize(text_size);
      png.resize(png_size);
      ok = pread(fd, &text[0], text_size, begin) == static_cast<ssize_t>(text_size)
        && pread(fd, png.data(), png_size, begin + static_cast<off_t>(text_size)) == static_cast<ssize_t>(png_size);
    }
  }
  close(fd);
  return ok;
}

bool has_flag(int argc, char** argv) {
  for (int i = 1; i < argc; ++i)
    if (strcmp(argv[i], kFlag) == 0 || strncmp(argv[i], "--startup-screen=", 17) == 0) return true;
  return false;
}

int status_of(int status) {
  if (WIFEXITED(status)) return WEXITSTATUS(status);
  if (WIFSIGNALED(status)) return 128 + WTERMSIG(status);
  return 1;
}

}  // namespace

int main(int argc, char** argv) {
  const std::string self = self_path();
  const std::string app = app_of(self);
  if (app.empty()) {
    fprintf(stderr, "shenora starter: the app is not beside %s (<App>.App, or lib/<App>.App)\n", self.c_str());
    return 127;
  }

  std::unique_ptr<shenora::StartupScreen> screen;
  shenora::StartupScreenDescription description;
  std::vector<unsigned char> png;
  std::string text;
  if (!has_flag(argc, argv) && read_stamp(self, text, png) && shenora_chromium::parse_screen_description(text, description)) {
    description.png = png.data();
    description.png_size = png.size();
    std::string error;
    screen = shenora::StartupScreen::show(description, error);
    if (!screen && !error.empty()) fprintf(stderr, "shenora starter: no startup screen: %s\n", error.c_str());
  }

  std::vector<std::string> arguments{self};
  if (screen) {
    arguments.push_back(kFlag);
    arguments.push_back(screen->window_id());
  }
  for (int i = 1; i < argc; ++i) arguments.push_back(argv[i]);
  std::vector<char*> exec_argv;
  for (std::string& argument : arguments) exec_argv.push_back(&argument[0]);
  exec_argv.push_back(nullptr);

  // No screen: this process becomes the app.
  if (!screen) {
    execv(app.c_str(), exec_argv.data());
    fprintf(stderr, "shenora starter: %s: %s\n", app.c_str(), strerror(errno));
    return 127;
  }

  for (const int signal : {SIGINT, SIGTERM, SIGHUP, SIGQUIT}) {
    struct sigaction action;
    memset(&action, 0, sizeof action);
    action.sa_handler = forward;
    sigemptyset(&action.sa_mask);
    sigaction(signal, &action, nullptr);
  }
  const pid_t child = fork();
  if (child < 0) {
    fprintf(stderr, "shenora starter: fork: %s\n", strerror(errno));
    return 127;
  }
  if (child == 0) {
    prctl(PR_SET_PDEATHSIG, SIGTERM);   // the app does not outlive a starter that is killed
    execv(app.c_str(), exec_argv.data());
    fprintf(stderr, "shenora starter: %s: %s\n", app.c_str(), strerror(errno));
    _exit(127);
  }
  g_child = child;

  int status = 0;
  bool ended = false;
  const auto reap = [&](int flags) {
    pid_t result;
    do result = waitpid(child, &status, flags); while (result < 0 && errno == EINTR);
    if (result == child || result < 0) ended = true;
    return ended;
  };
  // The screen moves until the app closes it, the timeout passes, or the app ends.
  screen->pump_until([&] { return reap(WNOHANG); }, description.timeout_ms);
  screen.reset();
  if (!ended) reap(0);
  return status_of(status);
}
