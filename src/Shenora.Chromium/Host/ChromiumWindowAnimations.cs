#if CEF_WINDOWS
using static Shenora.Chromium.Host.WindowsSplashNative;
#elif CEF_MACOS
using static Shenora.Chromium.Host.MacSplashNative;
#endif

namespace Shenora.Chromium.Host;

/// <summary>A window's system animations (<see cref="Shenora.Core.Shell.WindowAnimations"/>), turned off before it shows.</summary>
internal static unsafe class ChromiumWindowAnimations
{
    /// <summary>
    /// Turn off the animations the system plays for the window, given the handle CEF reports for it: on Windows an HWND
    /// (DWM turns open, close, minimize and maximize off together), on macOS a view or its window (the window's open and
    /// close). False when the system refused. On Linux the window manager decides: nothing to do, and true.
    /// </summary>
    public static bool Disable(nint handle)
    {
        if (handle == 0) return false;
#if CEF_WINDOWS
        var on = 1;
        return DwmSetWindowAttribute(handle, DWMWA_TRANSITIONS_FORCEDISABLED, &on, sizeof(int)) == 0;
#elif CEF_MACOS
        var window = GetBool(handle, "isKindOfClass:", Class("NSWindow")) ? handle : Send(handle, "window");
        if (window == 0) return false;
        SendLong(window, "setAnimationBehavior:", 2 /* NSWindowAnimationBehaviorNone */);
        return true;
#else
        return true;
#endif
    }
}
