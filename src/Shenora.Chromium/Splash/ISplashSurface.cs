using System.Drawing;

namespace Shenora.Chromium.Host;

/// <summary>The session's frame for a window of <paramref name="windowPx"/> at <paramref name="scale"/> pixels per DIP,
/// measured by the surface's own text engine. Called on the surface's thread.</summary>
internal delegate SplashFrame SplashRender(Size windowPx, float scale, ISplashTextMeasurer measurer);

/// <summary>How the splash sits on the main window: whether the window is frameless, the strip it then leaves at the
/// top (or, where the platform has no caption buttons of the kit's, draws and forwards), and what the strip's buttons
/// and double-click do. The callbacks run on any thread; they post to CEF's UI thread themselves.</summary>
internal readonly record struct SplashOverlayLayout(bool Frameless, double StripDips, SplashTitleBarOptions TitleBar,
    Action<CaptionButtonKind>? CaptionClicked, Action? ToggleMaximize);

/// <summary>
/// One platform's splash window: borderless, never focused, drawn by the platform's own 2D and text APIs. One instance
/// is EITHER the card (<see cref="ShowCard"/>, on the runner's thread) OR the window's splash (<see cref="ShowOver"/>,
/// on CEF's UI thread, the main thread on macOS). <see cref="FollowOwner"/> runs on CEF's UI thread; the rest from any
/// thread. <see cref="IDisposable.Dispose"/> destroys the window at once and may run twice.
/// </summary>
internal interface ISplashSurface : IDisposable
{
    /// <summary>Open as the card at <paramref name="dipRect"/> (CEF's device-independent screen coordinates), with its
    /// first frame already drawn.</summary>
    void ShowCard(Rectangle dipRect, SplashRender render);

    /// <summary>Make the splash over <paramref name="mainWindow"/>'s render area (CEF's window handle), owned by it so it
    /// stays above it and hides with it, and draw its first frame — hidden, BEFORE CEF shows the main window, and without
    /// holding CEF's thread for the drawing where the platform allows. <see cref="Reveal"/> shows it.</summary>
    void ShowOver(nint mainWindow, SplashOverlayLayout layout, SplashRender render);

    /// <summary>The main window is on screen: show the splash over it, at its render area as it is then (it may have opened
    /// maximized), as soon as its first frame is drawn. Returns at once: CEF's thread never waits on the splash's, whose
    /// window, owned by CEF's, may itself be waiting on CEF's thread. <paramref name="shown"/> runs once, on any thread,
    /// when it is showing, or when it never will (it failed). The card calls it at once.</summary>
    void Reveal(Action shown);

    /// <summary>Draw again soon. Calls close together draw once.</summary>
    void Invalidate();

    /// <summary>The main window moved, resized, maximized or changed DPI: take its render area again.</summary>
    void FollowOwner();

    /// <summary>Fade out over <paramref name="duration"/>, then destroy the window; <paramref name="done"/> runs once
    /// after.</summary>
    void FadeOut(TimeSpan duration, Action done);
}
