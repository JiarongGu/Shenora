using System.Drawing;

namespace Shenora.Chromium.Host;

/// <summary>Which corners of the overlay its window rounds, so the overlay cuts the same ones.</summary>
[Flags]
internal enum OverlayCorners
{
    None = 0,
    Top = 1,
    Bottom = 2,
}

/// <summary>Where the splash's windows go, from the main window's plan and its client area. Pure.</summary>
internal static class SplashGeometry
{
    /// <summary>A caption button's width in DIPs: the width Windows 10 and 11 draw theirs at.</summary>
    public const double CaptionButtonDips = 46;

    /// <summary>The resize band Chromium keeps inside a frameless window's edges, in DIPs (measured on Windows: its
    /// hit-test answers HTLEFT and HTBOTTOM for the outer 8 pixels at 200 %).</summary>
    public const double ResizeBandDips = 4;

    /// <summary>The card, in DIPs: centred on the window's planned rect, on the work area of its display when it opens
    /// maximized (the plan carries the restored bounds), or on the primary work area when the plan has no place; clamped
    /// into the display it lands on.</summary>
    public static Rectangle CardRect(ChromiumWindowGeometry.Plan plan, IReadOnlyList<Rectangle> workAreas, double width, double height)
    {
        var primary = workAreas.Count > 0 ? workAreas[0] : new Rectangle(0, 0, plan.Width, plan.Height);
        var around = plan is { X: { } x, Y: { } y } ? new Rectangle(x, y, plan.Width, plan.Height) : primary;
        var centre = new Point(around.X + around.Width / 2, around.Y + around.Height / 2);
        var display = workAreas.FirstOrDefault(a => a.Contains(centre));
        if (display.IsEmpty) display = primary;
        if (plan.Maximized) centre = new Point(display.X + display.Width / 2, display.Y + display.Height / 2);
        var w = Math.Min((int)Math.Round(width), display.Width);
        var h = Math.Min((int)Math.Round(height), display.Height);
        var left = Math.Clamp(centre.X - w / 2, display.Left, display.Right - w);
        var top = Math.Clamp(centre.Y - h / 2, display.Top, display.Bottom - h);
        return new Rectangle(left, top, w, h);
    }

    /// <summary>The overlay, in screen pixels: the client area; on a frameless window, below the strip and inside the
    /// resize band of <paramref name="edgePx"/> that Chromium keeps inside its left, right and bottom edges (zero when
    /// maximized). Never less than one pixel either way.</summary>
    public static Rectangle OverlayRect(Rectangle clientScreenPx, bool frameless, int stripPx, int edgePx = 0)
    {
        if (!frameless) return clientScreenPx;
        var strip = Math.Min(Math.Max(0, stripPx), Math.Max(0, clientScreenPx.Height - 1));
        var edge = Math.Clamp(edgePx, 0, Math.Max(0, (clientScreenPx.Width - 1) / 2));
        var bottom = Math.Min(Math.Max(0, edgePx), Math.Max(0, clientScreenPx.Height - strip - 1));
        return new Rectangle(clientScreenPx.X + edge, clientScreenPx.Y + strip, clientScreenPx.Width - (2 * edge),
            clientScreenPx.Height - strip - bottom);
    }

    /// <summary>
    /// The splash over a window's client area on Linux, where it covers a frameless window's strip too and draws it: the
    /// resize band Chromium keeps inside a frameless window's edges is left on every side (unless maximized); otherwise
    /// its bottom row of pixels, since X counts a window covered entirely as fully obscured and Chromium stops drawing it.
    /// </summary>
    public static Rectangle LinuxOverlayRect(Rectangle client, bool frameless, bool maximized, int bandPx)
    {
        if (frameless && !maximized && bandPx > 0 && client.Width > 2 * bandPx && client.Height > 2 * bandPx)
            return Rectangle.Inflate(client, -bandPx, -bandPx);
        return new Rectangle(client.X, client.Y, client.Width, client.Height > 1 ? client.Height - 1 : client.Height);
    }

    /// <summary>The corners the window rounds below its title area: the bottom two, except when maximized or on a
    /// platform that does not round windows. <paramref name="framed"/> does not change it: a framed window's title bar and
    /// a frameless one's strip are both above the overlay.</summary>
    public static OverlayCorners Corners(bool roundedPlatform, bool maximized, bool framed) =>
        roundedPlatform && !maximized ? OverlayCorners.Bottom : OverlayCorners.None;

    /// <summary>The strip's caption buttons, in client pixels: minimize, maximize, close, right-aligned.</summary>
    public static CaptionButtonRect[] DefaultCaptionButtons(int clientWidthPx, int stripPx, double scale)
    {
        var w = (int)Math.Round(CaptionButtonDips * scale);
        return
        [
            new(CaptionButtonKind.Minimize, clientWidthPx - 3 * w, 0, w, stripPx),
            new(CaptionButtonKind.Maximize, clientWidthPx - 2 * w, 0, w, stripPx),
            new(CaptionButtonKind.Close, clientWidthPx - w, 0, w, stripPx),
        ];
    }

    /// <summary>The strip's drag area, in client pixels: all of it left of the buttons.</summary>
    public static Rectangle StripDragRect(int clientWidthPx, int stripPx, IReadOnlyList<CaptionButtonRect> buttons)
    {
        var right = buttons.Count == 0 ? clientWidthPx : buttons.Min(b => b.X);
        return new Rectangle(0, 0, Math.Max(0, right), stripPx);
    }
}
