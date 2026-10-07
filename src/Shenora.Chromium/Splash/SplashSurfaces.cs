using System.Drawing;
using Microsoft.Extensions.Logging;

namespace Shenora.Chromium.Host;

/// <summary>This platform's splash window, and the displays' work areas it plans the main window against before CEF can
/// say where they are.</summary>
internal static class SplashSurfaces
{
    /// <summary>This build can draw a splash. Where it cannot, an app with one keeps D87's early start, since nothing
    /// would show while it waited.</summary>
    public const bool Supported =
#if CEF_WINDOWS || CEF_MACOS || CEF_LINUX
        true;
#else
        false;
#endif

    /// <summary>A splash window for this platform, or null where it cannot draw one.</summary>
    public static ISplashSurface? Create(ILogger? log)
    {
#if CEF_WINDOWS
        return new WindowsSplashSurface(log);
#elif CEF_MACOS
        return new MacSplashSurface(log);
#elif CEF_LINUX
        // An X11 window: none without a display (CEF itself runs on X11, Xwayland included).
        return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ? null : new LinuxSplashSurface(log);
#else
        _ = log;
        return null;
#endif
    }

    /// <summary>The displays' work areas in device-independent pixels, primary first; empty when unknown. On macOS, on
    /// the main thread.</summary>
    public static IReadOnlyList<Rectangle> WorkAreas()
    {
#if CEF_WINDOWS
        return WindowsScreens.WorkAreasDip(WindowsScreens.All());
#elif CEF_MACOS
        MacPlatform.Prepare();   // NSApp is CEF's own before AppKit is touched
        MacSplashNative.LoadFrameworks();
        return MacScreens.WorkAreasDip(MacScreens.All());
#elif CEF_LINUX
        return LinuxScreens.WorkAreasDip();
#else
        return [];
#endif
    }
}
