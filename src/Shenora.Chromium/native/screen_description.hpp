// The startup screen's description (D103), as the app's build stamps it: one `key=value` per line, which the Windows shim reads from
// a resource of the launcher and the Linux starter from the end of its own file. One reader, so the two cannot disagree on a key.
#pragma once
#include <cstdint>
#include <cstdlib>
#include <string>

#include "shenora/startup_screen.hpp"

namespace shenora_chromium {

// A key it does not know is skipped, a value it cannot read stays the default, and a screen with no size, or no time, is none.
inline bool parse_screen_description(const std::string& all, shenora::StartupScreenDescription& d) {
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

}  // namespace shenora_chromium
