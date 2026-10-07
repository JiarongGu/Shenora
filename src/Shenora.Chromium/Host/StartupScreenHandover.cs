using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>Closes the launcher's startup screen at the app's first window: the splash card, else the main window.</summary>
internal static class StartupScreenHandover
{
    public static void Wire(IStartupScreen screen, StartupScreenMode mode, Action<Action> onCard, Action<Action> onMainShown)
    {
        if (mode is not StartupScreenMode.FirstWindow || !screen.IsShown) return;
        onCard(screen.Close);
        onMainShown(screen.Close);
    }
}
