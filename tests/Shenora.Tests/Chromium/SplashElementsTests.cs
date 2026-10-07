using Shenora.Chromium;

namespace Shenora.Tests.Chromium;

/// <summary>The splash's element records and options: the defaults an app gets without saying anything.</summary>
public class SplashElementsTests
{
    [Fact]
    public void Elements_centre_by_default_and_are_visible()
    {
        var text = new SplashText("x");
        Assert.Equal(SplashAlign.Center, text.HorizontalAlign);
        Assert.Equal(SplashAlign.Center, text.VerticalAlign);
        Assert.True(text.Visible);
        Assert.Equal(13, text.FontSize);
        Assert.Equal(SplashOrientation.Vertical, new SplashStack().Orientation);
        Assert.Empty(new SplashLayer().Children);
    }

    [Fact]
    public void A_number_is_a_uniform_inset() => Assert.Equal(new SplashInsets(4, 4, 4, 4), (SplashInsets)4);

    [Fact]
    public void Options_refuse_a_timeout_that_is_not_positive_and_a_negative_fade()
    {
        Assert.Throws<ArgumentException>(() => new ChromiumSplashOptions { Timeout = TimeSpan.Zero }.Validate("o"));
        Assert.Throws<ArgumentException>(() => new ChromiumSplashOptions { FadeOut = TimeSpan.FromMilliseconds(-1) }.Validate("o"));
        new ChromiumSplashOptions { FadeOut = TimeSpan.Zero }.Validate("o");
    }

    [Fact]
    public void The_options_default_to_the_handshake_a_15_second_timeout_and_a_short_fade()
    {
        var options = new ChromiumSplashOptions();
        Assert.False(options.HoldUntilClosed);
        Assert.Equal(TimeSpan.FromSeconds(15), options.Timeout);
        Assert.Equal(TimeSpan.FromMilliseconds(150), options.FadeOut);
        Assert.Null(options.Component);
    }

    [Fact]
    public void An_image_keeps_whichever_source_it_was_given()
    {
        Assert.Equal("logo.png", new SplashImage("logo.png").Path);
        var bytes = new SplashImage(new byte[] { 1, 2, 3 });
        Assert.Null(bytes.Path);
        Assert.Equal(3, bytes.Bytes.Length);
    }

    [Fact]
    public void UseChromium_refuses_splash_options_that_cannot_work()
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "SplashOptionsTest" });
        Assert.Throws<ArgumentException>(() => builder.UseChromium(new ChromiumHostOptions
        {
            SingleInstance = null,
            Splash = new ChromiumSplashOptions { Timeout = TimeSpan.FromSeconds(-1) },
        }));
    }
}
