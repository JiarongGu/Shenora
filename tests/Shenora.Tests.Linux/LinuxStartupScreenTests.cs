using System.Diagnostics;
using System.Drawing;

namespace Shenora.Tests.Linux;

/// <summary>
/// A Linux Chromium app's startup screen without the update launcher (D103), on real CEF: the Chromium sample published with
/// <c>ShenoraChromiumStartupScreen</c> has the kit's starter as <c>&lt;App&gt;</c>, which draws the screen (the update launcher's own X11
/// window and pixels) before it starts the app, and the app closes it at its first window. Read from outside, from the X server under openbox
/// (<c>linux-tests.sh</c> builds the sample, the screen being 300x180 and the build's timeout 60 s): the screen is on the display
/// before the app's own window, is the box the build asked for, and is gone once the app's window shows, closed by the app and not
/// by the timeout; the starter ends with the app, and draws nothing when the arguments already carry <c>--startup-screen</c>.
/// </summary>
public class LinuxStartupScreenTests
{
    private const string Title = "Shenora Chromium Sample", Class = "ShenoraStartupScreen";

    [Fact]
    public async Task The_screen_is_up_before_the_apps_window_is_the_box_asked_for_and_goes_at_the_first_window_and_the_starter_ends_with_the_app()
    {
        var sample = Environment.GetEnvironmentVariable("SHENORA_LINUX_SCREEN_SAMPLE");
        using var run = new LinuxWebPanelTests.PanelRun(sample, new Dictionary<string, string>());
        var x = run.Screen;
        var started = Stopwatch.StartNew();
        uint screen = 0, window = 0;
        Size box = default;
        TimeSpan? screenAt = null, windowAt = null, goneAt = null;
        var deadline = TimeSpan.FromMinutes(4);
        while (started.Elapsed < deadline)
        {
            if (screen == 0 && x.FindClass(Class) is var s and not 0) { screen = s; screenAt = started.Elapsed; var size = x.Size(s); box = new Size(size.Width, size.Height); }
            if (window == 0 && x.FindWindow(Title) is var w and not 0) { window = w; windowAt = started.Elapsed; }
            if (screen != 0 && window != 0 && !x.Exists(screen)) { goneAt = started.Elapsed; break; }
            if (run.Exited) Assert.Fail($"the starter ended (exit {run.ExitCode}) before the screen went:{Environment.NewLine}{run.Describe()}");
            await Task.Delay(20);
        }
        Assert.True(screenAt is not null, $"no startup screen (WM_CLASS {Class}) appeared:{Environment.NewLine}{run.Describe()}");
        Assert.True(windowAt is not null, $"the app's window never appeared:{Environment.NewLine}{run.Describe()}");
        Assert.True(goneAt is not null, $"the screen was still there when the app's window had been up for {started.Elapsed - windowAt}:{Environment.NewLine}{run.Describe()}");

        Assert.True(screenAt < windowAt, $"the screen appeared at {screenAt}, after the app's own window at {windowAt}");
        Assert.Equal(new Size(300, 180), box);
        // Closed by the app at its first window: within seconds of it, where the screen's own timeout is a minute.
        Assert.True(goneAt - windowAt < TimeSpan.FromSeconds(15), $"the screen stayed {goneAt - windowAt} after the app's window showed");
        Assert.True(goneAt < TimeSpan.FromSeconds(50), $"the screen went at {goneAt}: its timeout, not the app");

        // The app is the starter's child, started with the screen's window id as the update launcher passes it, and the starter ends with it:
        // the window's close ends both.
        Assert.Contains($"--startup-screen {screen}", run.Processes(full: true));
        run.Screen.AskToClose(window);
        await run.ExitsAsync();
        Assert.Equal(0, run.ExitCode);
        Assert.False(run.Screen.Exists(window), "the window is still on the display");
    }

    [Fact]
    public async Task A_start_with_the_screen_argument_already_there_draws_none_and_the_app_starts_beside_the_starter()
    {
        var sample = Environment.GetEnvironmentVariable("SHENORA_LINUX_SCREEN_SAMPLE");
        // The update launcher's own screen is the one up: the starter becomes the app, which is passed the argument as it came.
        using var run = new LinuxWebPanelTests.PanelRun(sample, new Dictionary<string, string>(), null, "--startup-screen", "1");
        var window = await run.WindowAsync();
        Assert.Equal(0u, run.Screen.FindClass(Class));
        run.Screen.AskToClose(window);
        await run.ExitsAsync();
        Assert.Equal(0, run.ExitCode);
    }
}
