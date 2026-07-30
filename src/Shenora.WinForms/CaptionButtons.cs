using System.Drawing;

namespace Shenora.WinForms;

/// <summary>
/// Which system caption button a region stands in for. "Caption button" is Windows' own name for
/// the minimize/maximize/close group, so this is the mechanism's vocabulary, not a scenario's (D22).
/// </summary>
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
/// <para>
/// Client px, not CSS px, on purpose: this is a Windows-side contract and the conversion belongs
/// to whoever knows the page's device pixel ratio (the IPC facade does it through
/// <see cref="DpiHelper"/>). A rectangle in the wrong unit here would put the hit-test in the wrong
/// place, which presents as "the button sometimes works" — the worst kind of bug to chase.
/// </para>
/// </summary>
/// <param name="Kind">Which button this region is.</param>
/// <param name="Bounds">Its rectangle in client coordinates.</param>
public readonly record struct CaptionButtonRegion(CaptionButtonKind Kind, Rectangle Bounds);

/// <summary>
/// What the OS is doing to a caption button right now.
/// <para>
/// Only useful when <see cref="OptimizedFormOptions.NativeCaptionButtons"/> is OFF, so the app draws
/// the buttons itself and needs to be told what to render. With it on, the window paints them and
/// this state is its own business.
/// </para>
/// <para>
/// It exists at all because claiming the hit-test COSTS the drawer its own hover: once
/// <c>WM_NCHITTEST</c> answers <c>HTMAXBUTTON</c> for a region, Windows treats it as non-client, so
/// a page there stops receiving mouse events and CSS <c>:hover</c> never fires. The Snap Layouts
/// flyout makes it worse — the button must stay "hot" while the pointer is over the FLYOUT, which is
/// a different window entirely and something a page could never observe.
/// </para>
/// </summary>
/// <param name="Hot">The button under the pointer, or null when none is.</param>
/// <param name="Pressed">The button being pressed, or null.</param>
public readonly record struct CaptionButtonState(CaptionButtonKind? Hot, CaptionButtonKind? Pressed);

/// <summary>
/// Palette for the caption buttons the window paints itself when
/// <see cref="OptimizedFormOptions.NativeCaptionButtons"/> is on — the app's colors, because the kit
/// ships no design (D13). Same split as <see cref="TrayMenuColors"/>: the kit owns the renderer
/// (glyphs, hit-states, DPI, the maximize↔restore glyph swap), the app owns every pixel's color.
/// <para>
/// Null on the form = a neutral fallback derived from the form's own <see cref="Control.BackColor"/>,
/// so the buttons are never invisible while an app is still wiring this up. Set it.
/// </para>
/// </summary>
public sealed class CaptionButtonColors
{
    /// <summary>
    /// The cluster's idle background. Match it to the page's title-bar color — this rectangle is cut
    /// out of the web view, so any difference shows as a visible seam beside the buttons.
    /// </summary>
    public required Color Surface { get; init; }

    /// <summary>Background of the hovered minimize/maximize button.</summary>
    public required Color Hover { get; init; }

    /// <summary>Background of the pressed minimize/maximize button.</summary>
    public required Color Pressed { get; init; }

    /// <summary>The glyph color on <see cref="Surface"/>, <see cref="Hover"/> and <see cref="Pressed"/>.</summary>
    public required Color Glyph { get; init; }

    /// <summary>
    /// Background of the hovered CLOSE button. Close has its own pair because going red on hover is
    /// the platform convention users expect; set it equal to <see cref="Hover"/> to opt out.
    /// </summary>
    public required Color CloseHover { get; init; }

    /// <summary>Background of the pressed close button.</summary>
    public required Color ClosePressed { get; init; }

    /// <summary>
    /// The close glyph's color while close is hot or pressed — white over the conventional red.
    /// Null (default) reuses <see cref="Glyph"/>, which is right when close does not change color.
    /// </summary>
    public Color? CloseGlyphHot { get; init; }
}
