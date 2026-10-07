using System.Drawing;

namespace Shenora.Chromium;

/// <summary>
/// The title strip a frameless main window has while its splash is up (<see cref="ChromiumSplashOptions.TitleBar"/>),
/// before its page reports a title bar of its own: the window's drag area, and its caption buttons. On Windows and macOS
/// the page's own caption buttons or drag regions, or the lift, end it; on Linux the lift.
/// <list type="bullet">
/// <item>Windows: the window's own area, which the splash leaves uncovered, showing the window's background
/// (<see cref="ChromiumWindowOptions.BackgroundColor"/>) and then the page's title bar once it paints. Its caption buttons
/// are the kit's, painted and hit-tested by the window as real ones are, so maximize offers Snap Layouts.</item>
/// <item>macOS: likewise uncovered; its buttons are the traffic lights, where
/// <see cref="ChromiumWindowOptions.NativeCaptionButtons"/> shows them.</item>
/// <item>Linux: drawn by the splash itself, in the splash's background, with minimize, maximize and close, until the
/// splash lifts; a drag on it is handed to the window manager, and a double-click maximizes or restores.</item>
/// </list>
/// Chromium's resize band inside a frameless window's edges is left uncovered on every desktop.
/// </summary>
public sealed class SplashTitleBarOptions
{
    /// <summary>Its height in device-independent pixels. Match the page's own title bar, so nothing moves as the splash
    /// lifts.</summary>
    public double Height { get; init; } = 32;

    /// <summary>The caption buttons' glyphs. Null: white on a dark background, black on a light one (on Windows, the
    /// page's own theme once it sets one).</summary>
    public Color? Glyph { get; init; }

    /// <summary>A caption button's fill under the pointer. Null: the platform's for that background. Close is always the
    /// platform's red.</summary>
    public Color? Hover { get; init; }

    /// <summary>A caption button's fill while pressed. Null: the platform's for that background.</summary>
    public Color? Pressed { get; init; }
}

/// <summary>Which window a splash's render function is drawing (<see cref="SplashContext.Surface"/>).</summary>
public enum SplashSurface
{
    /// <summary>The card shown before the main window exists (<see cref="ChromiumSplashOptions.Card"/>).</summary>
    Card,

    /// <summary>The main window's render area.</summary>
    Window,
}
