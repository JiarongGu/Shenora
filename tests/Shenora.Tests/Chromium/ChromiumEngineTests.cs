using Shenora.Chromium;

namespace Shenora.Tests.Chromium;

/// <summary>The embedding engine's order of calls, where getting it wrong must say so rather than reach CEF.</summary>
public class ChromiumEngineTests
{
    [Fact]
    public void A_browser_needs_a_started_engine()
    {
        var engine = new ChromiumEngine(new ChromiumEngineOptions());

        var error = Assert.Throws<InvalidOperationException>(() => new ChromiumChildBrowser(engine, parentWindow: 1));
        Assert.Contains("Start", error.Message);
    }

    [Fact]
    public void An_app_without_CEF_beside_it_is_told_which_package_lays_it_out()
    {
        // The test output has no libcef: nothing here runs the package's build targets.
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "libcef.dll")));

        var error = Assert.Throws<InvalidOperationException>(() => ChromiumEngine.RunIfSubprocess(out _));

        Assert.Contains("Reference the Shenora.Chromium package", error.Message);
        Assert.IsType<DllNotFoundException>(error.InnerException);
    }

    [Fact]
    public void Stopping_an_engine_that_never_started_does_nothing()
    {
        var engine = new ChromiumEngine(new ChromiumEngineOptions());

        engine.Stop();

        Assert.False(engine.IsRunning);
        Assert.False(engine.Ready.IsCompleted);
    }
}
