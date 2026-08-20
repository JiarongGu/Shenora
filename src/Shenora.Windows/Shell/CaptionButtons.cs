using System.Drawing;

namespace Shenora.Windows;

/// <summary>Which system caption button a region stands in for.</summary>
public enum CaptionButtonKind
{
    /// <summary>Minimize (<c>HTMINBUTTON</c>).</summary>
    Minimize,

    /// <summary>Maximize/restore (<c>HTMAXBUTTON</c>) — the one Snap Layouts attaches to.</summary>
    Maximize,

    /// <summary>Close (<c>HTCLOSE</c>).</summary>
    Close,
}

/// <summary>
/// Where the page reserved space for one caption button, in the form's CLIENT pixels.
/// ⚠ Client px, not CSS px — the conversion belongs to whoever knows the page's device pixel ratio (the
/// IPC facade does it through <see cref="DpiHelper"/>), and a rectangle in the wrong unit puts the
/// hit-test somewhere the user cannot see.
/// </summary>
/// <param name="Kind">Which button this region is.</param>
/// <param name="Bounds">Its rectangle in client coordinates.</param>
public readonly record struct CaptionButtonRegion(CaptionButtonKind Kind, Rectangle Bounds);

/// <summary>
/// What the OS is doing to a caption button right now. Only useful when
/// <see cref="OptimizedFormOptions.NativeCaptionButtons"/> is OFF, so the app draws the buttons itself:
/// claiming the hit-test costs it every mouse event in those rectangles (Windows treats them as
/// non-client, so CSS <c>:hover</c> never fires) and the Snap Layouts flyout is a different window
/// entirely, which a page could never observe.
/// </summary>
/// <param name="Hot">The button under the pointer, or null when none is.</param>
/// <param name="Pressed">The button being pressed, or null.</param>
public readonly record struct CaptionButtonState(CaptionButtonKind? Hot, CaptionButtonKind? Pressed);

/// <summary>
/// Palette for the caption buttons the window paints itself when
/// <see cref="OptimizedFormOptions.NativeCaptionButtons"/> is on — the app's colors, because the kit ships
/// no design (D13). Null on the form = a neutral fallback derived from the form's own
/// <see cref="Control.BackColor"/>, so the buttons are never invisible while an app is wiring this up.
/// </summary>
public sealed class CaptionButtonColors
{
    /// <summary>The cluster's idle background. Match it to the page's title-bar color — this rectangle is
    /// cut out of the web view, so any difference shows as a visible seam beside the buttons.</summary>
    public required Color Surface { get; init; }

    /// <summary>Background of the hovered minimize/maximize button.</summary>
    public required Color Hover { get; init; }

    /// <summary>Background of the pressed minimize/maximize button.</summary>
    public required Color Pressed { get; init; }

    /// <summary>The glyph color on <see cref="Surface"/>, <see cref="Hover"/> and <see cref="Pressed"/>.</summary>
    public required Color Glyph { get; init; }

    /// <summary>Background of the hovered CLOSE button — its own pair because going red on hover is the
    /// platform convention. Set it equal to <see cref="Hover"/> to opt out.</summary>
    public required Color CloseHover { get; init; }

    /// <summary>Background of the pressed close button.</summary>
    public required Color ClosePressed { get; init; }

    /// <summary>The close glyph's color while close is hot or pressed — white over the conventional red.
    /// Null (default) reuses <see cref="Glyph"/>.</summary>
    public Color? CloseGlyphHot { get; init; }
}
