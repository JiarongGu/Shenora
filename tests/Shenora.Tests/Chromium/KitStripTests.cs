using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>When a frameless window's kit strip begins and ends: once, at the first of the page's caption buttons, the
/// page's drag regions or the lift.</summary>
public class KitStripTests
{
    private readonly List<KitStrip.Apply> _applied = [];
    private KitStrip Strip() => new(_applied.Add);

    [Theory]
    [InlineData("caption")]
    [InlineData("drag")]
    [InlineData("lift")]
    public void Each_signal_ends_it_once(string first)
    {
        var strip = Strip();
        strip.Begin();
        Assert.True(strip.Active);
        Signal(strip, first);
        Signal(strip, "caption");
        Signal(strip, "drag");
        Signal(strip, "lift");
        Assert.Equal([KitStrip.Apply.Begin, KitStrip.Apply.End], _applied);
        Assert.False(strip.Active);
    }

    [Fact]
    public void A_signal_before_it_began_means_it_never_begins()
    {
        var strip = Strip();
        strip.PageDragRegions();
        strip.Begin();
        Assert.Empty(_applied);
        Assert.False(strip.Active);
    }

    [Fact]
    public void It_begins_once()
    {
        var strip = Strip();
        strip.Begin();
        strip.Begin();
        Assert.Equal([KitStrip.Apply.Begin], _applied);
    }

    [Fact]
    public void The_lift_ends_it_when_the_page_never_reports_a_title_bar()
    {
        var strip = Strip();
        strip.Begin();
        strip.SplashLifted();
        Assert.Equal([KitStrip.Apply.Begin, KitStrip.Apply.End], _applied);
    }

    private static void Signal(KitStrip strip, string which)
    {
        switch (which)
        {
            case "caption": strip.PageCaptionButtons(); break;
            case "drag": strip.PageDragRegions(); break;
            default: strip.SplashLifted(); break;
        }
    }
}
