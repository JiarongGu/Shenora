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

    /// <summary>Chromium goes without its GPU path only on Linux reaching the GPU through WSL's passthrough device, whose
    /// presence is the whole test; elsewhere it keeps its own choice.</summary>
    [Fact]
    public void The_GPU_path_is_off_only_on_Linux_through_WSLs_passthrough()
    {
        var asked = new List<string>();
        var withDevice = ChromiumApp.WithoutGpu(path => { asked.Add(path); return path == "/dev/dxg"; });
        Assert.Equal(OperatingSystem.IsLinux(), withDevice);
        Assert.False(ChromiumApp.WithoutGpu(_ => false));
        if (OperatingSystem.IsLinux()) Assert.Equal(["/dev/dxg"], asked);
        else Assert.Empty(asked);   // nothing is probed off Linux
    }
}
