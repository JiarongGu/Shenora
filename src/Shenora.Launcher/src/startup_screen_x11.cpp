// The startup screen off Windows: an X11 window, libX11 loaded with dlopen so the launcher has no link-time X11
// dependency — started with no display (a terminal, SSH) or on a box without X11, it runs, with no screen. The headers
// are needed to BUILD only (types and macros), and without them (SHENORA_HAVE_X11 unset) the screen reports it cannot
// show and the launch goes on. Wayland desktops are reached through XWayland.
#if !defined(_WIN32) && defined(SHENORA_HAVE_X11)
#include "shenora/startup_screen.hpp"

#include <X11/Xatom.h>
#include <X11/Xlib.h>
#include <X11/Xutil.h>
#include <X11/extensions/Xrandr.h>
#include <dlfcn.h>
#include <poll.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdlib>

namespace shenora {
namespace {

// The app's close (the kit's StartupScreen) checks WM_CLASS before it sends one: an X id this launcher frees is the
// next client's to reuse. The Windows window class has the same name.
constexpr char kClass[] = "ShenoraStartupScreen\0ShenoraStartupScreen";   // WM_CLASS: instance, then class

struct X11 {
    void* lib = nullptr;
    void* randr = nullptr;
#define SHENORA_X(name) decltype(&::name) name = nullptr;
    SHENORA_X(XOpenDisplay) SHENORA_X(XCloseDisplay) SHENORA_X(XDefaultScreen) SHENORA_X(XRootWindow)
    SHENORA_X(XMatchVisualInfo) SHENORA_X(XCreateColormap) SHENORA_X(XCreateWindow) SHENORA_X(XInternAtom)
    SHENORA_X(XChangeProperty) SHENORA_X(XSetWMProtocols) SHENORA_X(XMapRaised) SHENORA_X(XCreateGC) SHENORA_X(XFreeGC)
    SHENORA_X(XCreateImage) SHENORA_X(XPutImage) SHENORA_X(XFlush) SHENORA_X(XPending) SHENORA_X(XNextEvent)
    SHENORA_X(XConnectionNumber) SHENORA_X(XGetSelectionOwner) SHENORA_X(XDestroyWindow) SHENORA_X(XGetDefault)
    SHENORA_X(XQueryPointer) SHENORA_X(XDefaultVisual) SHENORA_X(XDefaultDepth) SHENORA_X(XDisplayWidth)
    SHENORA_X(XDisplayHeight) SHENORA_X(XSetWMNormalHints) SHENORA_X(XSetErrorHandler) SHENORA_X(XSetWMHints)
    SHENORA_X(XSetIOErrorHandler) SHENORA_X(XGetWindowProperty) SHENORA_X(XFree)
    SHENORA_X(XRRGetScreenResourcesCurrent) SHENORA_X(XRRGetCrtcInfo) SHENORA_X(XRRFreeCrtcInfo)
    SHENORA_X(XRRFreeScreenResources) SHENORA_X(XRRGetOutputPrimary) SHENORA_X(XRRGetOutputInfo)
    SHENORA_X(XRRFreeOutputInfo)
#undef SHENORA_X
    // libX11 1.7 and later; declared here, so the headers of an older one still build this.
    using IOErrorExitHandler = void (*)(Display*, void*);
    void (*XSetIOErrorExitHandler)(Display*, IOErrorExitHandler, void*) = nullptr;

    bool load() {
        lib = dlopen("libX11.so.6", RTLD_NOW | RTLD_LOCAL);
        if (!lib) return false;
#define SHENORA_X(name) \
    name = reinterpret_cast<decltype(name)>(dlsym(lib, #name)); \
    if (!name) return false;
        SHENORA_X(XOpenDisplay) SHENORA_X(XCloseDisplay) SHENORA_X(XDefaultScreen) SHENORA_X(XRootWindow)
        SHENORA_X(XMatchVisualInfo) SHENORA_X(XCreateColormap) SHENORA_X(XCreateWindow) SHENORA_X(XInternAtom)
        SHENORA_X(XChangeProperty) SHENORA_X(XSetWMProtocols) SHENORA_X(XMapRaised) SHENORA_X(XCreateGC) SHENORA_X(XFreeGC)
        SHENORA_X(XCreateImage) SHENORA_X(XPutImage) SHENORA_X(XFlush) SHENORA_X(XPending) SHENORA_X(XNextEvent)
        SHENORA_X(XConnectionNumber) SHENORA_X(XGetSelectionOwner) SHENORA_X(XDestroyWindow) SHENORA_X(XGetDefault)
        SHENORA_X(XQueryPointer) SHENORA_X(XDefaultVisual) SHENORA_X(XDefaultDepth) SHENORA_X(XDisplayWidth)
        SHENORA_X(XDisplayHeight) SHENORA_X(XSetWMNormalHints) SHENORA_X(XSetErrorHandler) SHENORA_X(XSetWMHints)
        SHENORA_X(XSetIOErrorHandler) SHENORA_X(XGetWindowProperty) SHENORA_X(XFree)
#undef SHENORA_X
        XSetIOErrorExitHandler = reinterpret_cast<decltype(XSetIOErrorExitHandler)>(dlsym(lib, "XSetIOErrorExitHandler"));
        // RandR is optional: without it the screen centres on the whole root window.
        if ((randr = dlopen("libXrandr.so.2", RTLD_NOW | RTLD_LOCAL))) {
#define SHENORA_R(name) name = reinterpret_cast<decltype(name)>(dlsym(randr, #name));
            SHENORA_R(XRRGetScreenResourcesCurrent) SHENORA_R(XRRGetCrtcInfo) SHENORA_R(XRRFreeCrtcInfo)
            SHENORA_R(XRRFreeScreenResources) SHENORA_R(XRRGetOutputPrimary) SHENORA_R(XRRGetOutputInfo)
            SHENORA_R(XRRFreeOutputInfo)
#undef SHENORA_R
        }
        return true;
    }
};

struct Rect { int x, y, w, h; };

class X11Screen;
X11Screen* g_shown = nullptr;   // the one screen this process shows, for the error handlers
int on_x_error(Display*, XErrorEvent* e);
int on_x_io_error(Display*);
void on_x_io_exit(Display*, void*);

class X11Screen final : public StartupScreen {
public:
    X11Screen(const StartupScreenDescription& d, ScreenImage image) : desc_(d), image_(std::move(image)) {}
    ~X11Screen() override {
        if (g_shown == this) g_shown = nullptr;
        if (display_ && !dead_) {   // a lost connection takes no more calls, XCloseDisplay included
            if (gc_) x_.XFreeGC(display_, gc_);
            if (window_ && !closed_) x_.XDestroyWindow(display_, window_);
            x_.XCloseDisplay(display_);
        }
    }

    bool open(std::string& error) {
        if (!x_.load()) { error = "libX11 is not available"; return false; }
        if (!(display_ = x_.XOpenDisplay(nullptr))) { error = "no X display"; return false; }
        // Before any request: Xlib's default handler EXITS the process on an error, so a window destroyed from outside
        // (a window manager, xdotool) ended the launch, before the app had started if it came during the apply. This
        // one ends the screen instead, and the launch goes on.
        g_shown = this;
        x_.XSetErrorHandler(&on_x_error);
        // A lost connection (the server gone, a window manager's force-close) is fatal too: Xlib exits the process.
        // libX11 1.7 lets that exit be replaced, and the screen ends instead; an older one still exits.
        x_.XSetIOErrorHandler(&on_x_io_error);
        if (x_.XSetIOErrorExitHandler) x_.XSetIOErrorExitHandler(display_, &on_x_io_exit, nullptr);
        const int screen = x_.XDefaultScreen(display_);
        const Window root = x_.XRootWindow(display_, screen);

        // A compositor honours alpha: rounded corners only then.
        const std::string cm = "_NET_WM_CM_S" + std::to_string(screen);
        XVisualInfo vi{};
        argb_ = x_.XGetSelectionOwner(display_, x_.XInternAtom(display_, cm.c_str(), False)) != None
                && x_.XMatchVisualInfo(display_, screen, 32, TrueColor, &vi);
        Visual* visual = argb_ ? vi.visual : x_.XDefaultVisual(display_, screen);
        const int depth = argb_ ? 32 : x_.XDefaultDepth(display_, screen);

        const char* dpi = x_.XGetDefault(display_, "Xft", "dpi");
        scale_ = dpi ? std::max(1.0, std::atof(dpi) / 96.0) : 1.0;
        w_ = static_cast<int>(std::lround(desc_.width_dip * scale_));
        h_ = static_cast<int>(std::lround(desc_.height_dip * scale_));
        const Rect m = work_area(root, monitor(root, screen));
        const int x = m.x + (m.w - w_) / 2, y = m.y + (m.h - h_) / 2;

        XSetWindowAttributes attrs{};
        attrs.colormap = x_.XCreateColormap(display_, root, visual, AllocNone);
        attrs.border_pixel = 0;
        attrs.background_pixel = 0;
        attrs.event_mask = ExposureMask | StructureNotifyMask;
        window_ = x_.XCreateWindow(display_, root, x, y, static_cast<unsigned>(w_), static_cast<unsigned>(h_), 0, depth,
                                   InputOutput, visual, CWColormap | CWBorderPixel | CWBackPixel | CWEventMask, &attrs);
        if (!window_) { error = "XCreateWindow failed"; return false; }

        const Atom type = x_.XInternAtom(display_, "_NET_WM_WINDOW_TYPE", False);
        const Atom splash = x_.XInternAtom(display_, "_NET_WM_WINDOW_TYPE_SPLASH", False);
        x_.XChangeProperty(display_, window_, type, XA_ATOM, 32, PropModeReplace,
                           reinterpret_cast<const unsigned char*>(&splash), 1);
        const long motif[5] = {2, 0, 0, 0, 0};   // MWM_HINTS_DECORATIONS: none
        const Atom mwm = x_.XInternAtom(display_, "_MOTIF_WM_HINTS", False);
        x_.XChangeProperty(display_, window_, mwm, mwm, 32, PropModeReplace, reinterpret_cast<const unsigned char*>(motif), 5);
        x_.XChangeProperty(display_, window_, XA_WM_CLASS, XA_STRING, 8, PropModeReplace,
                           reinterpret_cast<const unsigned char*>(kClass), static_cast<int>(sizeof(kClass)));
        XSizeHints hints{};
        hints.flags = USPosition | USSize | PMinSize | PMaxSize;
        hints.x = x;
        hints.y = y;
        hints.width = hints.min_width = hints.max_width = w_;
        hints.height = hints.min_height = hints.max_height = h_;
        x_.XSetWMNormalHints(display_, window_, &hints);
        // ICCCM's "No Input": it never takes the keyboard, as the Windows screen never activates.
        XWMHints wm{};
        wm.flags = InputHint;
        wm.input = False;
        x_.XSetWMHints(display_, window_, &wm);
        protocols_ = x_.XInternAtom(display_, "WM_PROTOCOLS", False);
        delete_ = x_.XInternAtom(display_, "WM_DELETE_WINDOW", False);
        x_.XSetWMProtocols(display_, window_, &delete_, 1);

        gc_ = x_.XCreateGC(display_, window_, 0, nullptr);
        visual_ = visual;
        depth_ = depth;
        rounded_ = argb_ && desc_.corners == ScreenCorners::Rounded;
        base_ = compose_screen(desc_, image_.width > 0 ? &image_ : nullptr, w_, h_, scale_, rounded_);
        frame_ = base_;
        bar_ = std::min(h_, progress_bar_rows(desc_, scale_));
        start_ = std::chrono::steady_clock::now();
        x_.XMapRaised(display_, window_);
        paint(true);
        x_.XFlush(display_);
        // Mapped is the window manager's to grant, after a round trip: wait for it (a little), so "shown" means on
        // screen before the update apply and the app's start begin, as ShowWindow does on Windows.
        wait_mapped(500);
        return true;
    }

    std::string window_id() const override { return std::to_string(static_cast<unsigned long long>(window_)); }

    void pump_until(const std::function<bool()>& done, int timeout_ms) override {
        const auto until = timeout_ms < 0 ? std::chrono::steady_clock::time_point::max()
                                          : std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
        pollfd fd{x_.XConnectionNumber(display_), POLLIN, 0};
        for (;;) {
            // closed_ first: after a lost connection XNextEvent on an empty queue dereferences null.
            while (!closed_ && x_.XPending(display_)) {
                XEvent ev;
                x_.XNextEvent(display_, &ev);
                if (ev.type == ClientMessage && ev.xclient.message_type == protocols_
                    && static_cast<Atom>(ev.xclient.data.l[0]) == delete_) {
                    x_.XDestroyWindow(display_, window_);   // the app's close, or the window manager's
                    x_.XFlush(display_);
                    closed_ = true;
                } else if (ev.type == DestroyNotify && ev.xdestroywindow.window == window_) {
                    lost();   // destroyed from outside: no dead id is passed on, nor destroyed again
                } else if (ev.type == Expose) {
                    paint(true);
                }
            }
            if (closed_ || done()) return;
            const auto now = std::chrono::steady_clock::now();
            if (now >= until) return;
            if (desc_.progress_bar) paint(false);
            const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(until - now).count();
            poll(&fd, 1, static_cast<int>(std::min<long long>(16, left)));
        }
    }

    bool closed() const override { return closed_; }

    Window window() const { return window_; }
    void lost() {
        closed_ = true;
        window_ = 0;
    }
    void connection_lost() {
        dead_ = true;
        lost();
    }

private:
    void wait_mapped(int timeout_ms) {
        const auto until = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
        pollfd fd{x_.XConnectionNumber(display_), POLLIN, 0};
        while (!closed_) {
            while (!closed_ && x_.XPending(display_)) {
                XEvent ev;
                x_.XNextEvent(display_, &ev);
                if (ev.type == MapNotify && ev.xmap.window == window_) {
                    paint(true);
                    return;
                }
                if (ev.type == DestroyNotify && ev.xdestroywindow.window == window_) lost();
                else if (ev.type == Expose) paint(true);
            }
            const auto now = std::chrono::steady_clock::now();
            if (now >= until) return;
            const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(until - now).count();
            poll(&fd, 1, static_cast<int>(std::min<long long>(16, left)));
        }
    }

    Rect monitor(Window root, int screen) {
        const Rect whole{0, 0, x_.XDisplayWidth(display_, screen), x_.XDisplayHeight(display_, screen)};
        if (!x_.XRRGetScreenResourcesCurrent || !x_.XRRGetCrtcInfo || !x_.XRRFreeCrtcInfo || !x_.XRRFreeScreenResources)
            return whole;
        XRRScreenResources* res = x_.XRRGetScreenResourcesCurrent(display_, root);
        if (!res) return whole;
        int px = 0, py = 0;
        if (desc_.monitor == ScreenMonitor::Cursor) {
            Window r = 0, c = 0;
            int wx = 0, wy = 0;
            unsigned mask = 0;
            x_.XQueryPointer(display_, root, &r, &c, &px, &py, &wx, &wy, &mask);
        }
        RRCrtc primary = 0;
        if (desc_.monitor == ScreenMonitor::Primary && x_.XRRGetOutputPrimary && x_.XRRGetOutputInfo && x_.XRRFreeOutputInfo) {
            const RROutput out = x_.XRRGetOutputPrimary(display_, root);
            if (XRROutputInfo* info = out ? x_.XRRGetOutputInfo(display_, res, out) : nullptr) {
                primary = info->crtc;
                x_.XRRFreeOutputInfo(info);
            }
        }
        // With no primary output, the monitor at the origin, where Windows' primary always is. A point on no monitor (a
        // layout that leaves the origin uncovered) takes the first one lit, never the whole root across them.
        Rect found = whole, first = whole;
        bool any = false, hit = false;
        for (int i = 0; i < res->ncrtc && !hit; ++i) {
            XRRCrtcInfo* c = x_.XRRGetCrtcInfo(display_, res, res->crtcs[i]);
            if (!c) continue;
            const Rect r{c->x, c->y, static_cast<int>(c->width), static_cast<int>(c->height)};
            const bool lit = r.w > 0 && r.h > 0;
            if (lit && !any) {
                first = r;
                any = true;
            }
            hit = lit && (primary ? res->crtcs[i] == primary : px >= r.x && px < r.x + r.w && py >= r.y && py < r.y + r.h);
            if (hit) found = r;
            x_.XRRFreeCrtcInfo(c);
        }
        x_.XRRFreeScreenResources(res);
        return hit ? found : first;
    }

    /// `m` less the panels: the window manager's `_NET_WORKAREA` for the current desktop, intersected with it. `m` as
    /// it is when there is none, or the two do not meet. EWMH gives one rectangle across all monitors, so where a window
    /// manager trims it by a panel on another monitor's edge, this one is trimmed too: a screen a little off centre.
    Rect work_area(Window root, const Rect& m) {
        const auto cardinals = [&](const char* name) {
            std::vector<long> out;
            const Atom atom = x_.XInternAtom(display_, name, True);
            Atom type = 0;
            int format = 0;
            unsigned long count = 0, after = 0;
            unsigned char* data = nullptr;
            if (atom != None
                && x_.XGetWindowProperty(display_, root, atom, 0, 1024, False, XA_CARDINAL, &type, &format, &count, &after,
                                         &data) == Success
                && data && type == XA_CARDINAL && format == 32) {
                const long* values = reinterpret_cast<const long*>(data);   // format 32 arrives as longs
                out.assign(values, values + count);
            }
            if (data) x_.XFree(data);
            return out;
        };
        const std::vector<long> area = cardinals("_NET_WORKAREA");
        if (area.size() < 4) return m;
        const std::vector<long> desktop = cardinals("_NET_CURRENT_DESKTOP");
        std::size_t i = 0;
        if (!desktop.empty() && desktop[0] >= 0 && static_cast<std::size_t>(desktop[0]) < area.size() / 4)
            i = static_cast<std::size_t>(desktop[0]) * 4;
        const long x0 = std::max<long>(m.x, area[i]), y0 = std::max<long>(m.y, area[i + 1]);
        const long x1 = std::min<long>(m.x + m.w, area[i] + area[i + 2]);
        const long y1 = std::min<long>(m.y + m.h, area[i + 1] + area[i + 3]);
        if (x1 <= x0 || y1 <= y0) return m;
        return {static_cast<int>(x0), static_cast<int>(y0), static_cast<int>(x1 - x0), static_cast<int>(y1 - y0)};
    }

    /// `full` puts the whole frame (shown, exposed); a tick puts only the bar's rows, the one band that moves.
    void paint(bool full) {
        if (closed_ || !window_) return;
        const int band = full ? h_ : bar_;
        if (band <= 0) return;
        const std::size_t from = static_cast<std::size_t>(h_ - band) * w_;
        std::copy(base_.bgra.begin() + static_cast<std::ptrdiff_t>(from), base_.bgra.end(),
                  frame_.bgra.begin() + static_cast<std::ptrdiff_t>(from));
        const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start_).count();
        draw_progress(frame_, desc_, scale_, rounded_, std::fmod(ms / 1500.0, 1.0));
        XImage* img = x_.XCreateImage(display_, visual_, static_cast<unsigned>(depth_), ZPixmap, 0,
                                      reinterpret_cast<char*>(frame_.bgra.data()), static_cast<unsigned>(w_),
                                      static_cast<unsigned>(h_), 32, 0);
        if (!img) return;
        // The frame is 32-bit little-endian 0xAARRGGBB: declared so, Xlib swaps for a big-endian server; a server whose
        // format for this depth is not 32 bits a pixel would misread it, so nothing is drawn there.
        img->byte_order = LSBFirst;
        if (img->bits_per_pixel != 32) {
            img->data = nullptr;
            XDestroyImage(img);
            return;
        }
        x_.XPutImage(display_, window_, gc_, img, 0, h_ - band, 0, h_ - band, static_cast<unsigned>(w_),
                     static_cast<unsigned>(band));
        img->data = nullptr;   // the frame's, not Xlib's to free
        XDestroyImage(img);    // a macro: calls img->f.destroy_image, no symbol needed
        x_.XFlush(display_);
    }

    X11 x_;
    StartupScreenDescription desc_;
    ScreenImage image_;
    ScreenPixels base_;
    ScreenPixels frame_;   // what is on screen: base_ with the bar over its bottom rows
    int bar_ = 0;
    Display* display_ = nullptr;
    Window window_ = 0;
    GC gc_ = nullptr;
    Visual* visual_ = nullptr;
    int depth_ = 24;
    Atom protocols_ = 0;
    Atom delete_ = 0;
    int w_ = 0;
    int h_ = 0;
    double scale_ = 1.0;
    bool argb_ = false;
    bool rounded_ = false;
    bool closed_ = false;
    bool dead_ = false;   // the connection is gone
    std::chrono::steady_clock::time_point start_;
};

int on_x_error(Display*, XErrorEvent* e) {
    if (g_shown && e && e->resourceid != 0 && e->resourceid == g_shown->window()) g_shown->lost();
    return 0;
}

// Returning lets libX11 1.7 call the exit handler below in place of exit(); an older one exits after it regardless.
int on_x_io_error(Display*) {
    if (g_shown) g_shown->connection_lost();
    return 0;
}

void on_x_io_exit(Display*, void*) {
    if (g_shown) g_shown->connection_lost();
}

}  // namespace

std::unique_ptr<StartupScreen> StartupScreen::show(const StartupScreenDescription& d, std::string& error) {
    ScreenImage image;
    std::string why;
    if (d.png && d.png_size && !decode_png(d.png, d.png_size, image, &why)) {
        error = "the startup screen's PNG would not decode: " + why;
        return nullptr;
    }
    auto screen = std::make_unique<X11Screen>(d, std::move(image));
    if (!screen->open(error)) return nullptr;
    return screen;
}

}  // namespace shenora

#elif !defined(_WIN32)
#include "shenora/startup_screen.hpp"

namespace shenora {

std::unique_ptr<StartupScreen> StartupScreen::show(const StartupScreenDescription&, std::string& error) {
    error = "this launcher was built without the X11 headers";
    return nullptr;
}

}  // namespace shenora
#endif
