using Microsoft.Extensions.Logging;

namespace Shenora.Chromium.Host;

/// <summary>The platform's half of <see cref="ChromiumTray"/>: the icon and its menu. UI thread throughout.</summary>
internal abstract class NativeTray : IDisposable
{
    /// <summary>This OS's tray, null on one the shell has none for. It may be there and not <see cref="Shown"/>: on Linux
    /// with no session bus, or no panel to show it.</summary>
    public static NativeTray? Create(ChromiumTray tray, ILogger? log)
    {
#if CEF_WINDOWS
        return new WindowsTray(tray, log);
#elif CEF_MACOS
        return new MacTray(tray, log);
#elif CEF_LINUX
        return new LinuxTray(tray, log);
#else
        _ = tray;
        _ = log;
        return null;
#endif
    }

    /// <summary>
    /// Whether the icon is where the user can reach it. Closing the main window hides it only then: a desktop with no
    /// tray to show the icon in would otherwise leave an app running with no window and no way back to it.
    /// </summary>
    public virtual bool Shown => true;

    public abstract void Dispose();
}
