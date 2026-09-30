using Microsoft.Extensions.Logging;

namespace Shenora.Chromium.Host;

/// <summary>The platform's half of <see cref="ChromiumTray"/>: the icon and its menu. UI thread throughout.</summary>
internal abstract class NativeTray : IDisposable
{
    /// <summary>This OS's tray, shown; null where the shell has none yet (Linux).</summary>
    public static NativeTray? Create(ChromiumTray tray, ILogger? log)
    {
#if CEF_WINDOWS
        return new WindowsTray(tray, log);
#elif CEF_MACOS
        return new MacTray(tray, log);
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
