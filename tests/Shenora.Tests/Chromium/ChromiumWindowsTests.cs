using Shenora.Core.Shell;
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
            new ShellLauncher());

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

    private sealed class Store : IWindowStateStore
    {
        public WindowState? Load() => null;
        public void Save(WindowState state) { }
    }

    /// <summary>
    /// Each window keeps its state in its own store: the main window in the host's, any other in the one it was opened
    /// with, with its own minimum size. The main window refuses one of its own, so there is one place to set it.
    /// </summary>
    [Fact]
    public void Each_window_restores_from_its_own_store()
    {
        var mainStore = new Store();
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Windows test" });
        var options = new ChromiumHostOptions { WindowState = new WindowStateHostOptions { Store = _ => mainStore } };
        builder.UseChromium(options);
        using var app = builder.Build();
        var ui = new CefUiDispatcher(_ => true, () => false);
        var windows = new ChromiumWindows(options, ui, app.Services.GetRequiredService<IMessageDispatcher>(), null, null,
            new ShellLauncher());
        windows.Initialize(app, isDevelopment: false);

        Assert.Same(mainStore, windows.GeometryFor(ChromiumWindows.MainWindowName, options.Window)!.Store);

        var panelStore = new Store();
        var panel = windows.GeometryFor("panel", new ChromiumWindowOptions
        {
            StateStore = panelStore, StateOptions = new WindowStateOptions { MinWidth = 320, MinHeight = 240 },
        })!;
        Assert.Same(panelStore, panel.Store);
        Assert.Equal(new System.Drawing.Size(320, 240), panel.Minimum);
        Assert.Null(windows.GeometryFor("plain", new ChromiumWindowOptions()));   // no store, nothing kept

        ui.MarkReady();
        Assert.Throws<ArgumentException>(() => windows.Open(ChromiumWindows.MainWindowName, new ChromiumWindowOptions { StateStore = panelStore }));
        Assert.Throws<ArgumentException>(() => ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Windows test" })
            .UseChromium(new ChromiumHostOptions { Window = new ChromiumWindowOptions { StateStore = panelStore } }));
    }
}
