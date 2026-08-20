using System.Runtime.InteropServices;

namespace Shenora.Windows;

/// <summary>
/// DPI scaling helpers. Under PerMonitorV2 the web UI and WinForms control LAYOUT auto-scale, but a
/// WinForms FORM's outer size/position set in code is DEVICE px and is NOT auto-scaled — so window
/// geometry always needs an explicit logical↔physical conversion (see <see cref="WindowStateManager"/>,
/// which stores logical and restores physical).
/// <para>
/// Three members, deliberately: <see cref="SystemScale"/> (primary monitor, usable before any form
/// exists), <see cref="ScaleFromDeviceDpi"/> (a specific control's DPI) and the pure
/// <see cref="Scale"/>. ⚠ Compose them with the DPI you actually mean — a convenience overload that
/// picked one for you would bake in the PRIMARY monitor and silently mis-scale on a secondary.
/// </para>
/// </summary>
public static class DpiHelper
{
    /// <summary>Windows' 100% reference DPI. Exposed so callers never hardcode 96.</summary>
    public const double BaseDpi = 96.0;

    private const int LOGPIXELSX = 88;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    /// <summary>
    /// The PRIMARY monitor's DPI scale (1.0 at 100%, 1.5 at 150%, 2.0 at 200%), resolved fresh — usable
    /// before any form exists, because it reads a screen DC rather than a window.
    /// <para>
    /// ⚠ <b>PRIMARY, so it is the wrong answer for a window on a secondary monitor with a different
    /// scale.</b> Anything that has a form should use <see cref="ScaleFromDeviceDpi"/> over that form's
    /// own <c>DeviceDpi</c>.
    /// </para>
    /// </summary>
    public static double SystemScale()
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            return GetDeviceCaps(hdc, LOGPIXELSX) / BaseDpi;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <summary>
    /// Scale for a known device DPI (a WinForms <c>Control.DeviceDpi</c>): 96→1.0, 120→1.25, 144→1.5,
    /// 192→2.0, falling back to 1.0 for a non-positive DPI. Use this rather than
    /// <see cref="SystemScale"/> whenever a control exists.
    /// </summary>
    public static double ScaleFromDeviceDpi(int deviceDpi) => deviceDpi > 0 ? deviceDpi / BaseDpi : 1.0;

    /// <summary>Pure scaling: logical px × scale, rounded.</summary>
    public static int Scale(int logicalPixels, double scale) => (int)Math.Round(logicalPixels * scale);
}
