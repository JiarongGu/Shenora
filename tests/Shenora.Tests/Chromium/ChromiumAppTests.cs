using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The features the Chromium hosts turn off are ADDED to whatever the app's own command line turns off, never written
/// over it. The switch reaching Chromium was measured in the shell: a loopback fetch and the dev server's socket both
/// opened.
/// </summary>
public class ChromiumAppTests
{
    private static readonly string Ours = string.Join(',', ChromiumApp.DisabledFeatures);

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("AppOwn", "AppOwn,")]
    [InlineData(" AppOwn , Other ", "AppOwn,Other,")]
    public void The_hosts_features_join_the_apps_own(string? existing, string prefix) =>
        Assert.Equal(prefix + Ours, ChromiumApp.WithDisabledFeatures(existing, ChromiumApp.DisabledFeatures));

    [Fact]
    public void A_feature_already_there_is_not_added_twice() =>
        Assert.Equal("AppOwn," + Ours, ChromiumApp.WithDisabledFeatures("AppOwn," + ChromiumApp.DisabledFeatures[0], ChromiumApp.DisabledFeatures));

    [Fact]
    public void Both_local_network_checks_are_off() =>
        Assert.Equal(["LocalNetworkAccessChecks", "LocalNetworkAccessChecksWebSockets"], ChromiumApp.DisabledFeatures);
}
