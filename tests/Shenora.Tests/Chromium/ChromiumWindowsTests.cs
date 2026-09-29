using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Chromium;

/// <summary>
/// <see cref="ChromiumWindows"/> answers as <c>SecondaryWindows</c> does, so a caller reads the same result from either
/// shell: <c>Open</c> is false when the name is already open, which it activates instead. The window itself is made on
/// CEF's UI thread, which a test has none of, so the posts are recorded and never run.
/// </summary>
public class ChromiumWindowsTests
{
    [Fact]
    public void Open_reserves_the_name_at_once_and_a_second_open_activates_it_instead()
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Windows test" });
        var options = new ChromiumHostOptions();
        builder.UseChromium(options);
        using var app = builder.Build();
        var posted = new List<Action>();
        var accepting = true;
        var ui = new CefUiDispatcher(work => { if (accepting) posted.Add(work); return accepting; }, () => false);
        var windows = new ChromiumWindows(options, ui, app.Services.GetRequiredService<IMessageDispatcher>(), null, null,
            new ChromiumUrlLauncher());

        Assert.False(windows.Open("a", new ChromiumWindowOptions()));   // the shell has not started
        Assert.False(windows.HasWindow("a"));

        windows.Initialize(app, isDevelopment: false);
        ui.MarkReady();
        Assert.True(windows.Open("a", new ChromiumWindowOptions()));
        Assert.True(windows.HasWindow("a"));   // before the UI thread has made it
        Assert.False(windows.Open("a", new ChromiumWindowOptions()));
        Assert.Equal(2, posted.Count);   // the open, then the activation in place of a second one

        accepting = false;   // CEF's UI thread is gone
        Assert.False(windows.Open("b", new ChromiumWindowOptions()));
        Assert.False(windows.HasWindow("b"));   // a refused open keeps no name
    }
}
