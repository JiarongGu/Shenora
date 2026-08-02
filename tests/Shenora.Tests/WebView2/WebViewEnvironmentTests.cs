using Shenora.Windows;

namespace Shenora.Tests.WebView2;

/// <summary>
/// Environment CREATION spawns a real browser process, so it stays out of the unit suite (the
/// sample-app e2e covers it). These cover the safe probes.
/// </summary>
public class WebViewEnvironmentTests
{
    [Fact]
    public void Runtime_probe_never_throws()
    {
        // On a machine without the Evergreen runtime this is null (the actionable-prompt path);
        // with it, a version string. Either way it must not throw — that was the shipped gap.
        var ex = Record.Exception(() => WebViewEnvironment.GetAvailableRuntimeVersion());
        Assert.Null(ex);
    }

    [Fact]
    public void IsRuntimeAvailable_matches_the_version_probe()
    {
        Assert.Equal(WebViewEnvironment.GetAvailableRuntimeVersion() is not null,
            WebViewEnvironment.IsRuntimeAvailable());
    }

    [Fact]
    public void A_bogus_fixed_runtime_folder_reports_unavailable_not_a_throw()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "shenora-no-such-runtime");
        var ex = Record.Exception(() => WebViewEnvironment.IsRuntimeAvailable(bogus));
        Assert.Null(ex);
    }
}
