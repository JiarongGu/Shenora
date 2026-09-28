using Shenora.Chromium;

namespace Shenora.Tests.Chromium;

/// <summary>The embedding engine's order of calls, where getting it wrong must say so rather than reach CEF.</summary>
public class ChromiumEngineTests
{
    [Fact]
    public void A_browser_needs_a_started_engine()
    {
        var engine = new ChromiumEngine();

        var error = Assert.Throws<InvalidOperationException>(() => new ChromiumChildBrowser(engine, parentWindow: 1));
        Assert.Contains("Start", error.Message);
    }

    [Fact]
    public void Stopping_an_engine_that_never_started_does_nothing()
    {
        var engine = new ChromiumEngine();

        engine.Stop();

        Assert.False(engine.IsRunning);
        Assert.False(engine.Ready.IsCompleted);
    }
}
