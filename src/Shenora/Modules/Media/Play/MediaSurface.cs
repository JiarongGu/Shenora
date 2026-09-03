namespace Shenora.Modules.Media;

/// <summary>
/// Where the picture goes, in the page's own coordinates.
/// <para>
/// ⚠ <b>CSS pixels, and they cross to the shell UNCONVERTED.</b> A page measures with
/// <c>getBoundingClientRect()</c> and the mobile shells lay out in density-independent units, which are the
/// same unit — scaling by the device pixel ratio "to be safe" is what makes the picture several times too
/// big on a phone.
/// </para>
/// </summary>
/// <param name="X">Distance from the left edge of the webview.</param>
/// <param name="Y">Distance from the top edge of the webview.</param>
/// <param name="Width">Width of the picture.</param>
/// <param name="Height">Height of the picture.</param>
/// <param name="OnTop">
/// Draw ABOVE the webview instead of behind it. Behind is the default and is what lets the page paint over
/// the picture — captions, immersive chrome and a scrim all need it. A floating window that cannot punch a
/// transparent hole through what is behind it needs the other order.
/// </param>
public readonly record struct MediaSurfaceRegion(
    double X, double Y, double Width, double Height, bool OnTop = false)
{
    /// <summary>Below this, on either side, a region is not a picture. See <see cref="IsDrawable"/>.</summary>
    public const double MinimumSide = 2;

    /// <summary>
    /// Is there anything to draw? A page reports a zero-area rectangle whenever its stage is unmounted or
    /// has a <c>display:none</c> ancestor.
    /// <para>
    /// 🔴 <b>A region that is not drawable means HIDE</b>, never a 0×0 surface at the origin — which on both
    /// mobile shells is a visible artefact at the top-left corner rather than nothing.
    /// </para>
    /// </summary>
    public bool IsDrawable => Width >= MinimumSide && Height >= MinimumSide;
}

/// <summary>
/// The SHELL's picture surface — the second surface of the one media-play layer (D58), beside the page's
/// own element.
/// <para>
/// <b>What it is for.</b> A platform player decodes what the webview refuses, but its pixels have nowhere to
/// go: the shell draws them under a transparent region the page leaves, and the page keeps every control.
/// Registered by a shell that can do that, absent on one that cannot — the page asks for
/// <see cref="Core.Shell.ShellCapability.MediaSurface"/> rather than sniffing.
/// </para>
/// <para>
/// ⚠ <b><see cref="Show"/> does NOT mean "something is playing".</b> It means the page is rendering a hole.
/// A surface left visible behind an opaque page is invisible but still composited; behind a TRANSPARENT
/// region it is a coloured rectangle over the interface.
/// </para>
/// <para>
/// ⚠ Both methods are posted, never awaited — a page repositions on every scroll frame, and serialising the
/// interface against the bridge for that is what the fire-and-forget shape avoids.
/// </para>
/// </summary>
public interface IMediaSurface
{
    /// <summary>Put the picture at <paramref name="region"/> and make it visible.</summary>
    void Show(MediaSurfaceRegion region);

    /// <summary>Take the picture off screen. Idempotent; it does not stop playback.</summary>
    void Hide();
}
