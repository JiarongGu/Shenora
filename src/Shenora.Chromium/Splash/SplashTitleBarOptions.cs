using System.Drawing;

namespace Shenora.Chromium;

/// <summary>
/// The title strip a frameless main window has while its splash is up (<see cref="ChromiumSplashOptions.TitleBar"/>),
/// before its page reports a title bar of its own: the splash leaves it uncovered, and it drags the window. On Windows it
/// holds the kit's caption buttons, painted and hit-tested by the window as real ones are (a double-click on it maximizes
/// or restores, and maximize offers Snap Layouts); on macOS, the traffic lights, where
/// <see cref="ChromiumWindowOptions.NativeCaptionButtons"/> shows them. The page's own caption buttons or drag regions,
/// or the lift, end it. It is the window's own area, so it shows the window's background
/// (<see cref="ChromiumWindowOptions.BackgroundColor"/>), and the page's title bar as soon as the page paints one.
/// </summary>
public sealed class SplashTitleBarOptions
{
    /// <summary>Its height in device-independent pixels. Match the page's own title bar, so nothing moves as the splash
    /// lifts.</summary>
    public double Height { get; init; } = 32;

    /// <summary>The caption buttons' glyphs. Null follows the system theme.</summary>
    public Color? Glyph { get; init; }

    /// <summary>A caption button's fill under the pointer. Null follows the system theme. Close is always the platform's
    /// red.</summary>
    public Color? Hover { get; init; }

    /// <summary>A caption button's fill while pressed. Null follows the system theme.</summary>
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
