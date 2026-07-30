using System.Drawing;

namespace Shenora.WinForms;

/// <summary>
/// Which system caption button a page-drawn region stands in for. "Caption button" is Windows'
/// own name for the minimize/maximize/close group, so this is the mechanism's vocabulary, not a
/// scenario's (D22).
/// <para>
/// ⚠ NOT YET FUNCTIONAL OVER A WEBVIEW2 (P5.6). This type and everything around it is in place and
/// correct, but the OS never exercises it: WebView2 puts a child window over the whole client area,
/// so <c>WindowFromPoint</c> — which is how real mouse input is routed — resolves to that child and
/// the form is never asked to hit-test these regions. No hover, no Snap Layouts. The fix (making the
/// WebView2 child chain answer <c>HTTRANSPARENT</c> for these rectangles) is tracked in
/// <c>TASKS.md</c> P5.6. Do not rely on this yet.
/// </para>
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
/// Where a page drew one caption button, in the form's CLIENT pixels.
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
/// What the OS is doing to a page-drawn caption button right now, pushed to the app so it can render
/// the affordance.
/// <para>
/// This exists because claiming the hit-test COSTS the page its own hover: once
/// <c>WM_NCHITTEST</c> answers <c>HTMAXBUTTON</c> for a region, Windows treats it as non-client, the
/// WebView2 stops receiving mouse events there, and CSS <c>:hover</c> never fires. The Snap Layouts
/// flyout makes it worse — the button must stay "hot" while the pointer is over the FLYOUT, which is
/// a different window entirely and something the page could never observe.
/// </para>
/// <para>
/// Headless (D13): the kit reports STATE and ships no styling. What "hot" and "pressed" look like is
/// the app's decision — including whether close goes red.
/// </para>
/// </summary>
/// <param name="Hot">The button under the pointer, or null when none is.</param>
/// <param name="Pressed">The button being pressed, or null.</param>
public readonly record struct CaptionButtonState(CaptionButtonKind? Hot, CaptionButtonKind? Pressed);
