using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// Layout + colour behaviour. Expectations are DERIVED from <see cref="SplashPanelOptions"/> rather
/// than retyping its default values (P5.5 H7): these tests used to hardcode 400 / 4 / "70% of 400",
/// which is a second copy of the production defaults — changing a default made the test fail with a
/// number mismatch instead of telling anyone the contract had moved, and the arithmetic comment was
/// the only record of the RULE. What the panel actually promises is the rule, so that is what is
/// asserted: width = min(panelWidth × fraction, max), height = BarHeight, centered.
/// </summary>
public class SplashPanelTests
{
    private static readonly SplashPanelOptions Defaults = new();

    /// <summary>The documented sizing rule, evaluated from options — not a second copy of it.</summary>
    private static int ExpectedBarWidth(int panelWidth, SplashPanelOptions options) =>
        Math.Min((int)(panelWidth * options.BarWidthFraction), options.BarMaxWidth);

    [Fact]
    public void Defaults_apply_and_bar_centers()
    {
        using var panel = new SplashPanel();
        panel.Size = new Size(1000, 500);
        panel.UpdateBarLayout(); // resize recentering is debounced on a UI timer; drive directly

        Assert.Equal(Color.FromArgb(31, 31, 31), panel.BackColor);
        var content = panel.ContentPanel;
        // 1000 × 0.7 = 700, so the cap is what applies here — the branch this case exists for.
        Assert.Equal(Defaults.BarMaxWidth, ExpectedBarWidth(1000, Defaults));
        Assert.Equal(ExpectedBarWidth(1000, Defaults), content.Width);
        Assert.Equal(Defaults.BarHeight, content.Height);
        Assert.Equal((1000 - content.Width) / 2, content.Left);
        Assert.Equal((500 - content.Height) / 2, content.Top);
    }

    [Fact]
    public void Narrow_panel_uses_the_width_fraction()
    {
        using var panel = new SplashPanel();
        panel.Size = new Size(400, 300);
        panel.UpdateBarLayout();

        // 400 × 0.7 = 280 < the 400 cap, so the FRACTION applies — the other branch.
        Assert.True(ExpectedBarWidth(400, Defaults) < Defaults.BarMaxWidth);
        Assert.Equal(ExpectedBarWidth(400, Defaults), panel.ContentPanel.Width);
    }

    [Fact]
    public void Update_progress_switches_to_determinate_and_clamps()
    {
        using var panel = new SplashPanel();
        var bar = panel.Bar;
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
        var options = new SplashPanelOptions
        {
            BackColor = Color.White,
            BarColor = Color.Black,
            BarMaxWidth = 200,
            BarHeight = 6,
        };
        using var panel = new SplashPanel(options);
        panel.Size = new Size(1000, 500);
        panel.UpdateBarLayout();

        Assert.Equal(Color.White, panel.BackColor);
        Assert.Equal(ExpectedBarWidth(1000, options), panel.ContentPanel.Width);
        Assert.Equal(options.BarHeight, panel.ContentPanel.Height);
        Assert.Equal(Color.Black, panel.Bar.ForeColor);

        panel.SetColors(Color.Red, Color.Blue);
        Assert.Equal(Color.Red, panel.BackColor);
        Assert.Equal(Color.Blue, panel.Bar.ForeColor);
    }
}
