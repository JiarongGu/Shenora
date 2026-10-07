using Shenora.Chromium;

namespace Shenora.Tests.Chromium;

/// <summary>The splash's options: in-window by default, and what <c>Validate</c> refuses at <c>UseChromium</c>.</summary>
public class SplashOptionsTests
{
    [Fact]
    public void Defaults_are_in_window_with_a_32_dip_strip()
    {
        var options = new ChromiumSplashOptions();
        Assert.Null(options.Card);
        Assert.Equal(32, options.TitleBar.Height);
        Assert.Null(options.TitleBar.Glyph);
        Assert.Equal(480, new SplashCardOptions().Width);
        Assert.Equal(300, new SplashCardOptions().Height);
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(480, -1)]
    [InlineData(double.NaN, 300)]
    [InlineData(480, double.PositiveInfinity)]
    public void A_card_without_a_positive_finite_size_is_refused(double width, double height) =>
        Assert.Throws<ArgumentException>(() =>
            new ChromiumSplashOptions { Card = new SplashCardOptions { Width = width, Height = height } }.Validate("options"));

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    [InlineData(double.NaN)]
    public void A_strip_height_out_of_range_is_refused(double height) =>
        Assert.Throws<ArgumentException>(() =>
            new ChromiumSplashOptions { TitleBar = new SplashTitleBarOptions { Height = height } }.Validate("options"));

    [Fact]
    public void A_null_title_bar_is_refused() =>
        Assert.Throws<ArgumentException>(() => new ChromiumSplashOptions { TitleBar = null! }.Validate("options"));

    [Fact]
    public void A_card_and_a_strip_in_range_pass()
    {
        var options = new ChromiumSplashOptions { Card = new SplashCardOptions { Width = 1, Height = 1 }, TitleBar = new SplashTitleBarOptions { Height = 200 } };
        options.Validate("options");
    }
}
