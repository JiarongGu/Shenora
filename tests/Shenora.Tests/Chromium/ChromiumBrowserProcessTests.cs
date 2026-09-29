using Shenora.Chromium;
using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The browser process refuses what cannot work before CEF is touched, so a mistake is an exception the app can read
/// rather than a Chromium that starts wrong. What it does with CEF was measured end to end (D86).
/// </summary>
public class ChromiumBrowserProcessTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_profile_folder_is_required(string folder) =>
        Assert.Contains(nameof(ChromiumBrowserProcessOptions.UserDataFolder),
            Assert.Throws<ArgumentException>(() => ChromiumBrowserProcess.Run(new ChromiumBrowserProcessOptions { UserDataFolder = folder })).Message);

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void The_port_must_be_one(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ChromiumBrowserProcess.Run(new ChromiumBrowserProcessOptions { UserDataFolder = "profile", RemoteDebuggingPort = port }));

    [Fact]
    public void The_start_page_must_be_absolute() =>
        Assert.Contains(nameof(ChromiumBrowserProcessOptions.StartUrl),
            Assert.Throws<ArgumentException>(() => ChromiumBrowserProcess.Run(new ChromiumBrowserProcessOptions
            {
                UserDataFolder = "profile",
                StartUrl = new Uri("page.html", UriKind.Relative),
            })).Message);

    [Fact]
    public void The_browser_offers_no_first_run_and_no_default_browser_check() =>
        Assert.Equal(["no-first-run", "no-default-browser-check"], ChromiumApp.BrowserSwitches);
}
