using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Chromium shell's <see cref="IUiInteraction"/> takes the main window's input while something modal runs, nested
/// as the WinForms shell's is: the window gets its input back only when the last block is released. The window's own
/// change runs on CEF's UI thread, which a test has none of, so the posts are counted and never run.
/// </summary>
public class ChromiumUiInteractionTests
{
    [Fact]
    public void Blocks_nest_and_only_the_last_unblock_gives_the_input_back()
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Interaction test" });
        var options = new ChromiumHostOptions();
        builder.UseChromium(options);
        using var app = builder.Build();
        var posted = new List<Action>();
        var ui = new CefUiDispatcher(work => { posted.Add(work); return true; }, () => false);
        ui.MarkReady();
        var windows = new ChromiumWindows(options, ui, app.Services.GetRequiredService<IMessageDispatcher>(), null, null,
            new ShellLauncher());
        var interaction = new ChromiumUiInteraction(windows);

        interaction.BlockInteraction();
        interaction.BlockInteraction();
        Assert.False(windows.MainEnabled);
        Assert.Single(posted);   // one change for two blocks

        interaction.UnblockInteraction();
        Assert.False(windows.MainEnabled);   // one block still held
        interaction.UnblockInteraction();
        Assert.True(windows.MainEnabled);
        Assert.Equal(2, posted.Count);

        interaction.UnblockInteraction();   // unmatched: no change, and the next block still counts from one
        Assert.Equal(2, posted.Count);
        interaction.BlockInteraction();
        Assert.False(windows.MainEnabled);
        interaction.UnblockInteraction();
        Assert.True(windows.MainEnabled);
    }

    private sealed class Own : IUiInteraction
    {
        public void BlockInteraction() { }
        public void UnblockInteraction() { }
    }

    [Fact]
    public void The_shell_registers_it_and_an_apps_own_wins()
    {
        var plain = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Interaction test" });
        plain.UseChromium(new ChromiumHostOptions());
        using (var app = plain.Build())
            Assert.IsType<ChromiumUiInteraction>(app.Services.GetRequiredService<IUiInteraction>());

        var own = new Own();
        var custom = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Interaction test" });
        custom.Services.AddSingleton<IUiInteraction>(own);
        custom.UseChromium(new ChromiumHostOptions());
        using (var app = custom.Build())
            Assert.Same(own, app.Services.GetRequiredService<IUiInteraction>());
    }
}
