// The startup screen's pixels — portable: no OS calls, so both platforms draw the same frame.
#include "shenora/startup_screen.hpp"

#include <algorithm>
#include <cmath>

namespace shenora {
namespace {

constexpr double kCornerRadiusDip = 8.0;   // Windows 11's window radius
constexpr double kBarHeightDip = 3.0;
constexpr double kBarSpan = 0.3;           // the moving segment, as a share of the width

std::uint32_t premultiply(std::uint32_t argb, double coverage) {
    const double a = ((argb >> 24) & 0xFF) / 255.0 * coverage;
    const auto ch = [&](int shift) { return static_cast<std::uint32_t>(std::lround(((argb >> shift) & 0xFF) * a)); };
    return (static_cast<std::uint32_t>(std::lround(a * 255.0)) << 24) | (ch(16) << 16) | (ch(8) << 8) | ch(0);
}

/// Premultiplied source over premultiplied destination.
std::uint32_t over(std::uint32_t src, std::uint32_t dst) {
    const std::uint32_t inv = 255 - (src >> 24);
    const auto mix = [&](int shift) {
        const std::uint32_t s = (src >> shift) & 0xFF, d = (dst >> shift) & 0xFF;
        return std::min<std::uint32_t>(255, s + (d * inv + 127) / 255);
    };
    return (mix(24) << 24) | (mix(16) << 16) | (mix(8) << 8) | mix(0);
}

/// 1 inside the rounded box, 0 outside, antialiased across the corners' arcs.
double coverage(int x, int y, int w, int h, double r) {
    r = std::min(r, std::min(w, h) / 2.0);   // a box narrower than two radii is a pill, not undefined clamping
    if (r <= 0.0) return 1.0;
    const double px = x + 0.5, py = y + 0.5;
    const double cx = std::clamp(px, r, w - r), cy = std::clamp(py, r, h - r);
    const double dx = px - cx, dy = py - cy;
    if (dx == 0.0 && dy == 0.0) return 1.0;
    return std::clamp(r - std::sqrt(dx * dx + dy * dy) + 0.5, 0.0, 1.0);
}

/// The image's colour over a destination pixel's footprint [sx0,sx1)×[sy0,sy1): an area average when shrinking,
/// bilinear when growing. Premultiplied out.
std::uint32_t sample(const ScreenImage& img, double sx0, double sy0, double sx1, double sy1) {
    const auto at = [&](int x, int y) {
        return img.bgra[static_cast<std::size_t>(std::clamp(y, 0, img.height - 1)) * img.width + std::clamp(x, 0, img.width - 1)];
    };
    double acc[4] = {0, 0, 0, 0}, n = 0;
    const auto add = [&](std::uint32_t p, double weight) {
        const double a = ((p >> 24) & 0xFF) / 255.0;   // premultiply while averaging, or edges darken
        acc[0] += ((p >> 24) & 0xFF) * weight;
        acc[1] += ((p >> 16) & 0xFF) * a * weight;
        acc[2] += ((p >> 8) & 0xFF) * a * weight;
        acc[3] += (p & 0xFF) * a * weight;
        n += weight;
    };
    if (sx1 - sx0 <= 1.0 && sy1 - sy0 <= 1.0) {
        const double fx = (sx0 + sx1) / 2 - 0.5, fy = (sy0 + sy1) / 2 - 0.5;
        const int x = static_cast<int>(std::floor(fx)), y = static_cast<int>(std::floor(fy));
        const double tx = fx - x, ty = fy - y;
        add(at(x, y), (1 - tx) * (1 - ty));
        add(at(x + 1, y), tx * (1 - ty));
        add(at(x, y + 1), (1 - tx) * ty);
        add(at(x + 1, y + 1), tx * ty);
    } else {
        const int x0 = static_cast<int>(std::floor(sx0)), x1 = static_cast<int>(std::ceil(sx1));
        const int y0 = static_cast<int>(std::floor(sy0)), y1 = static_cast<int>(std::ceil(sy1));
        for (int y = y0; y < y1; ++y)
            for (int x = x0; x < x1; ++x) add(at(x, y), 1.0);
    }
    if (n <= 0) return 0;
    const auto c = [&](int i) { return static_cast<std::uint32_t>(std::clamp(std::lround(acc[i] / n), 0L, 255L)); };
    return (c(0) << 24) | (c(1) << 16) | (c(2) << 8) | c(3);
}

}  // namespace

ScreenPixels compose_screen(const StartupScreenDescription& d, const ScreenImage* image, int w, int h, double scale,
                            bool rounded) {
    ScreenPixels out{w, h, std::vector<std::uint32_t>(static_cast<std::size_t>(w) * h)};
    const double r = rounded ? kCornerRadiusDip * scale : 0.0;

    // The image's rectangle: fitted, aspect kept, centred.
    double ix = 0, iy = 0, iw = 0, ih = 0;
    if (image && image->width > 0 && image->height > 0) {
        const double fit = std::min(static_cast<double>(w) / image->width, static_cast<double>(h) / image->height);
        iw = image->width * fit;
        ih = image->height * fit;
        ix = (w - iw) / 2;
        iy = (h - ih) / 2;
    }
    const std::uint32_t background = premultiply(d.background, 1.0);

    for (int y = 0; y < h; ++y) {
        for (int x = 0; x < w; ++x) {
            std::uint32_t px = background;
            if (iw > 0 && x + 0.5 >= ix && x + 0.5 < ix + iw && y + 0.5 >= iy && y + 0.5 < iy + ih) {
                const double sx = image->width / iw, sy = image->height / ih;
                px = over(sample(*image, (x - ix) * sx, (y - iy) * sy, (x + 1 - ix) * sx, (y + 1 - iy) * sy), px);
            }
            // The corner's coverage scales the whole premultiplied pixel.
            const double cov = coverage(x, y, w, h, r);
            if (cov < 1.0) {
                const auto scaled = [&](int shift) { return static_cast<std::uint32_t>(std::lround(((px >> shift) & 0xFF) * cov)); };
                px = (scaled(24) << 24) | (scaled(16) << 16) | (scaled(8) << 8) | scaled(0);
            }
            out.bgra[static_cast<std::size_t>(y) * w + x] = px;
        }
    }
    return out;
}

void draw_progress(ScreenPixels& frame, const StartupScreenDescription& d, double scale, bool rounded, double phase) {
    if (!d.progress_bar || frame.width <= 0) return;
    const int w = frame.width, h = frame.height;
    const int bar = std::max(1, static_cast<int>(std::lround(kBarHeightDip * scale)));
    const double span = w * kBarSpan;
    const double start = phase * (w + span) - span;
    const double r = rounded ? kCornerRadiusDip * scale : 0.0;
    const int x0 = std::max(0, static_cast<int>(std::floor(start)));
    const int x1 = std::min(w, static_cast<int>(std::ceil(start + span)));
    for (int y = std::max(0, h - bar); y < h; ++y) {
        for (int x = x0; x < x1; ++x) {
            auto& px = frame.bgra[static_cast<std::size_t>(y) * w + x];
            px = over(premultiply(d.progress_color, coverage(x, y, w, h, r)), px);
        }
    }
}

}  // namespace shenora
