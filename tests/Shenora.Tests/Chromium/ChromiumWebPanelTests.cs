using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Core.Ipc;
using Shenora.Core.Sessions;
using Shenora.Core.Shell;

namespace Shenora.Tests.Chromium;

/// <summary>
/// <see cref="ChromiumWindows.CreateWebPanelAsync"/> embeds a web panel in a named window (D97). The browser itself is
/// made on CEF's UI thread, which a test has none of, so these cover the guards a test CAN reach without CEF: the
/// arguments, the not-running shell, and the window that is not open.
/// </summary>
public class ChromiumWebPanelTests
{
    private static ChromiumWebPanelOptions Options() =>
        new() { Session = new SessionBrowserOptions { ProfileDirectory = "profile" } };

    [Fact]
    public async Task CreateWebPanel_refuses_before_the_shell_has_started()
    {
        var (windows, _, _) = NewWindows(started: false, onUiThread: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => windows.CreateWebPanelAsync(ChromiumWindows.MainWindowName, Options()));
    }

    [Fact]
    public async Task CreateWebPanel_validates_its_arguments()
    {
        var (windows, _, _) = NewWindows(started: false, onUiThread: false);
        await Assert.ThrowsAsync<ArgumentNullException>(() => windows.CreateWebPanelAsync(ChromiumWindows.MainWindowName, null!));
        await Assert.ThrowsAsync<ArgumentException>(() => windows.CreateWebPanelAsync("", Options()));
    }

    [Fact]
    public async Task CreateWebPanel_refuses_a_window_that_is_not_open()
    {
        var (windows, _, _) = NewWindows(started: true, onUiThread: true);
        // The shell is running and a session host is present, so it reaches the UI thread to read the window's handle,
        // finds no window named "missing", and refuses — without ever touching CEF.
        await Assert.ThrowsAsync<InvalidOperationException>(() => windows.CreateWebPanelAsync("missing", Options()));
    }

    [Fact]
    public void WebPanelOptions_default_to_persisting_session_cookies()
    {
        var options = Options();
        Assert.True(options.PersistSessionCookies);
        Assert.Null(options.ProxyServer);
        Assert.Null(options.Url);
    }

    // A ChromiumWindows wired as UseChromium wires it, with a session host; started runs Initialize so a web panel's
    // first guard (the shell is running) passes.
    private static (ChromiumWindows Windows, CefUiDispatcher Ui, ShenoraApplication App) NewWindows(bool started, bool onUiThread)
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Web panel test" });
        var options = new ChromiumHostOptions();
        builder.UseChromium(options);
        var app = builder.Build();
        var ui = new CefUiDispatcher(work => { work(); return true; }, () => onUiThread);
        var sessionHost = new ChromiumSessionHost(ui, Path.Combine(Path.GetTempPath(), "shenora-webpanel-test"), offscreen: false);
        var windows = new ChromiumWindows(options, ui, app.Services.GetRequiredService<IMessageDispatcher>(), null, null,
            new ShellLauncher(), sessionHost);
        if (started)
        {
            windows.Initialize(app, isDevelopment: false);
            ui.MarkReady();
        }
        return (windows, ui, app);
    }
}
