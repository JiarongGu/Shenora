using System.Drawing;

namespace Shenora.Chromium.Host;

/// <summary>The session's frame for a window of <paramref name="windowPx"/> at <paramref name="scale"/> pixels per DIP,
/// measured by the surface's own text engine. Called on the surface's thread.</summary>
internal delegate SplashFrame SplashRender(Size windowPx, float scale, ISplashTextMeasurer measurer);

/// <summary>
/// One platform's splash window: borderless, never focused, drawn by the platform's own 2D and text APIs.
/// <see cref="Show"/> runs on the runner's thread, <see cref="Attach"/> and <see cref="FollowOwner"/> on CEF's UI thread
/// (both the main thread on macOS, where AppKit requires it); the rest from any thread. <see cref="IDisposable.Dispose"/>
/// destroys the window at once and may run twice.
/// </summary>
internal interface ISplashSurface : IDisposable
{
    /// <summary>Open the window where the main window will open (CEF's device-independent screen coordinates; with no
    /// place, centred the way that OS's Chromium centres a new window; maximized covers that display's work area), with
    /// its first frame already drawn.</summary>
    void Show(ChromiumWindowGeometry.Plan placement, SplashRender render);

    /// <summary>Draw again soon. Calls close together draw once.</summary>
    void Invalidate();

    /// <summary>The main window exists: the splash becomes owned by it, so it stays above it and hides with it, and
    /// takes its exact bounds. <paramref name="mainWindow"/> is CEF's window handle.</summary>
    void Attach(nint mainWindow);

    /// <summary>The main window moved or resized: take its bounds again.</summary>
    void FollowOwner();

    /// <summary>Fade out over <paramref name="duration"/>, then destroy the window; <paramref name="done"/> runs once
    /// after.</summary>
    void FadeOut(TimeSpan duration, Action done);
}
