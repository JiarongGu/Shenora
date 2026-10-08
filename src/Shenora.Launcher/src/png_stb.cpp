// The startup screen's PNG decoder off Windows: stb_image (third_party/, public domain / MIT — D51). Compiled with
// warnings off: third-party code, and the library's -Werror would hold it to a bar it was not written for.
#ifndef _WIN32
#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_PNG
#define STBI_NO_STDIO
#include "../third_party/stb_image.h"

#include "shenora/startup_screen.hpp"

namespace shenora {

bool decode_png(const unsigned char* data, std::size_t size, ScreenImage& out) {
    int w = 0, h = 0, n = 0;
    unsigned char* rgba = stbi_load_from_memory(data, static_cast<int>(size), &w, &h, &n, 4);
    if (!rgba) return false;
    out.width = w;
    out.height = h;
    out.bgra.resize(static_cast<std::size_t>(w) * h);
    for (std::size_t i = 0; i < out.bgra.size(); ++i) {
        const unsigned char* p = rgba + i * 4;
        out.bgra[i] = (std::uint32_t(p[3]) << 24) | (std::uint32_t(p[0]) << 16) | (std::uint32_t(p[1]) << 8) | p[2];
    }
    stbi_image_free(rgba);
    return true;
}

}  // namespace shenora
#endif  // !_WIN32
