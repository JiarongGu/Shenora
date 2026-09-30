using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Core.Shell;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Chromium shell registers the shell launcher, as the WinForms shell does: one instance under
/// <see cref="IShellLauncher"/> and its portable face <see cref="IUrlLauncher"/>, which the shell's popup handler
/// consults. An app's own registration wins.
/// </summary>
public class ChromiumShellLauncherTests
{
    private sealed class Recording : IUrlLauncher
    {
        public void OpenUrl(string url) { }
    }

    [Fact]
    public void The_shell_registers_one_launcher_under_both_faces_and_an_apps_own_wins()
    {
        var plain = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Launcher test" });
        plain.UseChromium(new ChromiumHostOptions());
        using (var app = plain.Build())
        {
            var shell = app.Services.GetRequiredService<IShellLauncher>();
            Assert.IsType<ShellLauncher>(shell);
            Assert.Same(shell, app.Services.GetRequiredService<IUrlLauncher>());
        }

        var own = new Recording();
        var custom = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Launcher test" });
        custom.Services.AddSingleton<IUrlLauncher>(own);
        custom.UseChromium(new ChromiumHostOptions());
        using (var app = custom.Build())
            Assert.Same(own, app.Services.GetRequiredService<IUrlLauncher>());
    }
}
