using System.Drawing;
using Shenora.WinForms;

namespace Shenora.Tests.WinForms;

public class WindowStateManagerTests
{
    private static readonly WindowStateOptions Options = new();

    // ---- ToPhysical ----

    [Fact]
    public void ToPhysical_is_identity_at_100_percent()
    {
        var (w, h, x, y, max) = WindowStateManager.ToPhysical(
            new WindowState(1000, 700, 30, 40, false), 1.0, Options);
        Assert.Equal((1000, 700), (w, h));
        Assert.Equal((30, 40), (x!.Value, y!.Value));
        Assert.False(max);
    }

    [Theory]
    [InlineData(1.5, 1500, 1050, 45, 60)]
    [InlineData(2.0, 2000, 1400, 60, 80)]
    public void ToPhysical_scales_size_and_position(double scale, int w, int h, int x, int y)
    {
        var result = WindowStateManager.ToPhysical(new WindowState(1000, 700, 30, 40, false), scale, Options);
        Assert.Equal((w, h, x, y), (result.Width, result.Height, result.X!.Value, result.Y!.Value));
    }

    [Fact]
    public void ToPhysical_falls_back_to_defaults_and_clamps_minimum_in_logical_space()
    {
        var defaults = WindowStateManager.ToPhysical(null, 2.0, Options);
        Assert.Equal((Options.DefaultWidth * 2, Options.DefaultHeight * 2), (defaults.Width, defaults.Height));
        Assert.Null(defaults.X);

        var clamped = WindowStateManager.ToPhysical(new WindowState(100, 100, null, null, false), 1.5, Options);
        Assert.Equal(((int)(Options.MinWidth * 1.5), (int)(Options.MinHeight * 1.5)), (clamped.Width, clamped.Height));
    }

    [Theory]
    [InlineData(30, null)]
    [InlineData(null, 40)]
    public void ToPhysical_position_is_both_or_neither(int? x, int? y)
    {
        var result = WindowStateManager.ToPhysical(new WindowState(1000, 700, x, y, false), 1.0, Options);
        Assert.Null(result.X);
        Assert.Null(result.Y);
    }

    [Fact]
    public void ToPhysical_survives_a_non_positive_scale()
    {
        var result = WindowStateManager.ToPhysical(new WindowState(1000, 700, 30, 40, false), 0, Options);
        Assert.Equal(1000, result.Width); // identity fallback, never zero/negative geometry
    }

    // ---- ToLogical ----

    [Fact]
    public void ToLogical_roundtrips_with_ToPhysical()
    {
        var logical = WindowStateManager.ToLogical(new Rectangle(60, 80, 2000, 1400), false, 2.0);
        Assert.Equal(new WindowState(1000, 700, 30, 40, false), logical);

        var physical = WindowStateManager.ToPhysical(logical, 2.0, Options);
        Assert.Equal((2000, 1400, 60, 80), (physical.Width, physical.Height, physical.X!.Value, physical.Y!.Value));
    }

    // ---- IsVisible ----

    private static readonly Rectangle[] SingleScreen = [new(0, 0, 1920, 1080)];

    [Fact]
    public void IsVisible_true_when_fully_on_screen()
    {
        Assert.True(WindowStateManager.IsVisible(100, 100, 800, 600, SingleScreen, Options));
    }

    [Fact]
    public void IsVisible_false_when_fully_off_screen()
    {
        Assert.False(WindowStateManager.IsVisible(2000, 100, 800, 600, SingleScreen, Options));
    }

    [Fact]
    public void IsVisible_requires_a_grabbable_strip_not_just_any_overlap()
    {
        // Only 50 px of width overlaps (< MinVisibleWidth 120) — a sliver is not grabbable.
        Assert.False(WindowStateManager.IsVisible(1870, 100, 800, 600, SingleScreen, Options));
        // 200 px overlap is.
        Assert.True(WindowStateManager.IsVisible(1720, 100, 800, 600, SingleScreen, Options));
    }

    [Fact]
    public void IsVisible_checks_every_screen()
    {
        Rectangle[] two = [new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080)];
        Assert.True(WindowStateManager.IsVisible(2200, 200, 800, 600, two, Options));
    }

    // ---- Store integration (pure part) ----

    private sealed class MemoryStore : IWindowStateStore
    {
        public WindowState? State;
        public WindowState? Load() => State;
        public void Save(WindowState state) => State = state;
    }

    [Fact]
    public void Apply_places_the_saved_position_even_when_maximized()
    {
        // The maximized flag must NOT discard placement: the pre-show position is what makes
        // WinForms maximize onto the SAVED monitor, and what restore-down returns to (Save
        // captures RestoreBounds for exactly this). Regression: a review found an added
        // !maximized condition silently re-centering every maximized launch.
        var scale = DpiHelper.SystemScale();
        var store = new MemoryStore { State = new WindowState(500, 400, 10, 10, Maximized: true) };
        using var form = new Form();
        new WindowStateManager(store).Apply(form);

        Assert.Equal(FormStartPosition.Manual, form.StartPosition);
        Assert.Equal(new Point(DpiHelper.Scale(10, scale), DpiHelper.Scale(10, scale)), form.Location);
        Assert.Equal(FormWindowState.Maximized, form.WindowState);
    }

    [Fact]
    public void JsonFileWindowStateStore_roundtrips_and_is_best_effort()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new JsonFileWindowStateStore(Path.Combine(dir, "window.json"));
            Assert.Null(store.Load());

            store.Save(new WindowState(1000, 700, 30, 40, true));
            Assert.Equal(new WindowState(1000, 700, 30, 40, true), store.Load());

            File.WriteAllText(Path.Combine(dir, "window.json"), "{not json");
            Assert.Null(store.Load()); // corrupt file → null, never a throw
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
