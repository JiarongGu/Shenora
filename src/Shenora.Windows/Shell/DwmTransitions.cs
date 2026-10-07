using System.Runtime.InteropServices;

namespace Shenora.Windows;

/// <summary>A window's system animations, through DWM (<see cref="Shenora.Core.Shell.WindowAnimations"/>).</summary>
internal static class DwmTransitions
{
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

    /// <summary>Turn the window's open, close, minimize and maximize animations off. False when DWM refused.</summary>
    public static bool Disable(nint hwnd)
    {
        var on = 1;
        return DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref on, sizeof(int)) == 0;
    }

    [DllImport("dwmapi")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
