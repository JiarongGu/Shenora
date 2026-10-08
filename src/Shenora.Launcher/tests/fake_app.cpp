// The app a screen launcher starts in the conformance harness. Records its arguments, then, per SHENORA_FAKE_APP:
// "close" — checks the launcher's window is visible and closes it, as the kit's shells do; "exit" — exits at once (a
// second launch that handed itself over); "stay" — runs until the harness writes stop.txt (the timeout case);
// "destroy" (X11 only) — destroys the window outright, as a window manager or xdotool can, rather than asking;
// "kill" (X11 only) — kills the launcher's X connection (XKillClient), as a window manager's force-close does.
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <fstream>
#include <string>
#include <thread>
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <X11/Xlib.h>
#include <dlfcn.h>
#endif

int main(int argc, char** argv) {
    const std::string self = argv[0];
    const std::string dir = self.substr(0, self.find_last_of("/\\") + 1);
    std::string window;
    {
        std::ofstream args(dir + "args.txt");
        for (int i = 1; i < argc; ++i) {
            args << argv[i] << '\n';
            if (std::string(argv[i]) == "--startup-screen" && i + 1 < argc) window = argv[i + 1];
        }
    }
    const char* mode = std::getenv("SHENORA_FAKE_APP");
    const std::string m = mode ? mode : "exit";
    if (m == "stay") {   // until the harness writes stop.txt, so the case leaves no process behind
        for (int i = 0; i < 300 && !std::ifstream(dir + "stop.txt"); ++i) std::this_thread::sleep_for(std::chrono::milliseconds(100));
        return 0;
    }
    if ((m != "close" && m != "destroy" && m != "kill") || window.empty()) return 0;
    bool visible = false;
#ifdef _WIN32
    const HWND hwnd = reinterpret_cast<HWND>(static_cast<std::uintptr_t>(std::stoull(window)));
    visible = IsWindow(hwnd) && IsWindowVisible(hwnd);
    PostMessageW(hwnd, WM_CLOSE, 0, 0);
#else
    if (void* x = dlopen("libX11.so.6", RTLD_NOW)) {
        auto open = reinterpret_cast<Display* (*)(const char*)>(dlsym(x, "XOpenDisplay"));
        auto attrs = reinterpret_cast<Status (*)(Display*, Window, XWindowAttributes*)>(dlsym(x, "XGetWindowAttributes"));
        auto atom = reinterpret_cast<Atom (*)(Display*, const char*, Bool)>(dlsym(x, "XInternAtom"));
        auto send = reinterpret_cast<Status (*)(Display*, Window, Bool, long, XEvent*)>(dlsym(x, "XSendEvent"));
        auto flush = reinterpret_cast<int (*)(Display*)>(dlsym(x, "XFlush"));
        auto destroy = reinterpret_cast<int (*)(Display*, Window)>(dlsym(x, "XDestroyWindow"));
        auto killClient = reinterpret_cast<int (*)(Display*, XID)>(dlsym(x, "XKillClient"));
        if (Display* d = open ? open(nullptr) : nullptr) {
            const Window w = static_cast<Window>(std::stoull(window));
            XWindowAttributes a{};
            visible = attrs(d, w, &a) && a.map_state == IsViewable;
            if (m == "destroy" || m == "kill") {
                if (m == "kill") killClient(d, w);
                else destroy(d, w);
                flush(d);
                std::ofstream(dir + "seen.txt") << "visible=" << (visible ? 1 : 0) << '\n';
                std::this_thread::sleep_for(std::chrono::seconds(3));
                return 0;
            }
            XEvent ev{};
            ev.xclient.type = ClientMessage;
            ev.xclient.window = w;
            ev.xclient.message_type = atom(d, "WM_PROTOCOLS", False);
            ev.xclient.format = 32;
            ev.xclient.data.l[0] = static_cast<long>(atom(d, "WM_DELETE_WINDOW", False));
            send(d, w, False, NoEventMask, &ev);
            flush(d);
        }
    }
#endif
    std::ofstream(dir + "seen.txt") << "visible=" << (visible ? 1 : 0) << '\n';
    std::this_thread::sleep_for(std::chrono::seconds(3));
    return 0;
}
