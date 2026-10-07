// The startup screen on Windows: a layered popup, per-monitor DPI, no open animation.
#ifdef _WIN32
#include "shenora/startup_screen.hpp"

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <dwmapi.h>
#include <shellscalingapi.h>
#include <wincodec.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <initializer_list>

namespace shenora {

bool decode_png(const unsigned char* data, std::size_t size, ScreenImage& out) {
    const HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    IWICImagingFactory* factory = nullptr;
    IWICStream* stream = nullptr;
    IWICBitmapDecoder* decoder = nullptr;
    IWICBitmapFrameDecode* frame = nullptr;
    IWICFormatConverter* converter = nullptr;
    bool ok = false;
    if (SUCCEEDED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)))
        && SUCCEEDED(factory->CreateStream(&stream))
        && SUCCEEDED(stream->InitializeFromMemory(const_cast<BYTE*>(data), static_cast<DWORD>(size)))
        && SUCCEEDED(factory->CreateDecoderFromStream(stream, nullptr, WICDecodeMetadataCacheOnDemand, &decoder))
        && SUCCEEDED(decoder->GetFrame(0, &frame))
        && SUCCEEDED(factory->CreateFormatConverter(&converter))
        && SUCCEEDED(converter->Initialize(frame, GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0.0,
                                           WICBitmapPaletteTypeCustom))) {
        UINT w = 0, h = 0;
        if (SUCCEEDED(converter->GetSize(&w, &h)) && w > 0 && h > 0) {
            out.width = static_cast<int>(w);
            out.height = static_cast<int>(h);
            out.bgra.resize(static_cast<std::size_t>(w) * h);
            ok = SUCCEEDED(converter->CopyPixels(nullptr, w * 4, w * h * 4, reinterpret_cast<BYTE*>(out.bgra.data())));
        }
    }
    for (IUnknown* p : std::initializer_list<IUnknown*>{converter, frame, decoder, stream, factory})
        if (p) p->Release();
    if (SUCCEEDED(init)) CoUninitialize();
    return ok;
}

namespace {

constexpr wchar_t kClass[] = L"ShenoraStartupScreen";

class Win32Screen final : public StartupScreen {
public:
    Win32Screen(const StartupScreenDescription& d, ScreenImage image) : desc_(d), image_(std::move(image)) {}
    ~Win32Screen() override {
        if (hwnd_) DestroyWindow(hwnd_);
        if (dc_) DeleteDC(dc_);              // first: a bitmap still selected into a DC will not delete
        if (bitmap_) DeleteObject(bitmap_);
    }

    bool open(std::string& error) {
        // Before any window: per-monitor DPI, so the screen is drawn crisp at the monitor's own scale.
        using SetContext = BOOL(WINAPI*)(DPI_AWARENESS_CONTEXT);
        if (auto set = reinterpret_cast<SetContext>(
                reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"user32"), "SetProcessDpiAwarenessContext"))))
            set(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        POINT at{};
        if (desc_.monitor == ScreenMonitor::Cursor) GetCursorPos(&at);
        const HMONITOR monitor = MonitorFromPoint(at, desc_.monitor == ScreenMonitor::Cursor ? MONITOR_DEFAULTTONEAREST
                                                                                           : MONITOR_DEFAULTTOPRIMARY);
        MONITORINFO info{};
        info.cbSize = sizeof(info);
        GetMonitorInfoW(monitor, &info);
        UINT dpiX = 96, dpiY = 96;
        if (FAILED(GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &dpiX, &dpiY))) dpiX = 96;
        scale_ = dpiX / 96.0;
        w_ = static_cast<int>(std::lround(desc_.width_dip * scale_));
        h_ = static_cast<int>(std::lround(desc_.height_dip * scale_));
        const RECT& work = info.rcWork;
        pos_ = {work.left + (work.right - work.left - w_) / 2, work.top + (work.bottom - work.top - h_) / 2};

        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = &Win32Screen::proc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = kClass;
        wc.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32650));   // IDC_APPSTARTING, whatever UNICODE says
        RegisterClassExW(&wc);   // a second registration in one process fails harmlessly
        hwnd_ = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, kClass, L"", WS_POPUP,
                                pos_.x, pos_.y, w_, h_, nullptr, nullptr, wc.hInstance, nullptr);
        if (!hwnd_) { error = "CreateWindowEx failed (" + std::to_string(GetLastError()) + ")"; return false; }
        SetWindowLongPtrW(hwnd_, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));
        // It appears and goes at once: no fade in, and none over the app's first window as it goes.
        BOOL on = TRUE;
        DwmSetWindowAttribute(hwnd_, DWMWA_TRANSITIONS_FORCEDISABLED, &on, sizeof(on));

        rounded_ = desc_.corners == ScreenCorners::Rounded;
        base_ = compose_screen(desc_, image_.width > 0 ? &image_ : nullptr, w_, h_, scale_, rounded_);
        start_ = std::chrono::steady_clock::now();
        paint();
        ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
        if (desc_.progress_bar) SetTimer(hwnd_, 1, 16, nullptr);
        return true;
    }

    std::string window_id() const override {
        return std::to_string(static_cast<unsigned long long>(reinterpret_cast<std::uintptr_t>(hwnd_)));
    }

    void pump_until(const std::function<bool()>& done, int timeout_ms) override {
        const auto until = timeout_ms < 0 ? std::chrono::steady_clock::time_point::max()
                                          : std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
        for (;;) {
            MSG msg;
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            if (closed_ || done()) return;
            const auto now = std::chrono::steady_clock::now();
            if (now >= until) return;
            const auto left = std::chrono::duration_cast<std::chrono::milliseconds>(until - now).count();
            MsgWaitForMultipleObjects(0, nullptr, FALSE, static_cast<DWORD>(std::min<long long>(16, left)), QS_ALLINPUT);
        }
    }

    bool closed() const override { return closed_; }

private:
    static LRESULT CALLBACK proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
        auto* self = reinterpret_cast<Win32Screen*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        switch (msg) {
        case WM_CLOSE:   // the app's close, or the window manager's
            DestroyWindow(hwnd);
            return 0;
        case WM_DESTROY:
            if (self) {
                self->closed_ = true;
                self->hwnd_ = nullptr;
            }
            return 0;
        case WM_TIMER:
            if (self) self->paint();
            return 0;
        case WM_MOUSEACTIVATE:
            return MA_NOACTIVATE;
        default:
            return DefWindowProcW(hwnd, msg, wp, lp);
        }
    }

    void paint() {
        if (!hwnd_) return;
        ScreenPixels frame = base_;
        const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start_).count();
        draw_progress(frame, desc_, scale_, rounded_, std::fmod(ms / 1500.0, 1.0));
        if (!dc_) {
            dc_ = CreateCompatibleDC(nullptr);
            BITMAPINFO bi{};
            bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            bi.bmiHeader.biWidth = w_;
            bi.bmiHeader.biHeight = -h_;   // top row first
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = BI_RGB;
            bitmap_ = CreateDIBSection(dc_, &bi, DIB_RGB_COLORS, &bits_, nullptr, 0);
            if (bitmap_) SelectObject(dc_, bitmap_);
        }
        if (!bits_) return;
        std::copy(frame.bgra.begin(), frame.bgra.end(), static_cast<std::uint32_t*>(bits_));
        SIZE size{w_, h_};
        POINT zero{0, 0};
        BLENDFUNCTION blend{AC_SRC_OVER, 0, 255, AC_SRC_ALPHA};
        UpdateLayeredWindow(hwnd_, nullptr, &pos_, &size, dc_, &zero, 0, &blend, ULW_ALPHA);
    }

    StartupScreenDescription desc_;
    ScreenImage image_;
    ScreenPixels base_;
    HWND hwnd_ = nullptr;
    HDC dc_ = nullptr;
    HBITMAP bitmap_ = nullptr;
    void* bits_ = nullptr;
    POINT pos_{};
    int w_ = 0;
    int h_ = 0;
    double scale_ = 1.0;
    bool rounded_ = true;
    bool closed_ = false;
    std::chrono::steady_clock::time_point start_;
};

}  // namespace

std::unique_ptr<StartupScreen> StartupScreen::show(const StartupScreenDescription& d, std::string& error) {
    ScreenImage image;
    if (d.png && d.png_size && !decode_png(d.png, d.png_size, image)) {
        error = "the startup screen's PNG would not decode";
        return nullptr;
    }
    auto screen = std::make_unique<Win32Screen>(d, std::move(image));
    if (!screen->open(error)) return nullptr;
    return screen;
}

}  // namespace shenora
#endif  // _WIN32
