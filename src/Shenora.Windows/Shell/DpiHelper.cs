using System.Runtime.InteropServices;

namespace Shenora.Windows;

/// <summary>
/// DPI scaling helpers — the family's most-repeated trap, solved once.
///
/// Under PerMonitorV2 the web UI and WinForms control LAYOUT auto-scale, but a WinForms FORM's
/// outer size/position set in code is DEVICE px and is NOT auto-scaled from a logical baseline —
/// so window size/position always need an explicit logical↔physical conversion (see
/// <see cref="WindowStateManager"/>, which stores logical and restores physical).
/// <para>
/// The surface is deliberately small: <see cref="SystemScale"/> (primary monitor, usable before any
/// form exists), <see cref="ScaleFromDeviceDpi"/> (a specific control's DPI — the right one when a form
/// may sit on a secondary monitor), and the pure <see cref="Scale"/>. <c>ScalePixels</c>/<c>ScaleSize</c>/
/// <c>ScalePoint</c> were removed in P5.5 H6: they had ZERO callers, and the consumer their own docs
/// named — the drop-zone overlay — lives in <c>Shenora.Windows</c> and does its conversion from the
/// control's <c>DeviceDpi</c> instead, which is the correct source. They also baked in the WRONG default
/// by being convenient: they used the PRIMARY monitor's scale, so anything that adopted them would
/// silently mis-scale on a secondary monitor. Compose <see cref="Scale"/> with the DPI you actually mean.
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
    /// The PRIMARY monitor's DPI scale (1.0 at 100%, 1.5 at 150%, 2.0 at 200%), resolved fresh —
    /// usable before any form exists (screen DC), which is why window-state restore uses it.
    /// In a PerMonitorV2 process this returns the primary monitor's real DPI (verified live in
    /// the source app, 2026-07-12).
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
    /// Scale for a known device DPI (a WinForms <c>Control.DeviceDpi</c>): 96→1.0, 120→1.25,
    /// 144→1.5, 192→2.0. Falls back to 1.0 for a non-positive/unknown DPI. Use this — not
    /// <see cref="SystemScale"/> — when saving a form's bounds: the form may sit on a secondary
    /// monitor with a different DPI.
    /// </summary>
    public static double ScaleFromDeviceDpi(int deviceDpi) => deviceDpi > 0 ? deviceDpi / BaseDpi : 1.0;

    /// <summary>
    /// Pure scaling: logical px × scale, rounded. Pair it with the DPI you mean —
    /// <see cref="ScaleFromDeviceDpi"/> for a control, <see cref="SystemScale"/> only when no control
    /// exists yet.
    /// </summary>
    public static int Scale(int logicalPixels, double scale) => (int)Math.Round(logicalPixels * scale);
}
