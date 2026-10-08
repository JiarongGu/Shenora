// The launcher's startup screen: a picture on screen within milliseconds of the click, held through an update apply
// and the app's start, until the app's first window closes it. Opt-in: compiled in by shenora_launcher_startup_screen().
#pragma once
#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace shenora {

enum class ScreenCorners { Rounded, Square };
enum class ScreenMonitor { Cursor, Primary };

struct StartupScreenDescription {
    int width_dip = 480;
    int height_dip = 300;
    std::uint32_t background = 0xFF202020;      // 0xAARRGGBB
    const unsigned char* png = nullptr;
    std::size_t png_size = 0;
    bool progress_bar = false;
    std::uint32_t progress_color = 0xFF3B82F6;  // 0xAARRGGBB
    ScreenCorners corners = ScreenCorners::Rounded;
    ScreenMonitor monitor = ScreenMonitor::Cursor;
    int timeout_ms = 20000;
};

/// Decoded, straight alpha, 0xAARRGGBB per pixel, top row first.
struct ScreenImage { int width = 0; int height = 0; std::vector<std::uint32_t> bgra; };

/// One frame, PREMULTIPLIED 0xAARRGGBB, top row first: what UpdateLayeredWindow and a 32-bit X visual take.
struct ScreenPixels { int width = 0; int height = 0; std::vector<std::uint32_t> bgra; };

/// The still part: the background (rounded corners when `rounded`), and the image fitted inside, aspect kept, centred.
ScreenPixels compose_screen(const StartupScreenDescription& d, const ScreenImage* image, int width_px, int height_px,
                            double scale, bool rounded);

/// The moving part: an indeterminate bar along the bottom at `phase` (0..1), drawn over `frame`. It touches only the
/// bottom `progress_bar_rows` rows, so a screen repaints those alone as it moves.
void draw_progress(ScreenPixels& frame, const StartupScreenDescription& d, double scale, bool rounded, double phase);

/// How many rows at the bottom the bar takes at `scale`; 0 with no bar.
int progress_bar_rows(const StartupScreenDescription& d, double scale);

/// PNG → straight 0xAARRGGBB. WIC on Windows, stb_image elsewhere. On failure, `reason` (if given) says why.
bool decode_png(const unsigned char* data, std::size_t size, ScreenImage& out, std::string* reason = nullptr);

class StartupScreen {
public:
    /// Shown, or null with `error` set (no display, no X11, a bad image): the launch goes on without one.
    static std::unique_ptr<StartupScreen> show(const StartupScreenDescription& d, std::string& error);
    virtual ~StartupScreen() = default;
    /// What the app is passed: an HWND or an X11 window id, in decimal.
    virtual std::string window_id() const = 0;
    /// Run the screen until `done()` is true, its window was closed, or `timeout_ms` passed (negative: no limit).
    virtual void pump_until(const std::function<bool()>& done, int timeout_ms) = 0;
    /// The window was closed (by the app, or the window manager).
    virtual bool closed() const = 0;
};

/// The description shenora_launcher_startup_screen() compiled into this executable.
const StartupScreenDescription* embedded_startup_screen();

}  // namespace shenora
