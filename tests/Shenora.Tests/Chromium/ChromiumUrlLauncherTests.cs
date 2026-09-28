using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Core.Shell;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Chromium shell's URL launcher: the user's browser for http and https only. The accepting path starts a
/// real browser, so only the refusals are exercised here; the shell's popup handler was seen consulting it.
/// </summary>
public class ChromiumUrlLauncherTests
{
    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/path")]
    [InlineData("ms-settings:privacy")]
    public void Anything_but_a_web_url_is_refused(string url) =>
        Assert.Throws<ArgumentException>(() => new ChromiumUrlLauncher().OpenUrl(url));

    private sealed class Recording : IUrlLauncher
    {
        public void OpenUrl(string url) { }
    }

    [Fact]
    public void The_shell_registers_its_launcher_and_an_apps_own_wins()
    {
        var plain = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Launcher test" });
        plain.UseChromium(new ChromiumHostOptions());
        using (var app = plain.Build())
            Assert.IsType<ChromiumUrlLauncher>(app.Services.GetRequiredService<IUrlLauncher>());

        var own = new Recording();
        var custom = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Launcher test" });
        custom.Services.AddSingleton<IUrlLauncher>(own);
        custom.UseChromium(new ChromiumHostOptions());
        using (var app = custom.Build())
            Assert.Same(own, app.Services.GetRequiredService<IUrlLauncher>());
    }
}
