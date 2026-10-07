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
#if CEF_WINDOWS
        true;
#else
        false;
#endif

    /// <summary>A splash window for this platform, or null where it cannot draw one.</summary>
    public static ISplashSurface? Create(ILogger? log)
    {
#if CEF_WINDOWS
        return new WindowsSplashSurface(log);
#else
        _ = log;
        return null;
#endif
    }

    /// <summary>The displays' work areas in device-independent pixels, primary first; empty when unknown.</summary>
    public static IReadOnlyList<Rectangle> WorkAreas()
    {
#if CEF_WINDOWS
        return WindowsScreens.WorkAreasDip(WindowsScreens.All());
#else
        return [];
#endif
    }
}
