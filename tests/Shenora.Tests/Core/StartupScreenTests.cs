using Shenora.Core.Shell;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Core;

/// <summary>
/// The app's side of a launcher's startup screen: the window it is passed, and the close that ends the screen.
/// </summary>
public class StartupScreenTests
{
    [Theory]
    [InlineData(new[] { "--app-root", "C:\\App", "--startup-screen", "123456" }, 123456UL)]
    [InlineData(new[] { "--startup-screen=789" }, 789UL)]
    [InlineData(new[] { "--STARTUP-SCREEN", "42" }, 42UL)]
    public void The_launcher_s_window_is_read_from_the_arguments(string[] args, ulong expected) =>
        Assert.Equal(expected, StartupScreen.Parse(args));

    [Theory]
    [InlineData(new object[] { new string[0] })]
    [InlineData(new object[] { new[] { "--startup-screen" } })]
    [InlineData(new object[] { new[] { "--startup-screen", "0" } })]
    [InlineData(new object[] { new[] { "--startup-screen", "not-a-number" } })]
    [InlineData(new object[] { new[] { "--startup-screen", "-5" } })]
    public void No_usable_window_is_no_screen(string[] args)
    {
        Assert.Null(StartupScreen.Parse(args));
        Assert.False(StartupScreen.FromArguments(args).IsShown);
    }

    [Fact]
    public void Closing_asks_the_platform_once_with_the_launcher_s_window()
    {
        var asked = new List<ulong>();
        var screen = new StartupScreen(77, w => { asked.Add(w); return true; }, log: null);
        Assert.True(screen.IsShown);

        screen.Close();
        screen.Close();

        Assert.Equal([77UL], asked);
        Assert.False(screen.IsShown);
    }

    [Fact]
    public void Without_a_window_closing_does_nothing()
    {
        var asked = false;
        new StartupScreen(null, _ => asked = true, log: null).Close();
        Assert.False(asked);
    }

    [Fact]
    public void A_platform_that_throws_costs_the_close_and_nothing_else()
    {
        var screen = new StartupScreen(5, _ => throw new InvalidOperationException("display gone"), log: null);
        screen.Close();   // never throws
        Assert.False(screen.IsShown);
    }

    [Fact]
    public void On_Windows_the_close_reaches_the_window_as_WM_CLOSE() => Sta.Run(() =>
    {
        if (!OperatingSystem.IsWindows()) return;
        using var form = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
        var closed = false;
        form.FormClosed += (_, _) => closed = true;
        form.Show();

        new StartupScreen((ulong)form.Handle).Close();
        for (var i = 0; i < 50 && !closed; i++) { Application.DoEvents(); Thread.Sleep(10); }

        Assert.True(closed);
    });
}
