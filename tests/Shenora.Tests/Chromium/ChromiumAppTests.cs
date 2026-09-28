using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The feature the shell turns off against a dev server is ADDED to whatever the app's own command line turns off,
/// never written over it. The switch reaching Chromium was measured in the shell (the dev server's socket opened).
/// </summary>
public class ChromiumAppTests
{
    private const string Feature = ChromiumApp.DevServerDisabledFeature;

    [Theory]
    [InlineData(null, Feature)]
    [InlineData("", Feature)]
    [InlineData("AppOwn", "AppOwn," + Feature)]
    [InlineData(" AppOwn , Other ", "AppOwn,Other," + Feature)]
    [InlineData("AppOwn," + Feature, "AppOwn," + Feature)]   // already there: not twice
    public void The_dev_server_feature_joins_the_apps_own(string? existing, string expected) =>
        Assert.Equal(expected, ChromiumApp.WithDisabledFeature(existing, Feature));
}
