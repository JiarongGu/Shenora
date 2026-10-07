using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Core.Shell;

namespace Shenora.Tests.Chromium;

/// <summary>Where the Chromium shell closes the launcher's screen. The hooks themselves (the card on screen, the main
/// window shown) need CEF for the window; the card's is pinned in SplashSessionTests, the window's is measured.</summary>
public class ChromiumStartupScreenTests
{
    private sealed class Screen(bool shown) : IStartupScreen
    {
        public int Closes;
        public bool IsShown => shown;
        public void Close() => Closes++;
    }

    [Fact]
    public void The_card_and_the_main_window_each_close_it()
    {
        var screen = new Screen(shown: true);
        Action? card = null, main = null;
        StartupScreenHandover.Wire(screen, StartupScreenMode.FirstWindow, a => card = a, a => main = a);

        card!();
        main!();

        Assert.Equal(2, screen.Closes);   // IStartupScreen.Close is idempotent; the kit's is pinned in Core
    }

    [Fact]
    public void Manual_mode_or_no_screen_wires_nothing()
    {
        var wired = 0;
        StartupScreenHandover.Wire(new Screen(shown: true), StartupScreenMode.Manual, _ => wired++, _ => wired++);
        StartupScreenHandover.Wire(new Screen(shown: false), StartupScreenMode.FirstWindow, _ => wired++, _ => wired++);
        Assert.Equal(0, wired);
    }

    [Fact]
    public void The_shell_closes_it_at_the_first_window_by_default() =>
        Assert.Equal(StartupScreenMode.FirstWindow, new ChromiumHostOptions().StartupScreen);
}
