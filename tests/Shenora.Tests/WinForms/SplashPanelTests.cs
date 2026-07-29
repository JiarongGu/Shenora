using Shenora.WinForms;

namespace Shenora.Tests.WinForms;

public class SplashPanelTests
{
    [Fact]
    public void Defaults_apply_and_bar_centers()
    {
        using var panel = new SplashPanel();
        panel.Size = new Size(1000, 500);
        panel.UpdateBarLayout(); // resize recentering is debounced on a UI timer; drive directly

        Assert.Equal(Color.FromArgb(31, 31, 31), panel.BackColor);
        var content = panel.Controls[0];
        Assert.Equal(400, content.Width); // 70% capped at BarMaxWidth
        Assert.Equal(4, content.Height);
        Assert.Equal((1000 - 400) / 2, content.Left);
        Assert.Equal((500 - 4) / 2, content.Top);
    }

    [Fact]
    public void Narrow_panel_uses_the_width_fraction()
    {
        using var panel = new SplashPanel();
        panel.Size = new Size(400, 300);
        panel.UpdateBarLayout();
        Assert.Equal(280, panel.Controls[0].Width); // 70% of 400
    }

    [Fact]
    public void Update_progress_switches_to_determinate_and_clamps()
    {
        using var panel = new SplashPanel();
        var bar = (ProgressBar)panel.Controls[0].Controls[0];
        Assert.Equal(ProgressBarStyle.Marquee, bar.Style);

        panel.UpdateProgress(150);
        Assert.Equal(ProgressBarStyle.Continuous, bar.Style);
        Assert.Equal(100, bar.Value);

        panel.UpdateProgress(-5);
        Assert.Equal(0, bar.Value);
    }

    [Fact]
    public void Colors_are_the_apps_choice()
    {
        using var panel = new SplashPanel(new SplashPanelOptions
        {
            BackColor = Color.White,
            BarColor = Color.Black,
            BarMaxWidth = 200,
            BarHeight = 6,
        });
        panel.Size = new Size(1000, 500);
        panel.UpdateBarLayout();

        Assert.Equal(Color.White, panel.BackColor);
        Assert.Equal(200, panel.Controls[0].Width);
        Assert.Equal(6, panel.Controls[0].Height);

        panel.SetColors(Color.Red, Color.Blue);
        Assert.Equal(Color.Red, panel.BackColor);
    }
}
